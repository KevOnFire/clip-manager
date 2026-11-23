namespace ClipManager.Core.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clipmgr-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative, DateTime? modifiedUtc = null, int bytes = 1)
    {
        // GetFullPath macht aus "/" unter Windows "\", damit Pfade mit denen vom Dateisystem übereinstimmen.
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relative));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, new byte[bytes]);
        if (modifiedUtc is { } t)
        {
            System.IO.File.SetLastWriteTimeUtc(full, t);
        }

        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
