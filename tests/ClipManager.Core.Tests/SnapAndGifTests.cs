using ClipManager.Core.Editing;
using ClipManager.Core.Export;
using ClipManager.Core.Media;

namespace ClipManager.Core.Tests;

public class OverlaySnapTests
{
    private static OverlayItem Item(string id, double start, double end) => new() { Id = id, Kind = OverlayKind.Box, Start = start, End = end };

    [Fact]
    public void Targets_AreOtherEdges_PlusClipEndsAndPlayhead()
    {
        var items = new[] { Item("a", 1, 3), Item("b", 2, 5) };
        var t = OverlaySnap.Targets(items, "a", 10, 4.5);
        Assert.Equal(new[] { 0d, 10, 2, 5, 4.5 }, t);
    }

    [Fact]
    public void Move_SnapsCloserEdge_KeepsLength()
    {
        var targets = new List<double> { 0, 10, 2, 5 };
        // Anfang 2,08 liegt nah an 2 → rastet dort ein
        Assert.Equal((2d, 4d, (double?)2), OverlaySnap.Move(2.08, 4.08, targets, 0.1, 10));
        // Ende 4,95 liegt nah an 5 → ganzer Balken rückt nach
        var (s, e, line) = OverlaySnap.Move(1.95, 4.95, targets, 0.1, 10);
        Assert.Equal(2, s, 6);
        Assert.Equal(5, e, 6);
        Assert.Equal(5, line);
        // Nichts in der Nähe
        Assert.Equal((3d, 4d, (double?)null), OverlaySnap.Move(3, 4, targets, 0.1, 10));
    }

    [Fact]
    public void Edge_SnapsOnlyWithinLimits()
    {
        var targets = new List<double> { 0, 10, 2, 5 };
        Assert.Equal((5d, (double?)5), OverlaySnap.Edge(4.93, targets, 0.1, 3, 10));
        // 2 wäre kürzer als die Mindestlänge erlaubt → kein Einrasten
        Assert.Equal((2.05, (double?)null), OverlaySnap.Edge(2.05, targets, 0.1, 2.5, 10));
    }
}

public class GifFramesTests
{
    [Theory]
    [InlineData("100/9", 11.111)]
    [InlineData("10/1\n", 10)]
    [InlineData("50/1", 30)]
    [InlineData("0/0", 15)]
    [InlineData("", 15)]
    public void ParseFps(string text, double expected) => Assert.Equal(expected, GifFrames.ParseFps(text), 3);

    [Fact]
    public void FrameAt_LoopsAndStartsAtZero()
    {
        var a = new GifAnimation("x", 5, 3);
        Assert.Equal(0, a.FrameAt(-1));
        Assert.Equal(0, a.FrameAt(0.1));
        Assert.Equal(1, a.FrameAt(0.2));
        Assert.Equal(2, a.FrameAt(0.5));
        Assert.Equal(0, a.FrameAt(0.6));
        Assert.EndsWith("0003.png", a.FramePath(2));
    }

    [Fact]
    public async Task RealFfmpeg_ExtractsGif_AndExportAnimatesFromObjectStart()
    {
        var ffmpeg = FfmpegLocator.Locate(null, Environment.GetEnvironmentVariable("CLIPMANAGER_FFMPEG_DIR") ?? "-", Environment.GetEnvironmentVariable("PATH"));
        if (ffmpeg?.Ffprobe == null)
        {
            return; // Kein ffmpeg auf diesem Rechner.
        }

        using var dir = new TempDir();
        var runner = new ProcessRunner();
        async Task Run(params string[] args)
        {
            var r = await runner.RunAsync(ffmpeg.Ffmpeg, args, TimeSpan.FromSeconds(60));
            Assert.True(r.ExitCode == 0, r.StdErr);
        }

        // GIF mit 3 Bildern (rot, grün, blau), 5 Bilder pro Sekunde
        var gif = Path.Combine(dir.Path, "bunt.gif");
        await Run("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            "color=s=40x40:r=5:d=0.6,format=rgb24,geq=r='if(lt(N\\,1)\\,255\\,0)':g='if(eq(N\\,1)\\,255\\,0)':b='if(gt(N\\,1)\\,255\\,0)'",
            "-y", gif);

        var anim = await GifFrames.ExtractAsync(runner, ffmpeg, gif);
        Assert.NotNull(anim);
        Assert.Equal(3, anim!.Count);
        Assert.Equal(5, anim.Fps, 3);
        Assert.Null(GifFrames.Load(Path.Combine(dir.Path, "fehlt.gif")));
        Assert.Equal(anim, GifFrames.Load(gif));

        // Frames vergrößert als Muster ablegen (wie die Oberfläche), Video schwarz
        for (var i = 0; i < anim.Count; i++)
        {
            await Run("-hide_banner", "-loglevel", "error", "-i", anim.FramePath(i), "-vf", "scale=80:80", "-y", Path.Combine(dir.Path, $"g_{i + 1:0000}.png"));
        }

        var video = Path.Combine(dir.Path, "clip.mp4");
        await Run("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=320x240:r=30:d=3", "-c:v", "mpeg4", "-q:v", "2", "-y", video);
        var output = Path.Combine(dir.Path, "out.mp4");
        var result = await new Exporter().RunAsync(
            new ExportRequest(
                ffmpeg.Ffmpeg,
                video,
                output,
                new[] { new TimeRange(TimeSpan.Zero, TimeSpan.FromSeconds(3)) },
                Array.Empty<TrackEdit>(),
                240,
                30,
                VideoEncoder.Cpu,
                null,
                new[] { new OverlayLayer(Path.Combine(dir.Path, "g_%04d.png"), new PixelRect(100, 80, 80, 80), 1, 2.5, 0, anim.Fps) }),
            null,
            CancellationToken.None);
        Assert.True(result.Success, result.Error);

        async Task<byte[]> Pixel(double t)
        {
            var raw = Path.Combine(dir.Path, $"p{t:0.00}.rgb");
            await Run("-hide_banner", "-loglevel", "error", "-ss", t.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", output,
                "-vf", "crop=2:2:139:119,scale=1:1", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-y", raw);
            return File.ReadAllBytes(raw);
        }

        static int Dominant(byte[] p) => p[0] > 120 && p[1] < 100 && p[2] < 100 ? 0 : p[1] > 120 && p[0] < 100 && p[2] < 100 ? 1 : p[2] > 120 && p[0] < 100 && p[1] < 100 ? 2 : -1;

        Assert.True((await Pixel(0.5)).All(b => b < 40), "vor dem Start schwarz");
        Assert.Equal(0, Dominant(await Pixel(1.08)));
        Assert.Equal(1, Dominant(await Pixel(1.3)));
        Assert.Equal(2, Dominant(await Pixel(1.5)));
        Assert.Equal(0, Dominant(await Pixel(1.7)));
        Assert.True((await Pixel(2.8)).All(b => b < 40), "nach dem Ende schwarz");
    }
}
