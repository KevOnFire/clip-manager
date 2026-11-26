using System.Globalization;

namespace ClipManager.Core.Media;

/// <summary>
/// Ein bewegtes Bild (GIF), zerlegt in einzelne PNGs mit fester Bildrate. Vorschau und Export zeigen bei
/// Sekunde s nach Objekt-Anfang dasselbe Bild (<see cref="FrameAt"/>), das GIF läuft in Schleife.
/// </summary>
public sealed record GifAnimation(string Folder, double Fps, int Count)
{
    /// <summary>Datei von Bild i (0-basiert).</summary>
    public string FramePath(int index) => Path.Combine(Folder, (index + 1).ToString("0000", CultureInfo.InvariantCulture) + ".png");

    /// <summary>Welches Bild bei <paramref name="seconds"/> Sekunden nach dem Anfang zu sehen ist.</summary>
    public int FrameAt(double seconds)
    {
        if (Count <= 1 || seconds <= 0)
        {
            return 0;
        }

        var i = (long)Math.Floor(seconds * Fps + 1e-6);
        return (int)(i % Count);
    }
}

/// <summary>Zerlegt GIFs per FFmpeg in Einzelbilder (einmal, liegt dann neben der Bild-Kopie).</summary>
public static class GifFrames
{
    public const double MaxFps = 30;
    public const int MaxFrames = 300;
    public const int MaxSize = 720;

    private const string InfoFile = "info.txt";

    public static bool IsGif(string? path) =>
        path != null && string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);

    public static string FolderFor(string gifPath) => gifPath + ".frames";

    /// <summary>Schon zerlegt? Dann die Bilder, sonst null.</summary>
    public static GifAnimation? Load(string gifPath)
    {
        var folder = FolderFor(gifPath);
        var info = Path.Combine(folder, InfoFile);
        try
        {
            if (!File.Exists(info))
            {
                return null;
            }

            var parts = File.ReadAllText(info).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var fps)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                || fps <= 0 || count <= 0)
            {
                return null;
            }

            var animation = new GifAnimation(folder, fps, count);
            return File.Exists(animation.FramePath(count - 1)) ? animation : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Bildrate aus ffprobe ("100/9"), begrenzt auf 1–30. Unbekannt: 15.</summary>
    public static double ParseFps(string? text)
    {
        text = text?.Trim().Split('\n')[0].Trim();
        double fps = 0;
        if (!string.IsNullOrEmpty(text))
        {
            var p = text.Split('/');
            if (p.Length == 2
                && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
                && b > 0)
            {
                fps = a / b;
            }
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                fps = v;
            }
        }

        if (double.IsNaN(fps) || fps <= 0)
        {
            return 15;
        }

        return Math.Round(Math.Clamp(fps, 1, MaxFps), 3);
    }

    /// <summary>Zerlegt das GIF (falls nötig). Null, wenn es nicht klappt oder nur ein Bild hat.</summary>
    public static async Task<GifAnimation?> ExtractAsync(IProcessRunner runner, FfmpegPaths paths, string gifPath, CancellationToken cancellationToken = default)
    {
        if (Load(gifPath) is { } existing)
        {
            return existing;
        }

        double fps = 15;
        if (paths.Ffprobe is { } probe)
        {
            var r = await runner.RunAsync(
                probe,
                new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=avg_frame_rate", "-of", "csv=p=0", gifPath },
                TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);
            if (r.ExitCode == 0)
            {
                fps = ParseFps(r.StdOut);
            }
        }

        var folder = FolderFor(gifPath);
        var temp = folder + ".tmp" + Guid.NewGuid().ToString("N")[..6];
        Directory.CreateDirectory(temp);
        try
        {
            var f = fps.ToString("0.###", CultureInfo.InvariantCulture);
            // Gleichmäßige Bildrate, große GIFs verkleinert (Speicher), Transparenz bleibt (rgba).
            var filter = $"fps={f},scale='if(gt(iw,ih),min({MaxSize},iw),-2)':'if(gt(iw,ih),-2,min({MaxSize},ih))',format=rgba";
            var result = await runner.RunAsync(
                paths.Ffmpeg,
                new[]
                {
                    "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", gifPath,
                    "-vf", filter, "-frames:v", MaxFrames.ToString(CultureInfo.InvariantCulture),
                    "-start_number", "1", Path.Combine(temp, "%04d.png"),
                },
                TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);

            var count = Directory.GetFiles(temp, "*.png").Length;
            if (result.ExitCode != 0 || count < 2)
            {
                Directory.Delete(temp, true);
                return null;
            }

            File.WriteAllText(Path.Combine(temp, InfoFile), $"{f} {count.ToString(CultureInfo.InvariantCulture)}");
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            Directory.Move(temp, folder);
            return Load(gifPath);
        }
        catch
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }

            throw;
        }
    }
}
