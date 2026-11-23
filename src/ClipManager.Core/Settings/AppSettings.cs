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

    /// <summary>Stand der Einstellungsdatei, für Umstellungen alter Werte (siehe SettingsStore.Migrate).</summary>
    public int Version { get; set; } = CurrentVersion;

    public const int CurrentVersion = 1;

    /// <summary>Eigene Namen für Tonspuren (Index ab 0), z.B. 2 → "Mikro". Gilt für alle Clips.</summary>
    public Dictionary<int, string> TrackNames { get; set; } = new();

    /// <summary>Anzeigename einer Spur: eigener Name, sonst Titel aus der Datei, sonst "Spur N".</summary>
    public string TrackName(int index, string? titleFromFile, int trackCount)
    {
        if (TrackNames.TryGetValue(index, out var own) && !string.IsNullOrWhiteSpace(own))
        {
            return own;
        }

        if (!string.IsNullOrWhiteSpace(titleFromFile) && !titleFromFile.StartsWith("Track", StringComparison.OrdinalIgnoreCase))
        {
            return titleFromFile;
        }

        return index == 0 && trackCount > 1 ? "Spur 1 (Mix)" : $"Spur {index + 1}";
    }

    /// <summary>Eigener Pfad zu ffmpeg.exe. Leer = automatisch suchen.</summary>
    public string? FfmpegPath { get; set; }

    public static Dictionary<DiscordTier, int> DefaultLimits() => new()
    {
        [DiscordTier.Free] = 20,
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
