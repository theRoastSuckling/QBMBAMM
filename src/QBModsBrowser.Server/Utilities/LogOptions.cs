namespace QBModsBrowser.Server.Utilities;

// Runtime-toggleable log filter flags; read on every log event so changes take effect immediately.
public static class LogOptions
{
    // When true, HTTP request and endpoint-routing entries for the polling endpoints are emitted.
    public static volatile bool ShowPollingLogs = false;
}
