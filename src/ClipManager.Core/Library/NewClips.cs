namespace ClipManager.Core.Library;

/// <summary>Welche Clips als "neu" (noch nicht angeschaut) markiert werden.</summary>
public static class NewClips
{
    public static bool IsNew(ClipFile clip, DateTime? since, IReadOnlySet<string> seen) =>
        since is { } start && clip.ModifiedUtc > start && !seen.Contains(clip.Path);
}
