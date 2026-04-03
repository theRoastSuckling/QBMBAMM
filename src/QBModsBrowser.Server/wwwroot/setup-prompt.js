// Provides Alpine methods and template loading for the first-run mods-folder setup prompt.
// setup-prompt.html includes a static keyboard and feature overview for new users (loaded into the mount point).

// Returns Alpine methods that handle the setup prompt interactions.
function buildSetupPromptMethods() {
    return {
        // Validates that the path exists on disk, then saves config and dismisses the setup prompt.
        async saveSetupModsPath() {
            const path = (this.managerConfig.modsPath || '').trim();
            if (!path) return;
            try {
                const res = await fetch(`/api/manager/check-folder?path=${encodeURIComponent(path)}`);
                const data = await res.json();
                if (!data.exists) {
                    this.setupPathError = 'Folder not found. Please check the path and try again.';
                    return;
                }
            } catch {
                this.setupPathError = 'Could not verify the folder path. Please try again.';
                return;
            }
            this.setupPathError = '';
            await this.saveManagerConfig();
            this.showSetupPrompt = false;
        },

        // Fetches the setup-prompt HTML partial and injects it into the mount point.
        async loadSetupPromptTemplate() {
            const mount = this.$refs.setupPromptMount;
            if (!mount) return;
            try {
                const res = await fetch('/setup-prompt.html', { cache: 'no-store' });
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                mount.innerHTML = await res.text();
                if (window.Alpine?.initTree) {
                    window.Alpine.initTree(mount);
                }
            } catch (e) {
                console.error('Failed to load setup prompt template:', e);
            }
        }
    };
}
