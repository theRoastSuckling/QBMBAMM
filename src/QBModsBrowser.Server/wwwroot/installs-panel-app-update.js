// Alpine state for the pinned "App update available" card in the Installs panel.
// Polled on every page load via modsApp.init(); server caches GitHub response for 60s.
function createAppUpdateState() {
    return {
        status: {
            currentVersion: '',
            remoteVersion: '',
            updateAvailable: false,
            releaseNotes: '',
            releaseUrl: '',
            lastError: ''
        },
        installing: false,
        parent: null,

        async fetchStatus() {
            try {
                const res = await fetch('/api/app-update/status', { cache: 'no-store' });
                if (!res.ok) return;
                this.status = await res.json();
            } catch (_) { /* silent: GitHub/network blips shouldn't disrupt the page */ }
        },

        // Disabled while any active install/download is in flight so we don't kill the app mid-download.
        hasActiveJobs() {
            const items = this.parent?.downloadLog?.items || [];
            return items.some(d =>
                d.status === 'Queued' ||
                d.status === 'Downloading' ||
                d.status === 'RetrievingInfo' ||
                d.status === 'Installing');
        },

        canInstall() {
            return !!this.status?.updateAvailable && !this.installing && !this.hasActiveJobs();
        },

        buttonTooltip() {
            if (this.installing) return 'Update in progress…';
            if (this.hasActiveJobs()) return 'Disabled during active installs';
            return '';
        },

        async install() {
            if (!this.canInstall()) return;
            this.installing = true;
            try {
                // Fire-and-forget: server responds 202 then exits shortly after; the bat takes over.
                await fetch('/api/app-update/install', { method: 'POST' });
            } catch (_) {
                // Connection drop is expected as the server shuts down.
            }
        }
    };
}
