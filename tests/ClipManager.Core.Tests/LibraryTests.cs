using ClipManager.Core.Library;
using ClipManager.Core.Media;

namespace ClipManager.Core.Tests;

public class LibraryTests
{
    private static ClipFile Clip(string name, int day, long size = 1, string root = "R1") =>
        new($"/{root}/{name}.mp4", root, size, new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Query_FiltersAndSorts()
    {
        var a = Clip("Ace Valorant", 1, size: 300);
        var b = Clip("clutch", 2, size: 100, root: "R2");
        var c = Clip("valorant fail", 3, size: 200);
        var all = new[] { a, b, c };
        var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a.Path.ToUpperInvariant(), c.Path };
        var durations = new Dictionary<string, TimeSpan> { [a.Path] = TimeSpan.FromSeconds(10), [b.Path] = TimeSpan.FromSeconds(50) };

        Assert.Equal(new[] { c, b, a }, LibraryQuery.Apply(all, new LibraryFilter(), favorites, durations));
        Assert.Equal(new[] { a, b, c }, LibraryQuery.Apply(all, new LibraryFilter(Sort: ClipSort.Oldest), favorites, durations));
        Assert.Equal(new[] { b, a, c }, LibraryQuery.Apply(all, new LibraryFilter(Sort: ClipSort.Longest), favorites, durations));
        Assert.Equal(new[] { a, c, b }, LibraryQuery.Apply(all, new LibraryFilter(Sort: ClipSort.Largest), favorites, durations));
        Assert.Equal(new[] { c, a }, LibraryQuery.Apply(all, new LibraryFilter(FavoritesOnly: true), favorites, durations));
        Assert.Equal(new[] { b }, LibraryQuery.Apply(all, new LibraryFilter(Folder: "r2"), favorites, durations));
        Assert.Equal(new[] { c, a }, LibraryQuery.Apply(all, new LibraryFilter(Search: "VALORANT"), favorites, durations));
        Assert.Equal(new[] { a }, LibraryQuery.Apply(all, new LibraryFilter(Search: "valorant ace"), favorites, durations));
    }

    [Fact]
    public void Query_EditedAndDisplayNames()
    {
        var a = Clip("Replay 2026-11-01", 1);
        var b = Clip("Replay 2026-11-02", 2);
        var c = Clip("Replay 2026-11-03", 3);
        var all = new[] { a, b, c };
        var none = new HashSet<string>();
        var durations = new Dictionary<string, TimeSpan>();
        var edited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a.Path };
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [b.Path] = "Ace auf Haven", [c.Path] = "Zocken" };

        Assert.Equal(new[] { a }, LibraryQuery.Apply(all, new LibraryFilter(Edited: EditedFilter.Edited), none, durations, edited, names));
        Assert.Equal(new[] { c, b }, LibraryQuery.Apply(all, new LibraryFilter(Edited: EditedFilter.Unedited), none, durations, edited, names));
        Assert.Equal(new[] { a, c, b }, LibraryQuery.Apply(all, new LibraryFilter(Sort: ClipSort.EditedFirst), none, durations, edited, names));
        // Suche und Sortierung nach Namen nutzen den Namen im Programm
        Assert.Equal(new[] { b }, LibraryQuery.Apply(all, new LibraryFilter(Search: "haven"), none, durations, edited, names));
        Assert.Equal(new[] { b, a, c }, LibraryQuery.Apply(all, new LibraryFilter(Sort: ClipSort.Name), none, durations, edited, names));
    }

    [Fact]
    public void Db_DisplayNames()
    {
        using var dir = new TempDir();
        var db = new LibraryDb(Path.Combine(dir.Path, "library.db"));
        Assert.Null(db.GetDisplayName("a.mp4"));

        db.SetDisplayName("a.mp4", "  Mein Ace ");
        Assert.Equal("Mein Ace", db.GetDisplayName("A.MP4"));
        db.SetDisplayName("a.mp4", "Neu");
        Assert.Equal("Neu", db.GetDisplayNames()["a.mp4"]);

        db.MovePath("a.mp4", "b.mp4");
        Assert.Null(db.GetDisplayName("a.mp4"));
        Assert.Equal("Neu", db.GetDisplayName("b.mp4"));

        db.SetDisplayName("b.mp4", "");
        Assert.Null(db.GetDisplayName("b.mp4"));
        db.SetDisplayName("b.mp4", "X");
        db.RemovePath("b.mp4");
        Assert.Empty(db.GetDisplayNames());
    }

    [Fact]
    public void Paging_ClampsToValidPages()
    {
        var items = Enumerable.Range(1, 50).ToList();
        Assert.Equal(3, LibraryQuery.PageCount(50, 24));
        Assert.Equal(1, LibraryQuery.PageCount(0, 24));
        Assert.Equal(new[] { 49, 50 }, LibraryQuery.Page(items, 2, 24));
        Assert.Equal(new[] { 49, 50 }, LibraryQuery.Page(items, 99, 24));
        Assert.Equal(24, LibraryQuery.Page(items, -1, 24).Count);
    }

    [Fact]
    public void Db_StoresFavoritesAndMediaInfo()
    {
        using var dir = new TempDir();
        var db = new LibraryDb(Path.Combine(dir.Path, "data", "library.db"));
        var clip = new ClipFile(Path.Combine(dir.Path, "a.mp4"), dir.Path, 1000, new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc));
        var info = new MediaInfo(TimeSpan.FromSeconds(46.5), 2560, 1440, 60, new[] { new AudioTrackInfo(0, null, 2), new AudioTrackInfo(1, "Mikro", 1) });

        db.SetFavorite(clip.Path, true);
        db.SetFavorite(clip.Path, true);
        db.SaveMediaInfo(clip, info);

        Assert.Contains(clip.Path, db.GetFavorites());
        var loaded = db.GetMediaInfo(clip)!;
        Assert.Equal(info.Duration, loaded.Duration);
        Assert.Equal(2560, loaded.Width);
        Assert.Equal(info.AudioTracks, loaded.AudioTracks);
        Assert.Equal(info.Duration, db.GetDurations()[clip.Path]);

        // Datei hat sich geändert: alte Infos zählen nicht mehr.
        Assert.Null(db.GetMediaInfo(clip with { SizeBytes = 2000 }));

        var renamed = Path.Combine(dir.Path, "b.mp4");
        db.MovePath(clip.Path, renamed);
        Assert.DoesNotContain(clip.Path, db.GetFavorites());
        Assert.Contains(renamed, db.GetFavorites());
        Assert.NotNull(db.GetMediaInfo(clip with { Path = renamed }));

        db.RemovePath(renamed);
        db.SetFavorite(renamed, false);
        Assert.Empty(db.GetFavorites());
        Assert.Null(db.GetMediaInfo(clip with { Path = renamed }));
    }

    [Fact]
    public void Copy_CreatesNumberedCopies()
    {
        using var dir = new TempDir();
        var original = dir.File("Replay 1.mp4", bytes: 5);
        var first = ClipFileOps.CreateCopy(original);
        var second = ClipFileOps.CreateCopy(original);
        Assert.Equal("Replay 1 (Kopie).mp4", Path.GetFileName(first));
        Assert.Equal("Replay 1 (Kopie 2).mp4", Path.GetFileName(second));
        Assert.Equal(5, new FileInfo(second).Length);
    }

    [Fact]
    public void Rename_KeepsExtensionAndRefusesConflicts()
    {
        using var dir = new TempDir();
        var a = dir.File("a.mkv");
        dir.File("taken.mkv");

        var renamed = ClipFileOps.Rename(a, "  Ace auf Ascent  ");
        Assert.Equal("Ace auf Ascent.mkv", Path.GetFileName(renamed));
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(a));

        Assert.Throws<IOException>(() => ClipFileOps.Rename(renamed, "taken"));
        Assert.Throws<ArgumentException>(() => ClipFileOps.Rename(renamed, "böse:name"));
        Assert.Equal(renamed, ClipFileOps.Rename(renamed, "Ace auf Ascent"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a/b", false)]
    [InlineData("frage?", false)]
    [InlineData("ende.", false)]
    [InlineData("Ace (1v4) – Ascent", true)]
    public void ValidateName(string name, bool ok)
    {
        Assert.Equal(ok, ClipFileOps.ValidateName(name) == null);
    }
}
