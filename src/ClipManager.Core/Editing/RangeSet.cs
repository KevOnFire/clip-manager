namespace ClipManager.Core.Editing;

/// <summary>Hilfen für sortierte, nicht überlappende Zeitbereiche (z.B. stumme Stellen einer Spur).</summary>
public static class RangeSet
{
    /// <summary>Fügt einen Bereich hinzu und fasst überlappende/angrenzende zusammen.</summary>
    public static List<TimeRange> Add(IEnumerable<TimeRange> ranges, TimeRange add)
    {
        var all = ranges.Append(add).Where(r => r.End > r.Start).OrderBy(r => r.Start).ToList();
        var result = new List<TimeRange>();
        foreach (var r in all)
        {
            if (result.Count > 0 && r.Start <= result[^1].End)
            {
                if (r.End > result[^1].End)
                {
                    result[^1] = result[^1] with { End = r.End };
                }
            }
            else
            {
                result.Add(r);
            }
        }

        return result;
    }

    /// <summary>Entfernt einen Bereich (schneidet überlappende Bereiche zurecht).</summary>
    public static List<TimeRange> Subtract(IEnumerable<TimeRange> ranges, TimeRange remove)
    {
        var result = new List<TimeRange>();
        foreach (var r in ranges)
        {
            if (r.End <= remove.Start || r.Start >= remove.End)
            {
                result.Add(r);
                continue;
            }

            if (r.Start < remove.Start)
            {
                result.Add(new TimeRange(r.Start, remove.Start));
            }

            if (r.End > remove.End)
            {
                result.Add(new TimeRange(remove.End, r.End));
            }
        }

        return result;
    }

    /// <summary>True, wenn der Bereich komplett abgedeckt ist.</summary>
    public static bool Covers(IEnumerable<TimeRange> ranges, TimeRange range) =>
        ranges.Any(r => r.Start <= range.Start && r.End >= range.End);

    public static bool Contains(IEnumerable<TimeRange> ranges, TimeSpan t) => ranges.Any(r => r.Contains(t));
}
