namespace ClipManager.Core.Library;

/// <summary>Ein Videoclip auf der Platte.</summary>
public sealed record ClipFile(string Path, string RootFolder, long SizeBytes, DateTime ModifiedUtc)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}
