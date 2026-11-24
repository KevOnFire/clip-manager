namespace ClipManager.Core.Editing;

/// <summary>Bearbeitung einer Tonspur: an/aus, Lautstärke, stumme Stellen (Zeiten im Original).</summary>
public sealed record TrackEdit(int Index, bool Enabled, double Volume, IReadOnlyList<TimeRange> Muted)
{
    public const double MaxVolume = 2.0;

    /// <summary>Stellen mit geänderter Lautstärke (z.B. −12 dB oder +6 dB), mit sanftem Übergang an den Rändern.</summary>
    public IReadOnlyList<DuckRange> Ducked { get; init; } = Array.Empty<DuckRange>();

    public bool IsMutedAt(TimeSpan t) => !Enabled || Volume <= 0 || RangeSet.Contains(Muted, t);

    /// <summary>Faktor durch Stellen mit geänderter Lautstärke bei t (Sekunden im Original); 1 = unverändert.</summary>
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
/// Eine Stelle mit geänderter Lautstärke: im Bereich um <see cref="Db"/> leiser (minus) oder lauter (plus).
/// Am Anfang wird über <see cref="FadeIn"/> Sekunden hingeblendet, am Ende über <see cref="FadeOut"/> zurück
/// (beides innerhalb des Bereichs). 0 = sofort.
/// </summary>
public sealed record DuckRange(TimeRange Range, double Db, double FadeIn = DuckRange.FadeSeconds, double FadeOut = DuckRange.FadeSeconds)
{
    /// <summary>Standard-Übergang: kurz genug, dass es nicht schleppt, lang genug, dass es nicht knackt.</summary>
    public const double FadeSeconds = 0.3;

    public const double MinDb = -40;
    public const double MaxDb = 12;
    public const double MaxFade = 5;

    /// <summary>Wert auf den erlaubten Bereich bringen (dB −40…+12, Übergänge 0–5 s).</summary>
    public static DuckRange Create(TimeRange range, double db, double fadeIn = FadeSeconds, double fadeOut = FadeSeconds) =>
        new(range,
            Math.Round(Math.Clamp(double.IsFinite(db) ? db : 0, MinDb, MaxDb), 1),
            Math.Round(Math.Clamp(double.IsFinite(fadeIn) ? fadeIn : FadeSeconds, 0, MaxFade), 2),
            Math.Round(Math.Clamp(double.IsFinite(fadeOut) ? fadeOut : FadeSeconds, 0, MaxFade), 2));

    /// <summary>Tatsächliche Übergänge: passen beide nicht in den Bereich, werden sie anteilig gekürzt.</summary>
    public (double In, double Out) Fades
    {
        get
        {
            var duration = Range.Duration.TotalSeconds;
            var total = FadeIn + FadeOut;
            return total <= duration || total <= 0
                ? (FadeIn, FadeOut)
                : (FadeIn * duration / total, FadeOut * duration / total);
        }
    }

    /// <summary>Anteil (0–1) der vollen Änderung bei t: 0 außerhalb, steigt am Anfang an, fällt am Ende ab.</summary>
    public double WeightAt(double seconds)
    {
        var start = Range.Start.TotalSeconds;
        var end = Range.End.TotalSeconds;
        if (seconds < start || seconds >= end)
        {
            return 0;
        }

        var (fadeIn, fadeOut) = Fades;
        var up = fadeIn > 0 ? (seconds - start) / fadeIn : 1;
        var down = fadeOut > 0 ? (end - seconds) / fadeOut : 1;
        return Math.Clamp(Math.Min(up, down), 0, 1);
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

    /// <summary>Stellen mit geänderter Lautstärke als [Start, Ende, dB, Übergang rein, Übergang raus] (ältere: nur 3 Werte).</summary>
    public List<double[]> Ducked { get; set; } = new();
}
