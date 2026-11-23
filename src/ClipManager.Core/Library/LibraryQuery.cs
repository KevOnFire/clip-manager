namespace ClipManager.Core.Library;

public enum ClipSort
{
    Newest,
    Oldest,
    Longest,
    Largest,
    Name,
}

public sealed record LibraryFilter(
    bool FavoritesOnly = false,
    string? Folder = null,
    string? Search = null,
    ClipSort Sort = ClipSort.Newest);

/// <summary>Filtern, Sortieren und Seiten für die Bibliothek.</summary>
public static class LibraryQuery
{
    public static IReadOnlyList<ClipFile> Apply(
        IEnumerable<ClipFile> clips,
        LibraryFilter filter,
        IReadOnlySet<string> favorites,
        IReadOnlyDictionary<string, TimeSpan> durations)
    {
        var query = clips;

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
            query = query.Where(c => words.All(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }

        // Clips ohne bekannte Dauer landen beim Sortieren nach Länge hinten.
        TimeSpan Duration(ClipFile c) => durations.TryGetValue(c.Path, out var d) ? d : TimeSpan.MinValue;

        query = filter.Sort switch
        {
            ClipSort.Oldest => query.OrderBy(c => c.ModifiedUtc),
            ClipSort.Longest => query.OrderByDescending(Duration).ThenByDescending(c => c.ModifiedUtc),
            ClipSort.Largest => query.OrderByDescending(c => c.SizeBytes),
            ClipSort.Name => query.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderByDescending(c => c.ModifiedUtc),
        };

        return query.ToList();
    }

    public static int PageCount(int total, int pageSize) =>
        Math.Max(1, (total + pageSize - 1) / pageSize);

    public static IReadOnlyList<T> Page<T>(IReadOnlyList<T> items, int page, int pageSize)
    {
        var safePage = Math.Clamp(page, 0, PageCount(items.Count, pageSize) - 1);
        return items.Skip(safePage * pageSize).Take(pageSize).ToList();
    }
}
