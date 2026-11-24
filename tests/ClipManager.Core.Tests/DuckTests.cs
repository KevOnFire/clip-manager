using ClipManager.Core.Editing;

namespace ClipManager.Core.Tests;

public class DuckTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Verlauf_SanftReinUndRaus()
    {
        var d = new DuckRange(new TimeRange(S(2), S(4)), -12);
        Assert.Equal(1, d.GainAt(1.9));
        Assert.Equal(1, d.GainAt(4.1));
        Assert.Equal(Math.Pow(10, -12 / 20.0), d.GainAt(3), 6);      // Mitte: volle Absenkung
        Assert.Equal(Math.Pow(10, -6 / 20.0), d.GainAt(2.15), 6);    // halb im Übergang (0,3 s) = halbe dB
        Assert.Equal(Math.Pow(10, -6 / 20.0), d.GainAt(3.85), 6);
    }

    [Fact]
    public void KurzerBereich_UebergangHoechstensHalbeLaenge()
    {
        var d = new DuckRange(new TimeRange(S(1), S(1.2)), -20);
        Assert.Equal(0.1, d.Fade, 6);
        Assert.Equal(Math.Pow(10, -1), d.GainAt(1.1), 6);
    }

    [Fact]
    public void Timeline_LeiserMachen_UeberschreibenEntfernen_Speichern()
    {
        var tl = new EditTimeline(S(10));
        tl.InitTracks(2, new[] { 0, 1 });
        Assert.True(tl.DuckTrackRange(1, S(5), S(2), -12));
        Assert.True(tl.IsEdited);
        Assert.Equal(new TimeRange(S(2), S(5)), Assert.Single(tl.Tracks[1].Ducked).Range);

        // Neue Stelle überschreibt den überlappenden Teil
        Assert.True(tl.DuckTrackRange(1, S(4), S(6), -20));
        Assert.Equal(new[] { (S(2), S(4), -12.0), (S(4), S(6), -20.0) }, tl.Tracks[1].Ducked.Select(d => (d.Range.Start, d.Range.End, d.Db)));

        Assert.True(tl.SetDuckDbAt(1, S(3), -6));
        Assert.Equal(-6, tl.Tracks[1].Ducked[0].Db);
        Assert.Equal(S(1), tl.MoveDuckEdge(1, 0, true, S(1)));

        // Speichern und wieder laden
        var state = tl.ToState();
        Assert.NotNull(state);
        var loaded = new EditTimeline(S(10));
        loaded.InitTracks(2, new[] { 0, 1 });
        loaded.Load(state);
        Assert.Equal(tl.Tracks[1].Ducked, loaded.Tracks[1].Ducked);

        Assert.True(tl.RemoveDuckAt(1, S(5)));
        Assert.True(tl.RemoveDuckAt(1, S(2)));
        Assert.False(tl.IsEdited);

        tl.Undo();
        Assert.Single(tl.Tracks[1].Ducked);
    }
}
