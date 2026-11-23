namespace ClipManager.Core.Library;

/// <summary>Sucht Videoclips in den Clip-Ordnern.</summary>
public static class ClipScanner
{
    /// <summary>Dateiendungen, die OBS schreiben kann.</summary>
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".flv", ".webm", ".ts", ".m4v",
    };

    /// <summary>
    /// Findet alle Clips in den Ordnern (inklusive Unterordner), neueste zuerst.
    /// Ordner, die sich überschneiden, liefern jede Datei nur einmal.
    /// Dateien im Export-Ordner werden ausgelassen, damit Exporte nicht als Clips auftauchen.
    /// </summary>
    public static IReadOnlyList<ClipFile> Scan(IEnumerable<string> folders, string? excludeFolder = null)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var exclude = string.IsNullOrWhiteSpace(excludeFolder)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(excludeFolder)) + Path.DirectorySeparatorChar;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clips = new List<ClipFile>();

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
            {
                if (!VideoExtensions.Contains(file.Extension))
                {
                    continue;
                }

                if (exclude != null && file.FullName.StartsWith(exclude, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seen.Add(file.FullName))
                {
                    clips.Add(new ClipFile(file.FullName, root, file.Length, file.LastWriteTimeUtc));
                }
            }
        }

        return clips.OrderByDescending(c => c.ModifiedUtc).ToList();
    }
}
