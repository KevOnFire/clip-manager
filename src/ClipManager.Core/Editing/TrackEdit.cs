namespace ClipManager.Core.Editing;

/// <summary>Bearbeitung einer Tonspur: an/aus, Lautstärke, stumme Stellen (Zeiten im Original).</summary>
public sealed record TrackEdit(int Index, bool Enabled, double Volume, IReadOnlyList<TimeRange> Muted)
{
    public const double MaxVolume = 2.0;

    public bool IsMutedAt(TimeSpan t) => !Enabled || Volume <= 0 || RangeSet.Contains(Muted, t);
}

/// <summary>Speicherbare Form (JSON) einer Spur.</summary>
public sealed class TrackEditState
{
    public int Index { get; set; }

    public bool Enabled { get; set; } = true;

    public double Volume { get; set; } = 1;

    /// <summary>Stumme Stellen als Paare [Start, Ende] in Sekunden.</summary>
    public List<double[]> Muted { get; set; } = new();
}
