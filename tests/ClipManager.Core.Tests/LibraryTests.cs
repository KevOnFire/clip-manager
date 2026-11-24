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

    [Theory]
    [InlineData(1560, 312, 5, 25)] // 5 Spalten
    [InlineData(1559, 312, 5, 20)] // knapp zu schmal für 5
    [InlineData(1300, 312, 5, 20)]
    [InlineData(100, 312, 5, 5)]   // schmaler als eine Kachel: trotzdem eine Spalte
    [InlineData(0, 312, 5, 5)]
    public void SeitengroesseFuelltReihen(double width, double slot, int rows, int expected)
    {
        Assert.Equal(expected, LibraryQuery.PageSizeFor(width, slot, rows));
    }

    [Fact]
    public void SeiteNachGroessenaenderung_BehaeltErstenClip()
    {
        // Seite 3 mit 25 = Clips ab 75; mit 20 pro Seite liegt Clip 75 auf Seite 3 (60–79).
        Assert.Equal(3, LibraryQuery.PageAfterResize(3, 25, 20));
        Assert.Equal(0, LibraryQuery.PageAfterResize(0, 25, 30));
        Assert.Equal(2, LibraryQuery.PageAfterResize(3, 20, 25));
    }

    [Fact]
    public void Kategorien_AnlegenUmbenennenLoeschen()
    {
        using var dir = new TempDir();
        var db = new LibraryDb(Path.Combine(dir.Path, "library.db"));
        var aces = db.AddCategory(" Aces ");
        Assert.Equal("Aces", aces.Name);
        Assert.Equal(aces.Id, db.AddCategory("aces").Id); // gleicher Name = gleiche Kategorie
        var funny = db.AddCategory("Lustig");
        Assert.Equal(new[] { "Aces", "Lustig" }, db.GetCategories().Select(c => c.Name));

        Assert.False(db.RenameCategory(funny.Id, "ACES"));
        Assert.False(db.RenameCategory(funny.Id, "  "));
        Assert.True(db.RenameCategory(funny.Id, "Fails"));
        Assert.Equal(new[] { "Aces", "Fails" }, db.GetCategories().Select(c => c.Name));

        db.SetInCategory(funny.Id, new[] { @"C:\a.mp4" }, true);
        db.DeleteCategory(funny.Id);
        Assert.Single(db.GetCategories());
        Assert.False(db.GetCategoryMembers().ContainsKey(funny.Id));
    }

    [Fact]
    public void Kategorien_ClipsReinUndRaus_UndWandernBeimUmbenennen()
    {
        using var dir = new TempDir();
        var db = new LibraryDb(Path.Combine(dir.Path, "library.db"));
        var cat = db.AddCategory("Aces");
        db.SetInCategory(cat.Id, new[] { @"C:\a.mp4", @"C:\b.mp4", @"C:\c.mp4" }, true);
        db.SetInCategory(cat.Id, new[] { @"C:\a.mp4" }, true); // doppelt schadet nicht
        db.SetInCategory(cat.Id, new[] { @"C:\b.mp4" }, false);
        Assert.Equal(new[] { @"C:\a.mp4", @"C:\c.mp4" }, db.GetCategoryMembers()[cat.Id].OrderBy(p => p));

        db.MovePath(@"C:\a.mp4", @"C:\x.mp4");
        db.RemovePath(@"C:\c.mp4");
        Assert.Equal(new[] { @"C:\x.mp4" }, db.GetCategoryMembers()[cat.Id]);
    }

    [Fact]
    public void Filter_NurKategorie()
    {
        var clips = new[]
        {
            new ClipFile(@"C:\a.mp4", @"C:\", 1, DateTime.UtcNow),
            new ClipFile(@"C:\b.mp4", @"C:\", 1, DateTime.UtcNow.AddMinutes(-1)),
        };
        var only = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"c:\B.mp4" };
        var result = LibraryQuery.Apply(clips, new LibraryFilter(OnlyPaths: only), new HashSet<string>(), new Dictionary<string, TimeSpan>());
        Assert.Equal(@"C:\b.mp4", Assert.Single(result).Path);
    }

    [Theory]
    [InlineData("5", 90, 4)]
    [InlineData(" 1 ", 90, 0)]
    [InlineData("500", 90, 89)]
    [InlineData("0", 90, 0)]
    [InlineData("-3", 90, 0)]
    public void SeiteEingeben(string text, int pages, int expected)
    {
        Assert.Equal(expected, LibraryQuery.ParsePageInput(text, pages));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(null)]
    public void SeiteEingeben_Unsinn(string? text)
    {
        Assert.Null(LibraryQuery.ParsePageInput(text, 10));
    }

    [Fact]
    public void Auswahlbereich_InBeideRichtungen()
    {
        Assert.Equal(new[] { 2, 3, 4, 5 }, LibraryQuery.SelectionRange(2, 5));
        Assert.Equal(new[] { 2, 3, 4, 5 }, LibraryQuery.SelectionRange(5, 2));
        Assert.Equal(new[] { 3 }, LibraryQuery.SelectionRange(3, 3));
    }
}
