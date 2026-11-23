using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClipManager.Core;
using ClipManager.Core.Library;
using ClipManager.Core.Media;
using ClipManager.Core.Settings;

namespace ClipManager;

public partial class App : Application
{
    public static SettingsStore SettingsStore { get; private set; } = null!;

    public static LibraryDb Db { get; private set; } = null!;

    public static ThumbnailCache Thumbnails { get; private set; } = null!;

    public static FilmstripCache Filmstrips { get; private set; } = null!;

    public static AudioTrackCache AudioTracks { get; private set; } = null!;

    /// <summary>Hier landen Exporte, bis man sie speichert (Reinziehen/Kopieren braucht keine zweite Datei).</summary>
    public static string ExportTempFolder => Path.Combine(Path.GetTempPath(), "ClipManager", "Export");

    public static IProcessRunner ProcessRunner { get; } = new ProcessRunner();

    public static AppSettings Settings { get; private set; } = null!;

    /// <summary>
    /// Ordner, in dem die ClipManager.exe liegt. Bewusst über den Prozesspfad und nicht über
    /// AppContext.BaseDirectory, das bei Single-File-Programmen auf einen Entpack-Ordner zeigen kann.
    /// </summary>
    public static string AppDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } dir ? dir : AppContext.BaseDirectory;

    /// <summary>
    /// True, wenn das Programm aus einem Temp-Ordner läuft, z.B. weil es direkt aus der ZIP-Datei
    /// gestartet wurde, ohne sie zu entpacken. Dann fehlt der ffmpeg-Ordner daneben.
    /// </summary>
    public static bool RunsFromTempFolder =>
        AppDirectory.StartsWith(Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Standard-Exportordner, falls in den Einstellungen keiner gewählt ist.</summary>
    public static string DefaultExportFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clip Manager Exporte");

    public static string ExportFolder =>
        string.IsNullOrWhiteSpace(Settings.ExportFolder) ? DefaultExportFolder : Settings.ExportFolder;

    public static string LogFolder => Path.GetDirectoryName(Log.FilePath) ?? AppDirectory;

    private static Task<FfmpegStatus>? _ffmpegStatus;

    /// <summary>FFmpeg- und GPU-Prüfung, nur einmal pro Programmstart (der GPU-Test dauert ein paar Sekunden).</summary>
    public static Task<FfmpegStatus> GetFfmpegStatusAsync() =>
        _ffmpegStatus ??= new FfmpegProbe(ProcessRunner).CheckAsync(LocateFfmpeg());

    public static void SaveSettings() => SettingsStore.Save(Settings);

    public static FfmpegPaths? LocateFfmpeg() =>
        FfmpegLocator.Locate(Settings.FfmpegPath, AppDirectory, Environment.GetEnvironmentVariable("PATH"));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unbeobachteter Fehler in Hintergrundaufgabe", args.Exception);
            args.SetObserved();
        };

        var localDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipManager");
        Log.FilePath = Path.Combine(localDir, "log.txt");
        Log.Info($"Start, Version {AppUpdater.CurrentVersionText}, Ordner {AppDirectory} (BaseDirectory {AppContext.BaseDirectory})");

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipManager");
        SettingsStore = new SettingsStore(Path.Combine(dataDir, "settings.json"));
        Settings = SettingsStore.Load();
        Db = new LibraryDb(Path.Combine(dataDir, "library.db"));

        Thumbnails = new ThumbnailCache(Path.Combine(localDir, "thumbs"), ProcessRunner);
        Filmstrips = new FilmstripCache(Path.Combine(localDir, "thumbs"), ProcessRunner);
        AudioTracks = new AudioTrackCache(Path.Combine(localDir, "audio"), ProcessRunner);
        CleanupExportTemp();

        var ffmpeg = LocateFfmpeg();
        Log.Info(ffmpeg == null ? "FFmpeg nicht gefunden" : $"FFmpeg: {ffmpeg.Ffmpeg}, ffprobe: {ffmpeg.Ffprobe ?? "fehlt"}");

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>Alte Zwischen-Exporte wegräumen (älter als 2 Tage, z.B. nur in Discord gezogen).</summary>
    private static void CleanupExportTemp()
    {
        try
        {
            if (!Directory.Exists(ExportTempFolder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(ExportTempFolder))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(2))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Alte Exporte konnten nicht aufgeräumt werden", ex);
        }
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unbehandelter Fehler", e.Exception);
        MessageBox.Show(
            $"Da ist etwas schiefgelaufen:\n\n{e.Exception.Message}",
            "Clip Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
