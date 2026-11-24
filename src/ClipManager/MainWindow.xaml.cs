using System.Windows;
using System.Windows.Input;
using ClipManager.Core.Library;
using ClipManager.Core.Media;
using ClipManager.Core.Updates;
using ClipManager.Editor;
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
            $"Version {release.Version.ToString(3)} wird heruntergeladen. Danach startet Clippy kurz neu.",
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

    private EditorView? _editor;
    private WindowState _stateBeforeFullscreen;
    private WindowStyle _styleBeforeFullscreen;
    private ResizeMode _resizeBeforeFullscreen;

    /// <summary>Öffnet den Editor für einen Clip direkt im Hauptfenster (statt der Bibliothek).</summary>
    public void OpenEditor(ClipFile clip, MediaInfo? info)
    {
        if (_editor != null && string.Equals(_editor.ClipPath, clip.Path, StringComparison.OrdinalIgnoreCase))
        {
            PageHost.Content = _editor;
            UpdateChrome();
            return;
        }

        CloseEditor();
        _library.StopPreviews();
        var editor = new EditorView(clip, info);
        editor.CloseRequested += (_, _) =>
        {
            if (ReferenceEquals(_editor, editor))
            {
                _editor = null;
                ShowLibrary();
            }
        };
        editor.FullscreenRequested += (_, on) => SetFullscreen(on);
        editor.NeighborRequested += (_, dir) => OpenNeighbor(editor, dir);
        editor.SetNeighbors(_library.Neighbor(clip.Path, -1) != null, _library.Neighbor(clip.Path, 1) != null);
        _editor = editor;
        PageHost.Content = editor;
        LibraryNav.Appearance = ControlAppearance.Primary;
        SettingsNav.Appearance = ControlAppearance.Transparent;
        UpdateChrome();
    }

    /// <summary>Nächsten/vorherigen Clip der Bibliothek im Editor öffnen (Vollbild bleibt an).</summary>
    private void OpenNeighbor(EditorView from, int direction)
    {
        if (!ReferenceEquals(_editor, from) || _library.Neighbor(from.ClipPath, direction) is not { } next)
        {
            return;
        }

        var fullscreen = from.IsFullscreen;
        OpenEditor(next, null);
        if (fullscreen)
        {
            _editor?.SetFullscreen(true);
        }
    }

    private void CloseEditor()
    {
        var editor = _editor;
        _editor = null;
        editor?.Close();
    }

    /// <summary>Vollbild für den Editor: Navigation und Titelleiste weg, Fenster über den ganzen Bildschirm.</summary>
    private void SetFullscreen(bool on)
    {
        AppTitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        TitleLogo.Visibility = AppTitleBar.Visibility;
        UpdateChrome();
        if (on)
        {
            _stateBeforeFullscreen = WindowState;
            _styleBeforeFullscreen = WindowStyle;
            _resizeBeforeFullscreen = ResizeMode;
            // Ohne Mica-Hintergrund: der halbdurchsichtige Fensterhintergrund hat das Video im Vollbild blass gemacht.
            WindowBackdropType = WindowBackdropType.None;
            Background = System.Windows.Media.Brushes.Black;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // Erst normal, dann maximiert: sonst bleibt die Taskleiste sichtbar.
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _styleBeforeFullscreen;
            ResizeMode = _resizeBeforeFullscreen;
            WindowState = _stateBeforeFullscreen;
            ClearValue(BackgroundProperty);
            WindowBackdropType = WindowBackdropType.Mica;
        }
    }

    /// <summary>Im Editor keine Navigation (mehr Platz fürs Video), sonst schon.</summary>
    private void UpdateChrome()
    {
        var inEditor = _editor != null && ReferenceEquals(PageHost.Content, _editor);
        NavBar.Visibility = inEditor ? Visibility.Collapsed : Visibility.Visible;
        var fullscreen = _editor?.IsFullscreen == true && inEditor;
        PageHost.Margin = fullscreen ? new Thickness(0) : inEditor ? new Thickness(0, 0, 0, 0) : new Thickness(16, 4, 16, 16);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_editor != null && ReferenceEquals(PageHost.Content, _editor))
        {
            _editor.HandleKey(e);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseEditor();
        base.OnClosed(e);
    }

    private void ShowLibrary()
    {
        CloseEditor();
        PageHost.Content = _library;
        LibraryNav.Appearance = ControlAppearance.Primary;
        SettingsNav.Appearance = ControlAppearance.Transparent;
        UpdateChrome();
        _ = _library.RefreshAsync();
    }

    private void ShowSettings()
    {
        CloseEditor();
        PageHost.Content = _settings;
        LibraryNav.Appearance = ControlAppearance.Transparent;
        SettingsNav.Appearance = ControlAppearance.Primary;
        UpdateChrome();
        _settings.Refresh();
    }
}
