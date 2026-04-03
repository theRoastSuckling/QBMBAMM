// Provides shared page-load utilities used across frontend pages.
window.QBPageLoad = window.QBPageLoad || {};

// Triggers a local mod rescan on page load so browser refresh picks up manual installs.
window.QBPageLoad.scanLocalModsOnPageLoad = async function () {
    try {
        await fetch('/api/manager/scan', { method: 'POST' });
    } catch (_) {}
};
