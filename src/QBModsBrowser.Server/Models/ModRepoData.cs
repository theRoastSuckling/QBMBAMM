using System.Text.Json.Serialization;

namespace QBModsBrowser.Server.Models;

// Root model for serialized StarsectorModRepo cache payload.
public class ModRepoData
{
    [JsonPropertyName("lastUpdated")]
    public string? LastUpdated { get; set; }

    [JsonPropertyName("items")]
    public List<ModRepoEntry> Items { get; set; } = [];
}

// Represents one ModRepo entry used for enrichment and download metadata.
public class ModRepoEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("modVersion")]
    public string? ModVersion { get; set; }

    [JsonPropertyName("gameVersionReq")]
    public string? GameVersionReq { get; set; }

    [JsonPropertyName("authorsList")]
    public List<string>? AuthorsList { get; set; }

    [JsonPropertyName("categories")]
    public List<string>? Categories { get; set; }

    [JsonPropertyName("sources")]
    public List<string>? Sources { get; set; }

    [JsonPropertyName("urls")]
    public Dictionary<string, string>? Urls { get; set; }

    [JsonPropertyName("images")]
    public Dictionary<string, ModRepoImage>? Images { get; set; }

    [JsonPropertyName("dateTimeCreated")]
    public string? DateTimeCreated { get; set; }

    [JsonPropertyName("dateTimeEdited")]
    public string? DateTimeEdited { get; set; }

    // Returns a URL by key (for example Forum, DirectDownload, or NexusMods).
    public string? GetUrl(string key) =>
        Urls != null && Urls.TryGetValue(key, out var url) ? url : null;
}

// Represents one image entry attached to a ModRepo item.
public class ModRepoImage
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("proxyUrl")]
    public string? ProxyUrl { get; set; }
}

