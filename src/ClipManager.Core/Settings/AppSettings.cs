namespace ClipManager.Core.Settings;

/// <summary>Alle Einstellungen, die zwischen zwei Programmstarts erhalten bleiben.</summary>
public sealed class AppSettings
{
    /// <summary>Ordner, in denen OBS die Clips speichert.</summary>
    public List<string> ClipFolders { get; set; } = new();

    /// <summary>Wohin Exporte gespeichert werden. Leer = Standardordner.</summary>
    public string? ExportFolder { get; set; }

    /// <summary>Welche Discord-Stufe der Nutzer hat (Standard beim Export).</summary>
    public DiscordTier Tier { get; set; } = DiscordTier.Free;

    /// <summary>Upload-Limits in MB pro Stufe. Änderbar, falls Discord die Werte anpasst.</summary>
    public Dictionary<DiscordTier, int> TierLimitsMb { get; set; } = DefaultLimits();

    /// <summary>Eigener Pfad zu ffmpeg.exe. Leer = automatisch suchen.</summary>
    public string? FfmpegPath { get; set; }

    public static Dictionary<DiscordTier, int> DefaultLimits() => new()
    {
        [DiscordTier.Free] = 10,
        [DiscordTier.NitroBasic] = 50,
        [DiscordTier.Nitro] = 500,
    };

    /// <summary>Limit in MB für eine Stufe. Fehlende oder ungültige Werte fallen auf den Standard zurück.</summary>
    public int LimitFor(DiscordTier tier) =>
        TierLimitsMb.TryGetValue(tier, out var mb) && mb > 0 ? mb : DefaultLimits()[tier];

    /// <summary>Fügt einen Clip-Ordner hinzu. Gibt false zurück, wenn er schon drin ist.</summary>
    public bool AddClipFolder(string folder)
    {
        var normalized = NormalizeFolder(folder);
        if (ClipFolders.Any(f => PathsEqual(f, normalized)))
        {
            return false;
        }

        ClipFolders.Add(normalized);
        return true;
    }

    public bool RemoveClipFolder(string folder)
    {
        var normalized = NormalizeFolder(folder);
        return ClipFolders.RemoveAll(f => PathsEqual(f, normalized)) > 0;
    }

    public static string NormalizeFolder(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    // Windows-Pfade sind nicht case-sensitiv; OBS-Ordner werden oft unterschiedlich geschrieben.
    public static bool PathsEqual(string a, string b) =>
        string.Equals(NormalizeFolder(a), NormalizeFolder(b), StringComparison.OrdinalIgnoreCase);
}
