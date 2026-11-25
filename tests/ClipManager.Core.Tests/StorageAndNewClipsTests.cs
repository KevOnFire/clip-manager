using ClipManager.Core.Library;
using ClipManager.Core.Settings;
using ClipManager.Core.Storage;
using Xunit;

namespace ClipManager.Core.Tests;

public class StorageAndNewClipsTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void ForFolders_ListsEachDriveOnce()
    {
        var probed = new List<string>();
        var dir = Path.GetTempPath();
        var usage = DiskUsage.ForFolders(new[] { dir, Path.Combine(dir, "a"), Path.Combine(dir, "b") }, root =>
        {
            probed.Add(root);
            return (1000 * Gb, 250 * Gb);
        });

        Assert.Single(usage);
        Assert.Single(probed);
        Assert.Equal(75, usage[0].UsedPercent, 3);
    }

    [Fact]
    public void ForFolders_SkipsMissingDrives()
    {
        var usage = DiskUsage.ForFolders(new[] { Path.GetTempPath() }, _ => null);
        Assert.Empty(usage);
    }

    [Theory]
    [InlineData(94.9, true, 95, false)]
    [InlineData(95.0, true, 95, true)]
    [InlineData(99.0, false, 95, false)]
    [InlineData(80.0, true, 80, true)]
    [InlineData(60.0, true, 10, true)] // zu kleine Werte werden auf 50 % angehoben
    public void ShouldWarn_UsesThreshold(double usedPercent, bool enabled, int threshold, bool expected)
    {
        var settings = new AppSettings { DiskWarningEnabled = enabled, DiskWarningPercent = threshold };
        var total = 1000 * Gb;
        var usage = new DiskUsage("F:\\", total, (long)(total * (100 - usedPercent) / 100));
        Assert.Equal(expected, settings.ShouldWarn(usage));
    }

    [Fact]
    public void DefaultWarnsAt95Percent()
    {
        var settings = new AppSettings();
        Assert.True(settings.DiskWarningEnabled);
        Assert.Equal(95, settings.DiskWarningPercent);
    }

    [Theory]
    [InlineData(850L * 1024 * 1024, "850 MB")]
    [InlineData(5L * 1024 * 1024 * 1024 + 512L * 1024 * 1024, "5,5 GB")]
    [InlineData(312L * 1024 * 1024 * 1024, "312 GB")]
    [InlineData(2L * 1024 * 1024 * 1024 * 1024, "2,0 TB")]
    public void FormatBytes_IsReadable(long bytes, string expected) => Assert.Equal(expected, DiskUsage.FormatBytes(bytes));

    [Fact]
    public void NewClip_OnlyAfterStartAndUnseen()
    {
        var start = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        var old = new ClipFile(@"C:\c\old.mp4", @"C:\c", 1, start.AddHours(-1));
        var fresh = new ClipFile(@"C:\c\new.mp4", @"C:\c", 1, start.AddHours(1));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.False(NewClips.IsNew(old, start, seen));
        Assert.True(NewClips.IsNew(fresh, start, seen));
        Assert.False(NewClips.IsNew(fresh, null, seen));

        seen.Add(@"C:\C\NEW.mp4");
        Assert.False(NewClips.IsNew(fresh, start, seen));
    }

    [Fact]
    public void Db_RemembersSeenClips_AndMovesThemOnRename()
    {
        using var dir = new TempDir();
        var db = new LibraryDb(Path.Combine(dir.Path, "library.db"));
        db.MarkSeen(new[] { "a.mp4", "b.mp4", "a.mp4" });
        Assert.Equal(2, db.GetSeen().Count);

        db.MovePath("a.mp4", "c.mp4");
        var seen = db.GetSeen();
        Assert.Contains("c.mp4", seen);
        Assert.DoesNotContain("a.mp4", seen);

        db.RemovePath("b.mp4");
        Assert.DoesNotContain("b.mp4", db.GetSeen());
    }
}
