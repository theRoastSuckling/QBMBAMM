// Exposes shared version comparison helpers used by list and detail pages.
window.QBVersionCompare = {
    // Normalizes game version strings before equality checks.
    normalizeGameVersion(v) {
        if (!v) return '';
        return String(v).replace(/-RC\d*$/i, '').toLowerCase();
    },

    // Compares mixed version strings like 0.97, 0.97a, and 1.0-RC1.
    compareVersion(a, b) {
        const parse = v => {
            if (!v) return [0, 0, ''];
            const m = String(v).replace(/-RC\d*$/i, '').match(/(\d+)\.(\d+)(.*)/);
            if (!m) return [0, 0, String(v).toLowerCase()];
            return [parseInt(m[1], 10), parseInt(m[2], 10), (m[3] || '').toLowerCase()];
        };
        const pa = parse(a);
        const pb = parse(b);
        if (pa[0] !== pb[0]) return pa[0] - pb[0];
        if (pa[1] !== pb[1]) return pa[1] - pb[1];
        if (pa[2] < pb[2]) return -1;
        if (pa[2] > pb[2]) return 1;
        return 0;
    }
};
