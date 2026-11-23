namespace ClipManager.Core.Library;

/// <summary>Kopieren und Umbenennen von Clip-Dateien.</summary>
public static class ClipFileOps
{
    /// <summary>Erstellt "Name (Kopie).mp4" bzw. "Name (Kopie 2).mp4" im selben Ordner.</summary>
    public static string CreateCopy(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        var target = Path.Combine(dir, $"{name} (Kopie){ext}");
        for (var i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(dir, $"{name} (Kopie {i}){ext}");
        }

        File.Copy(path, target);
        return target;
    }

    /// <summary>Prüft einen neuen Dateinamen (ohne Endung). Gibt eine Fehlermeldung zurück oder null, wenn alles passt.</summary>
    public static string? ValidateName(string? newName)
    {
        var name = newName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return "Der Name darf nicht leer sein.";
        }

        // Windows-Regeln gelten immer, auch wenn die Tests unter Linux laufen.
        const string invalid = "<>:\"/\\|?*";
        if (name.Any(c => invalid.Contains(c) || c < 32))
        {
            return "Diese Zeichen sind nicht erlaubt: < > : \" / \\ | ? *";
        }

        if (name.EndsWith('.'))
        {
            return "Der Name darf nicht mit einem Punkt enden.";
        }

        if (name.Length > 200)
        {
            return "Der Name ist zu lang.";
        }

        return null;
    }

    /// <summary>Benennt die Datei um, die Endung bleibt. Gibt den neuen Pfad zurück.</summary>
    public static string Rename(string path, string newName)
    {
        if (ValidateName(newName) is { } error)
        {
            throw new ArgumentException(error, nameof(newName));
        }

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var target = Path.Combine(dir, newName.Trim() + Path.GetExtension(path));
        if (string.Equals(target, path, StringComparison.Ordinal))
        {
            return path;
        }

        // Nur Groß-/Kleinschreibung geändert: unter Windows ist das dieselbe Datei.
        var sameFile = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
        if (!sameFile && File.Exists(target))
        {
            throw new IOException($"Es gibt schon einen Clip namens \"{Path.GetFileName(target)}\".");
        }

        File.Move(path, target);
        return target;
    }
}
