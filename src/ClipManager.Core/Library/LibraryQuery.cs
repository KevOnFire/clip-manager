namespace ClipManager.Core.Library;

public enum ClipSort
{
    Newest,
    Oldest,
    Longest,
    Largest,
    Name,
    EditedFirst,
}

/// <summary>Filter: alle, nur geschnittene oder nur ungeschnittene Clips.</summary>
public enum EditedFilter
{
    All,
    Edited,
    Unedited,
}

public sealed record LibraryFilter(
    bool FavoritesOnly = false,
    string? Folder = null,
    string? Search = null,
    ClipSort Sort = ClipSort.Newest,
    EditedFilter Edited = EditedFilter.All,
    IReadOnlySet<string>? OnlyPaths = null);

/// <summary>Filtern, Sortieren und Seiten für die Bibliothek.</summary>
public static class LibraryQuery
{
    public static IReadOnlyList<ClipFile> Apply(
        IEnumerable<ClipFile> clips,
        LibraryFilter filter,
        IReadOnlySet<string> favorites,
        IReadOnlyDictionary<string, TimeSpan> durations,
        IReadOnlySet<string>? edited = null,
        IReadOnlyDictionary<string, string>? displayNames = null)
    {
        var query = clips;
        bool IsEdited(ClipFile c) => edited?.Contains(c.Path) == true;
        string Title(ClipFile c) => displayNames != null && displayNames.TryGetValue(c.Path, out var n) ? n : c.Name;

        query = filter.Edited switch
        {
            EditedFilter.Edited => query.Where(IsEdited),
            EditedFilter.Unedited => query.Where(c => !IsEdited(c)),
            _ => query,
        };

        // Eigene Kategorie: nur Clips, die drin sind.
        if (filter.OnlyPaths is { } only)
        {
            query = query.Where(c => only.Contains(c.Path));
        }

        if (filter.FavoritesOnly)
        {
            query = query.Where(c => favorites.Contains(c.Path));
        }

        if (!string.IsNullOrWhiteSpace(filter.Folder))
        {
            query = query.Where(c => string.Equals(c.RootFolder, filter.Folder, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var words = filter.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            query = query.Where(c => words.All(w =>
                c.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || Title(c).Contains(w, StringComparison.OrdinalIgnoreCase)));
        }

        // Clips ohne bekannte Dauer landen beim Sortieren nach Länge hinten.
        TimeSpan Duration(ClipFile c) => durations.TryGetValue(c.Path, out var d) ? d : TimeSpan.MinValue;

        query = filter.Sort switch
        {
            ClipSort.Oldest => query.OrderBy(c => c.ModifiedUtc),
            ClipSort.Longest => query.OrderByDescending(Duration).ThenByDescending(c => c.ModifiedUtc),
            ClipSort.Largest => query.OrderByDescending(c => c.SizeBytes),
            ClipSort.Name => query.OrderBy(Title, StringComparer.CurrentCultureIgnoreCase),
            ClipSort.EditedFirst => query.OrderByDescending(IsEdited).ThenByDescending(c => c.ModifiedUtc),
            _ => query.OrderByDescending(c => c.ModifiedUtc),
        };

        return query.ToList();
    }

    /// <summary>
    /// Clips pro Seite, so dass jede Reihe voll ist: Spalten (aus Breite und Kachelbreite inkl. Abstand)
    /// mal Reihen. Mindestens eine Spalte.
    /// </summary>
    public static int PageSizeFor(double availableWidth, double tileSlotWidth, int rows)
    {
        var columns = tileSlotWidth > 0 && availableWidth > 0 ? (int)Math.Floor(availableWidth / tileSlotWidth) : 1;
        return Math.Max(1, columns) * Math.Max(1, rows);
    }

    /// <summary>Seite, auf der der erste Clip der alten Seite nach dem Ändern der Seitengröße landet.</summary>
    public static int PageAfterResize(int page, int oldPageSize, int newPageSize) =>
        newPageSize <= 0 ? 0 : Math.Max(0, page) * Math.Max(1, oldPageSize) / newPageSize;

    /// <summary>
    /// Eingabe im Seitenfeld (1-basiert, wie angezeigt) in eine Seite (0-basiert) umwandeln.
    /// Zu groß/klein wird auf die erste/letzte Seite gesetzt; Unsinn gibt null.
    /// </summary>
    public static int? ParsePageInput(string? text, int pageCount)
    {
        if (!int.TryParse(text?.Trim(), out var page))
        {
            return null;
        }

        return Math.Clamp(page, 1, Math.Max(1, pageCount)) - 1;
    }

    /// <summary>
    /// Bereich für Mehrfachauswahl (Umschalt-Klick oder Ziehen): alle Positionen von <paramref name="anchor"/>
    /// bis <paramref name="current"/>, egal in welche Richtung.
    /// </summary>
    public static IEnumerable<int> SelectionRange(int anchor, int current)
    {
        var from = Math.Min(anchor, current);
        var to = Math.Max(anchor, current);
        return Enumerable.Range(from, to - from + 1);
    }

    public static int PageCount(int total, int pageSize) =>
        Math.Max(1, (total + pageSize - 1) / pageSize);

    public static IReadOnlyList<T> Page<T>(IReadOnlyList<T> items, int page, int pageSize)
    {
        var safePage = Math.Clamp(page, 0, PageCount(items.Count, pageSize) - 1);
        return items.Skip(safePage * pageSize).Take(pageSize).ToList();
    }

    /// <summary>Seite (0-basiert), auf der der Clip mit diesem Pfad liegt, oder null, wenn er nicht (mehr) in der Liste ist.</summary>
    public static int? PageOf(IReadOnlyList<ClipFile> items, string path, int pageSize)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return pageSize > 0 ? i / pageSize : 0;
            }
        }

        return null;
    }
}
