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

        // Forces an immediate re-download of the QBForumModData bundle, bypassing the 6-hour TTL.
        async forceRefreshForumData() {
            try {
                this.managerMessage = 'Refreshing forum data...';
                await fetch('/api/scraper/force-refresh-remote-data', { method: 'POST' });
                this.managerMessage = 'Forum data refreshed';
                await this.panel.fetchRemoteDataInfo();
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

        // Fetches app-level config (Discord IDs etc.) and the build version from the server in parallel.
        async loadAppConfig() {
            try {
                const [configRes, infoRes] = await Promise.all([
                    fetch('/api/app-config'),
                    fetch('/api/app-info'),
                ]);
                if (configRes.ok) this._discordConfig = await configRes.json();
                if (infoRes.ok) {
                    const info = await infoRes.json();
                    this.appVersion = info.version ?? '';
                }
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
