using ClipManager.Core.Library;

namespace ClipManager.Core.Tests;

public class ClipScannerTests
{
    [Fact]
    public void Scan_FindsVideosRecursively_NewestFirst()
    {
        using var dir = new TempDir();
        var old = dir.File("alt.mkv", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var neu = dir.File("Valorant/neu.MP4", new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc));
        dir.File("notiz.txt");

        var clips = ClipScanner.Scan(new[] { dir.Path });

        Assert.Equal(new[] { neu, old }, clips.Select(c => c.Path));
        Assert.Equal("neu", clips[0].Name);
    }

    [Fact]
    public void Scan_OverlappingFolders_ListEachFileOnce()
    {
        using var dir = new TempDir();
        dir.File("sub/a.mp4");
        var clips = ClipScanner.Scan(new[] { dir.Path, System.IO.Path.Combine(dir.Path, "sub") });
        Assert.Single(clips);
    }

    [Fact]
    public void Scan_SkipsExportFolderAndMissingFolders()
    {
        using var dir = new TempDir();
        dir.File("a.mp4");
        dir.File("Exporte/a-discord.mp4");
        var clips = ClipScanner.Scan(
            new[] { dir.Path, System.IO.Path.Combine(dir.Path, "gibtsnicht") },
            excludeFolder: System.IO.Path.Combine(dir.Path, "Exporte"));
        Assert.Single(clips);
        Assert.Equal("a", clips[0].Name);
    }
}
