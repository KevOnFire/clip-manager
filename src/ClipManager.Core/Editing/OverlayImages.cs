using System.Security.Cryptography;

namespace ClipManager.Core.Editing;

/// <summary>
/// Bilder für Objekte werden in einen eigenen Ordner kopiert (Name = Prüfsumme), damit sie
/// auch dann noch da sind, wenn das Original verschoben oder gelöscht wird.
/// </summary>
public static class OverlayImages
{
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Kopiert das Bild (falls noch nicht da) und gibt den Pfad der Kopie zurück.</summary>
    public static string Import(string source, string folder)
    {
        Directory.CreateDirectory(folder);
        string hash;
        using (var stream = File.OpenRead(source))
        {
            hash = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
        }

        var target = Path.Combine(folder, hash + Path.GetExtension(source).ToLowerInvariant());
        if (!File.Exists(target))
        {
            File.Copy(source, target);
        }

        return target;
    }
}
