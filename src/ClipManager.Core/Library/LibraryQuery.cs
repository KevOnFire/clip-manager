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
    EditedFilter Edited = EditedFilter.All);

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

    public static int PageCount(int total, int pageSize) =>
        Math.Max(1, (total + pageSize - 1) / pageSize);

    public static IReadOnlyList<T> Page<T>(IReadOnlyList<T> items, int page, int pageSize)
    {
        var safePage = Math.Clamp(page, 0, PageCount(items.Count, pageSize) - 1);
        return items.Skip(safePage * pageSize).Take(pageSize).ToList();
    }
}
