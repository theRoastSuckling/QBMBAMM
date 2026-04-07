namespace QBModsBrowser.Server.Models;

// Top-level application configuration loaded from app-config.json at the project root.
public class AppConfig
{
    // Discord-specific identifiers used by the bug-report button.
    public DiscordConfig Discord { get; set; } = new();

    // Forum data repo sync settings for publishing scraped data and fetching it on client machines.
    public ForumDataRepoConfig ForumDataRepo { get; set; } = new();
}

// Discord server and channel identifiers.
public class DiscordConfig
{
    public string ServerId { get; set; } = string.Empty;
    public string BugReportThreadId { get; set; } = string.Empty;
}
