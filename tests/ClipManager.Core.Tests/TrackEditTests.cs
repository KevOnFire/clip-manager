using ClipManager.Core.Editing;
using ClipManager.Core.Export;

namespace ClipManager.Core.Tests;

public class TrackEditTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    private static EditTimeline Obs()
    {
        var t = new EditTimeline(S(60));
        t.InitTracks(5, ExportPlanner.DefaultAudioTracks(5));
        return t;
    }

    [Fact]
    public void Init_ObsTracks_MixOffRestOn()
    {
        var t = Obs();
        Assert.Equal(5, t.Tracks.Count);
        Assert.False(t.Tracks[0].Enabled);
        Assert.All(t.Tracks.Skip(1), tr => Assert.True(tr.Enabled));
        Assert.False(t.IsEdited);
        Assert.Null(t.ToState().Tracks);
    }

    [Fact]
    public void EnableAndVolume_CountAsEditAndUndo()
    {
        var t = Obs();
        Assert.True(t.SetTrackEnabled(4, false));
        Assert.False(t.SetTrackEnabled(4, false));
        Assert.True(t.IsEdited);
        Assert.Equal(new[] { 1, 2, 3 }, t.AudibleTracks.Select(x => x.Index));
        t.Undo();
        Assert.False(t.IsEdited);

        t.SetTrackVolume(2, 5);
        Assert.Equal(TrackEdit.MaxVolume, t.Tracks[2].Volume);
        t.SetTrackVolume(2, 0);
        Assert.DoesNotContain(t.AudibleTracks, x => x.Index == 2);
    }

    [Fact]
    public void VolumeDrag_IsOneUndoStep()
    {
        var t = Obs();
        t.BeginTrackVolumeChange();
        t.SetTrackVolume(3, 0.8, recordUndo: false);
        t.SetTrackVolume(3, 0.5, recordUndo: false);
        t.SetTrackVolume(3, 0.3, recordUndo: false);
        Assert.True(t.Undo());
        Assert.Equal(1, t.Tracks[3].Volume);
        Assert.False(t.CanUndo);
    }

    [Fact]
    public void ToggleMute_UsesSegmentBetweenCuts()
    {
        var t = Obs();
        t.Split(S(10));
        t.Split(S(20));
        Assert.True(t.ToggleTrackMuteAt(2, S(15)));
        Assert.Equal(new[] { new TimeRange(S(10), S(20)) }, t.Tracks[2].Muted);
        Assert.True(t.IsTrackMutedAt(2, S(12)));
        Assert.False(t.IsTrackMutedAt(2, S(25)));
        Assert.False(t.IsTrackMutedAt(3, S(12)));

        // Nochmal klicken = wieder laut
        t.ToggleTrackMuteAt(2, S(15));
        Assert.Empty(t.Tracks[2].Muted);
    }

    [Fact]
    public void MuteRanges_MergeAndSurviveNewCuts()
    {
        var t = Obs();
        t.Split(S(10));
        t.Split(S(20));
        t.ToggleTrackMuteAt(3, S(5));
        t.ToggleTrackMuteAt(3, S(15));
        Assert.Equal(new[] { new TimeRange(S(0), S(20)) }, t.Tracks[3].Muted);

        // Neuer Schnitt mitten im stummen Bereich, dann einen Teil wieder laut
        t.Split(S(15));
        t.ToggleTrackMuteAt(3, S(17));
        Assert.Equal(new[] { new TimeRange(S(0), S(15)) }, t.Tracks[3].Muted);
    }

    [Fact]
    public void Tracks_SaveAndLoad()
    {
        var t = Obs();
        t.Split(S(30));
        t.ToggleTrackMuteAt(2, S(40));
        t.SetTrackVolume(1, 1.5);
        t.SetTrackEnabled(0, true);
        var state = t.ToState();
        Assert.NotNull(state.Tracks);

        var json = System.Text.Json.JsonSerializer.Serialize(state);
        var loaded = Obs();
        loaded.Load(System.Text.Json.JsonSerializer.Deserialize<ClipEditState>(json)!);
        Assert.True(loaded.Tracks[0].Enabled);
        Assert.Equal(1.5, loaded.Tracks[1].Volume);
        Assert.Equal(new[] { new TimeRange(S(30), S(60)) }, loaded.Tracks[2].Muted);
    }

    [Fact]
    public void OldSavedState_WithoutTracks_KeepsDefaults()
    {
        var t = Obs();
        t.Load(new ClipEditState { In = 5, Out = 50 });
        Assert.False(t.Tracks[0].Enabled);
        Assert.True(t.Tracks[1].Enabled);
    }

    [Fact]
    public void Reset_RestoresTracks()
    {
        var t = Obs();
        t.SetTrackEnabled(2, false);
        t.Reset();
        Assert.True(t.Tracks[2].Enabled);
        Assert.False(t.IsEdited);
    }

    [Fact]
    public void RangeSet_AddSubtractCovers()
    {
        var r = RangeSet.Add(new[] { new TimeRange(S(0), S(5)) }, new TimeRange(S(3), S(8)));
        Assert.Equal(new[] { new TimeRange(S(0), S(8)) }, r);
        r = RangeSet.Subtract(r, new TimeRange(S(2), S(4)));
        Assert.Equal(new[] { new TimeRange(S(0), S(2)), new TimeRange(S(4), S(8)) }, r);
        Assert.True(RangeSet.Covers(r, new TimeRange(S(5), S(6))));
        Assert.False(RangeSet.Covers(r, new TimeRange(S(1), S(5))));
    }

    [Fact]
    public void MuteTrackRange_ByDragging()
    {
        var t = Obs();
        Assert.True(t.MuteTrackRange(3, S(20), S(12)));
        Assert.Equal(new[] { new TimeRange(S(12), S(20)) }, t.Tracks[3].Muted);
        Assert.False(t.MuteTrackRange(3, S(13), S(15))); // schon stumm
        Assert.Equal(0, t.MutedRangeIndexAt(3, S(14)));
        Assert.Equal(-1, t.MutedRangeIndexAt(3, S(25)));
        Assert.True(t.UnmuteTrackAt(3, S(14)));
        Assert.Empty(t.Tracks[3].Muted);
    }

    [Fact]
    public void MoveMutedEdge_ResizesAndMerges()
    {
        var t = Obs();
        t.MuteTrackRange(2, S(10), S(20));
        t.MuteTrackRange(2, S(30), S(40));
        t.BeginChange();
        Assert.Equal(S(8), t.MoveMutedEdge(2, 0, start: true, S(8), recordUndo: false));
        Assert.Equal(S(19.9), t.MoveMutedEdge(2, 0, start: true, S(25), recordUndo: false)); // nicht über das Ende hinaus
        t.Undo();
        Assert.Equal(S(10), t.Tracks[2].Muted[0].Start);

        // Ende über den nächsten Bereich ziehen → zusammengelegt
        t.MoveMutedEdge(2, 0, start: false, S(35));
        Assert.Equal(new[] { new TimeRange(S(10), S(40)) }, t.Tracks[2].Muted);
    }

    [Fact]
    public void MoveSplit_ResizesRemovedPart()
    {
        var t = Obs();
        t.RemoveRange(S(20), S(30));
        Assert.Equal(new[] { S(20), S(30) }, t.RemovedEdges);

        Assert.Equal(S(25), t.MoveSplit(S(30), S(25)));
        Assert.Equal(new[] { new TimeRange(S(0), S(20)), new TimeRange(S(25), S(60)) }, t.KeptRanges);

        Assert.Equal(S(15), t.MoveSplit(S(20), S(15)));
        Assert.Equal(new[] { new TimeRange(S(0), S(15)), new TimeRange(S(25), S(60)) }, t.KeptRanges);

        // Nicht über den Nachbarn hinaus
        Assert.Equal(S(24.9), t.MoveSplit(S(15), S(50)));
        Assert.Null(t.MoveSplit(S(3), S(4)));
    }

    [Fact]
    public void RestoreAt_BringsRemovedPartBack()
    {
        var t = Obs();
        t.RemoveRange(S(20), S(30));
        Assert.False(t.RestoreAt(S(10)));
        Assert.True(t.RestoreAt(S(25)));
        Assert.Equal(S(60), t.OutputDuration);
    }
}
