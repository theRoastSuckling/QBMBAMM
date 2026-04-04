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
