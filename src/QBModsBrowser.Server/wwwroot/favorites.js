(function () {
    const KEY = 'qbmodsbrowser.favorites';

    // Normalizes stored topic ids to positive unique integers.
    function normalizeIds(arr) {
        if (!Array.isArray(arr)) return [];
        const out = [];
        const seen = new Set();
        for (const x of arr) {
            const n = Number(x);
            if (Number.isInteger(n) && n > 0 && !seen.has(n)) {
                seen.add(n);
                out.push(n);
            }
        }
        return out;
    }

    window.QBFavorites = {
        // Loads favorites from local storage with safe JSON fallback.
        load() {
            try {
                const raw = localStorage.getItem(KEY);
                if (!raw) return [];
                const o = JSON.parse(raw);
                return normalizeIds(o);
            } catch (_) {
                return [];
            }
        },

        // Saves normalized favorite ids to local storage.
        save(ids) {
            try {
                localStorage.setItem(KEY, JSON.stringify(normalizeIds(ids)));
            } catch (_) {}
        },

        // Checks whether a topic id exists in favorites.
        has(id) {
            const n = Number(id);
            if (!Number.isInteger(n) || n <= 0) return false;
            return this.load().includes(n);
        },

        // Adds or removes a topic id and returns its new state.
        toggle(id) {
            const n = Number(id);
            if (!Number.isInteger(n) || n <= 0) return false;
            const ids = this.load();
            const i = ids.indexOf(n);
            if (i >= 0) {
                ids.splice(i, 1);
                this.save(ids);
                return false;
            }
            ids.push(n);
            this.save(ids);
            return true;
        }
    };
})();
