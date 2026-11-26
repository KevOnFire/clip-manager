using ClipManager.Core.Library;

namespace ClipManager.Core.Tests;

public class PageOfTests
{
    [Fact]
    public void PageOf_FindsPageOfClip_IgnoringCase()
    {
        var clips = Enumerable.Range(0, 95)
            .Select(i => new ClipFile($@"C:\Clips\clip{i}.mp4", @"C:\Clips", 1, DateTime.UtcNow))
            .ToList();
        Assert.Equal(0, LibraryQuery.PageOf(clips, @"C:\Clips\clip0.mp4", 40));
        Assert.Equal(1, LibraryQuery.PageOf(clips, @"c:\clips\CLIP40.mp4", 40));
        Assert.Equal(2, LibraryQuery.PageOf(clips, @"C:\Clips\clip89.mp4", 40));
        Assert.Null(LibraryQuery.PageOf(clips, @"C:\Clips\weg.mp4", 40));
    }
}
