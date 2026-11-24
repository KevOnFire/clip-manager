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
    public void KurzerBereich_UebergaengeAnteiligGekuerzt()
    {
        var d = new DuckRange(new TimeRange(S(1), S(1.2)), -20);
        Assert.Equal(0.1, d.Fades.In, 6);
        Assert.Equal(0.1, d.Fades.Out, 6);
        Assert.Equal(Math.Pow(10, -1), d.GainAt(1.1), 6);

        // 1 s rein + 3 s raus in 2 s → 0,5 + 1,5
        var e = new DuckRange(new TimeRange(S(0), S(2)), -10, 1, 3);
        Assert.Equal(0.5, e.Fades.In, 6);
        Assert.Equal(1.5, e.Fades.Out, 6);
    }

    [Fact]
    public void EigeneUebergaenge_UndLauter()
    {
        // 1 s rein, sofort raus, +6 dB
        var d = new DuckRange(new TimeRange(S(2), S(6)), 6, 1, 0);
        Assert.Equal(1, d.GainAt(1.99));
        Assert.Equal(Math.Pow(10, 3 / 20.0), d.GainAt(2.5), 6);   // halb im Übergang rein
        Assert.Equal(Math.Pow(10, 6 / 20.0), d.GainAt(5.99), 6);  // bis zum Ende voll
        Assert.Equal(1, d.GainAt(6));

        // Beide Übergänge 0: hart an und aus
        var hard = new DuckRange(new TimeRange(S(1), S(2)), -20, 0, 0);
        Assert.Equal(0.1, hard.GainAt(1), 6);
        Assert.Equal(1, hard.GainAt(2));
    }

    [Fact]
    public void Create_BegrenztWerte()
    {
        var d = DuckRange.Create(new TimeRange(S(0), S(1)), 50, -1, 99);
        Assert.Equal(DuckRange.MaxDb, d.Db);
        Assert.Equal(0, d.FadeIn);
        Assert.Equal(DuckRange.MaxFade, d.FadeOut);
        Assert.Equal(DuckRange.MinDb, DuckRange.Create(new TimeRange(S(0), S(1)), -99).Db);
    }

    [Fact]
    public void Timeline_EinstellenLauterUndAltesFormat()
    {
        var tl = new EditTimeline(S(10));
        tl.InitTracks(1, new[] { 0 });
        Assert.False(tl.DuckTrackRange(0, S(1), S(3), 0));          // 0 dB ändert nichts
        Assert.True(tl.DuckTrackRange(0, S(1), S(3), 8, 0.5, 1.2));
        Assert.Equal(DuckRange.Create(new TimeRange(S(1), S(3)), 8, 0.5, 1.2), tl.DuckAt(0, S(2)));

        Assert.True(tl.SetDuckAt(0, S(2), -15, 0, 2));
        Assert.Equal((-15.0, 0.0, 2.0), (tl.DuckAt(0, S(2))!.Db, tl.DuckAt(0, S(2))!.FadeIn, tl.DuckAt(0, S(2))!.FadeOut));

        // Speichern/Laden mit eigenen Übergängen
        var loaded = new EditTimeline(S(10));
        loaded.InitTracks(1, new[] { 0 });
        loaded.Load(tl.ToState());
        Assert.Equal(tl.Tracks[0].Ducked, loaded.Tracks[0].Ducked);

        // Altes Format [Start, Ende, dB] bekommt Standard-Übergänge
        var state = tl.ToState()!;
        state.Tracks![0].Ducked = new List<double[]> { new[] { 4.0, 6.0, -12.0 } };
        loaded.Load(state);
        Assert.Equal(new DuckRange(new TimeRange(S(4), S(6)), -12), Assert.Single(loaded.Tracks[0].Ducked));

        // Auf 0 dB stellen = Stelle weg
        Assert.True(tl.SetDuckAt(0, S(2), 0, 0.3, 0.3));
        Assert.Empty(tl.Tracks[0].Ducked);
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

        Assert.True(tl.SetDuckAt(1, S(3), -6, 0.3, 0.3));
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
