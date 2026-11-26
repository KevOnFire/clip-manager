using System.Text.Json;
using ClipManager.Core.Editing;
using ClipManager.Core.Export;
using ClipManager.Core.Media;

namespace ClipManager.Core.Tests;

public class OverlayTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    private static OverlayItem Box(double start, double end, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        Kind = OverlayKind.Box,
        Start = start,
        End = end,
        X = 0.1,
        Y = 0.1,
        W = 0.2,
        H = 0.2,
        Color = "#111111",
        Fill = true,
    };

    [Fact]
    public void Add_PutsNewItemInFront_AndCountsAsEdited()
    {
        var tl = new EditTimeline(S(30));
        Assert.False(tl.IsEdited);
        var a = tl.AddOverlay(Box(1, 5, "a"));
        var b = tl.AddOverlay(Box(2, 6, "b"));
        Assert.True(tl.IsEdited);
        Assert.Equal(new[] { "a", "b" }, tl.Overlays.Select(o => o.Id));
        Assert.Equal(0, tl.FindOverlay("a")!.Z);
        Assert.Equal(1, tl.FindOverlay("b")!.Z);
        Assert.Equal(new[] { "a", "b" }, tl.OverlaysAt(S(3)).Select(o => o.Id));
        Assert.Equal(new[] { "b" }, tl.OverlaysAt(S(5.5)).Select(o => o.Id));
        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public void Update_Clamps_AndUndoRestores()
    {
        var tl = new EditTimeline(S(10));
        tl.AddOverlay(Box(1, 5, "a"));
        tl.UpdateOverlay("a", o =>
        {
            o.Start = -3;
            o.End = 99;
            o.Rotation = 270;
            o.Blur = 500;
            o.Color = "#ff00aa";
        });
        var item = tl.FindOverlay("a")!;
        Assert.Equal(0, item.Start);
        Assert.Equal(10, item.End);
        Assert.Equal(-90, item.Rotation);
        Assert.Equal(OverlayItem.MaxBlur, item.Blur);
        Assert.Equal("#FF00AA", item.Color);

        Assert.True(tl.Undo());
        Assert.Equal(1, tl.FindOverlay("a")!.Start);
        Assert.True(tl.Undo());
        Assert.Empty(tl.Overlays);
    }

    [Fact]
    public void Dragging_IsOneUndoStep()
    {
        var tl = new EditTimeline(S(10));
        tl.AddOverlay(Box(1, 5, "a"));
        tl.BeginChange();
        for (var i = 1; i <= 5; i++)
        {
            tl.UpdateOverlay("a", o => o.X = 0.1 + i * 0.05, recordUndo: false);
        }

        Assert.Equal(0.35, tl.FindOverlay("a")!.X, 6);
        tl.Undo();
        Assert.Equal(0.1, tl.FindOverlay("a")!.X, 6);
    }

    [Fact]
    public void LayerOrder_MoveAndSet()
    {
        var tl = new EditTimeline(S(10));
        tl.AddOverlay(Box(0, 5, "a"));
        tl.AddOverlay(Box(0, 5, "b"));
        tl.AddOverlay(Box(0, 5, "c"));

        Assert.True(tl.MoveOverlayLayer("a", +1));
        Assert.Equal(new[] { "b", "a", "c" }, tl.Overlays.Select(o => o.Id));
        Assert.False(tl.MoveOverlayLayer("c", +1)); // schon ganz vorne

        // Ebenen-Liste: vorne zuerst
        Assert.True(tl.SetOverlayOrder(new[] { "b", "c", "a" }));
        Assert.Equal(new[] { "a", "c", "b" }, tl.Overlays.Select(o => o.Id));
        Assert.Equal(new[] { 0, 1, 2 }, tl.Overlays.Select(o => o.Z));
        Assert.False(tl.SetOverlayOrder(new[] { "b", "c", "a" }));

        Assert.True(tl.RemoveOverlay("c"));
        Assert.Equal(new[] { "a", "b" }, tl.Overlays.Select(o => o.Id));
    }

    [Fact]
    public void SaveAndLoad_KeepsEverything_ThroughJson()
    {
        var tl = new EditTimeline(S(20));
        tl.AddOverlay(Box(1, 4, "box"));
        tl.AddOverlay(new OverlayItem
        {
            Id = "pen",
            Kind = OverlayKind.Pen,
            Start = 2,
            End = 3,
            X = 0,
            Y = 0,
            W = 1,
            H = 1,
            Rotation = 45, // Stift dreht nicht
            Paths = new() { new() { new[] { 0.1, 0.2 }, new[] { 0.3, 0.4 } } },
        });
        tl.AddOverlay(new OverlayItem { Id = "txt", Kind = OverlayKind.Text, Start = 0, End = 2, W = 0.3, H = 0.1, Text = "Clutch!", Rotation = -10 });

        var json = JsonSerializer.Serialize(tl.ToState());
        var loaded = new EditTimeline(S(20));
        loaded.Load(JsonSerializer.Deserialize<ClipEditState>(json)!);

        Assert.Equal(new[] { "box", "pen", "txt" }, loaded.Overlays.Select(o => o.Id));
        var pen = loaded.FindOverlay("pen")!;
        Assert.Equal(OverlayKind.Pen, pen.Kind);
        Assert.Equal(0, pen.Rotation);
        Assert.Equal(0.4, pen.Paths![0][1][1]);
        Assert.Equal("Clutch!", loaded.FindOverlay("txt")!.Text);
        Assert.Equal(-10, loaded.FindOverlay("txt")!.Rotation);
        Assert.True(loaded.IsEdited);

        // Alte Einträge ohne Objekte
        var old = new EditTimeline(S(20));
        old.Load(JsonSerializer.Deserialize<ClipEditState>("{\"In\":0,\"Out\":20,\"Splits\":[],\"Removed\":[]}")!);
        Assert.Empty(old.Overlays);
        Assert.Null(old.ToState().Overlays);
    }

    [Fact]
    public void Snapshot_IsNotChangedByLaterEdits()
    {
        var tl = new EditTimeline(S(10));
        tl.AddOverlay(Box(1, 5, "a"));
        var state = tl.ToState();
        tl.UpdateOverlay("a", o => o.X = 0.7);
        Assert.Equal(0.1, state.Overlays![0].X, 6);
    }

    [Fact]
    public void Reset_RemovesOverlays()
    {
        var tl = new EditTimeline(S(10));
        tl.AddOverlay(Box(1, 5));
        tl.Reset();
        Assert.Empty(tl.Overlays);
        Assert.False(tl.IsEdited);
    }

    [Fact]
    public void Lanes_StackOverlappingItems()
    {
        var items = new[] { Box(0, 4, "a"), Box(2, 6, "b"), Box(4, 8, "c"), Box(3, 5, "d") };
        var (lanes, count) = OverlayLayout.AssignLanes(items);
        Assert.Equal(0, lanes["a"]);
        Assert.Equal(1, lanes["b"]);
        Assert.Equal(2, lanes["d"]);
        Assert.Equal(0, lanes["c"]); // a ist bei 4 vorbei
        Assert.Equal(3, count);
        Assert.Equal(0, OverlayLayout.AssignLanes(Array.Empty<OverlayItem>()).Count);
    }

    [Fact]
    public void Bounds_EvenPixels_RotatedAndClamped()
    {
        var item = new OverlayItem { Kind = OverlayKind.Blur, X = 0.25, Y = 0.25, W = 0.5, H = 0.5 };
        Assert.Equal(new PixelRect(480, 270, 960, 540), OverlayLayout.Bounds(item, 1920, 1080));

        // 90° gedreht: Breite und Höhe tauschen
        item.Rotation = 90;
        var r = OverlayLayout.Bounds(item, 1920, 1080)!.Value;
        Assert.Equal(540, r.Width);
        Assert.Equal(960, r.Height);
        Assert.Equal(new PixelRect(690, 60, 540, 960), r);
        Assert.All(new[] { r.X, r.Y, r.Width, r.Height }, v => Assert.Equal(0, v % 2));

        // Ganz außerhalb
        Assert.Null(OverlayLayout.Bounds(new OverlayItem { Kind = OverlayKind.Blur, X = 2, Y = 2, W = 0.1, H = 0.1 }, 1920, 1080));
    }

    [Fact]
    public void BlurRadius_ScalesWithHeight_AndFitsArea()
    {
        Assert.Equal(12, OverlayLayout.BlurRadius(12, 540, new PixelRect(0, 0, 400, 400)));
        Assert.Equal(32, OverlayLayout.BlurRadius(12, 1440, new PixelRect(0, 0, 400, 400)));
        Assert.Equal(9, OverlayLayout.BlurRadius(40, 1440, new PixelRect(0, 0, 40, 400)));
    }

    [Fact]
    public void Filter_OverlaysBeforeCuts()
    {
        var plan = new ExportPlan(2000, 128, 1080, 60, 1, false);
        var layers = new[]
        {
            new OverlayLayer("box.png", new PixelRect(100, 50, 200, 100), 6, 8),
            new OverlayLayer("mask.png", new PixelRect(0, 0, 400, 200), 0, 30, BlurRadius: 10),
            new OverlayLayer("weg.png", new PixelRect(0, 0, 40, 20), 25, 30), // außerhalb des Exports
        };
        var ranges = new[] { new TimeRange(S(5), S(10)), new TimeRange(S(12), S(15)) };
        var args = ExportCommand.Build("in.mp4", "out.mp4", ranges, Array.Empty<TrackEdit>(), plan, VideoEncoder.Cpu, 1080, 60, layers).ToList();

        Assert.Equal(3, args.Count(a => a == "-i"));
        Assert.Contains("box.png", args);
        Assert.DoesNotContain("weg.png", args);
        var filter = args[args.IndexOf("-filter_complex") + 1];
        Assert.StartsWith("[0:v][1:v]overlay=100:50:enable='between(t,1,3)'[ol0];", filter);
        Assert.Contains("[ol0]split[o1a][o1b];[o1b]crop=400:200:0:0,boxblur=luma_radius=10:luma_power=2:enable='between(t,0,25)'[o1c];[2:v]format=gray[o1m];[o1c][o1m]alphamerge[o1d];[o1a][o1d]overlay=0:0:enable='between(t,0,25)'[ol1];", filter);
        Assert.Contains("[ol1]split=2[vs0][vs1];[vs0]trim=start=0:end=5", filter);
        Assert.Contains("[vs1]trim=start=7:end=10", filter);
    }

    [Fact]
    public async Task RealFfmpeg_BurnsInBoxAndBlur()
    {
        var ffmpeg = FfmpegLocator.Locate(null, Environment.GetEnvironmentVariable("CLIPMANAGER_FFMPEG_DIR") ?? "-", Environment.GetEnvironmentVariable("PATH"));
        if (ffmpeg == null)
        {
            return; // Kein ffmpeg auf diesem Rechner.
        }

        using var dir = new TempDir();
        var runner = new ProcessRunner();
        var video = Path.Combine(dir.Path, "clip.mp4");
        var box = Path.Combine(dir.Path, "box.png");
        var mask = Path.Combine(dir.Path, "mask.png");
        async Task Make(params string[] args)
        {
            var r = await runner.RunAsync(ffmpeg.Ffmpeg, args, TimeSpan.FromSeconds(60));
            Assert.True(r.ExitCode == 0, r.StdErr);
        }

        await Make("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=3", "-f", "lavfi", "-i", "sine=d=3",
            "-map", "0", "-map", "1", "-c:v", "mpeg4", "-c:a", "aac", "-y", video);
        await Make("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red@0.8:s=100x60,format=rgba", "-frames:v", "1", "-y", box);
        await Make("-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=white:s=120x80", "-frames:v", "1", "-y", mask);

        var output = Path.Combine(dir.Path, "out.mp4");
        var result = await new Exporter().RunAsync(
            new ExportRequest(
                ffmpeg.Ffmpeg,
                video,
                output,
                new[] { new TimeRange(S(0), S(1)), new TimeRange(S(1.5), S(3)) },
                ExportPlanner.DefaultMix(1),
                360,
                30,
                VideoEncoder.Cpu,
                null,
                new[]
                {
                    new OverlayLayer(mask, new PixelRect(200, 100, 120, 80), 0, 3, OverlayLayout.BlurRadius(12, 360, new PixelRect(200, 100, 120, 80))),
                    new OverlayLayer(box, new PixelRect(20, 20, 100, 60), 0.5, 2),
                }),
            null,
            CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.True(new FileInfo(output).Length > 1000);
        var info = await new MediaInfoReader(runner).ReadAsync(ffmpeg.Ffprobe!, output);
        Assert.Equal(2.5, info!.Duration.TotalSeconds, 0);
    }
}

public class OverlayExtraTests
{
    [Fact]
    public void KeepOnly_SetsInAndOutInOneStep()
    {
        var tl = new EditTimeline(TimeSpan.FromSeconds(30));
        Assert.True(tl.KeepOnly(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(4)));
        Assert.Equal(TimeSpan.FromSeconds(4), tl.In);
        Assert.Equal(TimeSpan.FromSeconds(12), tl.Out);
        Assert.False(tl.KeepOnly(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12)));
        tl.Undo();
        Assert.Equal(TimeSpan.Zero, tl.In);
        Assert.Equal(TimeSpan.FromSeconds(30), tl.Out);
    }

    [Fact]
    public void Images_AreCopiedOnce_ByContent()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "Logo.PNG");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3 });
        var store = Path.Combine(dir.Path, "bilder");
        var a = OverlayImages.Import(src, store);
        var b = OverlayImages.Import(src, store);
        Assert.Equal(a, b);
        Assert.EndsWith(".png", a);
        Assert.Single(Directory.GetFiles(store));
        Assert.True(OverlayImages.IsSupported("x.JPG"));
        Assert.False(OverlayImages.IsSupported("x.mp4"));
    }

    [Fact]
    public void CustomColors_NewestFirst_NoDuplicates_Max6()
    {
        var s = new ClipManager.Core.Settings.AppSettings();
        foreach (var c in new[] { "#111111", "#222222", "#333333", "#444444", "#555555", "#666666", "#777777", "#222222" })
        {
            s.RememberColor(c);
        }

        Assert.Equal(new[] { "#222222", "#777777", "#666666", "#555555", "#444444", "#333333" }, s.CustomColors);
    }
}
