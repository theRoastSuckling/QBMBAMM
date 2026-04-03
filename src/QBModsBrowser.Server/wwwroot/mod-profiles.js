// Builds Alpine methods and state for the mod profiles feature.
function buildModProfileMethods() {
    return {
        profileStore: { profiles: [], state: { selectedProfileId: null, isEquipped: false, cleanupCounts: {} } },
        profileLoading: false,
        profileRenaming: false,

        // Fetches profile store from the server and injects the HTML partial.
        async initProfiles() {
            await this.loadProfilesTemplate();
            await this.refreshProfileStore();
        },

        // Loads the mod-profiles.html partial into the header mount point.
        async loadProfilesTemplate() {
            const mount = document.getElementById('mod-profiles-mount');
            if (!mount) return;
            try {
                const res = await fetch('/mod-profiles.html', { cache: 'no-store' });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                mount.innerHTML = await res.text();
                if (window.Alpine?.initTree) window.Alpine.initTree(mount);
            } catch (e) {
                console.error('Failed to load profiles template:', e);
            }
        },

        // Fetches the full profile store from the API.
        async refreshProfileStore() {
            try {
                const res = await fetch('/api/profiles');
                if (res.ok) this.profileStore = await res.json();
            } catch (e) { console.error('Failed to load profiles:', e); }
        },

        // Returns the currently selected profile object or null.
        selectedProfile() {
            if (!this.profileStore.state.selectedProfileId) return null;
            return this.profileStore.profiles.find(p => p.id === this.profileStore.state.selectedProfileId) || null;
        },

        // Returns profiles that are not currently selected (for the inactive grid).
        inactiveProfiles() {
            return this.profileStore.profiles.filter(p => p.id !== this.profileStore.state.selectedProfileId);
        },

        // Enables all installed local mods from disk and syncs profile tracking when equipped.
        async enableAllInstalledMods() {
            await this.enableInstalledModsInternal({ favoritesOnly: false });
        },

        // Enables only installed mods whose topics are currently favorited.
        async enableAllFavoritedInstalledMods() {
            await this.enableInstalledModsInternal({ favoritesOnly: true });
        },

        // Loads local mods and bulk-enables a filtered set through manager endpoints.
        async enableInstalledModsInternal(opts = {}) {
            const favoritesOnly = !!opts.favoritesOnly;
            this.profileLoading = true;
            try {
                const localRes = await fetch('/api/manager/local-mods');
                if (!localRes.ok) return;
                const localMods = await localRes.json();
                if (!Array.isArray(localMods) || localMods.length === 0) return;

                const favoriteSet = new Set((this.favoriteIds || []).map(id => Number(id)));
                const selected = localMods.filter(m => {
                    if (!favoritesOnly) return true;
                    const tidRaw = m?.versionChecker?.modThreadId;
                    const tid = Number.parseInt(String(tidRaw || ''), 10);
                    return Number.isInteger(tid) && favoriteSet.has(tid);
                });

                for (const mod of selected) {
                    const modId = mod?.modId;
                    if (!modId) continue;
                    const res = await fetch(`/api/manager/enable/${encodeURIComponent(modId)}`, { method: 'POST' });
                    if (res.ok) await this.onModToggled(modId, true);
                }

                await this.fetchMods();
            } catch (e) {
                console.error('Bulk enable failed:', e);
            } finally {
                this.profileLoading = false;
            }
        },

        // Creates a new profile, selects it, and equips if previously equipped.
        async createProfile() {
            this.profileLoading = true;
            try {
                const wasEquipped = this.profileStore.state.isEquipped;
                const res = await fetch('/api/profiles', { method: 'POST' });
                if (!res.ok) return;
                const profile = await res.json();
                await this.refreshProfileStore();
                if (wasEquipped) {
                    await this.equipProfile();
                }
            } finally { this.profileLoading = false; }
        },

        // Selects an inactive profile. If currently equipped, equips the newly selected one.
        async selectProfile(id) {
            if (id === this.profileStore.state.selectedProfileId) return;
            this.profileLoading = true;
            try {
                const wasEquipped = this.profileStore.state.isEquipped;
                const res = await fetch(`/api/profiles/${id}/select`, { method: 'POST' });
                if (!res.ok) return;
                this.profileStore = await res.json();
                if (wasEquipped) {
                    await this.equipProfile();
                }
            } finally { this.profileLoading = false; }
        },

        // Equips the currently selected profile (bulk disable all, enable profile mods).
        async equipProfile() {
            const sel = this.profileStore.state.selectedProfileId;
            if (!sel) return;
            this.profileLoading = true;
            try {
                const res = await fetch(`/api/profiles/${sel}/equip`, { method: 'POST' });
                if (res.ok) {
                    this.profileStore = await res.json();
                    await this.fetchMods();
                }
            } finally { this.profileLoading = false; }
        },

        // Unequips the active profile without changing mod states.
        async unequipProfile() {
            this.profileLoading = true;
            try {
                const res = await fetch('/api/profiles/unequip', { method: 'POST' });
                if (res.ok) {
                    this.profileStore = await res.json();
                    await this.fetchMods();
                }
            } finally { this.profileLoading = false; }
        },

        // Adds all currently enabled mods to the selected profile.
        async addEnabledToProfile() {
            const sel = this.profileStore.state.selectedProfileId;
            if (!sel) return;
            this.profileLoading = true;
            try {
                const res = await fetch(`/api/profiles/${sel}/add-enabled`, { method: 'POST' });
                if (res.ok) this.profileStore = await res.json();
            } finally { this.profileLoading = false; }
        },

        // Overwrites the selected profile's mod list with currently enabled mods.
        async overwriteProfile() {
            const sel = this.profileStore.state.selectedProfileId;
            if (!sel) return;
            if (!confirm('Replace this profile\'s mods with the currently enabled mods?')) return;
            this.profileLoading = true;
            try {
                const res = await fetch(`/api/profiles/${sel}/overwrite-enabled`, { method: 'POST' });
                if (res.ok) this.profileStore = await res.json();
            } finally { this.profileLoading = false; }
        },

        // Deletes a profile. If it's equipped, unequips first. Selects next if available.
        async deleteProfile(id) {
            const profile = this.profileStore.profiles.find(p => p.id === id);
            const name = profile?.name || 'this profile';
            if (!confirm(`Delete "${name}"?`)) return;
            this.profileLoading = true;
            try {
                const res = await fetch(`/api/profiles/${id}`, { method: 'DELETE' });
                if (res.ok) {
                    this.profileStore = await res.json();
                    if (!this.profileStore.state.isEquipped) await this.fetchMods();
                }
            } finally { this.profileLoading = false; }
        },

        // Renames the selected profile inline.
        async renameProfile(id, name) {
            try {
                const res = await fetch(`/api/profiles/${id}`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ name })
                });
                if (res.ok) await this.refreshProfileStore();
            } catch (e) { console.error('Rename failed:', e); }
        },

        // Handles blur on the profile name input to persist the rename.
        onProfileNameBlur(event, id) {
            const name = event.target.value.trim();
            if (name) this.renameProfile(id, name);
            this.profileRenaming = false;
        },

        // Dismisses the cleanup warning for the selected profile.
        async dismissCleanup(id) {
            try {
                const res = await fetch(`/api/profiles/${id}/dismiss-cleanup`, { method: 'POST' });
                if (res.ok) this.profileStore = await res.json();
            } catch (e) { console.error('Dismiss failed:', e); }
        },

        // Returns the cleanup count for a given profile ID (0 if none).
        cleanupCount(id) {
            return this.profileStore.state.cleanupCounts?.[id] || 0;
        },

        // Called after any single mod toggle while a profile is equipped. Syncs to server.
        async onModToggled(modId, isEnabled) {
            if (!this.profileStore.state.isEquipped || !this.profileStore.state.selectedProfileId) return;
            try {
                await fetch('/api/profiles/sync-toggle', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ modId, isEnabled })
                });
                await this.refreshProfileStore();
            } catch (e) { console.error('Profile sync failed:', e); }
        }
    };
}
