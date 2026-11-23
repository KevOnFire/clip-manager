namespace ClipManager.Core.Updates;

/// <summary>
/// Baut das kleine Windows-Skript, das nach dem Beenden des Programms die neuen Dateien
/// über den Programmordner kopiert und das Programm neu startet.
/// (Ein laufendes Programm kann sich unter Windows nicht selbst überschreiben.)
/// </summary>
public static class UpdateScript
{
    public static string Build(int processId, string stagingDirectory, string appDirectory, string exeName)
    {
        var staging = stagingDirectory.TrimEnd('\\', '/');
        var app = appDirectory.TrimEnd('\\', '/');
        return string.Join("\r\n", new[]
        {
            "@echo off",
            "chcp 65001 >nul",
            ":wait",
            $"tasklist /FI \"PID eq {processId}\" 2>nul | find \"{processId}\" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto wait",
            ")",
            $"robocopy \"{staging}\" \"{app}\" /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
            $"start \"\" \"{app}\\{exeName}\"",
            $"rmdir /S /Q \"{staging}\" >nul 2>&1",
            "(goto) 2>nul & del \"%~f0\"",
            string.Empty,
        });
    }
}
