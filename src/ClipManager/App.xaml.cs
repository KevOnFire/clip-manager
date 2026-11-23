using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClipManager.Core.Media;
using ClipManager.Core.Settings;

namespace ClipManager;

public partial class App : Application
{
    public static SettingsStore SettingsStore { get; private set; } = null!;

    public static AppSettings Settings { get; private set; } = null!;

    /// <summary>Ordner, in dem die ClipManager.exe liegt.</summary>
    public static string AppDirectory => AppContext.BaseDirectory;

    /// <summary>Standard-Exportordner, falls in den Einstellungen keiner gewählt ist.</summary>
    public static string DefaultExportFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clip Manager Exporte");

    public static string ExportFolder =>
        string.IsNullOrWhiteSpace(Settings.ExportFolder) ? DefaultExportFolder : Settings.ExportFolder;

    public static void SaveSettings() => SettingsStore.Save(Settings);

    public static FfmpegPaths? LocateFfmpeg() =>
        FfmpegLocator.Locate(Settings.FfmpegPath, AppDirectory, Environment.GetEnvironmentVariable("PATH"));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipManager");
        SettingsStore = new SettingsStore(Path.Combine(dataDir, "settings.json"));
        Settings = SettingsStore.Load();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Da ist etwas schiefgelaufen:\n\n{e.Exception.Message}",
            "Clip Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
