using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClipManager.Core.Library;

namespace ClipManager.Core.Media;

/// <summary>
/// Erstellt den "Filmstreifen" für die Zeitleiste im Editor: ein Bild mit vielen kleinen
/// Vorschaubildern nebeneinander, gleichmäßig über den Clip verteilt.
/// Es werden nur Keyframes dekodiert, damit das auch bei langen Clips schnell geht.
/// </summary>
public sealed class FilmstripCache
{
    public const int Frames = 16;
    public const int FrameHeight = 72;

    private readonly string _cacheDirectory;
    private readonly IProcessRunner _runner;

    public FilmstripCache(string cacheDirectory, IProcessRunner runner)
    {
        _cacheDirectory = cacheDirectory;
        _runner = runner;
    }

    public string PathFor(ClipFile clip)
    {
        var key = string.Join('|', "strip", Frames, FrameHeight, clip.Path.ToUpperInvariant(), clip.SizeBytes, clip.ModifiedUtc.Ticks);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return System.IO.Path.Combine(_cacheDirectory, hash + ".strip.jpg");
    }

    /// <summary>
    /// FFmpeg-Argumente. Die Bildrate ist minimal höher als nötig (Faktor 0,95), sonst fehlt
    /// am Ende manchmal das letzte Bild und der Streifen hat rechts eine schwarze Lücke.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string input, TimeSpan duration, string output)
    {
        var seconds = Math.Max(0.5, duration.TotalSeconds);
        var rate = (Frames / (seconds * 0.95)).ToString("0.######", CultureInfo.InvariantCulture);
        return new[]
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-skip_frame", "nokey", "-i", input, "-an", "-sn",
            "-vf", $"fps={rate},scale=-2:{FrameHeight},tile={Frames}x1",
            "-frames:v", "1", "-q:v", "5",
            "-y", output,
        };
    }

    public async Task<string?> GetOrCreateAsync(string ffmpegPath, ClipFile clip, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var target = PathFor(clip);
        if (File.Exists(target))
        {
            return target;
        }

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var partial = target[..^4] + ".part.jpg";
            var result = await _runner.RunAsync(
                ffmpegPath,
                BuildArguments(clip.Path, duration, partial),
                TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0 || !File.Exists(partial))
            {
                Log.Error($"Filmstreifen für \"{clip.Path}\" fehlgeschlagen (Exit {result.ExitCode}): {result.StdErr.Trim()}");
                return null;
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Filmstreifen für \"{clip.Path}\" fehlgeschlagen", ex);
            return null;
        }
    }
}
