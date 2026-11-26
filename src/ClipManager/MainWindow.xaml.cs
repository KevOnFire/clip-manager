using System.Windows;
using Wc = System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClipManager.Core.Library;
using ClipManager.Core.Media;
using ClipManager.Core.Storage;
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
        FirstLoad = ShowLibrary();
        ApplySavedBounds();
        ApplyTheme();
        Loaded += async (_, _) => ShowUpdate(await AppUpdater.CheckAsync());
        Loaded += (_, _) => RefreshDisk();
        _diskTimer.Tick += (_, _) => RefreshDisk();
        _diskTimer.Start();
    }

    // ---------- Speicherplatz ----------

    private readonly DispatcherTimer _diskTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly HashSet<string> _diskWarned = new(StringComparer.OrdinalIgnoreCase);
    private bool _diskRefreshing;

    /// <summary>Freien Speicher oben anzeigen und ggf. einmal warnen. Auch nach Löschen/Einstellungen aufrufen.</summary>
    public async void RefreshDisk(bool warn = true)
    {
        if (_diskRefreshing)
        {
            return;
        }

        _diskRefreshing = true;
        try
        {
            var folders = App.Settings.ClipFolders.ToList();
            // Laufwerke abzufragen kann bei Netzlaufwerken dauern, deshalb nicht im UI-Thread.
            var usages = await Task.Run(() => DiskUsage.ForFolders(folders));
            DiskPanel.Children.Clear();
            foreach (var usage in usages)
            {
                var full = App.Settings.ShouldWarn(usage);
                DiskPanel.Children.Add(DiskItem(usage, full));
                if (!full)
                {
                    _diskWarned.Remove(usage.Drive);
                }
                else if (warn && _diskWarned.Add(usage.Drive))
                {
                    await Dialogs.ConfirmAsync(
                        "Festplatte fast voll",
                        $"Laufwerk {usage.Label} ist zu {usage.UsedPercent:0} % belegt, nur noch {DiskUsage.FormatBytes(usage.FreeBytes)} frei.\n\n" +
                        "Lösch am besten ein paar alte Clips, sonst kann OBS bald keine neuen mehr speichern.\n" +
                        "Die Warnung kannst du in den Einstellungen unter „Allgemein“ ändern oder ausschalten.",
                        "OK");
                }
            }
        }
        catch (Exception ex)
        {
            Core.Log.Error("Speicherplatz konnte nicht gelesen werden", ex);
        }
        finally
        {
            _diskRefreshing = false;
        }
    }

    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x4A, 0x5F));

    private FrameworkElement DiskItem(DiskUsage usage, bool warn)
    {
        const double barWidth = 56;
        var fill = new Wc.Border { Width = Math.Max(2, barWidth * Math.Clamp(usage.UsedPercent, 0, 100) / 100), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
        var track = new Wc.Border { Width = barWidth, Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), Child = fill, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var icon = new SymbolIcon { Symbol = SymbolRegular.HardDrive20, FontSize = 15, Margin = new Thickness(0, 0, 6, 0) };
        var text = new Wc.TextBlock { Text = $"{usage.Label} {DiskUsage.FormatBytes(usage.FreeBytes)} frei", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        if (warn)
        {
            fill.Background = WarnBrush;
            icon.Foreground = WarnBrush;
            text.Foreground = WarnBrush;
        }
        else
        {
            fill.SetResourceReference(Wc.Border.BackgroundProperty, "AccentFillColorDefaultBrush");
            text.SetResourceReference(Wc.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            icon.SetResourceReference(Wc.Control.ForegroundProperty, "TextFillColorSecondaryBrush");
        }

        var panel = new Wc.StackPanel { Orientation = Wc.Orientation.Horizontal };
        panel.Children.Add(icon);
        panel.Children.Add(text);
        panel.Children.Add(track);
        var item = new Wc.Border
        {
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6, 0, 0, 0),
            CornerRadius = new CornerRadius(6),
            Child = panel,
            ToolTip = $"Laufwerk {usage.Label}: {usage.UsedPercent:0} % belegt, {DiskUsage.FormatBytes(usage.FreeBytes)} von {DiskUsage.FormatBytes(usage.TotalBytes)} frei" +
                (warn ? " – fast voll!" : ""),
        };
        item.SetResourceReference(Wc.Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        return item;
    }

    /// <summary>Fenster so groß und dort öffnen, wo es beim letzten Beenden war.</summary>
    private void ApplySavedBounds()
    {
        var saved = App.Settings.Window?.FitInto(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight, MinWidth, MinHeight);
        if (saved == null)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = saved.Left;
        Top = saved.Top;
        Width = saved.Width;
        Height = saved.Height;
        if (saved.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveBounds()
    {
        try
        {
            var fullscreen = AppTitleBar.Visibility != Visibility.Visible;
            var maximized = fullscreen ? _stateBeforeFullscreen == WindowState.Maximized : WindowState == WindowState.Maximized;
            // RestoreBounds = normale Größe, auch wenn das Fenster gerade maximiert ist.
            var r = WindowState == WindowState.Normal && !fullscreen ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0)
            {
                return;
            }

            App.Settings.Window = new Core.Settings.WindowBounds
            {
                Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height, Maximized = maximized,
            };
            App.SaveSettings();
        }
        catch (Exception ex)
        {
            Core.Log.Error("Fenstergröße konnte nicht gespeichert werden", ex);
        }
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
        _library.MarkSeen(clip.Path);
        var editor = new EditorView(clip, info);
        editor.CloseRequested += (_, _) =>
        {
            if (ReferenceEquals(_editor, editor))
            {
                _library.FocusClip(editor.ClipPath);
                _editor = null;
                ShowLibrary();
            }
        };
        editor.FullscreenRequested += (_, on) => SetFullscreen(on);
        editor.NeighborRequested += (_, dir) => OpenNeighbor(editor, dir);
        editor.ClipDeleted += (_, path) => OpenAfterDelete(editor, path);
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

    /// <summary>
    /// Nach dem Löschen im Editor gleich den nächsten Clip öffnen (wie Pfeil nach rechts),
    /// beim letzten Clip den davor, und nur wenn keiner mehr da ist zurück zur Bibliothek.
    /// </summary>
    private async void OpenAfterDelete(EditorView from, string deletedPath)
    {
        if (!ReferenceEquals(_editor, from))
        {
            return;
        }

        var next = _library.Neighbor(deletedPath, 1) ?? _library.Neighbor(deletedPath, -1);
        var fullscreen = from.IsFullscreen;
        if (next == null)
        {
            await ShowLibrary();
            return;
        }

        // Erst die Bibliothek neu einlesen, damit Vor/Zurück im nächsten Clip den gelöschten nicht mehr kennt.
        await _library.RefreshAsync();
        if (!ReferenceEquals(_editor, from))
        {
            return;
        }

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
            ThemeLayer.Visibility = Visibility.Collapsed;
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
            ApplyTheme();
        }
    }

    /// <summary>
    /// Eigenes Design anwenden (oder wieder das normale). Mit Farbverlauf ohne Mica, sonst würde der
    /// durchscheinende Windows-Hintergrund die Farben verfälschen.
    /// </summary>
    public void ApplyTheme()
    {
        if (AppTitleBar.Visibility != Visibility.Visible)
        {
            // Im Vollbild bleibt es schwarz; beim Verlassen wird das hier erneut aufgerufen.
            return;
        }

        var theme = App.Settings.Theme;
        if (theme.Enabled)
        {
            WindowBackdropType = WindowBackdropType.None;
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x14, 0x18));
            ThemeLayer.Fill = Views.ThemeBrush.Create(theme);
            ThemeLayer.Opacity = theme.ValidStrength();
            ThemeLayer.Visibility = Visibility.Visible;
        }
        else
        {
            ThemeLayer.Visibility = Visibility.Collapsed;
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
        else if (ReferenceEquals(PageHost.Content, _library))
        {
            _library.HandleKey(e);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveBounds();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseEditor();
        base.OnClosed(e);
    }

    /// <summary>Erstes Einlesen der Clips (der Startbildschirm wartet darauf).</summary>
    public Task FirstLoad { get; }

    private Task ShowLibrary()
    {
        // Aus dem Editor zurück: auf der Seite mit diesem Clip landen, er leuchtet kurz auf.
        if (_editor != null)
        {
            _library.FocusClip(_editor.ClipPath);
        }

        CloseEditor();
        PageHost.Content = _library;
        LibraryNav.Appearance = ControlAppearance.Primary;
        SettingsNav.Appearance = ControlAppearance.Transparent;
        UpdateChrome();
        if (IsLoaded)
        {
            RefreshDisk();
        }

        return _library.RefreshAsync();
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
