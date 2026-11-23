using System.Windows;
using ClipManager.Core.Updates;
using ClipManager.Views;
using Wpf.Ui.Controls;

namespace ClipManager;

public partial class MainWindow : FluentWindow
{
    private readonly LibraryView _library = new();
    private readonly SettingsView _settings = new();
    private ReleaseInfo? _pendingUpdate;
    private bool _installing;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppUpdater.CurrentVersionText}";
        ShowLibrary();
        Loaded += async (_, _) => ShowUpdate(await AppUpdater.CheckAsync());
    }

    /// <summary>Zeigt den Update-Knopf an (oder versteckt ihn bei null).</summary>
    public void ShowUpdate(ReleaseInfo? release)
    {
        _pendingUpdate = release;
        UpdateButton.Visibility = release == null ? Visibility.Collapsed : Visibility.Visible;
        if (release != null && !_installing)
        {
            UpdateButton.Content = $"Update auf {release.Version.ToString(3)}";
        }
    }

    /// <summary>Fragt nach und installiert das Update. Das Programm startet danach neu.</summary>
    public async Task InstallUpdateAsync(ReleaseInfo release)
    {
        if (_installing)
        {
            return;
        }

        if (!AppUpdater.CanWriteAppDirectory())
        {
            await Dialogs.ErrorAsync(
                "Das Programm liegt in einem geschützten Ordner (z.B. \"Programme\") und kann sich dort nicht selbst aktualisieren. " +
                "Verschieb den ClipManager-Ordner z.B. auf den Desktop oder nach Dokumente.");
            return;
        }

        var ok = await Dialogs.ConfirmAsync(
            "Update installieren?",
            $"Version {release.Version.ToString(3)} wird heruntergeladen. Danach startet der Clip Manager kurz neu.",
            "Installieren");
        if (!ok)
        {
            return;
        }

        _installing = true;
        UpdateButton.Visibility = Visibility.Visible;
        UpdateButton.IsEnabled = false;
        var progress = new Progress<double>(p => UpdateButton.Content = $"Lade … {p:P0}");
        try
        {
            await AppUpdater.PrepareAsync(release, progress);
            UpdateButton.Content = "Starte neu …";
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _installing = false;
            UpdateButton.IsEnabled = true;
            ShowUpdate(release);
            await Dialogs.ErrorAsync($"Das Update konnte nicht installiert werden:\n{ex.Message}");
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate != null)
        {
            await InstallUpdateAsync(_pendingUpdate);
        }
    }

    private void LibraryNav_Click(object sender, RoutedEventArgs e) => ShowLibrary();

    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void ShowLibrary()
    {
        PageHost.Content = _library;
        LibraryNav.Appearance = ControlAppearance.Primary;
        SettingsNav.Appearance = ControlAppearance.Transparent;
        _ = _library.RefreshAsync();
    }

    private void ShowSettings()
    {
        PageHost.Content = _settings;
        LibraryNav.Appearance = ControlAppearance.Transparent;
        SettingsNav.Appearance = ControlAppearance.Primary;
        _settings.Refresh();
    }
}
