using ClipManager.Core.Editing;
using ClipManager.Core.Library;
using ClipManager.Core.Media;

namespace ClipManager.Core.Tests;

public class EditTimelineTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void New_KeepsWholeClip()
    {
        var t = new EditTimeline(S(60));
        Assert.False(t.IsEdited);
        Assert.Equal(new[] { new TimeRange(S(0), S(60)) }, t.KeptRanges);
        Assert.Equal(S(60), t.OutputDuration);
    }

    [Fact]
    public void InOut_TrimStartAndEnd()
    {
        var t = new EditTimeline(S(60));
        Assert.True(t.SetIn(S(5)));
        Assert.True(t.SetOut(S(50)));
        Assert.True(t.IsEdited);
        Assert.Equal(new[] { new TimeRange(S(5), S(50)) }, t.KeptRanges);
        Assert.Equal(S(45), t.OutputDuration);
    }

    [Fact]
    public void InOut_CannotCross()
    {
        var t = new EditTimeline(S(60));
        t.SetOut(S(10));
        Assert.False(t.SetIn(S(20)));
        Assert.False(t.SetIn(S(10)));
        Assert.Equal(S(0), t.In);
    }

    [Fact]
    public void InOut_ClampedToDuration()
    {
        var t = new EditTimeline(S(60));
        t.SetOut(S(99));
        Assert.Equal(S(60), t.Out);
        t.SetIn(S(-3));
        Assert.Equal(S(0), t.In);
    }

    [Fact]
    public void SplitAndRemove_CutsMiddle()
    {
        var t = new EditTimeline(S(60));
        Assert.True(t.Split(S(20)));
        Assert.True(t.Split(S(30)));
        Assert.Equal(3, t.Segments.Count);
        Assert.True(t.ToggleRemovedAt(S(25)));
        Assert.Equal(new[] { new TimeRange(S(0), S(20)), new TimeRange(S(30), S(60)) }, t.KeptRanges);
        Assert.Equal(S(50), t.OutputDuration);

        // Zurückholen
        Assert.True(t.ToggleRemovedAt(S(25)));
        Assert.Equal(new[] { new TimeRange(S(0), S(60)) }, t.KeptRanges);
    }

    [Fact]
    public void Split_TooCloseToExistingCut_Ignored()
    {
        var t = new EditTimeline(S(60));
        t.Split(S(20));
        Assert.False(t.Split(S(20.05)));
        Assert.False(t.Split(S(0.01)));
        Assert.Single(t.Splits);
    }

    [Fact]
    public void Split_InsideRemovedPart_StaysRemoved()
    {
        var t = new EditTimeline(S(60));
        t.Split(S(20));
        t.Split(S(40));
        t.ToggleRemovedAt(S(30));
        t.Split(S(30));
        Assert.True(t.SegmentAt(S(25))!.Removed);
        Assert.True(t.SegmentAt(S(35))!.Removed);
        Assert.Equal(S(40), t.OutputDuration);
    }

    [Fact]
    public void LastKeptPart_CannotBeRemoved()
    {
        var t = new EditTimeline(S(60));
        Assert.False(t.ToggleRemovedAt(S(10)));
        t.Split(S(30));
        Assert.True(t.ToggleRemovedAt(S(10)));
        Assert.False(t.ToggleRemovedAt(S(40)));
    }

    [Fact]
    public void RemovedFirstPart_StaysRemovedWhenInMoves()
    {
        var t = new EditTimeline(S(60));
        t.Split(S(20));
        t.ToggleRemovedAt(S(5));
        t.SetIn(S(10));
        Assert.True(t.SegmentAt(S(15))!.Removed);
        Assert.Equal(new[] { new TimeRange(S(20), S(60)) }, t.KeptRanges);
    }

    [Fact]
    public void RemoveRange_SetsCutsAndRemoves()
    {
        var t = new EditTimeline(S(60));
        Assert.True(t.RemoveRange(S(40), S(10)));
        Assert.Equal(new[] { new TimeRange(S(0), S(10)), new TimeRange(S(40), S(60)) }, t.KeptRanges);

        // Ein Schritt rückgängig macht alles davon rückgängig.
        Assert.True(t.Undo());
        Assert.False(t.CanUndo);
        Assert.Empty(t.Splits);
        Assert.False(t.IsEdited);
    }

    [Fact]
    public void RemoveRange_EverythingRefused()
    {
        var t = new EditTimeline(S(60));
        Assert.False(t.RemoveRange(S(0), S(60)));
        Assert.False(t.IsEdited);
        Assert.Empty(t.Splits);
    }

    [Fact]
    public void Undo_RestoresStepByStep()
    {
        var t = new EditTimeline(S(60));
        t.SetIn(S(5));
        t.Split(S(30));
        t.ToggleRemovedAt(S(40));
        Assert.True(t.Undo());
        Assert.Equal(S(55), t.OutputDuration);
        Assert.True(t.Undo());
        Assert.Empty(t.Splits);
        Assert.True(t.Undo());
        Assert.Equal(S(0), t.In);
        Assert.False(t.Undo());
    }

    [Fact]
    public void Reset_ClearsEverything_AndIsUndoable()
    {
        var t = new EditTimeline(S(60));
        t.SetIn(S(5));
        t.RemoveRange(S(20), S(30));
        t.Reset();
        Assert.False(t.IsEdited);
        Assert.Empty(t.Splits);
        t.Undo();
        Assert.True(t.IsEdited);
        Assert.Equal(S(45), t.OutputDuration);
    }

    [Fact]
    public void NextPlayable_SkipsRemovedParts()
    {
        var t = new EditTimeline(S(60));
        t.SetIn(S(5));
        t.SetOut(S(50));
        t.RemoveRange(S(20), S(30));
        Assert.Equal(S(5), t.NextPlayable(S(0)));
        Assert.Equal(S(10), t.NextPlayable(S(10)));
        Assert.Equal(S(30), t.NextPlayable(S(22)));
        Assert.Null(t.NextPlayable(S(50)));
        Assert.Null(t.NextPlayable(S(55)));
    }

    [Fact]
    public void State_RoundTrips()
    {
        var t = new EditTimeline(S(60));
        t.SetIn(S(2.5));
        t.SetOut(S(55));
        t.RemoveRange(S(20), S(30));
        var state = t.ToState();

        var loaded = new EditTimeline(S(60));
        var changed = 0;
        loaded.Changed += (_, _) => changed++;
        loaded.Load(state);
        Assert.Equal(1, changed);
        Assert.Equal(t.KeptRanges, loaded.KeptRanges);
        Assert.False(loaded.CanUndo);
    }

    [Fact]
    public void State_FromLongerClip_IsClamped()
    {
        var loaded = new EditTimeline(S(30));
        loaded.Load(new ClipEditState { In = 10, Out = 50, Splits = { 20, 40 } });
        Assert.Equal(S(30), loaded.Out);
        Assert.Single(loaded.Splits);

        // Unsinn (Ende vor Anfang) → ganzer Clip
        loaded.Load(new ClipEditState { In = 25, Out = 5 });
        Assert.Equal(S(0), loaded.In);
        Assert.Equal(S(30), loaded.Out);
    }

    [Fact]
    public void Changed_FiresOnEdits()
    {
        var t = new EditTimeline(S(60));
        var changed = 0;
        t.Changed += (_, _) => changed++;
        t.SetIn(S(1));
        t.Split(S(10));
        t.Split(S(10)); // abgelehnt, kein Event
        Assert.Equal(2, changed);
    }

    [Theory]
    [InlineData(0, "0:00.0")]
    [InlineData(42.37, "0:42.3")]
    [InlineData(59.96, "0:59.9")]
    [InlineData(65, "1:05.0")]
    [InlineData(3725.5, "1:02:05.5")]
    [InlineData(-1, "0:00.0")]
    public void TimeText_Formats(double seconds, string expected) =>
        Assert.Equal(expected, TimeText.Format(S(seconds)));

    [Fact]
    public void TimeText_WithoutTenths() => Assert.Equal("1:05", TimeText.Format(S(65.9), tenths: false));

    [Fact]
    public void Db_SavesLoadsAndMovesEdits()
    {
        using var dir = new TempDir();
        var db = new LibraryDb(Path.Combine(dir.Path, "lib.db"));
        Assert.Null(db.GetEdit("a.mp4"));

        db.SaveEdit("a.mp4", new ClipEditState { In = 1, Out = 9, Splits = { 4 }, Removed = { 4 } });
        var loaded = db.GetEdit("A.MP4");
        Assert.NotNull(loaded);
        Assert.Equal(9, loaded!.Out);
        Assert.Equal(new[] { 4.0 }, loaded.Removed);
        Assert.Contains("a.mp4", db.GetEditedPaths());

        db.MovePath("a.mp4", "b.mp4");
        Assert.Null(db.GetEdit("a.mp4"));
        Assert.NotNull(db.GetEdit("b.mp4"));

        db.SaveEdit("b.mp4", null);
        Assert.Null(db.GetEdit("b.mp4"));

        db.SaveEdit("c.mp4", new ClipEditState { Out = 3 });
        db.RemovePath("c.mp4");
        Assert.Empty(db.GetEditedPaths());
    }

    [Fact]
    public void Filmstrip_Arguments()
    {
        var args = FilmstripCache.BuildArguments("in.mp4", S(60), "out.jpg");
        var vf = args[args.ToList().IndexOf("-vf") + 1];
        Assert.StartsWith("fps=0.280702,", vf);
        Assert.Contains($"tile={FilmstripCache.Frames}x1", vf);
        Assert.Contains("nokey", args);
        Assert.Equal("out.jpg", args[^1]);
    }
}
