// Creates control panel state and scraper actions for the mods page.
function createPanelState() {
    return {
        parent: null,
        open: false,
        // Expands the forum scrape accordion in the control panel drawer.
        scrapeSectionExpanded: false,
        status: { state: 'idle', isScraping: false, job: {}, stats: {} },
        config: { autoScrapeIntervalHours: 0, delayBetweenPagesMs: 1500, delayBetweenTopicsMs: 1500, defaultSpoilersOpen: false, openLinksInNewTab: true, cacheExternalImages: false },
        scope: 'new',
        // Per-board toggles sent with board-based scopes (new/all/pages).
        boards: { main: true, lesser: true, libraries: true },
        maxPages: 1,
        topicIdsStr: '',
        message: '',
        configSaved: false,
        _pollTimer: null,
        extCache: { fileCount: 0, totalSizeFormatted: '—', loaded: false },
        extCacheClearing: false,
        // null = not yet checked; true/false = result of playwright-status check.
        playwrightInstalled: null,
        // Remote bundle metadata: when the scraped data was last updated and when this app last fetched it.
        remoteDataInfo: { updatedAt: null, lastFetched: null },
        // Live install progress state.
        playwrightInstall: { running: false, succeeded: null, lines: [], _pollTimer: null },

        // Log pane state and methods are maintained in control-panel-log.js.
        ...buildLogPaneMethods(),

        // Toggles a board filter and persists it through the shared list-filter storage.
        toggleBoard(boardKey) {
            if (!Object.prototype.hasOwnProperty.call(this.boards, boardKey)) return;
            this.boards[boardKey] = !this.boards[boardKey];
            if (this.parent && typeof this.parent.saveFilters === 'function') this.parent.saveFilters();
        },

        // Loads panel data and starts status refresh and log polling while panel is open.
        // fetchStatus() also updates playwrightInstalled so no separate playwright check is needed here.
        async refresh() {
            await Promise.all([
                this.fetchStatus(), this.fetchConfig(), this.fetchExtCacheStats(),
                this.fetchLogs(), this.fetchLogOptions(),
                this.fetchRemoteDataInfo()
            ]);
            if (!this._pollTimer) {
                this._pollTimer = setInterval(() => {
                    if (this.open) {
                        this.fetchStatus();
                        this.fetchLogs();
                    }
                }, 2000);
            }
        },

        // Retrieves current scraper state and Playwright installation status.
        // isPlaywrightInstalled is embedded in the status response so it is always fresh
        // from the very first poll, even before the panel is opened.
        async fetchStatus() {
            try {
                const res = await fetch('/api/scraper/status');
                const data = await res.json();
                this.status = data;
                if (data.isPlaywrightInstalled !== undefined)
                    this.playwrightInstalled = data.isPlaywrightInstalled;
            } catch (_) {}
        },

        // Loads scraper settings into the panel form.
        async fetchConfig() {
            try {
                const res = await fetch('/api/scraper/config');
                const cfg = await res.json();
                this.config = {
                    autoScrapeIntervalHours: 0,
                    delayBetweenPagesMs: 1500,
                    delayBetweenTopicsMs: 1500,
                    defaultSpoilersOpen: false,
                    openLinksInNewTab: true,
                    cacheExternalImages: false,
                    ...cfg
                };
                this.config.openLinksInNewTab = (cfg.openLinksInNewTab ?? cfg.openModLinksInNewTab) !== false;
                this.config.cacheExternalImages = cfg.cacheExternalImages === true;
            } catch (_) {}
        },

        // Fetches the current external image cache file count and size.
        async fetchExtCacheStats() {
            try {
                const res = await fetch('/api/images/external-cache/stats');
                const data = await res.json();
                this.extCache = { fileCount: data.fileCount, totalSizeFormatted: data.totalSizeFormatted, loaded: true };
            } catch (_) {
                this.extCache = { fileCount: 0, totalSizeFormatted: '—', loaded: true };
            }
        },

        // Checks whether Playwright Chromium is installed and updates playwrightInstalled.
        async fetchPlaywrightStatus() {
            try {
                const res = await fetch('/api/scraper/playwright-status');
                const data = await res.json();
                this.playwrightInstalled = data.isInstalled;
            } catch (_) {}
        },

        // Fetches remote bundle metadata (when the scraped data was last updated and last fetched).
        async fetchRemoteDataInfo() {
            try {
                const res = await fetch('/api/scraper/remote-data-info');
                this.remoteDataInfo = await res.json();
            } catch (_) {}
        },

        // Starts a Playwright Chromium installation and polls for live progress until complete.
        async installPlaywright() {
            if (this.playwrightInstall.running) return;
            this.playwrightInstall.lines = [];
            this.playwrightInstall.succeeded = null;
            try {
                const res = await fetch('/api/scraper/playwright/install', { method: 'POST' });
                if (!res.ok) {
                    const err = await res.json().catch(() => ({}));
                    this.playwrightInstall.lines = [err.error || 'Failed to start installation'];
                    return;
                }
                this.playwrightInstall.running = true;
                this._pollPlaywrightInstall();
            } catch (e) {
                this.playwrightInstall.lines = ['Error: ' + e.message];
            }
        },

        // Polls install progress every 1.5 s until the backend reports completion.
        _pollPlaywrightInstall() {
            if (this.playwrightInstall._pollTimer) clearInterval(this.playwrightInstall._pollTimer);
            this.playwrightInstall._pollTimer = setInterval(async () => {
                try {
                    const res = await fetch('/api/scraper/playwright/install-status');
                    const data = await res.json();
                    this.playwrightInstall.lines = data.lines || [];
                    this.playwrightInstall.running = data.running;
                    this.playwrightInstall.succeeded = data.succeeded;
                    if (!data.running) {
                        clearInterval(this.playwrightInstall._pollTimer);
                        this.playwrightInstall._pollTimer = null;
                        // Re-check installation state so the UI switches to the scrape controls.
                        await this.fetchPlaywrightStatus();
                    }
                } catch (_) {}
            }, 1500);
        },

        // Deletes all cached external images and refreshes the stats display.
        async clearExtCache() {
            if (this.extCacheClearing) return;
            this.extCacheClearing = true;
            try {
                await fetch('/api/images/external-cache', { method: 'DELETE' });
                await this.fetchExtCacheStats();
            } catch (_) {}
            this.extCacheClearing = false;
        },

        // Starts a scrape job using the selected scope and board options.
        async startScrape() {
            this.message = '';
            const body = { scope: this.scope };
            if (this.scope === 'pages') body.pages = this.maxPages;
            if (this.scope === 'topics') {
                body.topicIds = this.topicIdsStr.split(',').map(s => parseInt(s.trim())).filter(n => !isNaN(n));
            }
            // Include board selection for board-based scopes.
            if (this.scope !== 'topics') {
                const bl = [];
                if (this.boards.main) bl.push('main');
                if (this.boards.lesser) bl.push('lesser');
                if (this.boards.libraries) bl.push('libraries');
                body.boards = bl;
            }
            try {
                const res = await fetch('/api/scraper/start', {
                    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
                });
                const data = await res.json();
                this.message = data.message || data.error || 'Started';
                if (res.ok && this.parent) {
                    await this.parent.fetchHeaderStats();
                    this.parent.updateHeaderPolling(true);
                }
            } catch (e) { this.message = 'Error: ' + e.message; }
        },

        // Sends a stop signal to the running scrape job.
        async stopScrape() {
            try {
                await fetch('/api/scraper/stop', { method: 'POST' });
                this.message = 'Stop signal sent';
                if (this.parent) await this.parent.fetchHeaderStats();
            }
            catch (e) { this.message = 'Error: ' + e.message; }
        },

        // Persists scraper settings and shows brief saved feedback.
        async saveConfig(opts = {}) {
            const silent = !!opts.silent;
            try {
                await fetch('/api/scraper/config', {
                    method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(this.config)
                });
                this.configSaved = true;
                setTimeout(() => { this.configSaved = false; }, 1400);
            } catch (e) {
                if (!silent) console.error('Failed to save config:', e);
            }
        },

        // Formats optional timestamps shown in panel labels using 24-hour time.
        formatDate(d) {
            return d
                ? new Date(d).toLocaleString(undefined, {
                    year: 'numeric',
                    month: 'short',
                    day: 'numeric',
                    hour: '2-digit',
                    minute: '2-digit',
                    second: '2-digit',
                    hour12: false
                })
                : '-';
        }
    };
}
