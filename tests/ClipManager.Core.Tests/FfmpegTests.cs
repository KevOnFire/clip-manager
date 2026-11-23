using ClipManager.Core.Media;

namespace ClipManager.Core.Tests;

public class FfmpegTests
{
    private const string EncodersOutput = """
Encoders:
 V..... = Video
 A..... = Audio
 ------
 V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
 V....D h264_amf             AMD AMF H.264 Encoder (codec h264)
 V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
 A....D aac                  AAC (Advanced Audio Coding)
""";

    [Fact]
    public void ParseVersion_ReadsFirstLine()
    {
        Assert.Equal("7.1-full_build-www.gyan.dev", FfmpegProbe.ParseVersion("ffmpeg version 7.1-full_build-www.gyan.dev Copyright (c) 2000-2024\nbuilt with gcc"));
        Assert.Equal("N-117000-gabc", FfmpegProbe.ParseVersion("ffmpeg version N-117000-gabc Copyright"));
        Assert.Null(FfmpegProbe.ParseVersion("irgendwas anderes"));
    }

    [Fact]
    public void ParseEncoders_ReadsNamesAfterHeader()
    {
        var encoders = FfmpegProbe.ParseEncoders(EncodersOutput.Replace("\n", "\r\n"));
        Assert.Contains("libx264", encoders);
        Assert.Contains("h264_nvenc", encoders);
        Assert.Contains("h264_amf", encoders);
        Assert.Contains("aac", encoders);
        Assert.DoesNotContain("=", encoders);
        Assert.Equal(4, encoders.Count);
    }

    [Fact]
    public void Locate_PrefersOverrideThenAppFolderThenPath()
    {
        var existing = new HashSet<string>
        {
            Path.Combine("app", "ffmpeg", "ffmpeg.exe"),
            Path.Combine("app", "ffmpeg", "ffprobe.exe"),
            Path.Combine("tools", "ffmpeg.exe"),
            "custom.exe",
        };
        bool Exists(string p) => existing.Contains(p);

        Assert.Equal("custom.exe", FfmpegLocator.Locate("custom.exe", "app", "tools", Exists, windows: true)!.Ffmpeg);

        var fromApp = FfmpegLocator.Locate(null, "app", "tools", Exists, windows: true)!;
        Assert.Equal(Path.Combine("app", "ffmpeg", "ffmpeg.exe"), fromApp.Ffmpeg);
        Assert.Equal(Path.Combine("app", "ffmpeg", "ffprobe.exe"), fromApp.Ffprobe);

        var fromPath = FfmpegLocator.Locate("fehlt.exe", "anderswo", "x;\"tools\"", Exists, windows: true)!;
        Assert.Equal(Path.Combine("tools", "ffmpeg.exe"), fromPath.Ffmpeg);
        Assert.Null(fromPath.Ffprobe);

        Assert.Null(FfmpegLocator.Locate(null, "anderswo", null, Exists, windows: true));
    }

    [Fact]
    public async Task Check_ReportsVersionAndWorkingEncoders()
    {
        var runner = new FakeRunner(args =>
        {
            if (args.Contains("-version")) return new ProcessResult(0, "ffmpeg version 7.1 Copyright", "");
            if (args.Contains("-encoders")) return new ProcessResult(0, EncodersOutput, "");
            if (args.Contains("h264_nvenc")) return new ProcessResult(0, "", "");
            return new ProcessResult(1, "", "No AMF device found");
        });

        var status = await new FfmpegProbe(runner).CheckAsync(new FfmpegPaths("ffmpeg.exe", null));

        Assert.True(status.Found);
        Assert.Equal("7.1", status.Version);
        Assert.Equal(GpuEncoderState.Available, status.Nvidia);
        Assert.Equal(GpuEncoderState.Unavailable, status.Amd);
    }

    [Fact]
    public async Task Check_EncoderMissingFromBuild_IsNotTested()
    {
        var runner = new FakeRunner(args =>
        {
            if (args.Contains("-version")) return new ProcessResult(0, "ffmpeg version 7.1", "");
            if (args.Contains("-encoders")) return new ProcessResult(0, " ------\n V....D libx264  x264", "");
            throw new InvalidOperationException("darf nicht aufgerufen werden");
        });

        var status = await new FfmpegProbe(runner).CheckAsync(new FfmpegPaths("ffmpeg.exe", null));

        Assert.Equal(GpuEncoderState.NotInBuild, status.Nvidia);
        Assert.Equal(GpuEncoderState.NotInBuild, status.Amd);
    }

    [Fact]
    public async Task Check_NotFoundOrNotStartable()
    {
        var notFound = await new FfmpegProbe(new FakeRunner(_ => throw new InvalidOperationException())).CheckAsync(null);
        Assert.False(notFound.Found);

        var broken = await new FfmpegProbe(new FakeRunner(_ => throw new System.ComponentModel.Win32Exception("nicht gefunden")))
            .CheckAsync(new FfmpegPaths("ffmpeg.exe", null));
        Assert.False(broken.Found);
        Assert.Contains("nicht gefunden", broken.Error);
    }

    private sealed class FakeRunner : IProcessRunner
    {
        private readonly Func<IReadOnlyList<string>, ProcessResult> _respond;

        public FakeRunner(Func<IReadOnlyList<string>, ProcessResult> respond) => _respond = respond;

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(_respond(arguments));
    }
}
