using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using ClipManager.Core.Updates;

namespace ClipManager;

/// <summary>Sucht nach neuen Versionen auf GitHub und installiert sie.</summary>
internal static class AppUpdater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    /// <summary>Gibt ein neueres Release zurück, oder null (auch wenn offline).</summary>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await new UpdateChecker(Http).GetLatestAsync(cancellationToken);
            return release != null && UpdateChecker.IsNewer(release.Version, CurrentVersion) ? release : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>Kann das Programm in seinen eigenen Ordner schreiben? (Nicht der Fall z.B. unter C:\Program Files.)</summary>
    public static bool CanWriteAppDirectory()
    {
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, ".update-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Lädt das Release herunter, entpackt es und startet ein Skript, das nach dem Beenden
    /// die Dateien ersetzt und das Programm neu startet. Danach muss das Programm beendet werden.
    /// </summary>
    public static async Task PrepareAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "ClipManagerUpdate");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "update.zip");
        var staging = Path.Combine(root, "files");

        using (var response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.SizeBytes;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(zipPath);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                if (total > 0)
                {
                    progress.Report((double)done / total);
                }
            }
        }

        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, staging), cancellationToken);

        var exeName = Path.GetFileName(Environment.ProcessPath) ?? "ClipManager.exe";
        if (!File.Exists(Path.Combine(staging, "ClipManager.exe")))
        {
            throw new IOException("Das Update-Paket ist unvollständig (ClipManager.exe fehlt).");
        }

        var script = Path.Combine(root, "update.cmd");
        await File.WriteAllTextAsync(
            script,
            UpdateScript.Build(Environment.ProcessId, staging, AppContext.BaseDirectory, exeName),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }
}
