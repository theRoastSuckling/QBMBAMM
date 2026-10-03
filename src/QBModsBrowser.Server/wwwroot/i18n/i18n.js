// QBMBAMM i18n 运行时（方案 B：运行时整句替换，不改任何业务代码）
// 原理：
//   1. 词典来自 /i18n/zh.js 的 window.QB_I18N_ZH：
//      exact    整句精确匹配（匹配前做空白归一化，容忍 HTML 源码换行缩进）
//      patterns 模板句，${...} 转懒惰捕获组，译文按 $1/$2… 回填
//      regex    原生正则数组 [source, template]，处理动态拼接文本（时钟等）
//   2. 初始 TreeWalker 全量替换文本节点与 title/placeholder/aria-label 属性
//   3. MutationObserver 在回调内**同步**处理突变目标（微任务时机，先于绘制，
//      对每秒刷新的时钟也不会闪英文）；另有 2 秒看门狗全量重扫兜底
//   4. 替换幂等（译文不含英文键），不会死循环；匹配不到保持英文（兜底）
(function () {
    'use strict';

    var DICT = window.QB_I18N_ZH;
    if (!DICT) return; // 词典没加载成功就整体不启用，页面保持英文
    var EXACT = DICT.exact || {};

    function escapeRe(s) {
        return s.replace(/[.*+?^'"${}()|[\]\\]/g, '\\$&');
    }

    // 模板句 → 正则（长键优先，避免短键截胡）
    var PATTERNS = [];
    Object.keys(DICT.patterns || {}).forEach(function (k) {
        var parts = k.split(/\$\{[^}]*\}/);
        var src = '^';
        for (var i = 0; i < parts.length; i++) {
            src += escapeRe(parts[i]);
            if (i < parts.length - 1) src += '(.*?)';
        }
        src += '$';
        try {
            PATTERNS.push({ re: new RegExp(src), tpl: DICT.patterns[k], len: k.length });
        } catch (e) { /* 单条坏了跳过该条 */ }
    });
    PATTERNS.sort(function (a, b) { return b.len - a.len; });

    // 原生正则层
    var REGEXES = [];
    (DICT.regex || []).forEach(function (pair) {
        try {
            REGEXES.push({ re: new RegExp(pair[0]), tpl: pair[1] });
        } catch (e) { /* 同上 */ }
    });

    var ATTRS = ['title', 'placeholder', 'aria-label'];
    var SKIP_TAGS = { SCRIPT: 1, STYLE: 1, CODE: 1, PRE: 1, TEXTAREA: 1, NOSCRIPT: 1 };

    // 翻译一段文本。匹配基于“trim + 内部空白归一化”后的内容；
    // 替换只换掉原串里 trim 后的部分，保留首尾空白。函数形式回填避免 $ 序列被二次解释。
    function translateString(s) {
        if (!s || s.length < 2) return null;
        if (!/[A-Za-z]/.test(s)) return null;
        var trimmed = s.trim();
        if (!trimmed) return null;
        var t = trimmed.replace(/\s+/g, ' ');

        function fill(tpl, m) {
            var out = tpl;
            if (m) {
                for (var g = 1; g < m.length; g++) {
                    out = out.split('$' + g).join(m[g] == null ? '' : m[g]);
                }
            }
            return out;
        }

        var zh = null;
        if (Object.prototype.hasOwnProperty.call(EXACT, t)) {
            zh = EXACT[t] === t ? null : EXACT[t];
        }
        if (zh === null) {
            for (var i = 0; i < PATTERNS.length && zh === null; i++) {
                var m = t.match(PATTERNS[i].re);
                if (m) zh = fill(PATTERNS[i].tpl, m);
            }
        }
        if (zh === null) {
            for (var j = 0; j < REGEXES.length && zh === null; j++) {
                var m2 = t.match(REGEXES[j].re);
                if (m2) zh = fill(REGEXES[j].tpl, m2);
            }
        }
        if (zh === null || zh === trimmed) return null;
        return s.replace(trimmed, function () { return zh; });
    }

    function processTextNode(node) {
        var txt = node.nodeValue;
        if (!txt || !/[A-Za-z]/.test(txt)) return;
        var rep = translateString(txt);
        if (rep && rep !== txt) node.nodeValue = rep;
    }

    function processElement(el) {
        for (var i = 0; i < ATTRS.length; i++) {
            var a = ATTRS[i];
            if (el.hasAttribute(a)) {
                var v = el.getAttribute(a);
                if (!v) continue;
                var r = translateString(v);
                if (r && r !== v) el.setAttribute(a, r);
            }
        }
    }

    function walk(root) {
        if (!root) return;
        var nodes = [];
        try {
            var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT, {
                acceptNode: function (n) {
                    if (n.nodeType === 1 && SKIP_TAGS[n.tagName]) return NodeFilter.FILTER_REJECT;
                    return NodeFilter.FILTER_ACCEPT;
                }
            });
            while (walker.nextNode()) nodes.push(walker.currentNode);
        } catch (e) { return; }
        for (var i = 0; i < nodes.length; i++) {
            var n = nodes[i];
            if (n.nodeType === 3) processTextNode(n);
            else if (n.nodeType === 1) processElement(n);
        }
    }

    function processAdded(node) {
        if (node.nodeType === 3) { processTextNode(node); return; }
        if (node.nodeType === 1) {
            if (SKIP_TAGS[node.tagName]) return;
            processElement(node);
            walk(node);
        }
    }

    function translateTitle() {
        var r = translateString(document.title || '');
        if (r && r !== document.title) document.title = r;
    }

    function run() {
        walk(document.body);
        translateTitle();
    }

    // 同步处理突变：MutationObserver 回调在微任务时机执行，先于本轮绘制，
    // 因此 Alpine 每秒重渲染的文本也不会闪现英文。
    var mo = new MutationObserver(function (muts) {
        for (var i = 0; i < muts.length; i++) {
            var m = muts[i];
            if (m.type === 'characterData') {
                processTextNode(m.target);
            } else if (m.type === 'attributes') {
                if (m.target && m.target.nodeType === 1) processElement(m.target);
            } else if (m.type === 'childList') {
                for (var j = 0; j < m.addedNodes.length; j++) processAdded(m.addedNodes[j]);
            }
        }
        translateTitle();
    });

    function start() {
        run();
        mo.observe(document.body, {
            childList: true,
            subtree: true,
            characterData: true,
            attributes: true,
            attributeFilter: ATTRS
        });
        // 看门狗：任何漏网（时序、边界情况）最迟 2 秒收敛
        setInterval(run, 2000);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
