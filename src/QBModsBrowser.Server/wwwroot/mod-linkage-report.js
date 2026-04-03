// Provides the Alpine store and popup injection for the mod linkage-report feature.
// Loaded as a plain (non-deferred) script so the alpine:init listener is registered
// before Alpine's deferred initialization runs.

// Begin fetching popup HTML early so it is likely cached when alpine:initialized fires.
const _dlkPopupHtmlPromise = fetch('/mod-linkage-report-popup.html').then(r => r.text());

// Registers the dlk store and injects the popup HTML into the DOM.
document.addEventListener('alpine:init', () => {
    // Global store shared between the main modDetail component and the popup component.
    Alpine.store('dlk', {
        open: false,
        data: null,
        loading: false,
        modName: '',
        topicId: null,

        // Opens the popup for a given topic; fetches data if not already loaded.
        async openForMod(topicId, modName) {
            this.topicId = topicId;
            this.modName = modName || '';
            this.open = true;
            this.setPageScrollLocked(true);
            if ((!this.data || this.data.topicId !== topicId) && !this.loading) {
                await this._fetchData(topicId);
            }
        },

        // Closes the popup.
        close() {
            this.open = false;
            this.setPageScrollLocked(false);
        },

        // Locks or unlocks page-level scrolling while the modal is open.
        setPageScrollLocked(locked) {
            const value = locked ? 'hidden' : '';
            document.documentElement.style.overflow = value;
            document.body.style.overflow = value;
        },

        // Fetches the data-linkage payload from the API.
        async _fetchData(topicId) {
            if (!topicId) return;
            this.loading = true;
            this.data = null;
            try {
                const res = await fetch('/api/mods/' + topicId + '/linkage-report');
                if (res.ok) this.data = await res.json();
            } catch (_) {
                // leave data null on error
            } finally {
                this.loading = false;
            }
        },

        // Returns true when the given modId was downloaded via QBMBAMM (topic-archive map match).
        isDownloadedViaQB(modId) {
            if (!this.data?.topicArchiveMap?.data) return false;
            return this.data.topicArchiveMap.data.some(
                ae => ae.modIds?.some(mid => mid.toLowerCase() === modId.toLowerCase())
            );
        },

        // Scrolls to a linkage card and briefly highlights it for visual focus.
        focusCard(cardId) {
            if (!cardId) return;
            const el = document.getElementById(cardId);
            if (!el) return;
            el.scrollIntoView({ behavior: 'smooth', block: 'start' });
            if (el.__dlkFlashTimer) {
                clearTimeout(el.__dlkFlashTimer);
                el.__dlkFlashTimer = null;
            }
            if (el.__dlkFlashAnim) {
                el.__dlkFlashAnim.cancel();
                el.__dlkFlashAnim = null;
            }
            // Start after scroll begins so the flash is seen on the destination card.
            el.__dlkFlashTimer = setTimeout(() => {
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
                el.__dlkFlashAnim = anim;
                anim.onfinish = () => {
                    if (el.__dlkFlashAnim === anim) el.__dlkFlashAnim = null;
                };
                anim.oncancel = () => {
                    if (el.__dlkFlashAnim === anim) el.__dlkFlashAnim = null;
                };
                el.__dlkFlashTimer = null;
            }, 140);
        },

        // Compares two versions using the shared frontend helper.
        compareVersion(a, b) {
            return window.QBVersionCompare
                ? window.QBVersionCompare.compareVersion(a, b)
                : 0;
        },

        // Finds the highest local mod/game version currently linked to this topic.
        getHighestLocalVersion(kind) {
            const items = this.data?.localMods?.data || [];
            const values = items
                .map(lm => (kind === 'game' ? lm?.gameVersion : lm?.version))
                .filter(v => String(v || '').trim().length > 0);
            if (!values.length) return '';
            return values.reduce((max, current) =>
                this.compareVersion(current, max) > 0 ? current : max
            );
        },

        // Collects remote versions that can be compared against local data.
        getRemoteVersions(kind) {
            const out = [];
            if (kind === 'game') {
                out.push(this.data?.forumScrape?.indexData?.gameVersion);
                out.push(this.data?.modRepo?.data?.gameVersionReq);
            } else {
                out.push(this.data?.modRepo?.data?.modVersion);
                for (const vc of (this.data?.versionChecker?.data || [])) {
                    out.push(vc?.remoteVersion);
                }
            }
            return out.filter(v => String(v || '').trim().length > 0);
        },

        // Returns yellow warning text when remote and local versions differ.
        remoteVersionWarningClass(remoteVersion, kind) {
            const localBest = this.getHighestLocalVersion(kind);
            if (!localBest || !remoteVersion) return '';
            return this.compareVersion(remoteVersion, localBest) !== 0
                ? 'text-yellow-300 font-semibold'
                : '';
        },

        // Returns yellow warning text when one local version differs from any remote source.
        localVersionWarningClass(localVersion, kind) {
            if (!localVersion) return '';
            const remotes = this.getRemoteVersions(kind);
            if (!remotes.length) return '';
            return remotes.some(rv => this.compareVersion(localVersion, rv) !== 0)
                ? 'text-yellow-300 font-semibold'
                : '';
        }
    });
});

// Injects the popup HTML after Alpine has finished initializing the page, then
// calls Alpine.initTree so the new element's directives are processed.
document.addEventListener('alpine:initialized', async () => {
    const portal = document.getElementById('dlk-portal');
    if (!portal) return;
    try {
        const html = await _dlkPopupHtmlPromise;
        const temp = document.createElement('div');
        temp.innerHTML = html.trim();
        const popupRoot = temp.firstElementChild;
        if (!popupRoot) return;
        portal.replaceWith(popupRoot);
        Alpine.initTree(popupRoot);
    } catch (_) {
        // non-fatal: popup just won't render
    }
});
