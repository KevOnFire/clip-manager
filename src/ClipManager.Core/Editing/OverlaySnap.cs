namespace ClipManager.Core.Editing;

/// <summary>
/// Einrasten der Objekt-Balken in der Zeitleiste: Liegt ein Rand fast auf dem Rand eines anderen Objekts
/// (oder auf Anfang, Ende, Abspielkopf), springt er genau dorthin, damit die Balken sauber bündig sind.
/// </summary>
public static class OverlaySnap
{
    /// <summary>Mögliche Einrast-Stellen in Sekunden: Ränder der anderen Objekte, Clip-Anfang/-Ende, Abspielkopf.</summary>
    public static List<double> Targets(IEnumerable<OverlayItem> items, string? exceptId, double duration, double? playhead = null)
    {
        var targets = new List<double> { 0, duration };
        foreach (var item in items)
        {
            if (item.Id != exceptId)
            {
                targets.Add(item.Start);
                targets.Add(item.End);
            }
        }

        if (playhead is { } p)
        {
            targets.Add(p);
        }

        return targets.Distinct().ToList();
    }

    /// <summary>Nächste Einrast-Stelle innerhalb der Toleranz, sonst null.</summary>
    public static double? Nearest(double value, IReadOnlyList<double> targets, double tolerance)
    {
        double? best = null;
        var bestDistance = tolerance;
        foreach (var t in targets)
        {
            var d = Math.Abs(t - value);
            if (d <= bestDistance)
            {
                best = t;
                bestDistance = d;
            }
        }

        return best;
    }

    /// <summary>
    /// Ganzen Balken verschieben: Anfang oder Ende rastet ein (was näher dran ist), die Länge bleibt.
    /// Gibt auch die Stelle zurück, an der eingerastet wurde (für die Hilfslinie).
    /// </summary>
    public static (double Start, double End, double? Line) Move(double start, double end, IReadOnlyList<double> targets, double tolerance, double duration)
    {
        var length = end - start;
        var original = start;
        var s = Nearest(start, targets, tolerance);
        var e = Nearest(end, targets, tolerance);
        double? line = null;
        if (s is { } sv && (e is not { } ev0 || Math.Abs(sv - start) <= Math.Abs(ev0 - end)))
        {
            start = sv;
            line = sv;
        }
        else if (e is { } ev)
        {
            start = ev - length;
            line = ev;
        }

        if (start < 0 || start + length > duration + 1e-9)
        {
            // Würde rausrutschen: lieber nicht einrasten.
            return (original, original + length, null);
        }

        return (start, start + length, line);
    }

    /// <summary>Einen Rand länger/kürzer ziehen; rastet nur ein, wenn die Mindestlänge bleibt.</summary>
    public static (double Value, double? Line) Edge(double value, IReadOnlyList<double> targets, double tolerance, double min, double max)
    {
        if (Nearest(value, targets, tolerance) is { } snapped && snapped >= min - 1e-9 && snapped <= max + 1e-9)
        {
            return (snapped, snapped);
        }

        return (value, null);
    }
}
