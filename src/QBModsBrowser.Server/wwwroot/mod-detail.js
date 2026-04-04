// Provides Alpine state and actions for the mod details page.
function modDetail() {
    return {
        mod: null,
        manager: null,
        loading: true,
        error: null,
        defaultSpoilersOpen: false,
        openLinksInNewTab: true,
        hasSpoilers: false,
        favoriteIds: [],
        downloadStatus: null,
        topicDownloadStatuses: [],
        _dlPollTimer: null,
        _dlRefreshedId: null,
        assumedDownloads: [],
        assumedDownloadsLoading: false,
        _resolvingUrls: {},
        _optimisticCandidateStatus: {},
        // Prev/next mod IDs resolved from nav context stored by the list page.
        prevModId: null,
        nextModId: null,
        // Full nav context parsed from localStorage; used for cross-page A/D navigation.
        _navCtx: null,
        _fetchingAdjacentMod: false,

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

        // Toggles favorite state for the current topic.
        toggleFavorite(topicId) {
            if (topicId == null || !window.QBFavorites) return;
            QBFavorites.toggle(topicId);
            this.favoriteIds = QBFavorites.load();
        },

        // Checks whether the current topic is favorited.
        isFavorite(topicId) {
            if (topicId == null) return false;
            return this.favoriteIds.includes(topicId);
        },

        // Formats created date shown under the mod title (date only, no time).
        formatCreatedDate(s) {
            if (!s) return '';
            const d = new Date(s);
            if (isNaN(d.getTime())) return s;
            return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
        },

        // Formats last edit date with author suffix in 24h, no seconds.
        formatLastEditDate(s) {
            if (!s) return '';
            const parts = String(s).split(/\s+by\s+/i);
            const d = new Date(parts[0].trim());
            if (isNaN(d.getTime())) return s;
            const formatted = d.toLocaleString(undefined, {
                month: 'short', day: 'numeric', year: 'numeric',
                hour: '2-digit', minute: '2-digit', hour12: false
            });
            return parts.length > 1
                ? `${formatted} by ${parts.slice(1).join(' by ').trim()}`
                : formatted;
        },

        // Removes forum title prefixes for cleaner names.
        titleMain(title) {
            if (typeof title !== 'string') return '';
            return title.replace(/^\s*(?:Re:\s*)?(?:\[[^\]]+\]\s*)?/i, '').trimStart();
        },

        // Chooses best display name from repo, local, or forum title.
        displayModName() {
            const m = this.manager;
            const t = this.mod?.title;
            if (m?.modRepoName) return m.modRepoName;
            if (m?.localModName) return m.localModName;
            return this.titleMain(t) || t || '';
        },

        // Loads detail data, config flags, and post enhancements on mount.
        async init() {
            this.favoriteIds = window.QBFavorites ? QBFavorites.load() : [];
            const params = new URLSearchParams(window.location.search);
            const id = params.get('id');
            if (!id) {
                this.error = 'No mod ID specified';
                this.loading = false;
                return;
            }

            try {
                await window.QBPageLoad.scanLocalModsOnPageLoad();
                // Load spoiler default from config
                try {
                    const cfgRes = await fetch('/api/scraper/config');
                    if (cfgRes.ok) {
                        const cfg = await cfgRes.json();
                        this.defaultSpoilersOpen = cfg.defaultSpoilersOpen ?? false;
                        this.openLinksInNewTab = (cfg.openLinksInNewTab ?? cfg.openModLinksInNewTab) !== false;
                    }
                } catch (_) {}

                const res = await fetch('/api/mods/' + id);
                if (!res.ok) {
                    this.error = 'Mod not found (HTTP ' + res.status + ')';
                    this.loading = false;
                    return;
                }
                const data = await res.json();
                this.mod = data.detail || data;
                this.manager = data.manager || null;
                this.assumedDownloads = data.assumedDownloads || [];
                document.title = (this.displayModName() || 'Mod') + ' - QB Mod Browser and Mod Manager';

                // Resolve prev/next mod and store full context for cross-page navigation.
                try {
                    const raw = localStorage.getItem('qb.modNavContext');
                    if (raw) {
                        const ctx = JSON.parse(raw);
                        this._navCtx = ctx;
                        const ids = ctx.ids || [];
                        const pos = ids.indexOf(this.mod.topicId);
                        if (pos !== -1) {
                            this.prevModId = pos > 0 ? ids[pos - 1] : null;
                            this.nextModId = pos < ids.length - 1 ? ids[pos + 1] : null;
                        }
                    }
                } catch (_) {}

                this.$nextTick(() => {
                    this.initSpoilers();
                    this.applyPostContentLinkTargets();
                });

                // Resolve assumed downloads if not already in cache from first GET.
                if (!this.assumedDownloads.length)
                    this.fetchAssumedDownloads(this.mod?.topicId || parseInt(id, 10));
            } catch (e) {
                this.error = e.message;
            } finally {
                this.loading = false;
            }
        },

        // Refreshes assumed-download candidates without blocking initial page render.
        async fetchAssumedDownloads(topicId) {
            if (!topicId || this.assumedDownloadsLoading) return;
            this.assumedDownloadsLoading = true;
            try {
                const res = await fetch('/api/mods/' + topicId + '/assumed-downloads');
                if (!res.ok) return;
                const data = await res.json();
                this.assumedDownloads = data.assumedDownloads || [];
                if (data.manager) {
                    this.manager = data.manager;
                }
            } catch (_) {
            } finally {
                this.assumedDownloadsLoading = false;
            }
        },

        // Applies target and rel attributes inside rendered post content.
        applyPostContentLinkTargets() {
            const container = this.$refs.postContent;
            if (!container) return;
            container.querySelectorAll('a[href]').forEach(a => {
                const href = (a.getAttribute('href') || '').trim();
                if (!href || href.startsWith('#') || href.toLowerCase().startsWith('javascript:'))
                    return;
                if (this.openLinksInNewTab) {
                    a.setAttribute('target', '_blank');
                    a.setAttribute('rel', 'noopener');
                } else {
                    a.removeAttribute('target');
                    const rel = (a.getAttribute('rel') || '').trim().toLowerCase();
                    if (rel === 'noopener' || rel === 'noreferrer noopener' || rel === 'noopener noreferrer')
                        a.removeAttribute('rel');
                }
            });
        },

        // Wires spoiler toggles and collapse bars in post content.
        initSpoilers() {
            const container = this.$refs.postContent;
            if (!container) return;

            const wraps = container.querySelectorAll('.sp-wrap');
            this.hasSpoilers = wraps.length > 0;

            wraps.forEach(wrap => {
                const head = wrap.querySelector('.sp-head');
                const body = wrap.querySelector('.sp-body');
                if (!head) return;

                if (this.defaultSpoilersOpen) {
                    wrap.classList.add('open');
                } else {
                    wrap.classList.remove('open');
                }

                head.addEventListener('click', () => {
                    wrap.classList.toggle('open');
                });

                if (body && !body.querySelector('.sp-collapse-bar')) {
                    const bar = document.createElement('div');
                    bar.className = 'sp-collapse-bar relative group';
                    // Custom tooltip span replacing native title tooltip
                    const tip = document.createElement('span');
                    tip.className = 'pointer-events-none absolute bottom-full left-1/2 z-50 mb-1 -translate-x-1/2 whitespace-nowrap rounded bg-gray-900 px-2 py-0.5 text-[10px] font-medium text-white opacity-0 shadow-lg transition-opacity duration-0 group-hover:opacity-100';
                    tip.textContent = 'Click to collapse';
                    bar.appendChild(tip);
                    bar.addEventListener('click', () => wrap.classList.remove('open'));

                    const content = document.createElement('div');
                    content.className = 'sp-body-content';
                    while (body.firstChild) content.appendChild(body.firstChild);
                    body.appendChild(bar);
                    body.appendChild(content);
                }
            });
        },

        // Expands every spoiler block from the sidebar action.
        expandAllSpoilers() {
            const container = this.$refs.postContent;
            if (!container) return;
            container.querySelectorAll('.sp-wrap').forEach(w => w.classList.add('open'));
        },

        // Collapses every spoiler block from the sidebar action.
        collapseAllSpoilers() {
            const container = this.$refs.postContent;
            if (!container) return;
            container.querySelectorAll('.sp-wrap').forEach(w => w.classList.remove('open'));
        },

        // Formats byte counts for download progress labels.
        formatBytes(bytes) {
            if (bytes < 1024) return bytes + ' B';
            if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
            return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
        },

        // Returns assumed links that can be auto-downloaded.
        assumedNonManualCandidates() {
            return (this.assumedDownloads || []).filter(a => !a.requiresManualStep);
        },

        // Returns assumed links that still need manual steps.
        assumedManualCandidates() {
            return (this.assumedDownloads || []).filter(a => a.requiresManualStep);
        },

        // Chooses caution color class for one assumed candidate link.
        assumedDifficultyClass(candidate) {
            const cands = this.assumedNonManualCandidates();
            if (cands.length > 1) return 'bg-orange-500/20 text-orange-300 border border-orange-400/40 hover:bg-orange-500/30';
            const confidence = String(candidate?.confidence || cands[0]?.confidence || '').toLowerCase();
            return confidence === 'high'
                ? 'bg-lime-500/20 text-lime-300 border border-lime-400/40 hover:bg-lime-500/30'
                : 'bg-yellow-500/20 text-yellow-300 border border-yellow-400/40 hover:bg-yellow-500/30';
        },

        // Builds a flat list of installed primary and extra local mods.
        getInstalledMods() {
            const out = [];
            if (this.manager?.isInstalled && this.manager?.localModId) {
                out.push({
                    modId: this.manager.localModId,
                    name: this.manager.localModName || this.manager.localModId,
                    isEnabled: !!this.manager.isEnabled,
                    isPrimary: true
                });
            }
            for (const extra of (this.manager?.additionalLocalMods || []).filter(Boolean)) {
                if (!extra?.modId) continue;
                out.push({
                    modId: extra.modId,
                    name: extra.name || extra.modId,
                    isEnabled: !!extra.isEnabled,
                    isPrimary: false
                });
            }
            return out;
        },

        // Compares candidate and download rows by normalized URL.
        sameDownloadCandidate(ad, dl) {
            if (!ad || !dl) return false;
            const norm = u => {
                if (!u) return '';
                try {
                    const url = new URL(u, window.location.origin);
                    return (url.origin + url.pathname).replace(/\/+$/, '').toLowerCase();
                } catch (_) {
                    return String(u).trim().replace(/\/+$/, '').toLowerCase();
                }
            };
            const dlNorm = norm(dl.url);
            return dlNorm === norm(ad.resolvedDirectUrl) || dlNorm === norm(ad.originalUrl);
        },

        // Returns active download row for primary direct or update link.
        activeDirectDownload() {
            const target = this.manager?.updateDownloadUrl || this.manager?.directDownloadUrl;
            if (!target) return null;
            const pseudo = { originalUrl: target, resolvedDirectUrl: target };
            return (this.topicDownloadStatuses || []).find(dl =>
                ['Queued', 'RetrievingInfo', 'Downloading', 'Installing'].includes(dl.status)
                && this.sameDownloadCandidate(pseudo, dl));
        },

        // Returns active or optimistic status for one candidate.
        activeDownloadForCandidate(ad) {
            const active = (this.topicDownloadStatuses || []).find(dl =>
                ['Queued', 'RetrievingInfo', 'Downloading', 'Installing'].includes(dl.status)
                && this.sameDownloadCandidate(ad, dl));
            if (active) return active;
            const key = ad?.originalUrl;
            if (key && this._optimisticCandidateStatus[key]) {
                return { status: 'RetrievingInfo', totalBytes: 0, downloadedBytes: 0, progressPercent: 0 };
            }
            return null;
        },

        // Checks whether any topic download is currently active.
        hasAnyActiveTopicDownload() {
            return (this.topicDownloadStatuses || []).some(dl =>
                ['Queued', 'RetrievingInfo', 'Downloading', 'Installing'].includes(dl.status));
        },

        // Indicates whether a candidate URL is being resolved now.
        isResolvingCandidate(url) {
            return !!(url && this._resolvingUrls[url]);
        },

        // Normalizes URLs for cross-source matching.
        normalizeUrl(u) {
            if (!u) return '';
            try {
                const url = new URL(u, window.location.origin);
                return (url.origin + url.pathname).replace(/\/+$/, '').toLowerCase();
            } catch (_) {
                return String(u).trim().replace(/\/+$/, '').toLowerCase();
            }
        },


        // Normalizes archive filenames for candidate matching.
        normalizeArchiveName(name) {
            return String(name || '')
                .toLowerCase()
                .replace(/\.[a-z0-9]{2,6}$/, '')
                .replace(/[_\-.]+/g, '')
                .trim();
        },

        // Finds archive metadata that corresponds to a candidate.
        findArchiveEntryForCandidate(ad) {
            const entries = this.manager?.topicArchiveEntries || [];
            if (!ad || entries.length === 0) return null;

            const adUrls = [ad.resolvedDirectUrl, ad.originalUrl].map(u => this.normalizeUrl(u)).filter(Boolean);
            const adFile = this.normalizeArchiveName(ad.fileName);

            for (const e of entries) {
                const entryUrl = this.normalizeUrl(e.downloadUrl);
                if (entryUrl && adUrls.includes(entryUrl)) return e;
            }

            if (adFile) {
                for (const e of entries) {
                    const entryFile = this.normalizeArchiveName(e.archiveName);
                    if (entryFile && entryFile === adFile) return e;
                }
            }

            return null;
        },

        // Resolves installed mods tied to a candidate archive entry.
        candidateInstalledMods(ad) {
            const entry = this.findArchiveEntryForCandidate(ad);
            if (!entry?.modIds?.length) return [];
            const installed = this.getInstalledMods();
            return entry.modIds
                .map(modId => installed.find(x => x.modId === modId))
                .filter(Boolean);
        },

        // Checks whether the primary installed mod is already shown via a candidate archive entry.
        isPrimaryModCoveredByCandidate() {
            if (!this.manager?.isInstalled || !this.manager?.localModId) return false;
            for (const ad of this.assumedNonManualCandidates()) {
                if (this.candidateInstalledMods(ad).some(m => m.modId === this.manager.localModId))
                    return true;
            }
            return false;
        },

        // Returns additional local mods not already displayed inside a candidate archive slot.
        getAdditionalModsNotCoveredByCandidates() {
            const all = (this.manager?.additionalLocalMods || []).filter(Boolean);
            if (this.assumedNonManualCandidates().length === 0) return all;
            const coveredIds = new Set(
                this.assumedNonManualCandidates().flatMap(ad =>
                    this.candidateInstalledMods(ad).map(m => m.modId)
                )
            );
            return all.filter(extra => extra?.modId && !coveredIds.has(extra.modId));
        },

        // Toggles enabled state for a candidate-matched installed mod.
        async toggleCandidateEnabled(modEntry) {
            const m = modEntry;
            if (!m?.modId) return;
            if (m.isPrimary) return this.toggleEnabled();
            return this.toggleExtraEnabled({ modId: m.modId, isEnabled: m.isEnabled, name: m.name });
        },

        // Deletes a candidate-matched installed mod from disk.
        async deleteCandidate(modEntry) {
            const m = modEntry;
            if (!m?.modId) return;
            if (m.isPrimary) return this.deleteMod();
            return this.deleteExtraMod({ modId: m.modId, name: m.name });
        },

        // Starts direct or update download for this topic.
        async downloadMod(url) {
            if (!this.manager) return;
            const dlUrl = url || this.manager.updateDownloadUrl || this.manager.directDownloadUrl;
            if (!dlUrl) return;
            try {
                await fetch('/api/manager/download', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        url: dlUrl,
                        modName: this.displayModName(),
                        topicId: this.mod.topicId,
                        gameVersion: this.mod.gameVersion,
                        modVersion: this.manager.onlineVersion || this.manager.modRepoVersion
                    })
                });
                this.startDlPolling();
            } catch (e) {
                alert('Failed to start download: ' + e.message);
            }
        },

        // Starts polling topic download status updates.
        startDlPolling() {
            if (this._dlPollTimer) return;
            this._dlRefreshedId = null;
            this.pollDlStatus();
            this._dlPollTimer = setInterval(() => this.pollDlStatus(), 1000);
        },

        // Stops topic download status polling timer.
        stopDlPolling() {
            if (!this._dlPollTimer) return;
            clearInterval(this._dlPollTimer);
            this._dlPollTimer = null;
        },

        // Polls manager downloads and reconciles detail page state.
        async pollDlStatus() {
            try {
                const res = await fetch('/api/manager/downloads');
                const items = await res.json();
                const prevActiveCount = (this.topicDownloadStatuses || []).filter(d =>
                    ['Queued', 'RetrievingInfo', 'Downloading', 'Installing'].includes(d.status)).length;
                const topicItems = items.filter(d => d.topicId === this.mod?.topicId);
                this.topicDownloadStatuses = topicItems;
                // Drop optimistic statuses once backend confirms active status for candidate.
                for (const ad of this.assumedNonManualCandidates()) {
                    if (topicItems.some(dl => this.sameDownloadCandidate(ad, dl))) {
                        delete this._optimisticCandidateStatus[ad.originalUrl];
                    }
                }
                const dl = topicItems[0] || null;
                this.downloadStatus = dl;
                if (dl && dl.status === 'Completed' && this._dlRefreshedId !== dl.id) {
                    this._dlRefreshedId = dl.id;
                    await this.refreshManager();
                    setTimeout(() => this.stopDlPolling(), 3000);
                } else if (prevActiveCount > 0 && !this.hasAnyActiveTopicDownload()) {
                    // If an active item vanishes between polls, reconcile UI state immediately.
                    await this.refreshManager();
                    setTimeout(() => this.stopDlPolling(), 1500);
                } else if (!dl || dl.status === 'Failed' || dl.status === 'Canceled') {
                    setTimeout(() => this.stopDlPolling(), 3000);
                }
            } catch (_) {}
        },

        // Refreshes manager data after download and install transitions.
        async refreshManager() {
            try {
                const res = await fetch('/api/mods/' + this.mod.topicId);
                if (res.ok) {
                    const data = await res.json();
                    this.manager = data.manager || null;
                    this.assumedDownloads = data.assumedDownloads || [];
                }
            } catch (_) {}
        },

        // Resolves and starts an assumed-download candidate.
        async downloadAssumed(candidate) {
            if (!candidate?.originalUrl) return;
            this._resolvingUrls[candidate.originalUrl] = true;
            this._optimisticCandidateStatus[candidate.originalUrl] = Date.now();
            try {
                let dlUrl = candidate.resolvedDirectUrl || candidate.originalUrl;
                if (!candidate.resolvedDirectUrl) {
                    const res = await fetch('/api/manager/resolve-assumed-download', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ url: candidate.originalUrl, topicId: this.mod?.topicId })
                    });
                    if (res.ok) {
                        const data = await res.json();
                        dlUrl = data.resolvedUrl || dlUrl;
                    }
                }
                await this.downloadMod(dlUrl);
            } catch (e) {
                alert('Failed: ' + e.message);
                delete this._optimisticCandidateStatus[candidate.originalUrl];
            } finally {
                delete this._resolvingUrls[candidate.originalUrl];
            }
        },

        // Toggles enabled state for the primary installed local mod.
        async toggleEnabled() {
            if (!this.manager?.localModId) return;
            const action = this.manager.isEnabled ? 'disable' : 'enable';
            try {
                const res = await fetch(`/api/manager/${action}/${encodeURIComponent(this.manager.localModId)}`, { method: 'POST' });
                if (res.ok) {
                    this.manager.isEnabled = !this.manager.isEnabled;
                    this.syncProfileToggle(this.manager.localModId, this.manager.isEnabled);
                } else {
                    const data = await res.json();
                    alert(data.error || 'Failed');
                }
            } catch (e) {
                alert('Error: ' + e.message);
            }
        },

        // Deletes the primary installed local mod after confirmation.
        async deleteMod() {
            if (!this.manager?.localModId) return;
            if (!confirm(`Delete mod "${this.manager.localModId}" from disk? This cannot be undone.`)) return;
            try {
                const res = await fetch(`/api/manager/mods/${encodeURIComponent(this.manager.localModId)}`, { method: 'DELETE' });
                if (res.ok) {
                    this.manager.isInstalled = false;
                    this.manager.isEnabled = false;
                    this.manager.localModId = null;
                    await this.refreshManager();
                } else {
                    const data = await res.json();
                    alert(data.error || 'Failed to delete');
                }
            } catch (e) {
                alert('Error: ' + e.message);
            }
        },

        // Toggles enabled state for an additional installed local mod.
        async toggleExtraEnabled(extra) {
            if (!extra.modId) return;
            const action = extra.isEnabled ? 'disable' : 'enable';
            try {
                const res = await fetch(`/api/manager/${action}/${encodeURIComponent(extra.modId)}`, { method: 'POST' });
                if (res.ok) {
                    extra.isEnabled = !extra.isEnabled;
                    this.syncProfileToggle(extra.modId, extra.isEnabled);
                } else {
                    const data = await res.json();
                    alert(data.error || 'Failed');
                }
            } catch (e) {
                alert('Error: ' + e.message);
            }
        },

        // Syncs a single mod enable/disable to the equipped profile on the server.
        async syncProfileToggle(modId, isEnabled) {
            try {
                await fetch('/api/profiles/sync-toggle', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ modId, isEnabled })
                });
            } catch (_) {}
        },

        // Fetches the adjacent list page and navigates to its first mod, updating the nav context for further A/D travel.
        // Also syncs the list page's saved filters so Q (back) lands on the correct page.
        async navigateToAdjacentPage(direction) {
            if (!this._navCtx || this._fetchingAdjacentMod) return;
            const { page, totalPages, queryParams } = this._navCtx;
            const targetPage = direction === 'prev' ? (page - 1) : (page + 1);
            if (targetPage < 1 || targetPage > totalPages) return;
            this._fetchingAdjacentMod = true;
            try {
                const p = new URLSearchParams(queryParams);
                p.set('page', String(targetPage));
                const res = await fetch('/api/mods?' + p.toString());
                if (!res.ok) return;
                const data = await res.json();
                const mods = data.mods || [];
                if (!mods.length) return;
                const target = mods[0];
                try {
                    const ids = mods.map(m => m.topicId);
                    localStorage.setItem('qb.modNavContext', JSON.stringify({
                        ids,
                        page: targetPage,
                        totalPages: data.totalPages || totalPages,
                        queryParams: p.toString(),
                        ts: Date.now()
                    }));
                    // Sync the list page's saved filters to the new page so a direct '/' navigation lands correctly.
                    const filtersRaw = localStorage.getItem('qbmodsbrowser.listFilters');
                    const filters = filtersRaw ? JSON.parse(filtersRaw) : {};
                    filters.page = targetPage;
                    localStorage.setItem('qbmodsbrowser.listFilters', JSON.stringify(filters));
                    // Flag that a cross-page hop occurred so Q bypasses bfcache and loads fresh.
                    localStorage.setItem('qb.modNavCrossedPage', '1');
                } catch (_) {}
                window.location.replace('mod.html?id=' + target.topicId);
            } catch (_) {
            } finally {
                this._fetchingAdjacentMod = false;
            }
        },

        // Handles detail-page keyboard shortcuts: S to go back, A/D for prev/next mod (crossing pages when at the boundary).
        handleDetailKeydown(e) {
            const tag = e.target?.tagName?.toUpperCase();
            if (tag === 'INPUT' || tag === 'TEXTAREA' || e.target?.isContentEditable) return;
            if (e.key === 's' || e.key === 'S') {
                e.preventDefault();
                // Tag the current mod so the list page can flash-highlight its card on return.
                if (this.mod?.topicId != null)
                    sessionStorage.setItem('qb.returnFromModId', String(this.mod.topicId));
                // If a cross-page hop occurred, bfcache would restore the wrong page;
                // navigate directly to '/' so the list reloads with the updated saved filters.
                const crossed = localStorage.getItem('qb.modNavCrossedPage');
                if (crossed) {
                    localStorage.removeItem('qb.modNavCrossedPage');
                    window.location.href = '/';
                } else {
                    history.length > 1 ? history.back() : (window.location.href = '/');
                }
            } else if (e.key === 'a' || e.key === 'A') {
                e.preventDefault();
                if (this.prevModId) window.location.replace('mod.html?id=' + this.prevModId);
                else this.navigateToAdjacentPage('prev');
            } else if (e.key === 'd' || e.key === 'D') {
                e.preventDefault();
                if (this.nextModId) window.location.replace('mod.html?id=' + this.nextModId);
                else this.navigateToAdjacentPage('next');
            }
        },

        // Deletes an additional installed local mod after confirmation.
        async deleteExtraMod(extra) {
            if (!extra.modId) return;
            if (!confirm(`Delete "${extra.name || extra.modId}" from disk? This cannot be undone.`)) return;
            try {
                const res = await fetch(`/api/manager/mods/${encodeURIComponent(extra.modId)}`, { method: 'DELETE' });
                if (res.ok) {
                    await this.refreshManager();
                } else {
                    const data = await res.json();
                    alert(data.error || 'Failed to delete');
                }
            } catch (e) {
                alert('Error: ' + e.message);
            }
        },

    };
}
