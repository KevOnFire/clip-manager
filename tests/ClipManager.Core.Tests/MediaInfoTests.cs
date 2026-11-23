using ClipManager.Core.Library;
using ClipManager.Core.Media;

namespace ClipManager.Core.Tests;

public class MediaInfoTests
{
    private const string ObsJson = """
    {
      "streams": [
        { "index": 0, "codec_type": "video", "width": 2560, "height": 1440, "avg_frame_rate": "60/1", "r_frame_rate": "60/1" },
        { "index": 1, "codec_type": "audio", "channels": 2 },
        { "index": 2, "codec_type": "audio", "channels": 2, "tags": { "title": "Spiel" } },
        { "index": 3, "codec_type": "audio", "channels": 1, "tags": { "title": " " } }
      ],
      "format": { "duration": "46.433000", "size": "123" }
    }
    """;

    [Fact]
    public void Parse_ReadsVideoAndAllAudioTracks()
    {
        var info = MediaInfo.ParseFfprobeJson(ObsJson)!;
        Assert.Equal(46.433, info.Duration.TotalSeconds, 3);
        Assert.Equal(2560, info.Width);
        Assert.Equal(1440, info.Height);
        Assert.Equal(60, info.Fps);
        Assert.Equal(3, info.AudioTracks.Count);
        Assert.Null(info.AudioTracks[0].Title);
        Assert.Equal("Spiel", info.AudioTracks[1].Title);
        Assert.Null(info.AudioTracks[2].Title);
        Assert.Equal(1, info.AudioTracks[2].Channels);
    }

    [Fact]
    public void Parse_FractionalFpsAndStreamDurationFallback()
    {
        const string json = """
        { "streams": [ { "codec_type": "video", "width": 1920, "height": 1080, "avg_frame_rate": "0/0", "r_frame_rate": "30000/1001", "duration": "12.5" } ], "format": {} }
        """;
        var info = MediaInfo.ParseFfprobeJson(json)!;
        Assert.Equal(29.97, info.Fps);
        Assert.Equal(12.5, info.Duration.TotalSeconds);
        Assert.Empty(info.AudioTracks);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsNull()
    {
        Assert.Null(MediaInfo.ParseFfprobeJson("kein json"));
    }

    [Fact]
    public void ThumbnailSeek_IsShortlyAfterStart()
    {
        Assert.Equal(TimeSpan.Zero, ThumbnailCache.SeekPosition(null));
        Assert.Equal(TimeSpan.FromSeconds(1), ThumbnailCache.SeekPosition(TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(2), ThumbnailCache.SeekPosition(TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void ThumbnailPath_ChangesWhenFileChanges()
    {
        var cache = new ThumbnailCache("cache", new ProcessRunner());
        var clip = new ClipFile("C:/a.mp4", "C:/", 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(cache.PathFor(clip), cache.PathFor(clip with { Path = "c:/A.MP4" }));
        Assert.NotEqual(cache.PathFor(clip), cache.PathFor(clip with { SizeBytes = 101 }));
    }

    [Fact]
    public async Task Thumbnail_CreatesFileViaFfmpegAndCachesIt()
    {
        using var dir = new TempDir();
        var calls = 0;
        var runner = new CallbackRunner(args =>
        {
            calls++;
            File.WriteAllText(args[^1], "jpg");
            return new ProcessResult(0, "", "");
        });
        var cache = new ThumbnailCache(Path.Combine(dir.Path, "thumbs"), runner);
        var clip = new ClipFile(dir.File("a.mp4"), dir.Path, 1, DateTime.UtcNow);

        var first = await cache.GetOrCreateAsync("ffmpeg", clip, TimeSpan.FromSeconds(30));
        var second = await cache.GetOrCreateAsync("ffmpeg", clip, TimeSpan.FromSeconds(30));

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.True(File.Exists(first));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Thumbnail_FfmpegFails_ReturnsNull()
    {
        using var dir = new TempDir();
        var cache = new ThumbnailCache(dir.Path, new CallbackRunner(_ => new ProcessResult(1, "", "kaputt")));
        var clip = new ClipFile(dir.File("a.mp4"), dir.Path, 1, DateTime.UtcNow);
        Assert.Null(await cache.GetOrCreateAsync("ffmpeg", clip, null));
    }
}

internal sealed class CallbackRunner : IProcessRunner
{
    private readonly Func<IReadOnlyList<string>, ProcessResult> _respond;

    public CallbackRunner(Func<IReadOnlyList<string>, ProcessResult> respond) => _respond = respond;

    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(_respond(arguments));
}
