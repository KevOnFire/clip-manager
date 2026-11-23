using System.Net.Http.Headers;
using System.Text.Json;

namespace ClipManager.Core.Updates;

public sealed record ReleaseInfo(Version Version, string Tag, string DownloadUrl, string? Notes, long SizeBytes);

/// <summary>
/// Fragt bei GitHub nach dem neuesten Release. Jedes Release enthält die Datei
/// <see cref="AssetName"/> mit dem kompletten Programmordner.
/// </summary>
public sealed class UpdateChecker
{
    public const string Owner = "KevOnFire";
    public const string Repository = "clip-manager";
    public const string AssetName = "ClipManager-win-x64.zip";

    private readonly HttpClient _http;

    public UpdateChecker(HttpClient http)
    {
        _http = http;
    }

    public static string LatestReleaseUrl => $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";

    /// <summary>Gibt das neueste Release zurück oder null, wenn es keins gibt oder GitHub nicht erreichbar ist.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ClipManager", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseRelease(json);
    }

    public static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (tag == null || ParseVersion(tag) is not { } version)
            {
                return null;
            }

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var name) && name.GetString() == AssetName
                    && asset.TryGetProperty("browser_download_url", out var url) && url.GetString() is { } downloadUrl)
                {
                    var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;
                    var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                    return new ReleaseInfo(version, tag, downloadUrl, notes, size);
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Liest "v0.12.0" oder "0.12" als Version.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = text.Trim().TrimStart('v', 'V');
        var plus = cleaned.IndexOfAny(new[] { '+', '-' });
        if (plus >= 0)
        {
            cleaned = cleaned[..plus];
        }

        return Version.TryParse(cleaned, out var v) ? Normalize(v) : null;
    }

    public static bool IsNewer(Version available, Version current) => Normalize(available) > Normalize(current);

    // Version(0,12) und Version(0,12,0) sollen gleich sein.
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
