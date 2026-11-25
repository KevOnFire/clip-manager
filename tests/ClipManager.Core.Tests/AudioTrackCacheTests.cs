using ClipManager.Core.Library;
using ClipManager.Core.Media;
using Xunit;

namespace ClipManager.Core.Tests;

public class AudioTrackCacheTests
{
    /// <summary>Tut so, als wäre es FFmpeg: legt die Ausgabedateien nach kurzer Zeit an und zählt die Aufrufe.</summary>
    private sealed class SlowFakeRunner : IProcessRunner
    {
        public int Calls;

        public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(150, cancellationToken);
            foreach (var output in arguments.Where(a => a.EndsWith(".wav", StringComparison.Ordinal)))
            {
                await File.WriteAllTextAsync(output, "x", cancellationToken);
            }

            return new ProcessResult(0, "", "");
        }
    }

    [Fact]
    public async Task SameClipTwiceAtOnce_RunsFfmpegOnlyOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clippy-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new SlowFakeRunner();
            var cache = new AudioTrackCache(dir, runner);
            var clip = new ClipFile(@"C:\Clips\a.mp4", @"C:\Clips", 123, new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc));

            var first = cache.GetOrCreateAsync("ffmpeg", clip, 2);
            var second = cache.GetOrCreateAsync("ffmpeg", clip, 2);
            var results = await Task.WhenAll(first, second);

            Assert.Equal(1, runner.Calls);
            Assert.All(results, r => Assert.NotNull(r));
            Assert.All(results[0]!, path => Assert.True(File.Exists(path)));

            // Danach liegt alles im Cache, FFmpeg wird nicht mehr gebraucht.
            var third = await cache.GetOrCreateAsync("ffmpeg", clip, 2);
            Assert.NotNull(third);
            Assert.Equal(1, runner.Calls);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
