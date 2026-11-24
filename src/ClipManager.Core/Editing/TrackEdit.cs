namespace ClipManager.Core.Editing;

/// <summary>Bearbeitung einer Tonspur: an/aus, Lautstärke, stumme Stellen (Zeiten im Original).</summary>
public sealed record TrackEdit(int Index, bool Enabled, double Volume, IReadOnlyList<TimeRange> Muted)
{
    public const double MaxVolume = 2.0;

    /// <summary>Leiser gemachte Stellen (z.B. −12 dB), mit sanftem Übergang an den Rändern.</summary>
    public IReadOnlyList<DuckRange> Ducked { get; init; } = Array.Empty<DuckRange>();

    public bool IsMutedAt(TimeSpan t) => !Enabled || Volume <= 0 || RangeSet.Contains(Muted, t);

    /// <summary>Faktor (0–1) durch leiser gemachte Stellen bei t (Sekunden im Original); 1 = unverändert.</summary>
    public double DuckGainAt(double seconds)
    {
        var gain = 1.0;
        foreach (var duck in Ducked)
        {
            gain *= duck.GainAt(seconds);
        }

        return gain;
    }

    /// <summary>Inhaltlich gleich (die Listen sind eigene Objekte).</summary>
    public bool SameAs(TrackEdit other) =>
        Index == other.Index && Enabled == other.Enabled
        && Math.Abs(Volume - other.Volume) < 0.0001
        && Muted.SequenceEqual(other.Muted)
        && Ducked.SequenceEqual(other.Ducked);
}

/// <summary>
/// Eine leiser gemachte Stelle: im Bereich um <see cref="Db"/> leiser. An Anfang und Ende wird über
/// <see cref="FadeSeconds"/> sanft übergeblendet (innerhalb des Bereichs), damit es nicht springt.
/// </summary>
public sealed record DuckRange(TimeRange Range, double Db)
{
    /// <summary>Übergang an jedem Rand: kurz genug, dass es nicht schleppt, lang genug, dass es nicht knackt.</summary>
    public const double FadeSeconds = 0.3;

    public const double MinDb = -40;

    /// <summary>Übergang für diesen Bereich (bei sehr kurzen Bereichen höchstens die halbe Länge).</summary>
    public double Fade => Math.Min(FadeSeconds, Range.Duration.TotalSeconds / 2);

    /// <summary>Anteil (0–1) der vollen Absenkung bei t: 0 außerhalb, steigt am Anfang an, fällt am Ende ab.</summary>
    public double WeightAt(double seconds)
    {
        var start = Range.Start.TotalSeconds;
        var end = Range.End.TotalSeconds;
        var fade = Fade;
        if (seconds <= start || seconds >= end || fade <= 0)
        {
            return 0;
        }

        return Math.Clamp(Math.Min((seconds - start) / fade, (end - seconds) / fade), 0, 1);
    }

    /// <summary>Lautstärke-Faktor bei t (Übergang in dB, klingt gleichmäßiger).</summary>
    public double GainAt(double seconds)
    {
        var weight = WeightAt(seconds);
        return weight <= 0 ? 1 : Math.Pow(10, Db * weight / 20);
    }
}

/// <summary>Speicherbare Form (JSON) einer Spur.</summary>
public sealed class TrackEditState
{
    public int Index { get; set; }

    public bool Enabled { get; set; } = true;

    public double Volume { get; set; } = 1;

    /// <summary>Stumme Stellen als Paare [Start, Ende] in Sekunden.</summary>
    public List<double[]> Muted { get; set; } = new();

    /// <summary>Leiser gemachte Stellen als [Start, Ende, dB].</summary>
    public List<double[]> Ducked { get; set; } = new();
}
