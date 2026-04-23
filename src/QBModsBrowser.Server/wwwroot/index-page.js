// Typical Windows Starsector mods folder; keep in sync with ManagerConfig.DefaultModsPath on the server.
const QB_DEFAULT_MODS_PATH = String.raw`C:\Program Files (x86)\Fractal Softworks\Starsector\mods`;

// Provides Alpine state and actions for the mods list page.
function modsApp() {
    return {
        mods: [],
        versions: [],
        versionTierMap: {},
        total: 0,
        totalPages: 1,
        page: 1,
        pageSize: 12,
        search: '',
        minVersion: '',
        versionMenuOpen: false,
        category: '',
        categories: [],
        favoritesFirst: false,
        favoriteIds: [],
        modIndexOnly: false,
        includeArchivedModIndex: false,
        sort: 'views',
        sortDir: 'desc',
        ageOptions: ['1m', '2m', '6m', '1y', '2y', '4y', '8y', 'all'],
        ageIndex: 7,
        installFilter: 'all',
        updatesFirst: true,
        installedFirst: true,
        installedCategoryFirst: false,
        loading: true,
        isInitialLoad: true,
        _searchTimer: null,
        clock: '',
        // App version string fetched from /api/app-info on init (e.g. "v2.1.2").
        appVersion: '',
        scraperState: 'idle',
        lastScraped: '',
        dataSize: '',
        _headerPollTimer: null,
        showScraperStatusOnMain: false,
        managerConfig: { modsPath: QB_DEFAULT_MODS_PATH },
        managerMessage: '',
        // True when starsector.exe is detected one folder above the configured mods path.
        gameExeAvailable: false,
        // True when no mods path is configured — triggers the first-run setup prompt.
        showSetupPrompt: false,
        // Validation error shown in the setup prompt when the entered path does not exist.
        setupPathError: '',
        isExtracting: false,
        extractFeedbackMessage: '',
        extractFeedbackTone: '',
        _extractFeedbackTimer: null,
        extractShowAlreadyInstalledLogs: false,
        extractVisibleAlreadyArchiveNames: [],
        hoverTip: { show: false, x: 0, y: 0, line1: '', line2: '' },
        // Discord IDs loaded from /api/app-config on init; used by openDiscordBugThread.
        _discordConfig: null,
        downloadLog: createDownloadLogState(),
        appUpdate: createAppUpdateState(),
        panel: createPanelState(),
        // Dependency overlay state — populated by fetchDependencies(), applied by _applyDepSort().
        depPriorityTopicIds: [],
        unmatchedDeps: [],
        // Initializes filters, timers, and first data fetches on page load.
        async init() {
            await this.loadControlPanelTemplate();
            await this.loadInstallsPanelTemplate();
            await this.loadSetupPromptTemplate();
            this.loadFilters();
            const qs = new URLSearchParams(window.location.search);
            if (qs.has('search')) {
                this.search = qs.get('search') || '';
                this.page = 1;
            }
            this.favoriteIds = window.QBFavorites ? QBFavorites.load() : [];
            this.panel.parent = this;
            this.downloadLog.parent = this;
            this.appUpdate.parent = this;
            this.computePageSize();
            let resizeTimer = null;
            window.addEventListener('resize', () => {
                clearTimeout(resizeTimer);
                resizeTimer = setTimeout(() => {
                    const prev = this.pageSize;
                    this.computePageSize();
                    if (prev !== this.pageSize) {
                        this.page = 1;
                        this.saveFilters();
                        this.fetchMods();
                    }
                }, 200);
            });
            // Flash the previously-viewed mod card when the browser restores this page from bfcache.
            window.addEventListener('pageshow', (e) => {
                if (e.persisted) this.$nextTick().then(() => this.flashReturnMod());
            });
            this.updateClock();
            setInterval(() => this.updateClock(), 1000);
            await window.QBPageLoad.scanLocalModsOnPageLoad();
            await this.fetchMods();
            await this.$nextTick();
            this.flashReturnMod();
            await this.initProfiles();
            await this.fetchHeaderStats();
            await this.loadManagerConfig();
            await this.loadAppConfig();
            await this.checkGameExe();
            // Show setup prompt for new users who haven't explicitly saved their mods path yet; pre-fill the default install path.
            if (!this.managerConfig.isUserConfigured) {
                if (!this.managerConfig.modsPath) this.managerConfig.modsPath = QB_DEFAULT_MODS_PATH;
                this.showSetupPrompt = true;
            }
            this.maybeRunDailyUpdateCheck();
            this.downloadLog.fetchItems();
            this.appUpdate.fetchStatus();
            // Non-blocking: wait for the startup version check then refresh mod cards.
            this._warmAndRefreshOnStartup();
        },

        // Reads the one-shot sessionStorage return-signal, scrolls the matching card into view,
        // and plays a brief glow-fadeout animation to indicate which mod was just viewed.
        flashReturnMod() {
            const id = sessionStorage.getItem('qb.returnFromModId');
            if (!id) return;
            sessionStorage.removeItem('qb.returnFromModId');
            const el = document.getElementById('mod-card-' + id);
            if (!el) return;
            el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
            const cs = window.getComputedStyle(el);
            const borderColor = cs.borderColor || 'rgba(148, 163, 184, 0.65)';
            const anim = el.animate([
                {
                    boxShadow: `0 0 0 3px ${borderColor}, 0 0 38px ${borderColor}`,
                    filter: 'brightness(1.62) saturate(1.3)',
                    offset: 0
                },
                {
                    boxShadow: `0 0 0 3px ${borderColor}, 0 0 32px ${borderColor}`,
                    filter: 'brightness(1.52) saturate(1.24)',
                    offset: 0.24
                },
                {
                    boxShadow: `0 0 0 2px ${borderColor}, 0 0 22px ${borderColor}`,
                    filter: 'brightness(1.34) saturate(1.16)',
                    offset: 0.5
                },
                {
                    boxShadow: `0 0 0 0 ${borderColor}`,
                    filter: 'brightness(1) saturate(1)'
                }
            ], {
                duration: 5000,
                easing: 'cubic-bezier(0.22, 1, 0.36, 1)'
            });
            anim.onfinish = () => { anim.cancel(); };
        },

        // Loads the control panel drawer markup from a dedicated HTML partial and initializes Alpine bindings.
        async loadControlPanelTemplate() {
            const mount = this.$refs.controlPanelMount;
            if (!mount) return;
            try {
                const res = await fetch('/control-panel.html', { cache: 'no-store' });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                mount.innerHTML = await res.text();
                if (window.Alpine?.initTree) {
                    window.Alpine.initTree(mount);
                }
            } catch (e) {
                console.error('Failed to load control panel template:', e);
                mount.innerHTML = '<div class="text-xs text-red-300 p-4">Failed to load Control Panel.</div>';
            }
        },

        // Loads the installs panel markup from a dedicated HTML partial and initializes Alpine bindings.
        async loadInstallsPanelTemplate() {
            const mount = this.$refs.installsPanelMount;
            if (!mount) return;
            try {
                const res = await fetch('/installs-panel.html', { cache: 'no-store' });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                mount.innerHTML = await res.text();
                if (window.Alpine?.initTree) {
                    window.Alpine.initTree(mount);
                }
            } catch (e) {
                console.error('Failed to load installs panel template:', e);
                mount.innerHTML = '<div class="w-72 shrink-0 text-xs text-red-300">Failed to load Installs panel.</div>';
            }
        },

        // Updates the live header clock once per second.
        updateClock() {
            const now = new Date();
            const days = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
            const h = String(now.getHours()).padStart(2, '0');
            const m = String(now.getMinutes()).padStart(2, '0');
            const s = String(now.getSeconds()).padStart(2, '0');
            this.clock = days[now.getDay()] + '  ' + h + ':' + m + ':' + s;
        },

        // Fetches scraper status summary displayed in the header.
        async fetchHeaderStats() {
            try {
                const res = await fetch('/api/scraper/status');
                const data = await res.json();
                this.scraperState = data.state || 'idle';
                this.dataSize = data.stats?.totalSizeFormatted || '-';
                this.updateHeaderPolling(!!data.isScraping || data.state === 'scraping');
                if (data.job?.finishedAt) {
                    this.lastScraped = new Date(data.job.finishedAt).toLocaleDateString(undefined, {
                        month: 'short',
                        day: 'numeric',
                        hour: '2-digit',
                        minute: '2-digit',
                        hour12: false
                    });
                }
            } catch (_) {}
        },

        // Starts periodic header status refresh while scraping.
        startHeaderPolling() {
            if (this._headerPollTimer) return;
            this._headerPollTimer = setInterval(() => this.fetchHeaderStats(), 2000);
        },

        // Stops periodic header status refresh timer.
        stopHeaderPolling() {
            if (!this._headerPollTimer) return;
            clearInterval(this._headerPollTimer);
            this._headerPollTimer = null;
        },

        // Switches header polling on or off from scraper state.
        updateHeaderPolling(isScraping) {
            if (isScraping) this.startHeaderPolling();
            else this.stopHeaderPolling();
        },

        // Fetches mod summaries using current filters and pagination. Pass replaceMods:false after installs to refresh dep overlay and pager without reordering the current grid.
        async fetchMods(opts = {}) {
            const replaceMods = opts.replaceMods !== false;
            if (replaceMods && this.isInitialLoad) this.loading = true;
            try {
                const params = new URLSearchParams({
                    page: this.page,
                    sort: this.sort,
                    dir: this.sortDir,
                    age: this.ageOptions[this.ageIndex],
                    pageSize: String(this.pageSize)
                });
                if (this.search) params.set('search', this.search);
                if (this.minVersion) params.set('minVersion', this.normalizeVersionValue(this.minVersion));
                if (this.category) params.set('category', this.category);
                if (this.modIndexOnly) params.set('modIndexOnly', 'true');
                if (this.modIndexOnly && this.includeArchivedModIndex) params.set('includeArchived', 'true');
                if (this.installFilter && this.installFilter !== 'all') params.set('installStatus', this.installFilter);
                if (this.favoritesFirst) {
                    this.favoriteIds = window.QBFavorites ? QBFavorites.load() : [];
                    if (this.favoriteIds.length > 0) {
                        params.set('topicIds', this.favoriteIds.join(','));
                        params.set('favoritesFirst', 'true');
                    }
                }
                if (this.updatesFirst) params.set('updatesFirst', 'true');
                if (this.installedFirst) params.set('installedFirst', 'true');
                if (this.installedCategoryFirst) params.set('installedCategoryFirst', 'true');
                const res = await fetch('/api/mods?' + params);
                const data = await res.json();
                if (replaceMods) {
                    this.mods = data.mods;
                } else {
                    // Overlay server fields onto existing rows so version/dep data updates in place without reordering the grid.
                    const byId = new Map(data.mods.map(m => [m.topicId, m]));
                    for (const row of this.mods) {
                        if (!row._isDepGhost) Object.assign(row, byId.get(row.topicId));
                    }
                }
                // Capture dep data returned alongside mods so _applyDepSort can tag/ghost without a second request.
                this.depPriorityTopicIds = (data.depPriorityTopicIds || []).map(t => t.topicId);
                this._depRequestersByTopicId = {};
                for (const t of (data.depPriorityTopicIds || [])) {
                    this._depRequestersByTopicId[t.topicId] = t.requesters || [];
                }
                this.unmatchedDeps = data.unmatchedDependencies || [];
                this._applyDepSort?.();
                this.total = data.total;
                this.totalPages = Math.max(1, data.totalPages || 1);
                if (data.versions) {
                    this.versions = data.versions;
                    this.rebuildVersionTierMap();
                }
                if (data.categories) this.categories = this.sortCategories(data.categories);
                if (this.category && !this.categories.includes(this.category)) this.category = '';
                if (!this.modIndexOnly) this.includeArchivedModIndex = false;
                // Persist current page's mod order and query context so detail pages can resolve prev/next mod and cross-page navigation.
                try {
                    const ids = (this.mods || []).map(m => m.topicId);
                    localStorage.setItem('qb.modNavContext', JSON.stringify({
                        ids,
                        page: this.page,
                        totalPages: this.totalPages,
                        queryParams: params.toString(),
                        ts: Date.now()
                    }));
                } catch (_) {}
                // Full list fetch: reload the slice for the clamped page. Meta-only (e.g. after install): keep current rows so the grid does not jump to a re-sorted server page.
                if (this.page > this.totalPages) {
                    this.page = this.totalPages;
                    this.saveFilters();
                    if (replaceMods) return await this.fetchMods();
                }
                this.saveFilters();
            } catch (e) { console.error('Failed to load mods:', e); }
            finally {
                if (replaceMods) {
                    this.loading = false;
                    this.isInitialLoad = false;
                }
            }
        },

        // Formats large counts with K and M suffixes.
        formatNumber(n) {
            if (n >= 1000000) return (n / 1000000).toFixed(1) + 'M';
            if (n >= 1000) return (n / 1000).toFixed(1) + 'K';
            return n;
        },

        // Opens the welcome / first-run setup overlay (same as the T shortcut).
        openWelcomePopup() {
            this.setupPathError = '';
            this.showSetupPrompt = true;
        },

        // Handles list-page keyboard shortcuts: A/D paging, Q/E category, T opens the welcome/setup prompt.
        handleListKeydown(e) {
            const tag = e.target?.tagName?.toUpperCase();
            if (tag === 'INPUT' || tag === 'TEXTAREA' || e.target?.isContentEditable) return;
            if (e.key === 'a' || e.key === 'A') {
                e.preventDefault();
                this.goPage(this.page - 1);
            } else if (e.key === 'd' || e.key === 'D') {
                e.preventDefault();
                this.goPage(this.page + 1);
            } else if (e.key === 'q' || e.key === 'Q' || e.key === 'e' || e.key === 'E') {
                const cats = ['', ...this.categories];
                const idx = cats.indexOf(this.category);
                const delta = e.key.toLowerCase() === 'e' ? 1 : -1;
                const next = ((idx + delta) % cats.length + cats.length) % cats.length;
                this.setCategory(cats[next]);
            } else if (e.key === 't' || e.key === 'T') {
                e.preventDefault();
                this.openWelcomePopup();
            }
        },

        ...buildListFilterMethods(),
        ...buildInstallsPanelMethods(),
        ...buildManagerMethods(),
        ...buildModSummaryMethods(),
        ...buildModProfileMethods(),
        ...buildDependencyMethods()
    };
}
