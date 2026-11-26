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

    /// <summary>Standard-Ort des Zwischenspeichers: %LocalAppData%\ClipManager.</summary>
    public static string DefaultCacheRoot { get; private set; } = "";

    /// <summary>Aktueller Ort des Zwischenspeichers (Tonspuren in "audio", Bilder in "thumbs").</summary>
    public static string CacheRoot
    {
        get
        {
            var custom = Settings.CacheFolder;
            // Eigener Ordner auf einem Laufwerk, das gerade fehlt (z.B. USB-Platte): Standard nehmen.
            if (!string.IsNullOrWhiteSpace(custom) && Path.GetPathRoot(custom) is { Length: > 0 } drive && !Directory.Exists(drive))
            {
                return DefaultCacheRoot;
            }

            return Core.Storage.CacheFolder.Resolve(custom, DefaultCacheRoot);
        }
    }

    /// <summary>Zwischenspeicher (neu) an <see cref="CacheRoot"/> einrichten, z.B. nach dem Ändern des Ordners.</summary>
    public static void ApplyCacheRoot()
    {
        var root = CacheRoot;
        Thumbnails = new ThumbnailCache(Path.Combine(root, Core.Storage.CacheFolder.ThumbsFolder), ProcessRunner);
        Filmstrips = new FilmstripCache(Path.Combine(root, Core.Storage.CacheFolder.ThumbsFolder), ProcessRunner);
        AudioTracks = new AudioTrackCache(Path.Combine(root, Core.Storage.CacheFolder.AudioFolder), ProcessRunner)
        {
            MaxBytes = Settings.AudioCacheMaxBytes(),
        };
    }

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

        // Läuft Clippy schon? Dann das offene Fenster nach vorne holen statt ein zweites zu öffnen.
        if (!SingleInstance.TryStart(() => SingleInstance.BringToFront(MainWindow is ClipManager.MainWindow main ? main : null)))
        {
            Log.Info("Clippy läuft schon, das offene Fenster wird nach vorne geholt");
            Shutdown();
            return;
        }

        Log.Info($"Start, Version {AppUpdater.CurrentVersionText}, Ordner {AppDirectory} (BaseDirectory {AppContext.BaseDirectory})");

        // Startbildschirm zuerst zeigen, dann im Hintergrund laden.
        SplashWindow? splash = null;
        try
        {
            splash = new SplashWindow();
            splash.Show();
        }
        catch (Exception ex)
        {
            // Ohne Startbildschirm geht es auch.
            Log.Error("Startbildschirm konnte nicht angezeigt werden", ex);
            splash = null;
        }

        _ = StartAsync(splash, localDir);
    }

    /// <summary>Lädt alles Nötige, zeigt dabei die Schritte im Startbildschirm und öffnet dann das Hauptfenster.</summary>
    private async Task StartAsync(SplashWindow? splash, string localDir)
    {
        var started = DateTime.UtcNow;

        // Kurz warten, damit der Startbildschirm gezeichnet wird, bevor gearbeitet wird.
        async Task Step(string text, double percent)
        {
            splash?.SetStep(text, percent);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        try
        {
            await Step("Einstellungen laden …", 12);
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipManager");
            SettingsStore = new SettingsStore(Path.Combine(dataDir, "settings.json"));
            Settings = SettingsStore.Load();
            if (Settings.NewClipsSince == null)
            {
                // Erster Start mit "NEU"-Markierung: nur Clips ab jetzt gelten als neu.
                Settings.NewClipsSince = DateTime.UtcNow;
                SaveSettings();
            }
            Db = new LibraryDb(Path.Combine(dataDir, "library.db"));

            await Step("Zwischenspeicher vorbereiten …", 25);
            DefaultCacheRoot = localDir;
            ApplyCacheRoot();
            await Task.Run(() => AudioTracks.Cleanup());
            await Task.Run(CleanupExportTemp);

            await Step("FFmpeg suchen …", 40);
            var ffmpeg = LocateFfmpeg();
            Log.Info(ffmpeg == null ? "FFmpeg nicht gefunden" : $"FFmpeg: {ffmpeg.Ffmpeg}, ffprobe: {ffmpeg.Ffprobe ?? "fehlt"}");

            // Grafikkarten-Test läuft im Hintergrund weiter; höchstens kurz darauf warten.
            await Step("Grafikkarte prüfen …", 55);
            await Task.WhenAny(GetFfmpegStatusAsync(), Task.Delay(1500));

            await Step("Clips einlesen …", 75);
            var window = new MainWindow();
            await Task.WhenAny(window.FirstLoad, Task.Delay(5000));

            await Step("Fertig", 100);

            // Mindestens so lange zeigen, bis die Einblend-Animation durch ist.
            var shown = DateTime.UtcNow - started;
            var rest = SplashWindow.IntroDuration + TimeSpan.FromSeconds(0.4) - shown;
            if (splash != null && rest > TimeSpan.Zero)
            {
                await Task.Delay(rest);
            }

            MainWindow = window;
            window.Show();
            window.Activate();
            if (splash != null)
            {
                await splash.FadeOutAsync();
                splash.Close();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Fehler beim Start", ex);
            MessageBox.Show(
                $"Clippy konnte nicht starten:\n\n{ex.Message}",
                "Clippy",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
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
            "Clippy",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
