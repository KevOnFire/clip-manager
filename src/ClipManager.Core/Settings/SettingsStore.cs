using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipManager.Core.Settings;

/// <summary>Liest und schreibt <see cref="AppSettings"/> als JSON-Datei.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SettingsStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>
    /// Lädt die Einstellungen. Fehlt die Datei, gibt es Standardwerte. Ist sie kaputt,
    /// wird sie als .bak gesichert und ebenfalls mit Standardwerten weitergemacht.
    /// </summary>
    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.ClipFolders ??= new();
            settings.TierLimitsMb ??= AppSettings.DefaultLimits();
            return settings;
        }
        catch (JsonException)
        {
            File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            return new AppSettings();
        }
    }

    /// <summary>Speichert atomar: erst in eine temporäre Datei, dann umbenennen.</summary>
    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
