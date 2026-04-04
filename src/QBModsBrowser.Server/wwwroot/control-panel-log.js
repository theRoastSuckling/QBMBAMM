// Renders a CLEF log message template by substituting named property fields.
// Serilog.Formatting.Compact v3 omits @m, so the UI must reconstruct it from @mt + properties.
// Tokens with a format specifier (e.g. {Elapsed:0.0000}) consume values from the @r renderings array in order.
function renderClefTemplate(obj) {
    const template = obj['@mt'] || '';
    const renderings = obj['@r'] || [];
    let rIdx = 0;
    return template.replace(/\{([^}@:]+)(?::([^}]*))?\}/g, (match, name, fmt) => {
        if (fmt !== undefined) {
            return rIdx < renderings.length ? renderings[rIdx++] : match;
        }
        const val = obj[name];
        if (val === undefined || val === null) return match;
        if (typeof val === 'object') return val.Name ?? JSON.stringify(val);
        return String(val);
    });
}

// Returns log pane state fields and methods to be spread into the control panel state object.
function buildLogPaneMethods() {
    return {
        // Log viewer state for the control panel log pane.
        logLines: [],
        logType: 'server',
        logAutoScroll: true,
        // When false, INF-level lines are hidden from the log pane.
        logShowInf: true,
        // When false, polling endpoint log entries are suppressed server-side and absent from log files.
        logShowPolling: false,
        _lastLogRaw: null,
        // Tracks raw line count separately since logLines includes synthetic divider entries.
        _lastLogCount: 0,

        // Reads the current server-side log options so the checkbox reflects live state.
        async fetchLogOptions() {
            try {
                const res = await fetch('/api/log-options');
                const data = await res.json();
                this.logShowPolling = !!data.showPollingLogs;
            } catch (_) {}
        },

        // Persists the logShowPolling toggle to the server so the filter takes effect immediately.
        async saveLogOptions() {
            try {
                await fetch('/api/log-options', {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ showPollingLogs: this.logShowPolling })
                });
                // Clear cached lines so next poll re-parses with updated suppression state.
                this.logLines = [];
                this._lastLogRaw = null;
                this._lastLogCount = 0;
                await this.fetchLogs();
            } catch (_) {}
        },

        // Fetches and parses the last 200 lines from the server or scraper log file for the log pane.
        // Injects divider entries before server-start messages to mark restarts visually.
        // Skips re-rendering when the tail is unchanged to avoid unnecessary DOM updates.
        async fetchLogs() {
            try {
                const res = await fetch(`/api/scraper/logs?lines=200&type=${this.logType}`);
                const data = await res.json();
                const rawLines = data.lines || [];
                const lastRaw = rawLines[rawLines.length - 1] ?? null;
                if (rawLines.length === this._lastLogCount && lastRaw === this._lastLogRaw) return;
                this._lastLogRaw = lastRaw;
                this._lastLogCount = rawLines.length;
                const result = [];
                for (let i = 0; i < rawLines.length; i++) {
                    try {
                        const obj = JSON.parse(rawLines[i]);
                        const ts = obj['@t']
                            ? new Date(obj['@t']).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false })
                            : '';
                        // @l is absent for Information in CLEF; @m is absent in v3 so render the template.
                        const level = obj['@l'] || 'INF';
                        const msg = obj['@m'] ?? renderClefTemplate(obj);
                        // Insert a divider sentinel before each "Server starting" entry.
                        if ((obj['@mt'] || msg).startsWith('Server starting')) {
                            result.push({ _i: `div-${i}`, isDivider: true });
                        }
                        // Dim noisy ASP.NET routing/response lines that add little diagnostic value.
                        const isDimmed = msg.includes('Executed endpoint') || msg.includes('responded 200 in');
                        result.push({ _i: i, ts, level, msg, isDimmed });
                    } catch {
                        const rawMsg = rawLines[i];
                        const isDimmed = rawMsg.includes('Executed endpoint') || rawMsg.includes('responded 200 in');
                        result.push({ _i: i, ts: '', level: 'INF', msg: rawMsg, isDimmed });
                    }
                }
                this.logLines = result;
            } catch (_) {}
        },
    };
}
