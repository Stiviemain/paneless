using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paneless.Core;

/// <summary>A shared script: what's in a .paneless-script file, a link or the clipboard.</summary>
public sealed class SharedScript
{
    [JsonPropertyName("paneless")] public int Format { get; set; } = 1;
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("commands")] public string Commands { get; set; } = "";
    [JsonPropertyName("confirm")] public bool Confirm { get; set; }
}

/// <summary>Export and import of scripts. Imported scripts always ask before running, until you've looked at them.</summary>
public static class ScriptSharing
{
    public const string Extension = ".paneless-script";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Export(AdbScript s) =>
        JsonSerializer.Serialize(new SharedScript { Name = s.Name, Description = s.Description, Commands = s.Commands, Confirm = s.Confirm }, Json);

    public static string ExportMany(IEnumerable<AdbScript> scripts) =>
        JsonSerializer.Serialize(scripts.Select(s => new SharedScript { Name = s.Name, Description = s.Description, Commands = s.Commands, Confirm = s.Confirm }).ToList(), Json);

    /// <summary>Reads one script or a list. Plain text that isn't JSON becomes one script with those commands.</summary>
    public static IReadOnlyList<SharedScript> Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new FormatException(Text.T("There’s nothing to import."));
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            var list = text.StartsWith('[')
                ? JsonSerializer.Deserialize<List<SharedScript>>(text) ?? []
                : [JsonSerializer.Deserialize<SharedScript>(text) ?? throw new FormatException(Text.T("This isn’t a Paneless script."))];
            list = list.Where(s => !string.IsNullOrWhiteSpace(s.Commands)).ToList();
            if (list.Count == 0) throw new FormatException(Text.T("No commands found."));
            foreach (var s in list) if (string.IsNullOrWhiteSpace(s.Name)) s.Name = Text.T("Imported script");
            return list;
        }
        // A plain list of adb commands, e.g. pasted from a website.
        var lines = ScriptText.Lines(text);
        if (lines.Count == 0 || lines.Any(l => l.Length > 2000)) throw new FormatException(Text.T("That doesn’t look like adb commands."));
        return [new SharedScript { Name = Text.T("Imported script"), Commands = text }];
    }

    /// <summary>Downloads a shared script: a direct link (e.g. a raw GitHub file) or a GitHub Gist page.</summary>
    public static async Task<string> DownloadAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new FormatException(Text.T("Use an https:// link."));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Paneless");
        if (uri.Host == "gist.github.com")
        {
            var id = uri.AbsolutePath.Trim('/').Split('/').Last();
            var json = await http.GetStringAsync($"https://api.github.com/gists/{id}", ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var f in doc.RootElement.GetProperty("files").EnumerateObject())
                if (f.Value.TryGetProperty("content", out var c)) return c.GetString() ?? "";
            throw new FormatException(Text.T("The gist has no files."));
        }
        if (uri.Host == "github.com" && uri.AbsolutePath.Contains("/blob/"))   // a file page: use its raw version
            uri = new Uri("https://raw.githubusercontent.com" + uri.AbsolutePath.Replace("/blob/", "/"));
        var bytes = await http.GetByteArrayAsync(uri, ct);
        if (bytes.Length > 256 * 1024) throw new FormatException(Text.T("That file is too big for a script."));
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
