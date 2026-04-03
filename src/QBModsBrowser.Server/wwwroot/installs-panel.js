// Builds methods dedicated to installs panel rendering and extract workflows.
function buildInstallsPanelMethods() {
    return {
        // Detects synthetic extract rows that only report "already installed" outcomes.
        isAlreadyInstalledExtractLog(dl) {
            if (!dl) return false;
            return dl.status === 'Canceled'
                && typeof dl.error === 'string'
                && dl.error.toLowerCase().startsWith('already installed');
        },

        // Filters the installs panel to hide "already installed" logs unless explicitly revealed for this run.
        visibleDownloadLogItems() {
            const items = this.downloadLog?.items || [];
            if (!items.length) return [];
            const visibleAlready = new Set((this.extractVisibleAlreadyArchiveNames || []).map(x => String(x || '').toLowerCase()));
            return items.filter(dl => {
                if (!this.isAlreadyInstalledExtractLog(dl)) return true;
                if (!this.extractShowAlreadyInstalledLogs) return false;
                const fileName = String(dl.fileName || '').toLowerCase();
                return visibleAlready.has(fileName);
            });
        },

        // Classifies install-log source so UI can show a stable icon for download, update, or local extract.
        logSourceType(dl) {
            if (!dl) return 'download';
            if (!dl.url) return 'extract';
            if (dl.previousModVersion || dl.previousGameVersion) return 'update';
            return 'download';
        },

        // Returns the icon name used beside each install-log mod label.
        logSourceIcon(dl) {
            const src = this.logSourceType(dl);
            if (src === 'extract') return 'heroicons:archive-box-arrow-down';
            if (src === 'update') return 'heroicons:arrow-path';
            return 'heroicons:arrow-down-tray';
        },

        // Returns a neutral icon tint class so log source glyphs remain visually consistent.
        logSourceIconClass(dl) {
            return 'text-gray-400';
        },

        // Provides short tooltip text for the install-log source icon.
        logSourceLabel(dl) {
            const src = this.logSourceType(dl);
            if (src === 'extract') return 'Local extraction';
            if (src === 'update') return 'Update download';
            return 'Fresh download';
        },

        // Resolves a comparable timestamp for one log row using completion time first, then start time.
        logTimestampMs(dl) {
            if (!dl) return null;
            const raw = dl.completedAt || dl.startedAt;
            if (!raw) return null;
            const ms = new Date(raw).getTime();
            return Number.isNaN(ms) ? null : ms;
        },

        // Decides whether to show a time-gap divider below a visible log item.
        shouldShowLogGapDivider(index) {
            const items = this.visibleDownloadLogItems();
            if (!Array.isArray(items) || index < 0 || index >= items.length - 1) return false;
            const currentMs = this.logTimestampMs(items[index]);
            const nextMs = this.logTimestampMs(items[index + 1]);
            if (currentMs == null || nextMs == null) return false;
            return (currentMs - nextMs) > 60000;
        },

        // Formats the divider caption from the older row timestamp without seconds.
        logGapDividerLabel(index) {
            const items = this.visibleDownloadLogItems();
            if (!Array.isArray(items) || index < 0 || index >= items.length - 1) return '';
            const nextMs = this.logTimestampMs(items[index + 1]);
            if (nextMs == null) return '';
            return new Date(nextMs).toLocaleString(undefined, {
                year: 'numeric',
                month: 'short',
                day: 'numeric',
                hour: '2-digit',
                minute: '2-digit',
                hour12: false
            });
        },

        // Extracts archives already in mods folder using backend install pipeline; starts polling immediately so live progress rows appear.
        async extractUnextractedArchives() {
            try {
                this.extractShowAlreadyInstalledLogs = false;
                this.extractVisibleAlreadyArchiveNames = [];
                if (this._extractFeedbackTimer) {
                    clearTimeout(this._extractFeedbackTimer);
                    this._extractFeedbackTimer = null;
                }
                this.isExtracting = true;
                // Start polling before the blocking POST so in-flight Installing items show progress immediately.
                if (this.downloadLog) this.downloadLog.startPolling();
                this.extractFeedbackTone = 'info';
                this.extractFeedbackMessage = 'Extracting archives from mods folder...';
                this.managerMessage = 'Extracting archives from mods folder...';
                const res = await fetch('/api/manager/extract-unextracted', { method: 'POST' });
                const data = await res.json();
                if (!res.ok) {
                    const err = data.error || 'Extraction failed';
                    this.managerMessage = err;
                    this.extractFeedbackTone = 'error';
                    this.extractFeedbackMessage = err;
                    return;
                }

                const summary = `Extracted ${data.extracted}, skipped ${data.skipped}, failed ${data.failed}`;
                this.managerMessage = summary;
                this.extractFeedbackTone = data.failed > 0 ? 'error' : 'success';
                this.extractFeedbackMessage = summary;
                // Show already-installed rows only when this extract click produced only skipped-already outcomes.
                const allRowsAlreadyInstalled = data.skipped > 0 && data.extracted === 0 && data.failed === 0;
                if (allRowsAlreadyInstalled) {
                    this.extractShowAlreadyInstalledLogs = true;
                    this.extractVisibleAlreadyArchiveNames = (data.skippedArchives || []).map(x => String(x || '').toLowerCase());
                }
                this.fetchMods();
                if (this.downloadLog) {
                    this.downloadLog.fetchItems();
                }
                setTimeout(() => { this.managerMessage = ''; }, 5000);
                this._extractFeedbackTimer = setTimeout(() => {
                    this.extractFeedbackMessage = '';
                    this.extractFeedbackTone = '';
                    this._extractFeedbackTimer = null;
                }, 7000);
            } catch (e) {
                const err = 'Error: ' + e.message;
                this.managerMessage = err;
                this.extractFeedbackTone = 'error';
                this.extractFeedbackMessage = err;
            } finally {
                this.isExtracting = false;
            }
        }
    };
}
