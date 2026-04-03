// Creates download log state and behavior for the mods list page.
function createDownloadLogState() {
    return {
        items: [],
        activeCount: 0,
        _pollTimer: null,
        _modDlMap: {},
        _refreshedCompletions: {},
        parent: null,

        // Starts polling download status while active jobs exist.
        startPolling() {
            if (this._pollTimer) return;
            this._pollTimer = setInterval(() => this.fetchItems(), 1000);
        },

        // Stops download polling when no active jobs remain.
        stopPolling() {
            if (!this._pollTimer) return;
            clearInterval(this._pollTimer);
            this._pollTimer = null;
        },

        // Fetches latest download rows and updates per-mod status map.
        async fetchItems() {
            try {
                const res = await fetch('/api/manager/downloads');
                this.items = await res.json();
                this.activeCount = this.items.filter(d =>
                    d.status === 'Queued' || d.status === 'Downloading' || d.status === 'RetrievingInfo' || d.status === 'Installing'
                ).length;

                const map = {};
                for (const dl of this.items) {
                    if (dl.topicId && !map[dl.topicId]) map[dl.topicId] = dl;
                }
                this._modDlMap = map;

                for (const dl of this.items) {
                    if (dl.topicId && dl.status === 'Completed' && !this._refreshedCompletions[dl.id]) {
                        this._refreshedCompletions[dl.id] = true;
                        if (this.parent) {
                            // Warm version cache, patch the installed card directly (it may no longer appear on this page slice after re-sort), then sync dep/pager metadata.
                            await this.parent.checkUpdates({ silent: true, force: true });
                            await this.parent.refreshSingleMod(dl.topicId);
                            await this.parent.fetchMods({ replaceMods: false });
                            // Sync newly installed (and therefore enabled) mods into the equipped profile.
                            if (dl.installedModIds && dl.installedModIds.length > 0) {
                                for (const modId of dl.installedModIds) {
                                    await this.parent.onModToggled(modId, true);
                                }
                            }
                        }
                    }
                }

                if (this.activeCount > 0 && !this._pollTimer) this.startPolling();
                else if (this.activeCount === 0 && this._pollTimer) {
                    setTimeout(() => {
                        if (this.activeCount === 0) this.stopPolling();
                    }, 5000);
                }
            } catch (_) {}
        }
    };
}

// Creates control panel state and scraper actions for the mods page.
function createPanelState() {
    return {
        parent: null,
        open: false,
        // Expands the forum scrape accordion in the control panel drawer.
        scrapeSectionExpanded: false,
        status: { state: 'idle', isScraping: false, job: {}, stats: {} },
        config: { autoScrapeIntervalHours: 0, delayBetweenPagesMs: 1500, delayBetweenTopicsMs: 1500, defaultSpoilersOpen: false, openLinksInNewTab: true, cacheExternalImages: true, showInfoConsoleLogs: false },
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

        // Toggles a board filter and persists it through the shared list-filter storage.
        toggleBoard(boardKey) {
            if (!Object.prototype.hasOwnProperty.call(this.boards, boardKey)) return;
            this.boards[boardKey] = !this.boards[boardKey];
            if (this.parent && typeof this.parent.saveFilters === 'function') this.parent.saveFilters();
        },

        // Loads panel data and starts status refresh while panel is open.
        async refresh() {
            await Promise.all([this.fetchStatus(), this.fetchConfig(), this.fetchExtCacheStats()]);
            if (!this._pollTimer) {
                this._pollTimer = setInterval(() => { if (this.open) this.fetchStatus(); }, 2000);
            }
        },

        // Retrieves current scraper state for the control panel.
        async fetchStatus() {
            try {
                const res = await fetch('/api/scraper/status');
                this.status = await res.json();
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
                    cacheExternalImages: true,
                    showInfoConsoleLogs: false,
                    ...cfg
                };
                this.config.openLinksInNewTab = (cfg.openLinksInNewTab ?? cfg.openModLinksInNewTab) !== false;
                this.config.cacheExternalImages = cfg.cacheExternalImages !== false;
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

// Builds manager and download actions for mod rows and panel tools.
function buildManagerMethods() {
    return {
        ...buildSetupPromptMethods(),

        // Saves manager config, triggers a local rescan, and re-checks if starsector.exe is available.
        async saveManagerConfig() {
            try {
                await fetch('/api/manager/config', {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(this.managerConfig)
                });
                this.managerMessage = 'Config saved. Rescanning...';
                await this.rescanLocalMods();
                await this.checkGameExe();
            } catch (e) {
                this.managerMessage = 'Error: ' + e.message;
            }
        },

        // Triggers local mods scan and refreshes list summaries (dep sort is included in fetchMods).
        async rescanLocalMods() {
            try {
                const res = await fetch('/api/manager/scan', { method: 'POST' });
                const data = await res.json();
                this.managerMessage = `Scan complete: ${data.count} mods found`;
                this.fetchMods();
                setTimeout(() => { this.managerMessage = ''; }, 3000);
            } catch (e) {
                this.managerMessage = 'Scan error: ' + e.message;
            }
        },

        // Runs update checks for installed local mods. Silent calls skip the full list refresh.
        async checkUpdates(opts = {}) {
            const silent = !!opts.silent;
            const force = !!opts.force;
            try {
                if (!silent) this.managerMessage = 'Checking updates...';
                const endpoint = force ? '/api/manager/check-updates?force=true' : '/api/manager/check-updates';
                const res = await fetch(endpoint, { method: 'POST' });
                const data = await res.json();
                if (!silent) this.managerMessage = `Checked ${data.totalChecked} mods, ${data.updatesAvailable} updates`;
                if (!silent) this.fetchMods();
                if (!silent) setTimeout(() => { this.managerMessage = ''; }, 5000);
            } catch (e) {
                if (!silent) this.managerMessage = 'Error: ' + e.message;
            }
        },

        // Runs at most once per day per browser to warm version-check cache on first page load.
        async maybeRunDailyUpdateCheck() {
            const key = 'qbmods:lastAutoUpdateCheckDate';
            const today = new Date().toISOString().slice(0, 10);
            let last = '';
            try {
                last = localStorage.getItem(key) || '';
            } catch (_) {}
            if (last === today) return;

            await this.checkUpdates({ silent: true, force: false });
            try {
                localStorage.setItem(key, today);
            } catch (_) {}
        },

        // Refreshes ModRepo cache used for matching and versions.
        async refreshModRepo() {
            try {
                this.managerMessage = 'Refreshing ModRepo...';
                const res = await fetch('/api/manager/mod-repo/refresh', { method: 'POST' });
                const data = await res.json();
                this.managerMessage = `ModRepo refreshed: ${data.count} entries`;
                this.fetchMods();
                setTimeout(() => { this.managerMessage = ''; }, 3000);
            } catch (e) {
                this.managerMessage = 'Error: ' + e.message;
            }
        },

        // Loads mod manager settings shown in the UI panel.
        async loadManagerConfig() {
            try {
                const res = await fetch('/api/manager/config');
                this.managerConfig = await res.json();
            } catch (_) {}
        },

        // Runs in background after init: blocks until the startup version check completes, then merges fresh data into the current page without reordering the grid.
        async _warmAndRefreshOnStartup() {
            try {
                await fetch('/api/manager/check-updates', { method: 'POST' });
                await this.fetchMods({ replaceMods: false });
            } catch (_) {}
        },

        // Checks whether starsector.exe exists one folder above the configured mods path and updates gameExeAvailable.
        async checkGameExe() {
            try {
                const res = await fetch('/api/manager/game-exe');
                if (!res.ok) return;
                const data = await res.json();
                this.gameExeAvailable = !!data.exists;
            } catch (_) {
                this.gameExeAvailable = false;
            }
        },

        // Launches starsector.exe via the server-side process launcher; retriggers a short CSS pulse on the button for feedback.
        async launchGame() {
            const btn = this.$refs.launchGameBtn;
            if (btn) {
                btn.classList.remove('is-launch-feedback');
                void btn.offsetWidth;
                btn.classList.add('is-launch-feedback');
                btn.addEventListener(
                    'animationend',
                    () => btn.classList.remove('is-launch-feedback'),
                    { once: true }
                );
            }
            try {
                await fetch('/api/manager/launch-game', { method: 'POST' });
            } catch (_) {}
        },

        // Fetches app-level config (Discord IDs etc.) from the server and stores it for use by other methods.
        async loadAppConfig() {
            try {
                const res = await fetch('/api/app-config');
                if (res.ok) this._discordConfig = await res.json();
            } catch (_) {}
        },

        // Opens the Discord bug-report thread using IDs from app-config.json; tries the discord:// protocol first, falls back to web URL.
        openDiscordBugThread() {
            const serverId = this._discordConfig?.discord?.serverId ?? '';
            const threadId = this._discordConfig?.discord?.bugReportThreadId ?? '';
            if (!serverId || !threadId) return;
            const webUrl = `https://discord.com/channels/${serverId}/${threadId}`;

            // Attempt to wake the Discord desktop app via its custom protocol handler.
            window.location.href = `discord://discord.com/channels/${serverId}/${threadId}`;

            // If the app didn't intercept, open the web fallback after a short pause.
            setTimeout(() => window.open(webUrl, '_blank', 'noopener,noreferrer'), 1500);
        }
    };
}
