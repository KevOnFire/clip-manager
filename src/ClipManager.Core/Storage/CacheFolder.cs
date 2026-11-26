namespace ClipManager.Core.Storage;

/// <summary>
/// Zwischenspeicher von Clippy: Tonspuren als WAV ("audio") und Vorschaubilder/Filmstreifen ("thumbs").
/// Alles darin darf jederzeit gelöscht werden, Clippy legt es bei Bedarf neu an.
/// </summary>
public static class CacheFolder
{
    public const string AudioFolder = "audio";
    public const string ThumbsFolder = "thumbs";

    /// <summary>Name des Unterordners, den Clippy in einem selbst gewählten Ordner anlegt.</summary>
    public const string CustomFolderName = "Clippy Zwischenspeicher";

    public static readonly string[] SubFolders = { AudioFolder, ThumbsFolder };

    /// <summary>Wurzel des Zwischenspeichers: eigener Ordner aus den Einstellungen oder der Standard.</summary>
    public static string Resolve(string? custom, string defaultRoot) =>
        string.IsNullOrWhiteSpace(custom) ? defaultRoot : custom;

    /// <summary>
    /// Aus einem gewählten Ordner (z.B. "D:\") den eigentlichen Zwischenspeicher machen ("D:\Clippy Zwischenspeicher"),
    /// damit Clippy keine losen Ordner "audio"/"thumbs" irgendwo hinlegt.
    /// </summary>
    public static string FromPicked(string picked)
    {
        var trimmed = picked.TrimEnd('\\', '/');
        return string.Equals(Path.GetFileName(trimmed), CustomFolderName, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : Path.Combine(picked, CustomFolderName);
    }

    /// <summary>Größe aller Dateien in einem Ordner (mit Unterordnern), 0 wenn es ihn nicht gibt.</summary>
    public static long Size(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in SafeFiles(directory, SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return total;
    }

    /// <summary>Löscht alle Dateien im Ordner. Gerade benutzte werden übersprungen. Gibt die Zahl der gelöschten zurück.</summary>
    public static int Clear(string directory)
    {
        var deleted = 0;
        foreach (var file in SafeFiles(directory, SearchOption.AllDirectories))
        {
            if (TryDelete(file))
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>
    /// Löscht die am längsten nicht benutzten Dateien, bis der Ordner höchstens <paramref name="maxBytes"/> groß ist.
    /// "Benutzt" = Änderungszeit (der Cache setzt sie bei jedem Zugriff neu). Gerade geöffnete Dateien bleiben.
    /// </summary>
    public static int TrimToSize(string directory, long maxBytes)
    {
        if (maxBytes <= 0 || !Directory.Exists(directory))
        {
            return 0;
        }

        var files = SafeFiles(directory, SearchOption.TopDirectoryOnly)
            .Select(f => new FileInfo(f))
            .Where(f => f.Exists)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();
        var total = files.Sum(f => f.Length);
        var deleted = 0;
        foreach (var file in files)
        {
            if (total <= maxBytes)
            {
                break;
            }

            if (TryDelete(file.FullName))
            {
                total -= file.Length;
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>
    /// Zieht den Zwischenspeicher um: verschiebt die Dateien aus "audio" und "thumbs" vom alten in den neuen Ordner.
    /// Andere Dateien im alten Ordner (z.B. log.txt) bleiben liegen. Gerade benutzte Dateien werden übersprungen.
    /// </summary>
    public static (int Moved, int Skipped) Move(string oldRoot, string newRoot)
    {
        var from = Path.GetFullPath(oldRoot);
        var to = Path.GetFullPath(newRoot);
        if (string.Equals(from.TrimEnd('\\', '/'), to.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return (0, 0);
        }

        int moved = 0, skipped = 0;
        foreach (var sub in SubFolders)
        {
            var source = Path.Combine(from, sub);
            if (!Directory.Exists(source))
            {
                continue;
            }

            var target = Path.Combine(to, sub);
            Directory.CreateDirectory(target);
            foreach (var file in SafeFiles(source, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    // Auf ein anderes Laufwerk kopiert .NET und löscht danach das Original.
                    File.Move(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                    moved++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(source).Any())
                {
                    Directory.Delete(source);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return (moved, skipped);
    }

    private static bool TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static List<string> SafeFiles(string directory, SearchOption option)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory, "*", option).ToList() : new List<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }
}
