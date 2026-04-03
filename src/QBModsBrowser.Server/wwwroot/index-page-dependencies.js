// Provides dependency-overlay methods merged into modsApp().
// State (depPriorityTopicIds, unmatchedDeps, _depRequestersByTopicId) lives in index-page.js.
// Server handles dep-priority ordering inside /api/mods; this layer only tags cards and injects ghosts.
function buildDependencyMethods() {
    return {
        // Tags dep-priority mods in this.mods and prepends ghost cards for unmatched deps.
        // Called after every fetchMods() — server already sorted dep mods to the correct tier.
        _applyDepSort() {
            const realMods = this.mods.filter(m => !m._isDepGhost);

            if (!this.depPriorityTopicIds || this.depPriorityTopicIds.length === 0) {
                // Clear any stale dep metadata and restore plain list.
                for (const m of realMods) { delete m._isDepPriority; delete m._depRequesters; }
                this.mods = realMods;
                return;
            }

            const depSet = new Set(this.depPriorityTopicIds);

            // Tag dep-priority mods so cards can show "Required by:" and the yellow border.
            for (const mod of realMods) {
                if (depSet.has(mod.topicId) && !mod.isInstalled) {
                    mod._isDepPriority = true;
                    mod._depRequesters = this._depRequestersByTopicId?.[mod.topicId] || [];
                } else {
                    delete mod._isDepPriority;
                    delete mod._depRequesters;
                }
            }

            // Prepend ghost cards only on page 1 (server omits unmatched list on later pages).
            const ghostSource = this.page === 1 ? (this.unmatchedDeps || []) : [];
            const ghostMods = ghostSource.map((dep, idx) => ({
                topicId: -(idx + 1),
                _isDepGhost: true,
                title: dep.name || dep.id || 'Unknown Dependency',
                _depRequesters: dep.requesters || [],
            }));

            this.mods = [...ghostMods, ...realMods];
        },

        // Returns display text for the "required by" line shown on dep-priority and ghost cards.
        depRequiredByText(requesters) {
            if (!requesters || requesters.length === 0) return '';
            const names = requesters
                .map(r => r.name || r.modId)
                .filter(Boolean)
                .slice(0, 3);
            const suffix = requesters.length > 3 ? ` +${requesters.length - 3} more` : '';
            return 'Required by: ' + names.join(', ') + suffix;
        },

        // Returns the display name for a ghost card.
        unmatchedDepName(dep) {
            return dep.name || dep.id || 'Unknown Dependency';
        },

        // Shows the ghost card hover tooltip using the shared tip system.
        showDepGhostTip(e, mod) {
            const requesterNames = (mod._depRequesters || [])
                .map(r => r.name || r.modId)
                .filter(Boolean)
                .slice(0, 2)
                .join(', ');
            const line2 = requesterNames ? 'Required by: ' + requesterNames : 'Find and install it manually.';
            this.showHoverTip(e, 'Dependency not found in mod index.', line2);
        },
    };
}
