using ClipManager.Core.Editing;
using ClipManager.Core.Export;
using ClipManager.Core.Media;

namespace ClipManager.Core.Tests;

public class ExportTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void VideoKbps_MatchesExampleFromPlan()
    {
        // 30 s, 10 MB, Audio 128 → ca. 2.460 kbit/s (siehe CLAUDE.md)
        Assert.InRange(ExportPlanner.VideoKbps(10, S(30), 128), 2_450, 2_470);
    }

    [Fact]
    public void Plan_ShortClipBigLimit_KeepsOriginal()
    {
        var plan = ExportPlanner.Plan(50, S(30), 1440, 60);
        Assert.Equal(1440, plan.Height);
        Assert.Equal(60, plan.Fps);
        Assert.Equal(ExportPlanner.VideoKbps(50, S(30), 128), plan.VideoKbps);
        Assert.False(plan.TooLongWarning);

        // Bei riesigem Limit auf ein sinnvolles Maximum begrenzt
        Assert.Equal(ExportPlanner.MaxUsefulKbps(1440), ExportPlanner.Plan(500, S(30), 1440, 60).VideoKbps);
    }

    [Fact]
    public void Plan_TenMbThirtySeconds_720p60()
    {
        var plan = ExportPlanner.Plan(10, S(30), 1080, 60);
        Assert.Equal(720, plan.Height);
        Assert.Equal(60, plan.Fps);
        Assert.Equal(128, plan.AudioKbps);
        Assert.Equal(10L * 1024 * 1024, plan.TargetBytes);
    }

    [Fact]
    public void Plan_TenMbOneMinute_720p30()
    {
        var plan = ExportPlanner.Plan(10, S(60), 1080, 60);
        Assert.Equal(720, plan.Height);
        Assert.Equal(30, plan.Fps);
        Assert.Equal(96, plan.AudioKbps); // knapp → Ton spart mit
    }

    [Fact]
    public void Plan_VeryLong_480pWithWarning()
    {
        var plan = ExportPlanner.Plan(10, S(300), 1080, 60);
        Assert.Equal(480, plan.Height);
        Assert.Equal(30, plan.Fps);
        Assert.True(plan.TooLongWarning);
        Assert.True(plan.VideoKbps >= 150);
    }

    [Fact]
    public void Plan_NeverUpscales()
    {
        var plan = ExportPlanner.Plan(500, S(10), 480, 30);
        Assert.Equal(480, plan.Height);
        Assert.Equal(30, plan.Fps);
    }

    [Fact]
    public void Plan_SizeMath_StaysUnderLimit()
    {
        foreach (var seconds in new[] { 5.0, 20, 45, 60, 120 })
        {
            var plan = ExportPlanner.Plan(10, S(seconds), 1080, 60);
            var expectedBytes = (plan.VideoKbps + plan.AudioKbps) * 1000.0 / 8 * seconds;
            Assert.True(expectedBytes < plan.TargetBytes, $"{seconds}s: {expectedBytes} > {plan.TargetBytes}");
        }
    }

    [Fact]
    public void ReduceAfterOvershoot_GoesBelowRatio()
    {
        var next = ExportPlanner.ReduceAfterOvershoot(2000, 11_000_000, 10_000_000);
        Assert.True(next < 2000 * 10.0 / 11);
    }

    [Theory]
    [InlineData(0, new int[0])]
    [InlineData(1, new[] { 0 })]
    [InlineData(2, new[] { 1 })]
    [InlineData(5, new[] { 1, 2, 3, 4 })]
    public void DefaultAudioTracks_SkipMixWhenSplitTracksExist(int count, int[] expected) =>
        Assert.Equal(expected, ExportPlanner.DefaultAudioTracks(count));

    [Fact]
    public void Filter_CutsAndMixes()
    {
        var plan = new ExportPlan(2000, 128, 720, 30, 1, false);
        var ranges = new[] { new TimeRange(S(2), S(4)), new TimeRange(S(6), S(7.5)) };
        var filter = ExportCommand.BuildFilter(ranges, S(2), ExportPlanner.DefaultMix(3), plan, 1080, 60);
        Assert.Contains("[0:v]trim=start=0:end=2,setpts=PTS-STARTPTS[v0]", filter);
        Assert.Contains("[0:v]trim=start=4:end=5.5,setpts=PTS-STARTPTS[v1]", filter);
        Assert.Contains("[v0][v1]concat=n=2:v=1:a=0[vc]", filter);
        Assert.Contains("[vc]scale=-2:720:flags=lanczos,fps=30[v]", filter);
        Assert.Contains("[0:a:2]asplit=2[s2_0][s2_1];", filter);
        Assert.Contains("[s2_1]atrim=start=4:end=5.5", filter);
        Assert.Contains("[t1][t2]amix=inputs=2:duration=longest:normalize=0[a]", filter);
    }

    [Fact]
    public void Filter_SingleRange_NoScaling()
    {
        var plan = new ExportPlan(9000, 128, 1080, 60, 1, false);
        var filter = ExportCommand.BuildFilter(new[] { new TimeRange(S(0), S(10)) }, S(0), ExportPlanner.DefaultMix(1), plan, 1080, 60);
        Assert.Contains("[v0]null[v]", filter);
        Assert.Contains("[t0]anull[a]", filter);
        Assert.DoesNotContain("concat", filter);
    }

    [Fact]
    public void Command_UsesSeekAndEncoder()
    {
        var plan = new ExportPlan(2000, 128, 720, 60, 1, false);
        var args = ExportCommand.Build("in.mp4", "out.mp4", new[] { new TimeRange(S(5), S(20)) }, ExportPlanner.DefaultMix(2), plan, VideoEncoder.Nvidia, 1080, 60).ToList();
        Assert.Equal("5", args[args.IndexOf("-ss") + 1]);
        Assert.Equal("15", args[args.IndexOf("-t") + 1]);
        Assert.Equal("h264_nvenc", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("2000k", args[args.IndexOf("-b:v") + 1]);
        Assert.Equal("out.mp4", args[^1]);
    }

    [Fact]
    public void Command_NoAudioTracks_NoAudioMap()
    {
        var plan = new ExportPlan(2000, 128, 720, 60, 1, false);
        var args = ExportCommand.Build("in.mp4", "out.mp4", new[] { new TimeRange(S(0), S(2)) }, Array.Empty<TrackEdit>(), plan, VideoEncoder.Cpu, 720, 60);
        Assert.DoesNotContain("[a]", args);
        Assert.Contains("libx264", args);
    }

    [Fact]
    public void GainFilter_VolumeAndMutedRanges()
    {
        var edit = new TrackEdit(3, true, 1.5, new[] { new TimeRange(S(1), S(3)), new TimeRange(S(8), S(9.25)) });
        Assert.Equal("volume=1.5,asetnsamples=n=64:p=0,volume=0:enable='between(t,0,1)+between(t,5.994,7.25)',", ExportCommand.TrackGainFilter(edit, S(2)));
        Assert.Equal(string.Empty, ExportCommand.TrackGainFilter(new TrackEdit(0, true, 1, Array.Empty<TimeRange>()), S(0)));
    }

    [Fact]
    public async Task RealExport_MutedRangeIsSilent()
    {
        var ffmpeg = FfmpegLocator.Locate(null, Environment.GetEnvironmentVariable("CLIPMANAGER_FFMPEG_DIR") ?? "-", Environment.GetEnvironmentVariable("PATH"));
        if (ffmpeg?.Ffprobe == null)
        {
            return;
        }

        using var dir = new TempDir();
        var runner = new ProcessRunner();
        var video = Path.Combine(dir.Path, "clip.mp4");
        var make = await runner.RunAsync(
            ffmpeg.Ffmpeg,
            new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=s=320x240:r=30:d=8",
                "-f", "lavfi", "-i", "sine=f=440:d=8",
                "-f", "lavfi", "-i", "sine=f=880:d=8",
                "-map", "0", "-map", "1", "-map", "2",
                "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-y", video,
            },
            TimeSpan.FromSeconds(60));
        Assert.True(make.ExitCode == 0, make.StdErr);

        // Spur 2 komplett aus, Spur 1 von 2–5 s stumm, Teil 3–4 rausgeschnitten → Ausgabe 1–2 s laut, 2–3 und 4–5 stumm.
        var timeline = new EditTimeline(S(8));
        timeline.InitTracks(2, new[] { 0, 1 });
        timeline.SetIn(S(1));
        timeline.SetOut(S(5));
        timeline.RemoveRange(S(3), S(4));
        timeline.Split(S(2));
        timeline.ToggleTrackMuteAt(0, S(2.5));
        timeline.ToggleTrackMuteAt(0, S(4.5));
        timeline.SetTrackEnabled(1, false);
        Assert.Equal(new[] { new TimeRange(S(2), S(3)), new TimeRange(S(4), S(5)) }, timeline.Tracks[0].Muted);

        var output = Path.Combine(dir.Path, "out.mp4");
        var result = await new Exporter().RunAsync(
            new ExportRequest(ffmpeg.Ffmpeg, video, output, timeline.KeptRanges, timeline.AudibleTracks.ToList(), 240, 30, VideoEncoder.Cpu, null),
            null,
            CancellationToken.None);
        Assert.True(result.Success, result.Error);

        async Task<double> MaxVolume(double from, double to)
        {
            var r = await runner.RunAsync(
                ffmpeg.Ffmpeg,
                new[] { "-hide_banner", "-ss", from.ToString(System.Globalization.CultureInfo.InvariantCulture), "-t", (to - from).ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", output, "-af", "volumedetect", "-vn", "-f", "null", "-" },
                TimeSpan.FromSeconds(30));
            var line = r.StdErr.Split('\n').First(l => l.Contains("max_volume"));
            var value = line[(line.IndexOf("max_volume:") + 11)..].Replace("dB", string.Empty).Trim();
            return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        var start = await MaxVolume(0.1, 0.9);
        Assert.True(start > -30, $"Anfang sollte laut sein: {start} dB");
        var silent = await MaxVolume(1.15, 2.85);
        Assert.True(silent < -60, $"Stumme Stellen sollten still sein: {silent} dB");
    }

    [Fact]
    public void GainFilter_LeiserGemachteStelle()
    {
        var edit = new TrackEdit(0, true, 1, Array.Empty<TimeRange>()) { Ducked = new[] { new DuckRange(new TimeRange(S(3), S(5)), -12) } };
        Assert.Equal(
            "asetnsamples=n=64:p=0,volume='pow(10,-12*clip(min((t-1)/0.3,(3-t)/0.3),0,1)/20)':eval=frame,",
            ExportCommand.TrackGainFilter(edit, S(2)));
    }

    [Fact]
    public async Task RealExport_LeiserGemachteStelleIstLeiser()
    {
        var ffmpeg = FfmpegLocator.Locate(null, Environment.GetEnvironmentVariable("CLIPMANAGER_FFMPEG_DIR") ?? "-", Environment.GetEnvironmentVariable("PATH"));
        if (ffmpeg?.Ffprobe == null)
        {
            return;
        }

        using var dir = new TempDir();
        var runner = new ProcessRunner();
        var video = Path.Combine(dir.Path, "clip.mp4");
        var make = await runner.RunAsync(
            ffmpeg.Ffmpeg,
            new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=s=320x240:r=30:d=6",
                "-f", "lavfi", "-i", "sine=f=440:d=6",
                "-map", "0", "-map", "1",
                "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-y", video,
            },
            TimeSpan.FromSeconds(60));
        Assert.True(make.ExitCode == 0, make.StdErr);

        var timeline = new EditTimeline(S(6));
        timeline.InitTracks(1, new[] { 0 });
        Assert.True(timeline.DuckTrackRange(0, S(2), S(4), -20));
        var output = Path.Combine(dir.Path, "out.mp4");
        var result = await new Exporter().RunAsync(
            new ExportRequest(ffmpeg.Ffmpeg, video, output, timeline.KeptRanges, timeline.AudibleTracks.ToList(), 240, 30, VideoEncoder.Cpu, null),
            null,
            CancellationToken.None);
        Assert.True(result.Success, result.Error);

        async Task<double> MaxVolume(double from, double to)
        {
            var r = await runner.RunAsync(
                ffmpeg.Ffmpeg,
                new[] { "-hide_banner", "-ss", from.ToString(System.Globalization.CultureInfo.InvariantCulture), "-t", (to - from).ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", output, "-af", "volumedetect", "-vn", "-f", "null", "-" },
                TimeSpan.FromSeconds(30));
            var line = r.StdErr.Split('\n').First(l => l.Contains("max_volume"));
            var value = line[(line.IndexOf("max_volume:") + 11)..].Replace("dB", string.Empty).Trim();
            return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        var normal = await MaxVolume(0.5, 1.5);
        var quiet = await MaxVolume(2.5, 3.5);
        var after = await MaxVolume(4.5, 5.5);
        Assert.InRange(normal - quiet, 17, 23);
        Assert.InRange(Math.Abs(normal - after), 0, 1.5);
    }

    [Theory]
    [InlineData("out_time_us=1500000", 1.5)]
    [InlineData("out_time_us=N/A", null)]
    [InlineData("frame=12", null)]
    public void ParseProgress(string line, double? seconds) =>
        Assert.Equal(seconds, ExportCommand.ParseProgress(line)?.TotalSeconds);

    [Fact]
    public void OutputPath_IsUnique()
    {
        using var dir = new TempDir();
        var first = ExportCommand.OutputPath(dir.Path, @"F:\OBS Clips\Replay 1.mp4".Replace('\\', Path.DirectorySeparatorChar), "Discord");
        Assert.EndsWith("Replay 1 (Discord).mp4", first);
        File.WriteAllText(first, "x");
        Assert.EndsWith("Replay 1 (Discord 2).mp4", ExportCommand.OutputPath(dir.Path, "Replay 1.mp4", "Discord"));
    }

    [Fact]
    public async Task RealExport_CutsMixesAndFitsLimit()
    {
        var ffmpeg = FfmpegLocator.Locate(null, Environment.GetEnvironmentVariable("CLIPMANAGER_FFMPEG_DIR") ?? "-", Environment.GetEnvironmentVariable("PATH"));
        if (ffmpeg?.Ffprobe == null)
        {
            return; // Kein ffmpeg auf diesem Rechner.
        }

        using var dir = new TempDir();
        var runner = new ProcessRunner();
        var video = Path.Combine(dir.Path, "clip.mp4");
        var make = await runner.RunAsync(
            ffmpeg.Ffmpeg,
            new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=60:d=10",
                "-f", "lavfi", "-i", "sine=f=440:d=10",
                "-f", "lavfi", "-i", "sine=f=660:d=10",
                "-f", "lavfi", "-i", "sine=f=880:d=10",
                "-map", "0", "-map", "1", "-map", "2", "-map", "3",
                "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-y", video,
            },
            TimeSpan.FromSeconds(60));
        Assert.True(make.ExitCode == 0, make.StdErr);

        var timeline = new EditTimeline(S(10));
        timeline.SetIn(S(1));
        timeline.RemoveRange(S(4), S(6));
        timeline.SetOut(S(9));
        var output = Path.Combine(dir.Path, "out", "clip (Discord).mp4");
        var reports = new List<ExportProgress>();
        var result = await new Exporter().RunAsync(
            new ExportRequest(ffmpeg.Ffmpeg, video, output, timeline.KeptRanges, ExportPlanner.DefaultMix(3), 720, 60, VideoEncoder.Cpu, 1),
            new SyncProgress(reports),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(output));
        Assert.True(result.Bytes <= 1024 * 1024);
        Assert.NotEmpty(reports);

        var info = await new MediaInfoReader(runner).ReadAsync(ffmpeg.Ffprobe, output);
        Assert.NotNull(info);
        Assert.Equal(6, info!.Duration.TotalSeconds, 0); // 1–4 und 6–9
        Assert.Single(info.AudioTracks);
        Assert.False(File.Exists(Path.Combine(dir.Path, "out", "clip (Discord).part.mp4")));
    }

    private sealed class SyncProgress(List<ExportProgress> list) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => list.Add(value);
    }
}
