using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClipManager.Core.Library;

namespace ClipManager.Core.Media;

/// <summary>
/// Erstellt Vorschaubilder mit FFmpeg und legt sie in einem Cache-Ordner ab.
/// Ändert sich die Datei (Größe oder Datum), wird ein neues Bild erzeugt.
/// </summary>
public sealed class ThumbnailCache
{
    public const int Width = 480;

    private readonly string _cacheDirectory;
    private readonly IProcessRunner _runner;
    private readonly SemaphoreSlim _parallel;

    public ThumbnailCache(string cacheDirectory, IProcessRunner runner, int maxParallel = 3)
    {
        _cacheDirectory = cacheDirectory;
        _runner = runner;
        _parallel = new SemaphoreSlim(maxParallel);
    }

    public string PathFor(ClipFile clip)
    {
        var key = string.Join('|', clip.Path.ToUpperInvariant(), clip.SizeBytes, clip.ModifiedUtc.Ticks);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return System.IO.Path.Combine(_cacheDirectory, hash + ".jpg");
    }

    /// <summary>Zeitpunkt für das Vorschaubild: etwas nach dem Anfang, damit kein schwarzes Bild kommt.</summary>
    public static TimeSpan SeekPosition(TimeSpan? duration)
    {
        if (duration is not { } d || d <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(Math.Min(2.0, d.TotalSeconds / 2));
    }

    /// <summary>Gibt den Pfad des Vorschaubilds zurück oder null, wenn es nicht erstellt werden konnte.</summary>
    public async Task<string?> GetOrCreateAsync(string ffmpegPath, ClipFile clip, TimeSpan? duration, CancellationToken cancellationToken = default)
    {
        var target = PathFor(clip);
        if (File.Exists(target))
        {
            return target;
        }

        await _parallel.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(target))
            {
                return target;
            }

            Directory.CreateDirectory(_cacheDirectory);
            var partial = target[..^4] + ".part.jpg";
            var seek = SeekPosition(duration).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

            var result = await _runner.RunAsync(
                ffmpegPath,
                new[]
                {
                    "-hide_banner", "-nostdin", "-loglevel", "error",
                    "-ss", seek, "-i", clip.Path,
                    "-frames:v", "1", "-vf", $"scale={Width}:-2", "-q:v", "4",
                    "-y", partial,
                },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0 || !File.Exists(partial))
            {
                Log.Error($"Vorschaubild für \"{clip.Path}\" fehlgeschlagen (Exit {result.ExitCode}): {result.StdErr.Trim()}");
                return null;
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Vorschaubild für \"{clip.Path}\" fehlgeschlagen", ex);
            return null;
        }
        finally
        {
            _parallel.Release();
        }
    }
}
