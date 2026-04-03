const LIST_FILTERS_KEY = 'qbmodsbrowser.listFilters';

// Builds filter, sorting, and list-display helpers for the mods page.
function buildListFilterMethods() {
    return {
        // Restores saved list filters from local storage.
        loadFilters() {
            try {
                const raw = localStorage.getItem(LIST_FILTERS_KEY);
                if (!raw) return;
                const o = JSON.parse(raw);
                if (typeof o.search === 'string') this.search = o.search;
                if (typeof o.minVersion === 'string') this.minVersion = this.normalizeVersionValue(o.minVersion);
                if (typeof o.category === 'string') this.category = o.category;
                if (typeof o.modIndexOnly === 'boolean') this.modIndexOnly = o.modIndexOnly;
                if (typeof o.includeArchivedModIndex === 'boolean') this.includeArchivedModIndex = o.includeArchivedModIndex;
                if (typeof o.sort === 'string') this.sort = o.sort === 'date' ? 'activity' : o.sort;
                if (o.sortDir === 'asc' || o.sortDir === 'desc') this.sortDir = o.sortDir;
                if (typeof o.ageIndex === 'number' && o.ageIndex >= 0 && o.ageIndex <= 7) this.ageIndex = o.ageIndex;
                if (typeof o.page === 'number' && o.page >= 1) this.page = o.page;
                if (typeof o.showScraperStatusOnMain === 'boolean') this.showScraperStatusOnMain = o.showScraperStatusOnMain;
                if (typeof o.favoritesFirst === 'boolean') this.favoritesFirst = o.favoritesFirst;
                else if (typeof o.favoritesOnly === 'boolean') this.favoritesFirst = o.favoritesOnly;
                if (typeof o.installFilter === 'string') this.installFilter = o.installFilter;
                if (typeof o.updatesFirst === 'boolean') this.updatesFirst = o.updatesFirst;
                if (typeof o.installedFirst === 'boolean') this.installedFirst = o.installedFirst;
                if (typeof o.installedCategoryFirst === 'boolean') this.installedCategoryFirst = o.installedCategoryFirst;
                if (o.panelBoards && typeof o.panelBoards === 'object') {
                    if (typeof o.panelBoards.main === 'boolean') this.panel.boards.main = o.panelBoards.main;
                    if (typeof o.panelBoards.lesser === 'boolean') this.panel.boards.lesser = o.panelBoards.lesser;
                    if (typeof o.panelBoards.libraries === 'boolean') this.panel.boards.libraries = o.panelBoards.libraries;
                }
            } catch (_) {}
        },

        // Persists current list filters for the next visit.
        saveFilters() {
            try {
                localStorage.setItem(LIST_FILTERS_KEY, JSON.stringify({
                    search: this.search,
                    minVersion: this.normalizeVersionValue(this.minVersion),
                    category: this.category,
                    modIndexOnly: this.modIndexOnly,
                    includeArchivedModIndex: this.includeArchivedModIndex,
                    sort: this.sort,
                    sortDir: this.sortDir,
                    ageIndex: this.ageIndex,
                    page: this.page,
                    showScraperStatusOnMain: this.showScraperStatusOnMain,
                    favoritesFirst: this.favoritesFirst,
                    installFilter: this.installFilter,
                    updatesFirst: this.updatesFirst,
                    installedFirst: this.installedFirst,
                    installedCategoryFirst: this.installedCategoryFirst,
                    panelBoards: this.panel?.boards
                }));
            } catch (_) {}
        },

        // Normalizes version input by trimming comparator prefixes.
        normalizeVersionValue(v) {
            if (typeof v !== 'string') return '';
            let out = v.trim();
            out = out.replace(/^(?:≥|>=)\s*/u, '');
            return out.trim();
        },

        // Builds the compact label for the version dropdown.
        versionSelectLabel() {
            const m = this.normalizeVersionValue(this.minVersion);
            if (!m) return 'All versions';
            return '≥ ' + m;
        },

        // Applies selected minimum version and reloads results.
        setMinVersion(v) {
            this.minVersion = v === '' || v === undefined ? '' : String(v);
            this.versionMenuOpen = false;
            this.onVersionChanged();
        },

        // Calculates responsive page size from viewport and grid.
        computePageSize() {
            const vh = window.innerHeight;
            const reserved = 268;
            const avail = Math.max(160, vh - reserved);
            const w = window.innerWidth;
            let cols = 1;
            if (w >= 1280) cols = 3;
            else if (w >= 640) cols = 2;
            const rowH = 108;
            const rows = Math.max(1, Math.floor(avail / rowH));
            const n = cols * rows;
            this.pageSize = Math.max(3, Math.min(48, n));
        },

        // Builds compact pagination items with gap markers.
        pageNumberItems() {
            const tp = this.totalPages, p = this.page;
            if (tp <= 1) return [];
            const nums = new Set([1, tp, p]);
            for (let i = p - 2; i <= p + 2; i++) if (i >= 1 && i <= tp) nums.add(i);
            const seeded = [...nums].sort((a, b) => a - b);
            if (seeded.length >= 2) {
                const leftGap = seeded[1] - seeded[0];
                if (leftGap > 2) nums.add(Math.floor((seeded[0] + seeded[1]) / 2));
                const n = seeded.length;
                const rightGap = seeded[n - 1] - seeded[n - 2];
                if (rightGap > 2) nums.add(Math.floor((seeded[n - 2] + seeded[n - 1]) / 2));
            }
            const sorted = [...nums].sort((a, b) => a - b);
            const out = [];
            for (let i = 0; i < sorted.length; i++) {
                if (i > 0 && sorted[i] - sorted[i - 1] > 1)
                    out.push({ key: 'gap-' + i + '-' + sorted[i], kind: 'gap' });
                out.push({ key: 'n-' + sorted[i], kind: 'num', n: sorted[i] });
            }
            return out;
        },

        // Navigates to a page and fetches fresh results.
        goPage(n) {
            if (n < 1 || n > this.totalPages) return;
            this.page = n;
            this.saveFilters();
            this.fetchMods();
        },

        // Updates sort mode and resets paging when changed.
        setSort(value) {
            if (this.sort !== value) {
                this.sort = value;
                this.page = 1;
                this.saveFilters();
                this.fetchMods();
            }
        },

        // Applies category filtering and reloads list results.
        setCategory(value) {
            if (this.category !== value) {
                this.category = value;
                this.page = 1;
                this.saveFilters();
                this.fetchMods();
            }
        },

        // Toggles favorites-first sorting and reloads the mod list.
        setFavoritesFirst(on) {
            const next = !!on;
            if (this.favoritesFirst === next) return;
            this.favoritesFirst = next;
            this.page = 1;
            this.saveFilters();
            this.fetchMods();
        },

        // Toggles a topic favorite and refreshes list when needed.
        toggleFavorite(topicId) {
            if (window.QBFavorites) QBFavorites.toggle(topicId);
            this.favoriteIds = window.QBFavorites ? QBFavorites.load() : [];
            if (this.favoritesFirst) this.fetchMods();
        },

        // Checks whether a topic is currently in favorites.
        isFavorite(topicId) {
            return this.favoriteIds.includes(topicId);
        },

        // Builds the canonical forum topic URL for a topic id.
        forumTopicUrl(topicId) {
            return `https://fractalsoftworks.com/forum/index.php?topic=${topicId}.0`;
        },

        // Exports favorited mods to a downloadable JSON file.
        async exportFavoritesJson() {
            const ids = window.QBFavorites ? QBFavorites.load() : [];
            if (!ids.length) {
                alert('No favourites to export.');
                return;
            }
            try {
                const pageSize = Math.min(Math.max(ids.length, 1), 2000);
                const params = new URLSearchParams({
                    topicIds: ids.join(','),
                    pageSize: String(pageSize),
                    page: '1',
                    sort: 'title',
                    dir: 'asc',
                    age: 'all'
                });
                const r = await fetch('/api/mods?' + params.toString());
                if (!r.ok) throw new Error(await r.text());
                const data = await r.json();
                const byId = new Map((data.mods || []).map(m => [m.topicId, m]));
                const items = ids.map(id => {
                    const m = byId.get(id);
                    const forumUrl = (m && m.topicUrl) ? m.topicUrl : this.forumTopicUrl(id);
                    return {
                        name: (m && m.title) ? m.title : `Topic ${id}`,
                        forumUrl
                    };
                });
                const blob = new Blob([JSON.stringify(items, null, 2)], { type: 'application/json;charset=utf-8' });
                const a = document.createElement('a');
                const url = URL.createObjectURL(blob);
                a.href = url;
                a.download = 'qb-starsector-favorites.json';
                a.rel = 'noopener';
                document.body.appendChild(a);
                a.click();
                a.remove();
                URL.revokeObjectURL(url);
            } catch (e) {
                console.error(e);
                alert('Could not export favourites. Is the server running?');
            }
        },

        // Maps category labels to stable icon category keys.
        categoryIcon(categoryName) {
            if (!categoryName) return 'uncategorized';
            const c = categoryName.toLowerCase();
            if (c.includes('uncategorized')) return 'uncategorized';
            if (c.includes('faction')) return 'faction';
            if (c.includes('feature overhaul') || c.includes('overhaul')) return 'overhaul';
            if (c.includes('library') || c.includes('libraries')) return 'library';
            if (c.includes('flag pack')) return 'flag';
            if (c.includes('portrait pack')) return 'portrait';
            if (c.includes('content expansion')) return 'expansion';
            if (c.includes('mega')) return 'mega';
            if (c.includes('misc')) return 'misc';
            if (c.includes('graphic') || c.includes('visual') || c.includes('portrait') || c.includes('audio')) return 'graphics';
            if (c.includes('standalone utilit')) return 'standalone-utility';
            if (c.includes('tool') || c.includes('utility')) return 'utility';
            return 'misc';
        },

        // Resolves the icon name for a category label.
        categoryIconName(categoryName) {
            const key = this.categoryIcon(categoryName);
            const iconMap = {
                faction: 'heroicons:shield-check',
                overhaul: 'heroicons:cog-6-tooth',
                expansion: 'heroicons:squares-plus',
                mega: 'heroicons:cube',
                misc: 'heroicons:sparkles',
                graphics: 'heroicons:photo',
                library: 'heroicons:book-open',
                flag: 'heroicons:flag',
                portrait: 'heroicons:user-circle',
                utility: 'heroicons:wrench-screwdriver',
                'standalone-utility': 'heroicons:command-line',
                uncategorized: 'heroicons:question-mark-circle'
            };
            return iconMap[key] || 'heroicons:question-mark-circle';
        },

        // Sorts categories using project-specific priority rules.
        sortCategories(list) {
            if (!Array.isArray(list)) return [];
            const ordered = [...list];
            ordered.sort((a, b) => {
                const aa = (a || '').toLowerCase();
                const bb = (b || '').toLowerCase();
                const rank = (value) => {
                    if (value === 'megamods') return -10;
                    if (value === 'flag packs') return 10;
                    if (value === 'portrait packs') return 11;
                    if (value === 'utility mods') return 12;
                    if (value === 'libraries') return 13;
                    if (value === 'standalone utilities') return 14;
                    if (value === 'uncategorized') return 15;
                    return 0;
                };
                const ra = rank(aa);
                const rb = rank(bb);
                if (ra !== rb) return ra - rb;
                return aa.localeCompare(bb);
            });
            return ordered;
        },

        // Extracts major and minor numbers for version tiering.
        extractMajorMinor(version) {
            if (typeof version !== 'string') return null;
            const m = version.match(/(\d+)\.(\d+)/);
            if (!m) return null;
            const major = Number(m[1]);
            const minor = Number(m[2]);
            if (Number.isNaN(major) || Number.isNaN(minor)) return null;
            return { major, minor, key: `${major}.${minor}` };
        },

        // Recomputes color tiers from available game versions.
        rebuildVersionTierMap() {
            const pairs = new Map();
            for (const v of this.versions || []) {
                const mm = this.extractMajorMinor(v);
                if (!mm) continue;
                pairs.set(mm.key, mm);
            }
            const sorted = [...pairs.values()].sort((a, b) => {
                if (a.major !== b.major) return b.major - a.major;
                return b.minor - a.minor;
            });
            const map = {};
            for (let i = 0; i < sorted.length; i++) {
                const key = sorted[i].key;
                map[key] = i === 0 ? 'green' : i === 1 ? 'yellow' : i === 2 ? 'orange' : 'red';
            }
            this.versionTierMap = map;
        },

        // Returns CSS classes for the current version tier.
        versionTagClass(version) {
            const mm = this.extractMajorMinor(version);
            const tier = mm ? this.versionTierMap[mm.key] : null;
            if (tier === 'green') return 'bg-emerald-500/20 text-emerald-300 border border-emerald-400/40';
            if (tier === 'yellow') return 'bg-yellow-500/20 text-yellow-300 border border-yellow-400/40';
            if (tier === 'orange') return 'bg-orange-500/20 text-orange-300 border border-orange-400/40';
            if (tier === 'red') return 'bg-red-500/20 text-red-300 border border-red-400/40';
            return 'bg-slate-500/20 text-slate-300 border border-slate-400/40';
        },

        // Debounces search input before querying the backend.
        onSearchChanged() {
            this.page = 1;
            this.saveFilters();
            this.syncSearchQueryParam();
            clearTimeout(this._searchTimer);
            this._searchTimer = setTimeout(() => this.fetchMods(), 200);
        },

        // Clears search text and reloads default results.
        clearSearch() {
            if (!this.search) return;
            this.search = '';
            this.page = 1;
            this.saveFilters();
            this.syncSearchQueryParam();
            clearTimeout(this._searchTimer);
            this.fetchMods();
        },

        // Keeps the URL search query in sync with the current search input.
        syncSearchQueryParam() {
            try {
                const url = new URL(window.location.href);
                const value = (this.search || '').trim();
                if (value) url.searchParams.set('search', value);
                else url.searchParams.delete('search');
                window.history.replaceState({}, '', url.toString());
            } catch (_) {}
        },

        // Applies typed version changes and reloads results.
        onVersionChanged() {
            this.minVersion = this.normalizeVersionValue(this.minVersion);
            this.page = 1;
            this.saveFilters();
            this.fetchMods();
        },

        // Applies install-status filter and refreshes page one.
        setInstallFilter(value) {
            if (this.installFilter !== value) {
                this.installFilter = value;
                this.page = 1;
                this.saveFilters();
                this.fetchMods();
            }
        },

        // Normalizes game version strings for comparisons.
        normalizeGameVer(v) {
            return window.QBVersionCompare
                ? window.QBVersionCompare.normalizeGameVersion(v)
                : (v ? String(v).replace(/-RC\d*$/i, '').toLowerCase() : '');
        },

        // Compares two game version strings for ordering.
        compareGameVer(a, b) {
            return window.QBVersionCompare
                ? window.QBVersionCompare.compareVersion(a, b)
                : 0;
        },

        // Checks whether local game version is newer than forum version.
        isLocalGameVerHigher(local, forum) {
            return this.compareGameVer(local, forum) > 0;
        },

        // Formats dates for compact mod list metadata.
        formatDateShort(dateStr) {
            if (!dateStr) return '';
            const d = new Date(dateStr);
            if (isNaN(d.getTime())) return '';
            return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
        }
    };
}
