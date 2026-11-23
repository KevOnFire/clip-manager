using ClipManager.Core.Library;
using ClipManager.Core.Media;

namespace ClipManager.Core.Tests;

/// <summary>
/// Läuft nur, wenn ein echtes ffmpeg gefunden wird (PATH oder Umgebungsvariable CLIPMANAGER_FFMPEG_DIR).
/// Prüft den kompletten Weg: Testvideo erzeugen, mit ffprobe lesen, Vorschaubild erstellen.
/// </summary>
public class RealFfmpegTests
{
    private static FfmpegPaths? Find() =>
        FfmpegLocator.Locate(null, Environment.GetEnvironmentVariable("CLIPMANAGER_FFMPEG_DIR") ?? "-", Environment.GetEnvironmentVariable("PATH"));

    [Fact]
    public async Task ProbeAndThumbnail_WorkWithRealFfmpeg()
    {
        var ffmpeg = Find();
        if (ffmpeg?.Ffprobe == null)
        {
            return; // Kein ffmpeg auf diesem Rechner.
        }

        using var dir = new TempDir();
        var runner = new ProcessRunner();
        var video = Path.Combine(dir.Path, "Replay 2026-11-23 21-00-00.mp4");
        var make = await runner.RunAsync(
            ffmpeg.Ffmpeg,
            new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=60:d=4",
                "-f", "lavfi", "-i", "sine=f=440:d=4",
                "-f", "lavfi", "-i", "sine=f=880:d=4",
                "-map", "0", "-map", "1", "-map", "2",
                "-c:v", "mpeg4", "-c:a", "aac", "-y", video,
            },
            TimeSpan.FromSeconds(60));
        Assert.True(make.ExitCode == 0, make.StdErr);

        var info = await new MediaInfoReader(runner).ReadAsync(ffmpeg.Ffprobe, video);
        Assert.NotNull(info);
        Assert.Equal(4, info!.Duration.TotalSeconds, 0);
        Assert.Equal(720, info.Height);
        Assert.Equal(60, info.Fps);
        Assert.Equal(2, info.AudioTracks.Count);

        var file = new FileInfo(video);
        var clip = new ClipFile(file.FullName, dir.Path, file.Length, file.LastWriteTimeUtc);
        var cache = new ThumbnailCache(Path.Combine(dir.Path, "thumbs"), runner);
        var thumb = await cache.GetOrCreateAsync(ffmpeg.Ffmpeg, clip, info.Duration);
        Assert.NotNull(thumb);
        Assert.True(new FileInfo(thumb!).Length > 1000);
    }
}
