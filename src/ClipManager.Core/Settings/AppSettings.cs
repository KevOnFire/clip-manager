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

    /// <summary>Auch Unterordner der Clip-Ordner nach Clips durchsuchen.</summary>
    public bool ScanSubfolders { get; set; } = true;

    /// <summary>
    /// Welche Spuren beim Öffnen eines Clips an sind (Index ab 0). Fehlt eine Spur hier, gilt die
    /// Standardregel: Spur 1 (Mix) aus, wenn es Einzelspuren gibt, sonst alles an.
    /// </summary>
    public Dictionary<int, bool> TrackEnabledByDefault { get; set; } = new();

    /// <summary>Spuren, die bei einem Clip mit trackCount Spuren am Anfang an sind.</summary>
    public IReadOnlyList<int> EnabledTracksFor(int trackCount)
    {
        var rule = Export.ExportPlanner.DefaultAudioTracks(trackCount);
        var result = Enumerable.Range(0, Math.Max(0, trackCount))
            .Where(i => TrackEnabledByDefault.TryGetValue(i, out var on) ? on : rule.Contains(i))
            .ToList();

        // Nie ganz ohne Ton starten (z.B. Clip mit nur einer Spur, die sonst aus wäre).
        return result.Count > 0 ? result : rule;
    }

    /// <summary>Eigene Farben für Tonspuren (Index ab 0) als "#RRGGBB". Fehlt eine, gilt die Standardfarbe.</summary>
    public Dictionary<int, string> TrackColors { get; set; } = new();

    /// <summary>Farben zur Auswahl und als Standard (Spur 1 grau-blau für den Mix, dann bunt).</summary>
    public static readonly IReadOnlyList<string> Palette = new[]
    {
        "#9AA5B8", "#FFB347", "#5AB0FF", "#8C7CFF", "#4CD07D", "#FF6FA8",
        "#3DD6D0", "#F2D04B", "#FF7A59", "#A3E36B", "#E86BFF", "#6B8BFF",
    };

    /// <summary>Standardfarbe einer Spur, wenn keine eigene gesetzt ist.</summary>
    public static string DefaultTrackColor(int index) => Palette[Math.Max(0, index) % Palette.Count];

    /// <summary>Farbe einer Spur: eigene Farbe, sonst Standardfarbe.</summary>
    public string TrackColor(int index) =>
        TrackColors.TryGetValue(index, out var own) && IsHexColor(own) ? own : DefaultTrackColor(index);

    /// <summary>Prüft auf "#RRGGBB".</summary>
    public static bool IsHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    /// <summary>Eigener Pfad zu ffmpeg.exe. Leer = automatisch suchen.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Video-Farben in der Vorschau von 16–235 auf 0–255 strecken (sonst wirkt das Bild oft blass).</summary>
    public bool CorrectVideoColors { get; set; } = true;

    /// <summary>
    /// Zuletzt beim Export gewähltes Ziel: "Free", "NitroBasic", "Nitro" oder "Full" (volle Qualität).
    /// Null = noch nie exportiert, dann gilt die Discord-Stufe aus den Einstellungen.
    /// </summary>
    public string? LastExportTarget { get; set; }

    /// <summary>Welcher Eintrag im Export-Fenster vorausgewählt ist: 0–2 = Discord-Stufen, 3 = volle Qualität.</summary>
    public int ExportTargetIndex() =>
        LastExportTarget == "Full" ? 3
        : Enum.TryParse<DiscordTier>(LastExportTarget, out var tier) && Enum.IsDefined(tier) ? (int)tier
        : Enum.IsDefined(Tier) ? (int)Tier : 0;

    /// <summary>Merkt sich das Ziel für den nächsten Export (null = volle Qualität).</summary>
    public void RememberExportTarget(DiscordTier? tier) => LastExportTarget = tier?.ToString() ?? "Full";

    /// <summary>Lautstärke der Vorschau im Editor (0–1), gilt für alle Clips.</summary>
    public double PreviewVolume { get; set; } = 0.8;

    /// <summary>Eigenes Design mit Farbverlauf (standardmäßig aus).</summary>
    public ThemeSettings Theme { get; set; } = new();

    /// <summary>Eigener Ordner für den Zwischenspeicher (Tonspuren, Vorschaubilder). Null = %LocalAppData%\ClipManager.</summary>
    public string? CacheFolder { get; set; }

    /// <summary>Höchstens so viele GB Tonspuren zwischenspeichern (1–100).</summary>
    public int AudioCacheMaxGb { get; set; } = 5;

    public long AudioCacheMaxBytes() => Math.Clamp(AudioCacheMaxGb, 1, 100) * 1024L * 1024 * 1024;

    /// <summary>Tonspuren im Editor eingeklappt (nur Filmstreifen + kleine An/Aus-Knöpfe). Gilt für alle Clips.</summary>
    public bool TracksCollapsed { get; set; }

    /// <summary>Unter dem Filmstreifen die Objekt-Reihen statt der Tonspuren zeigen.</summary>
    public bool ShowObjectLanes { get; set; }

    /// <summary>Zuletzt selbst gewählte Farben für Objekte ("#RRGGBB"), neueste zuerst.</summary>
    public List<string> CustomColors { get; set; } = new();

    /// <summary>Entfernt eine selbst gewählte Farbe. True, wenn sie dabei war.</summary>
    public bool ForgetColor(string color) =>
        CustomColors.RemoveAll(c => string.Equals(c, Editing.OverlayItem.NormalizeColor(color), StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>Merkt eine eigene Farbe (höchstens 6, keine doppelten).</summary>
    public void RememberColor(string color)
    {
        color = Editing.OverlayItem.NormalizeColor(color);
        CustomColors.RemoveAll(c => string.Equals(c, color, StringComparison.OrdinalIgnoreCase));
        CustomColors.Insert(0, color);
        if (CustomColors.Count > 6)
        {
            CustomColors.RemoveRange(6, CustomColors.Count - 6);
        }
    }

    /// <summary>Warnen, wenn die Festplatte mit den Clips fast voll ist.</summary>
    public bool DiskWarningEnabled { get; set; } = true;

    /// <summary>Ab wie viel Prozent belegt gewarnt wird (50–99).</summary>
    public int DiskWarningPercent { get; set; } = 95;

    /// <summary>
    /// Clips, die nach diesem Zeitpunkt gespeichert wurden und noch nicht geöffnet sind, gelten als "neu".
    /// Wird beim ersten Start gesetzt, damit nicht alle alten Clips als neu markiert werden.
    /// </summary>
    public DateTime? NewClipsSince { get; set; }

    /// <summary>Ist die Platte so voll, dass gewarnt werden soll?</summary>
    public bool ShouldWarn(Storage.DiskUsage usage) =>
        DiskWarningEnabled && usage.UsedPercent >= Math.Clamp(DiskWarningPercent, 50, 99);

    /// <summary>Fenstergröße und -lage vom letzten Mal (null = Standard).</summary>
    public WindowBounds? Window { get; set; }

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
