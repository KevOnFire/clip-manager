using System.Globalization;

namespace ClipManager.Core.Storage;

/// <summary>Belegung eines Laufwerks (z.B. "F:\").</summary>
public sealed record DiskUsage(string Drive, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;

    public double UsedPercent => TotalBytes <= 0 ? 0 : UsedBytes * 100.0 / TotalBytes;

    /// <summary>Laufwerksname ohne "\", z.B. "F:".</summary>
    public string Label => Drive.TrimEnd('\\', '/') is { Length: > 0 } label ? label : Drive;

    /// <summary>
    /// Belegung aller Laufwerke, auf denen die Clip-Ordner liegen (jedes Laufwerk einmal).
    /// <paramref name="probe"/> liefert (gesamt, frei) für ein Laufwerk, oder null, wenn es nicht da ist.
    /// </summary>
    public static List<DiskUsage> ForFolders(IEnumerable<string> folders, Func<string, (long Total, long Free)?>? probe = null)
    {
        probe ??= ProbeDrive;
        var result = new List<DiskUsage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            string? root;
            try
            {
                root = Path.GetPathRoot(Path.GetFullPath(folder));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (string.IsNullOrEmpty(root) || !seen.Add(root))
            {
                continue;
            }

            if (probe(root) is { Total: > 0 } space)
            {
                result.Add(new DiskUsage(root, space.Total, space.Free));
            }
        }

        return result;
    }

    private static (long Total, long Free)? ProbeDrive(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.TotalSize, drive.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Größe lesbar: "312 GB", "1,4 TB", "850 MB".</summary>
    public static string FormatBytes(long bytes)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024, tb = gb * 1024;
        return bytes >= tb ? (bytes / tb).ToString("0.0", de) + " TB"
            : bytes >= 100 * gb ? (bytes / gb).ToString("0", de) + " GB"
            : bytes >= gb ? (bytes / gb).ToString("0.0", de) + " GB"
            : (bytes / mb).ToString("0", de) + " MB";
    }
}
