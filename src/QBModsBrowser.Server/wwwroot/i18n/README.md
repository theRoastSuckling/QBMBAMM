# QBMBAMM 中文汉化层（方案 B：运行时替换，不改业务代码）

## 原理

- `zh.js`：词典。`exact`（整句 EN→ZH，匹配前做空白归一化，容忍 HTML 源码换行）、
  `patterns`（`${...}` 占位模板句，译文按 `$1/$2/$3` 回填）、`regex`（原生正则，处理时钟等动态拼接）。
- `i18n.js`：运行时。初始 TreeWalker 全量替换文本节点与 `title`/`placeholder`/`aria-label`；
  MutationObserver 在回调内**同步**处理后续突变（Alpine `x-text` 重渲染、fetch 注入的面板、
  后端 message/error 落进 DOM，都在绘制前完成替换，不闪英文）；另有 2 秒看门狗全量重扫兜底。
- `index.html` / `mod.html`：仅各加两行 `<script defer src="/i18n/...">`。
  其余 HTML（control-panel、installs-panel、mod-profiles、mod-linkage-report-popup、setup-prompt）
  都是 fetch 注入的片段，由宿主页运行时覆盖，**不需要也无法注入**。
- 匹配不到的字符串保持英文原样（天然兜底），不会出现 `undefined` 或布局破坏。
- 论坛抓取内容（mod 名/作者/正文/日期/URL/服务器日志）是数据不是 UI，一律不翻。

## 上游更新维护

上游合并后，只需：重跑漏网扫描（浏览器 console 里用 TreeWalker 列出含 `[A-Za-z]{3,}` 的文本节点，
排除 SCRIPT/TEMPLATE 子树），把新出现的英文串补进 `zh.js`。代码文件零冲突。

## 已知未翻（有意保留）

- 托盘菜单（TrayApp，WinForms，不经过浏览器）：Open / Exit / 提示气泡
- 服务器日志面板内容、mod 描述正文、键盘按键提示（A / D、Q / E）
- `fractalsoftworks.com/forum` 链接文本、版本过滤 chip 的 `≥` 前缀行
