using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using System.Text.Json;

namespace QBModsBrowser.Server.Services;

/// <summary>
/// Pre-processes Starsector quasi-JSON files (mod_info.json, .version files) that contain
/// comments, trailing commas, tabs, and YAML syntax before parsing as JSON.
/// </summary>
// Cleans Starsector quasi-JSON inputs so they can be parsed reliably by .NET serializers.
public static partial class JsonFixHelper
{
    // Applies text cleanup and YAML fallback conversion before JSON parsing.
    public static string FixJson(string raw)
    {
        // Step 1: Replace \# with #
        var text = raw.Replace("\\#", "#");

        // Step 2: Replace tabs with two spaces
        text = text.Replace("\t", "  ");

        // Step 3: Remove full-line // comments (but not URLs containing //)
        text = RemoveLineComments(text);

        // Step 4: Remove trailing commas before } or ]
        text = TrailingCommaRegex().Replace(text, "$1");

        // Step 5: Try YAML parse then re-encode as JSON for YAML-superset handling
        try
        {
            var deserializer = new DeserializerBuilder().Build();
            var yamlObj = deserializer.Deserialize<object>(text);
            if (yamlObj != null)
            {
                text = JsonSerializer.Serialize(yamlObj, new JsonSerializerOptions { WriteIndented = false });
            }
        }
        catch
        {
            // YAML parse failed, continue with the text we have from steps 1-4
        }

        return text;
    }

    // Convenience helper that fixes and deserializes malformed game metadata in one step.
    public static T? ParseFixedJson<T>(string raw, JsonSerializerOptions? options = null)
    {
        var fixed_ = FixJson(raw);
        return JsonSerializer.Deserialize<T>(fixed_, options ?? DefaultJsonOptions);
    }

    // Removes both full-line and inline comments from each line, tracking string context so
    // // URLs inside strings and # chars inside values are preserved correctly.
    private static string RemoveLineComments(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = StripLineComment(lines[i]);
        return string.Join('\n', lines);
    }

    // Strips // and # comments from a single line.
    // Full-line comments (line starts with comment token after whitespace) are blanked entirely.
    // Inline comments that appear outside a JSON string literal are truncated at the token.
    private static string StripLineComment(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//") || trimmed.StartsWith("#"))
            return "";

        bool inString = false;
        bool escape = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (!inString)
            {
                if (c == '#')
                    return line[..i].TrimEnd();
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                    return line[..i].TrimEnd();
            }
        }
        return line;
    }

    // Finds trailing commas before closing braces/brackets for JSON compatibility.
    [GeneratedRegex(@",\s*([}\]])", RegexOptions.Compiled)]
    private static partial Regex TrailingCommaRegex();

    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
