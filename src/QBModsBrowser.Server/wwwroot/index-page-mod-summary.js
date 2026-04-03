// Builds mod-summary card helpers and actions for the list page.
function buildModSummaryMethods() {
    return {
        // Strips forum title prefixes for cleaner display names.
        titleMain(title) {
            if (typeof title !== 'string') return '';
            return title.replace(/^\s*(?:Re:\s*)?(?:\[[^\]]+\]\s*)?/i, '').trimStart();
        },

        // Chooses best display name from repo, local, or forum title.
        displayModName(mod) {
            if (!mod) return '';
            return mod.modRepoName || mod.localModName || this.titleMain(mod.title);
        },

        // Limits visible extra local installs to keep rows compact.
        visibleAdditionalMods(mod) {
            const extras = mod?.additionalLocalMods || [];
            const maxExtras = mod?.isInstalled ? 2 : 3;
            return extras.slice(0, maxExtras);
        },

        // Returns count of hidden extra installs for overflow badge.
        additionalModsOverflowCount(mod) {
            const extras = mod?.additionalLocalMods || [];
            const maxExtras = mod?.isInstalled ? 2 : 3;
            return Math.max(0, extras.length - maxExtras);
        },

        // Returns primary local mod name used in multi-mod hover tooltip.
        hoverModName(mod) {
            const hasMultiple = (mod.additionalLocalMods || []).length > 0;
            if (!hasMultiple) return '';
            return mod.localModName || mod.localModId || this.displayModName(mod);
        },

        // Returns extra local mod name used in hover tooltip.
        hoverExtraModName(mod, extra) {
            const hasMultiple = (mod.additionalLocalMods || []).length > 0;
            if (!hasMultiple) return '';
            return extra.name || extra.modId || '';
        },

        // Shows the custom tooltip near the current pointer.
        showHoverTip(e, line1, line2) {
            this.hoverTip.show = true;
            this.hoverTip.line1 = line1 || '';
            this.hoverTip.line2 = line2 || '';
            this.moveHoverTip(e);
        },

        // Repositions the custom tooltip while pointer moves.
        moveHoverTip(e) {
            if (!this.hoverTip.show) return;
            const viewportWidth = window.innerWidth || 0;
            const minX = 180;
            const maxX = Math.max(minX, viewportWidth - 180);
            this.hoverTip.x = Math.min(Math.max(e.clientX, minX), maxX);
            this.hoverTip.y = Math.max(36, e.clientY - 10);
        },

        // Hides the custom hover tooltip on pointer exit.
        hideHoverTip() {
            this.hoverTip.show = false;
        },

        // Chooses the primary action icon from mod state.
        modActionIcon(mod) {
            if (mod.updateAvailable && mod.hasDirectDownload) return 'heroicons:arrow-up-circle';
            if (mod.updateAvailable) return 'heroicons:exclamation-circle';
            if (!mod.isInstalled && mod.hasDirectDownload) return 'heroicons:arrow-down-tray';
            if (!mod.isInstalled && mod.hasAssumedDownload) return 'heroicons:arrow-down-tray';
            if (mod.isInstalled) return 'heroicons:check-circle';
            return 'heroicons:minus-circle';
        },

        // Chooses primary action button styling from mod state.
        modActionBtnClass(mod) {
            if (mod.updateAvailable && mod.hasDirectDownload) return 'text-amber-400 hover:bg-amber-500/20 border border-amber-500/40';
            if (mod.updateAvailable) return 'text-amber-400/60 border border-gray-600';
            if (!mod.isInstalled && mod.hasDirectDownload) return 'text-blue-400 hover:bg-blue-500/20 border border-blue-500/40';
            if (!mod.isInstalled && mod.hasAssumedDownload) {
                if (mod.assumedDownloadCount > 1) return 'text-orange-400 hover:bg-orange-500/20 border border-orange-500/40';
                if (mod.assumedRequiresManualStep) return 'text-orange-400 hover:bg-orange-500/20 border border-orange-500/40';
                if (mod.isAssumedPatreonLink) return 'text-fuchsia-300 hover:bg-fuchsia-500/20 border border-fuchsia-500/50';
                if (mod.assumedConfidence === 'high') return 'text-lime-300 hover:bg-lime-500/20 border border-lime-500/40';
                return 'text-yellow-200 hover:bg-yellow-300/20 border border-yellow-300/35';
            }
            if (mod.isInstalled) return 'text-emerald-400/60 border border-gray-600 cursor-pointer';
            return 'text-gray-500 border border-gray-600 cursor-default opacity-50';
        },

        // Builds tooltip text for the primary action button.
        modActionTooltip(mod) {
            if (mod.updateAvailable && mod.hasDirectDownload) return 'Update available - click to download';
            if (mod.updateAvailable) return 'Update available - manual download required';
            if (!mod.isInstalled && mod.hasDirectDownload) return 'Download and install (direct link exists)';
            if (!mod.isInstalled && mod.hasAssumedDownload) {
                if (mod.assumedDownloadCount > 1) return 'Multiple or unclear download links, please choose';
                if (mod.assumedRequiresManualStep) return 'Download link found - visit mod page to download';
                if (mod.isAssumedPatreonLink) return 'Found Patreon link, please download manually';
                if (mod.assumedConfidence === 'high') return 'Download and install (link found on forum)';
                return 'Download and install (probable link found on forum)';
            }
            if (mod.isInstalled) return 'Installed';
            return 'No download link found - please install manually';
        },

        // Keeps template binding stable for primary action tooltip text.
        modPrimaryActionTooltip(mod) {
            return this.modActionTooltip(mod);
        },

        // Keeps template binding stable for primary action button classes.
        modPrimaryActionBtnClass(mod) {
            return this.modActionBtnClass(mod);
        },

        // Routes primary click to direct download, assumed flow, or details.
        async handleModAction(mod) {
            const url = mod.updateDownloadUrl || mod.directDownloadUrl;
            if (mod.isInstalled && !mod.updateAvailable) return;
            if (url && mod.hasDirectDownload) return this.startDownload(url, mod);
            if (mod.hasAssumedDownload) {
                const assumedCount = Number(mod.assumedDownloadCount || 0);
                if (assumedCount === 1 && !mod.assumedRequiresManualStep) return this.startAssumedDownloadFromSummary(mod);
                this.openModDetails(mod);
            }
        },

        // Attempts one-click assumed download from the list card.
        async startAssumedDownloadFromSummary(mod) {
            const dlUrl = await this.resolveSingleAssumedDownloadUrl(mod);
            if (dlUrl) return this.startDownload(dlUrl, mod);
            this.openModDetails(mod);
        },

        // Opens the mod details page in a new tab, saving the current page's mod order for prev/next navigation.
        openModDetails(mod) {
            try {
                const ids = (this.mods || []).map(m => m.topicId);
                const existing = JSON.parse(localStorage.getItem('qb.modNavContext') || '{}');
                localStorage.setItem('qb.modNavContext', JSON.stringify({
                    ...existing,
                    ids,
                    ts: Date.now()
                }));
            } catch (_) {}
            window.open('mod.html?id=' + mod.topicId, '_blank');
        },

        // Resolves a single assumed candidate to a direct URL using fresh assumed-download data.
        async resolveSingleAssumedDownloadUrl(mod) {
            try {
                const detailRes = await fetch('/api/mods/' + mod.topicId + '/assumed-downloads');
                if (!detailRes.ok) return null;
                const detail = await detailRes.json();
                const candidates = (detail.assumedDownloads || []).filter(a => !a.requiresManualStep);
                if (candidates.length !== 1) return null;
                const candidate = candidates[0];
                let dlUrl = candidate.resolvedDirectUrl || candidate.originalUrl;
                if (candidate.resolvedDirectUrl || !candidate.originalUrl) return dlUrl || null;
                try {
                    const resolveRes = await fetch('/api/manager/resolve-assumed-download', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ url: candidate.originalUrl, topicId: mod.topicId })
                    });
                    if (resolveRes.ok) {
                        const resolved = await resolveRes.json();
                        dlUrl = resolved.resolvedUrl || dlUrl;
                    }
                } catch (_) {}
                return dlUrl || null;
            } catch (_) {
                return null;
            }
        },

        // Deletes the primary installed local mod after confirmation.
        async deleteLocalMod(mod) {
            if (!mod.localModId) return;
            const name = this.displayModName(mod);
            if (!confirm(`Delete "${name}" from your mods folder? This cannot be undone.`)) return;
            try {
                const res = await fetch(`/api/manager/mods/${encodeURIComponent(mod.localModId)}`, { method: 'DELETE' });
                if (res.ok) {
                    mod.isInstalled = false;
                    mod.isEnabled = false;
                    mod.localModId = null;
                } else {
                    const data = await res.json();
                    console.error('Delete failed:', data.error);
                }
            } catch (e) {
                console.error('Delete failed:', e);
            }
        },

        // Toggles enabled state for the primary installed local mod.
        async toggleModEnabled(mod) {
            if (!mod.localModId) return;
            const action = mod.isEnabled ? 'disable' : 'enable';
            try {
                const res = await fetch(`/api/manager/${action}/${encodeURIComponent(mod.localModId)}`, { method: 'POST' });
                if (res.ok) {
                    mod.isEnabled = !mod.isEnabled;
                    this.onModToggled(mod.localModId, mod.isEnabled);
                } else {
                    const data = await res.json();
                    console.error('Toggle failed:', data.error);
                }
            } catch (e) {
                console.error('Toggle failed:', e);
            }
        },

        // Toggles enabled state for an additional installed local mod.
        async toggleExtraModEnabled(extra) {
            if (!extra.modId) return;
            const action = extra.isEnabled ? 'disable' : 'enable';
            try {
                const res = await fetch(`/api/manager/${action}/${encodeURIComponent(extra.modId)}`, { method: 'POST' });
                if (res.ok) {
                    extra.isEnabled = !extra.isEnabled;
                    this.onModToggled(extra.modId, extra.isEnabled);
                } else {
                    const data = await res.json();
                    console.error('Toggle failed:', data.error);
                }
            } catch (e) {
                console.error('Toggle failed:', e);
            }
        },

        // Deletes an additional installed local mod entry.
        async deleteExtraLocalMod(mod, extra) {
            if (!extra.modId) return;
            if (!confirm(`Delete "${extra.name || extra.modId}" from disk? This cannot be undone.`)) return;
            try {
                const res = await fetch(`/api/manager/mods/${encodeURIComponent(extra.modId)}`, { method: 'DELETE' });
                if (res.ok) {
                    this.refreshSingleMod(mod.topicId);
                } else {
                    const data = await res.json();
                    console.error('Delete failed:', data.error);
                }
            } catch (e) {
                console.error('Delete failed:', e);
            }
        },

        // Starts backend download and install workflow for the selected URL.
        async startDownload(url, mod) {
            try {
                const body = {
                    url,
                    modName: this.displayModName(mod),
                    topicId: mod.topicId,
                    gameVersion: mod.gameVersion,
                    previousGameVersion: (mod.localVersion && mod.localGameVersion) ? mod.localGameVersion : null,
                    modVersion: mod.onlineVersion || mod.modRepoVersion || null,
                    previousModVersion: mod.localVersion || null
                };
                await fetch('/api/manager/download', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(body)
                });
                this.downloadLog.startPolling();
                this.downloadLog.fetchItems();
            } catch (e) {
                console.error('Failed to start download:', e);
            }
        },

        // Returns active download row for a mod card, if any.
        modDlStatus(mod) {
            const dl = this.downloadLog._modDlMap[mod.topicId];
            if (!dl) return null;
            const s = dl.status;
            if (s === 'Queued' || s === 'RetrievingInfo' || s === 'Downloading' || s === 'Installing') return dl;
            return null;
        },

        // Formats compact tooltip text for current download progress.
        modDlTooltip(mod) {
            const dl = this.modDlStatus(mod);
            if (!dl) return '';
            if (dl.status === 'Queued') return 'Queued for download';
            if (dl.status === 'RetrievingInfo') return 'Retrieving file info…';
            if (dl.status === 'Downloading') {
                const bytes = this.formatBytes(dl.downloadedBytes);
                if (dl.totalBytes > 0) return 'Downloading ' + dl.progressPercent + '% (' + bytes + ' / ' + this.formatBytes(dl.totalBytes) + ')';
                return 'Downloading (' + bytes + ')';
            }
            if (dl.status === 'Installing') return 'Extracting & installing…';
            return '';
        },

        // Merges GET /api/mods/{id} manager fields into the visible card so the grid order stays stable after install/delete on this page.
        async refreshSingleMod(topicId) {
            const row = (this.mods || []).find(m => !m._isDepGhost && m.topicId === topicId);
            if (!row) return;
            try {
                const res = await fetch('/api/mods/' + topicId);
                if (!res.ok) return;
                const data = await res.json();
                if (data.manager && typeof data.manager === 'object')
                    Object.assign(row, data.manager);
            } catch (e) {
                console.error('Failed to refresh mod row:', e);
            }
        },

        // Formats byte counts used in download UI labels.
        formatBytes(bytes) {
            if (bytes < 1024) return bytes + ' B';
            if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
            return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
        }
    };
}
