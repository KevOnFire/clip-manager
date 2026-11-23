namespace ClipManager.Core.Media;

public sealed record FfmpegPaths(string Ffmpeg, string? Ffprobe);

/// <summary>Findet ffmpeg (und ffprobe) auf dem Rechner.</summary>
public static class FfmpegLocator
{
    /// <summary>
    /// Sucht in dieser Reihenfolge: eigener Pfad aus den Einstellungen, Unterordner "ffmpeg"
    /// neben dem Programm, direkt neben dem Programm, dann alle Ordner aus PATH.
    /// </summary>
    public static FfmpegPaths? Locate(
        string? overridePath,
        string appDirectory,
        string? pathVariable,
        Func<string, bool>? fileExists = null,
        bool? windows = null)
    {
        fileExists ??= File.Exists;
        var isWindows = windows ?? OperatingSystem.IsWindows();
        var ffmpegName = isWindows ? "ffmpeg.exe" : "ffmpeg";
        var ffprobeName = isWindows ? "ffprobe.exe" : "ffprobe";

        if (!string.IsNullOrWhiteSpace(overridePath) && fileExists(overridePath))
        {
            return WithProbe(overridePath);
        }

        var candidates = new List<string>
        {
            Path.Combine(appDirectory, "ffmpeg", ffmpegName),
            Path.Combine(appDirectory, ffmpegName),
        };

        if (!string.IsNullOrEmpty(pathVariable))
        {
            var separator = isWindows ? ';' : ':';
            foreach (var dir in pathVariable.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                candidates.Add(Path.Combine(dir.Trim('"'), ffmpegName));
            }
        }

        var found = candidates.FirstOrDefault(fileExists);
        return found == null ? null : WithProbe(found);

        FfmpegPaths WithProbe(string ffmpeg)
        {
            var probe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? string.Empty, ffprobeName);
            return new FfmpegPaths(ffmpeg, fileExists(probe) ? probe : null);
        }
    }
}
