using ClipManager.Core.Settings;
using ClipManager.Core.Storage;
using Xunit;

namespace ClipManager.Core.Tests;

public class CacheFolderTests
{
    [Fact]
    public void Resolve_UsesDefaultWhenEmpty()
    {
        Assert.Equal("def", CacheFolder.Resolve(null, "def"));
        Assert.Equal("def", CacheFolder.Resolve("  ", "def"));
        Assert.Equal("D:\\x", CacheFolder.Resolve("D:\\x", "def"));
    }

    [Fact]
    public void FromPicked_AddsOwnFolderOnce()
    {
        var picked = Path.Combine(Path.GetTempPath(), "cache");
        var first = CacheFolder.FromPicked(picked);
        Assert.Equal(Path.Combine(picked, CacheFolder.CustomFolderName), first);
        Assert.Equal(first, CacheFolder.FromPicked(first));
    }

    [Fact]
    public void Move_MovesAudioAndThumbs_LeavesOtherFiles()
    {
        using var dir = new TempDir();
        var oldRoot = Path.Combine(dir.Path, "old");
        var newRoot = Path.Combine(dir.Path, "new");
        dir.File(Path.Combine("old", "audio", "a_0.wav"), bytes: 100);
        dir.File(Path.Combine("old", "audio", "a_1.wav"), bytes: 100);
        dir.File(Path.Combine("old", "thumbs", "t.jpg"), bytes: 10);
        dir.File(Path.Combine("old", "log.txt"), bytes: 5);

        var (moved, skipped) = CacheFolder.Move(oldRoot, newRoot);

        Assert.Equal(3, moved);
        Assert.Equal(0, skipped);
        Assert.True(File.Exists(Path.Combine(newRoot, "audio", "a_0.wav")));
        Assert.True(File.Exists(Path.Combine(newRoot, "thumbs", "t.jpg")));
        Assert.True(File.Exists(Path.Combine(oldRoot, "log.txt")));
        Assert.False(Directory.Exists(Path.Combine(oldRoot, "audio")));
        Assert.Equal(210, CacheFolder.Size(newRoot));
    }

    [Fact]
    public void Move_ToSameFolder_DoesNothing()
    {
        using var dir = new TempDir();
        dir.File(Path.Combine("audio", "a.wav"));
        Assert.Equal((0, 0), CacheFolder.Move(dir.Path, dir.Path + Path.DirectorySeparatorChar));
        Assert.True(File.Exists(Path.Combine(dir.Path, "audio", "a.wav")));
    }

    [Fact]
    public void TrimToSize_DeletesOldestFirst()
    {
        using var dir = new TempDir();
        var now = DateTime.UtcNow;
        dir.File("old.wav", now.AddHours(-3), 100);
        dir.File("mid.wav", now.AddHours(-2), 100);
        dir.File("new.wav", now.AddHours(-1), 100);

        var deleted = CacheFolder.TrimToSize(dir.Path, 150);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(Path.Combine(dir.Path, "old.wav")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "mid.wav")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "new.wav")));
    }

    [Fact]
    public void Clear_DeletesEverything()
    {
        using var dir = new TempDir();
        dir.File("a.wav");
        dir.File("b.wav");
        Assert.Equal(2, CacheFolder.Clear(dir.Path));
        Assert.Equal(0, CacheFolder.Size(dir.Path));
    }

    [Fact]
    public void AudioCacheLimit_DefaultsTo5Gb_AndIsClamped()
    {
        Assert.Equal(5L * 1024 * 1024 * 1024, new AppSettings().AudioCacheMaxBytes());
        Assert.Equal(1L * 1024 * 1024 * 1024, new AppSettings { AudioCacheMaxGb = 0 }.AudioCacheMaxBytes());
    }
}
