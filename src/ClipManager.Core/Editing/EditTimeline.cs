namespace ClipManager.Core.Editing;

public readonly record struct TimeRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;

    public bool Contains(TimeSpan t) => t >= Start && t < End;
}

public sealed record TimelineSegment(TimeSpan Start, TimeSpan End, bool Removed)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Speicherbare Form der Bearbeitung (Sekunden als Zahlen, damit es schlicht als JSON geht).
/// </summary>
public sealed class ClipEditState
{
    public double In { get; set; }

    public double Out { get; set; }

    public List<double> Splits { get; set; } = new();

    /// <summary>Startzeitpunkte der entfernten Teile.</summary>
    public List<double> Removed { get; set; } = new();
}

/// <summary>
/// Die Schnitte eines Clips: Anfang/Ende (In/Out), Schnittpunkte und entfernte Teile.
/// Ein Teil ("Segment") ist der Bereich zwischen zwei Schnittpunkten.
/// Das Original wird nie verändert; das hier beschreibt nur, was im Export landet.
/// </summary>
public sealed class EditTimeline
{
    /// <summary>Kürzer als das wird kein Teil (verhindert Mini-Schnipsel durch Doppelklicks).</summary>
    public static readonly TimeSpan MinSegment = TimeSpan.FromMilliseconds(100);

    private readonly List<TimeSpan> _splits = new();
    private readonly HashSet<TimeSpan> _removed = new();
    private readonly Stack<ClipEditState> _undo = new();

    public EditTimeline(TimeSpan duration)
    {
        Duration = duration;
        Out = duration;
    }

    public event EventHandler? Changed;

    public TimeSpan Duration { get; }

    public TimeSpan In { get; private set; }

    public TimeSpan Out { get; private set; }

    public IReadOnlyList<TimeSpan> Splits => _splits;

    /// <summary>Schnittpunkte, die gerade sichtbar sind (zwischen Anfang und Ende).</summary>
    public IEnumerable<TimeSpan> VisibleSplits => _splits.Where(s => s > In && s < Out);

    public bool CanUndo => _undo.Count > 0;

    /// <summary>True, wenn überhaupt etwas geändert wurde.</summary>
    public bool IsEdited => In > TimeSpan.Zero || Out < Duration || _removed.Count > 0;

    /// <summary>Alle Teile zwischen In und Out, mit Markierung, ob sie entfernt sind.</summary>
    public IReadOnlyList<TimelineSegment> Segments
    {
        get
        {
            var points = new List<TimeSpan> { In };
            points.AddRange(_splits.Where(s => s > In && s < Out));
            points.Add(Out);

            var result = new List<TimelineSegment>();
            for (var i = 0; i < points.Count - 1; i++)
            {
                result.Add(new TimelineSegment(points[i], points[i + 1], _removed.Contains(RegionStart(points[i]))));
            }

            return result;
        }
    }

    /// <summary>Was im Export bleibt, benachbarte Teile zusammengefasst.</summary>
    public IReadOnlyList<TimeRange> KeptRanges
    {
        get
        {
            var result = new List<TimeRange>();
            foreach (var segment in Segments.Where(s => !s.Removed))
            {
                if (result.Count > 0 && result[^1].End == segment.Start)
                {
                    result[^1] = result[^1] with { End = segment.End };
                }
                else
                {
                    result.Add(new TimeRange(segment.Start, segment.End));
                }
            }

            return result;
        }
    }

    public TimeSpan OutputDuration => KeptRanges.Aggregate(TimeSpan.Zero, (sum, r) => sum + r.Duration);

    public TimelineSegment? SegmentAt(TimeSpan t) =>
        Segments.FirstOrDefault(s => t >= s.Start && t < s.End)
        ?? (t == Out ? Segments.LastOrDefault() : null);

    /// <summary>Setzt den Anfang. Liegt er hinter dem Ende, passiert nichts.</summary>
    public bool SetIn(TimeSpan t)
    {
        t = Clamp(t);
        if (Out - t < MinSegment || t == In)
        {
            return false;
        }

        Mutate(() => In = t);
        return true;
    }

    public bool SetOut(TimeSpan t)
    {
        t = Clamp(t);
        if (t - In < MinSegment || t == Out)
        {
            return false;
        }

        Mutate(() => Out = t);
        return true;
    }

    /// <summary>Setzt einen Schnittpunkt. Der neue rechte Teil erbt "entfernt" vom bisherigen Teil.</summary>
    public bool Split(TimeSpan t)
    {
        t = Clamp(t);
        if (!CanSplit(t))
        {
            return false;
        }

        Mutate(() => SplitCore(t));
        return true;
    }

    /// <summary>Entfernt den Teil an Position t oder holt ihn zurück.</summary>
    public bool ToggleRemovedAt(TimeSpan t)
    {
        var segment = SegmentAt(t);
        if (segment == null)
        {
            return false;
        }

        // Mindestens ein Teil muss übrig bleiben.
        if (!segment.Removed && Segments.Count(s => !s.Removed) <= 1)
        {
            return false;
        }

        var key = RegionStart(segment.Start);
        Mutate(() =>
        {
            if (!_removed.Remove(key))
            {
                _removed.Add(key);
            }
        });
        return true;
    }

    /// <summary>Entfernt den Bereich zwischen a und b (setzt dafür zwei Schnitte).</summary>
    public bool RemoveRange(TimeSpan a, TimeSpan b)
    {
        var start = Clamp(a < b ? a : b);
        var end = Clamp(a < b ? b : a);
        if (end - start < MinSegment)
        {
            return false;
        }

        var before = Snapshot();
        if (CanSplit(start))
        {
            SplitCore(start);
        }

        if (CanSplit(end))
        {
            SplitCore(end);
        }

        foreach (var segment in Segments.Where(s => s.Start >= start && s.End <= end))
        {
            _removed.Add(RegionStart(segment.Start));
        }

        if (!Segments.Any(s => !s.Removed) || before.Removed.Count == _removed.Count)
        {
            Restore(before);
            return false;
        }

        _undo.Push(before);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Reset()
    {
        if (!IsEdited && _splits.Count == 0)
        {
            return;
        }

        Mutate(() =>
        {
            In = TimeSpan.Zero;
            Out = Duration;
            _splits.Clear();
            _removed.Clear();
        });
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        Restore(_undo.Pop());
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Für die Vorschau: Wohin springen, wenn die Wiedergabe bei t ist?
    /// Gibt t zurück, wenn t in einem behaltenen Teil liegt, sonst den Anfang des nächsten,
    /// oder null, wenn danach nichts mehr kommt.
    /// </summary>
    public TimeSpan? NextPlayable(TimeSpan t)
    {
        foreach (var range in KeptRanges)
        {
            if (t < range.Start)
            {
                return range.Start;
            }

            if (t < range.End)
            {
                return t;
            }
        }

        return null;
    }

    public ClipEditState ToState() => Snapshot();

    public void Load(ClipEditState state)
    {
        Restore(state);
        _undo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool CanSplit(TimeSpan t)
    {
        var segment = SegmentAt(t);
        return segment != null && t - segment.Start >= MinSegment && segment.End - t >= MinSegment;
    }

    private void SplitCore(TimeSpan t)
    {
        var inheritsRemoved = _removed.Contains(RegionStart(t));
        _splits.Add(t);
        _splits.Sort();
        if (inheritsRemoved)
        {
            _removed.Add(t);
        }
    }

    /// <summary>
    /// "Entfernt" hängt an Bereichen zwischen Schnittpunkten (unabhängig von Anfang/Ende).
    /// Schlüssel ist der Schnittpunkt links davon, ganz vorne 0. So bleibt die Markierung
    /// erhalten, wenn Anfang oder Ende verschoben werden.
    /// </summary>
    private TimeSpan RegionStart(TimeSpan t)
    {
        var result = TimeSpan.Zero;
        foreach (var split in _splits)
        {
            if (split > t)
            {
                break;
            }

            result = split;
        }

        return result;
    }

    private ClipEditState Snapshot() => new()
    {
        In = In.TotalSeconds,
        Out = Out.TotalSeconds,
        Splits = _splits.Select(s => s.TotalSeconds).ToList(),
        Removed = _removed.Select(s => s.TotalSeconds).OrderBy(s => s).ToList(),
    };

    private void Restore(ClipEditState state)
    {
        In = Clamp(TimeSpan.FromSeconds(state.In));
        Out = state.Out <= 0 ? Duration : Clamp(TimeSpan.FromSeconds(state.Out));
        if (Out - In < MinSegment)
        {
            In = TimeSpan.Zero;
            Out = Duration;
        }

        _splits.Clear();
        _splits.AddRange(state.Splits.Select(TimeSpan.FromSeconds).Where(s => s > TimeSpan.Zero && s < Duration).Distinct().OrderBy(s => s));
        _removed.Clear();
        foreach (var r in state.Removed.Select(TimeSpan.FromSeconds))
        {
            _removed.Add(r);
        }
    }

    private void Mutate(Action change)
    {
        _undo.Push(Snapshot());
        change();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private TimeSpan Clamp(TimeSpan t) =>
        t < TimeSpan.Zero ? TimeSpan.Zero : t > Duration ? Duration : t;
}
