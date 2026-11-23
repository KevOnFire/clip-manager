using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipManager.Core;
using ClipManager.Core.Editing;
using ClipManager.Core.Library;
using ClipManager.Core.Media;
using Wpf.Ui.Controls;

namespace ClipManager.Editor;

/// <summary>
/// Editor für einen Clip: Vorschau, Zeitleiste, Anfang/Ende, Stellen rausschneiden, Tonspuren einzeln.
/// Das Bild kommt vom Windows-Player (MediaElement, stumm). Der Ton kommt aus den einzeln
/// herausgeholten Spuren (TrackPlayer), gemischt wie beim Export. Bis die Spuren bereit sind
/// (oder falls das nicht klappt), spielt der Windows-Player Spur 1 ab.
/// </summary>
public partial class EditorWindow : FluentWindow
{
    private static readonly Dictionary<string, EditorWindow> OpenWindows = new(StringComparer.OrdinalIgnoreCase);

    private readonly ClipFile _clip;
    private readonly TimeSpan? _knownDuration;
    private MediaInfo? _info;
    private TrackPlayer? _audio;
    private List<TrackRowViewModel> _trackRows = new();
    private DateTime _lastAudioSync = DateTime.MinValue;
    private readonly DispatcherTimer _timer;
    private EditTimeline? _timeline;
    private bool _playing;
    private bool _scrubbing;
    private bool _resumeAfterScrub;
    private double _fps = 60;
    private DateTime _lastSeekAt = DateTime.MinValue;
    private TimeSpan? _pendingSeek;

    private EditorWindow(ClipFile clip, MediaInfo? info)
    {
        InitializeComponent();
        _clip = clip;
        _knownDuration = info?.Duration;
        _info = info;
        if (info is { Fps: > 1 })
        {
            _fps = info.Fps;
        }

        Title = $"{clip.Name} – Clip bearbeiten";
        TitleBarControl.Title = $"{clip.Name}";

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };
        _timer.Tick += Timer_Tick;

        TimelineView.SeekRequested += (_, t) => ScrubTo(t);
        TimelineView.ScrubbingChanged += Timeline_ScrubbingChanged;
        TimelineView.RangeSelected += (_, e) => ShowRangeMenu(e.Track, e.Start, e.End);
        TimelineView.MutedClicked += (_, e) => ShowMenu(("Wieder laut machen", Wpf.Ui.Controls.SymbolRegular.Speaker224, () => _timeline?.UnmuteTrackAt(e.Track, e.Time)));
        TimelineView.RemovedClicked += (_, t) => ShowMenu(("Wieder reinnehmen", Wpf.Ui.Controls.SymbolRegular.ArrowUndo24, () => _timeline?.RestoreAt(t)));

        Player.Volume = VolumeSlider.Value;

        // Ton nur beim Abspielen. Sonst spielt der Player beim Springen/Ziehen kurze Tonfetzen ab.
        Player.IsMuted = true;
        Player.Source = new Uri(clip.Path);
        Loaded += (_, _) =>
        {
            // Erstes Bild anzeigen, ohne abzuspielen.
            Player.Play();
            Player.Pause();
        };
        Closed += OnClosed;
    }

    /// <summary>Öffnet den Editor für einen Clip, oder holt ein schon offenes Fenster nach vorne.</summary>
    public static void OpenFor(ClipFile clip, MediaInfo? info, Window? owner)
    {
        if (OpenWindows.TryGetValue(clip.Path, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var window = new EditorWindow(clip, info) { Owner = owner };
        OpenWindows[clip.Path] = window;
        window.Show();
    }

    /// <summary>Wird ausgelöst, wenn sich die gespeicherten Schnitte eines Clips ändern (für die Kacheln).</summary>
    public static event EventHandler<string>? EditsSaved;

    private TimeSpan Position => Player.Position;

    /// <summary>Ton kommt aus den einzelnen Spuren (sonst aus dem Windows-Player).</summary>
    private bool UsesTrackAudio => _audio != null;

    private async void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        LoadingText.Visibility = Visibility.Collapsed;
        var duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : _knownDuration ?? TimeSpan.Zero;
        if (duration <= TimeSpan.Zero)
        {
            ShowPlayerError("Die Länge des Clips konnte nicht ermittelt werden.");
            return;
        }

        _timeline = new EditTimeline(duration);
        var ffmpeg = App.LocateFfmpeg();
        if (_info == null && ffmpeg?.Ffprobe != null)
        {
            try
            {
                _info = await new MediaInfoReader(App.ProcessRunner).ReadAsync(ffmpeg.Ffprobe, _clip.Path);
            }
            catch (Exception ex)
            {
                Log.Error($"Clip-Infos für \"{_clip.Path}\" konnten nicht gelesen werden", ex);
            }
        }

        var trackCount = _info?.AudioTracks.Count ?? 0;
        _timeline.InitTracks(trackCount, Core.Export.ExportPlanner.DefaultAudioTracks(trackCount));
        try
        {
            if (App.Db.GetEdit(_clip.Path) is { } saved)
            {
                _timeline.Load(saved);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Schnitte für \"{_clip.Path}\" konnten nicht geladen werden", ex);
        }

        _timeline.Changed += Timeline_Changed;
        TimelineView.Timeline = _timeline;
        UpdateTexts();
        // Direkt ab dem Anfang des Schnitts abspielen. Erst abspielen, dann springen: Ein Sprung direkt
        // nach dem Öffnen geht beim Windows-Player sonst manchmal verloren. Der Timer springt zur Not nochmal.
        _timer.Start();
        SetPlaying(true);
        Seek(_timeline.In);
        _ = LoadFilmstripAsync(duration);
        BuildTrackRows();
        _ = LoadTrackAudioAsync();
    }

    private void BuildTrackRows()
    {
        if (_timeline == null)
        {
            return;
        }

        var count = _timeline.Tracks.Count;
        _trackRows = Enumerable.Range(0, count)
            .Select(i => new TrackRowViewModel(_timeline, i, App.Settings.TrackName(i, _info?.AudioTracks.ElementAtOrDefault(i)?.Title, count), TrackColorOf(i)))
            .ToList();
        TrackHeaders.ItemsSource = _trackRows;
        TimelineView.TrackColors = Enumerable.Range(0, count).Select(i => TrackColorOf(i)).ToList();
        TracksHint.Text = count switch
        {
            0 => "Dieser Clip hat keinen Ton.",
            1 => "Tonspur",
            _ => "Tonspuren · Haken = im Export · In einer Spur ziehen zum Stummschalten oder Rausschneiden",
        };
    }

    /// <summary>Spuren als WAV holen, Wellenformen berechnen, Spuren-Vorschau starten.</summary>
    private async Task LoadTrackAudioAsync()
    {
        var ffmpeg = App.LocateFfmpeg();
        if (ffmpeg == null || _timeline == null || _timeline.Tracks.Count == 0)
        {
            return;
        }

        try
        {
            var timeline = _timeline;
            var wavs = await App.AudioTracks.GetOrCreateAsync(ffmpeg.Ffmpeg, _clip, timeline.Tracks.Count);
            if (wavs == null || !IsLoaded)
            {
                return;
            }

            var peaks = await Task.Run(() => wavs.Select(w => (float[]?)Waveform.ReadPeaks(w, 4000)).ToList());
            if (!IsLoaded)
            {
                return;
            }

            TimelineView.Peaks = peaks;
            var audio = TrackPlayer.TryCreate(wavs, timeline);
            if (audio == null)
            {
                return;
            }

            audio.Volume = (float)VolumeSlider.Value;
            _audio = audio;

            // Ab jetzt kommt der Ton aus den Spuren, der Windows-Player bleibt stumm.
            Player.IsMuted = true;
            if (_playing)
            {
                _audio.Play(Position);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Tonspuren-Vorschau für \"{_clip.Path}\" fehlgeschlagen", ex);
        }
    }

    private async Task LoadFilmstripAsync(TimeSpan duration)
    {
        var ffmpeg = App.LocateFfmpeg();
        if (ffmpeg == null)
        {
            return;
        }

        try
        {
            var path = await App.Filmstrips.GetOrCreateAsync(ffmpeg.Ffmpeg, _clip, duration);
            if (path == null || !IsLoaded)
            {
                return;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            TimelineView.Filmstrip = image;
        }
        catch (Exception ex)
        {
            Log.Error($"Filmstreifen für \"{_clip.Path}\" konnte nicht angezeigt werden", ex);
        }
    }

    private void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        Log.Error($"Vorschau für \"{_clip.Path}\" kann nicht abgespielt werden", e.ErrorException);
        ShowPlayerError(e.ErrorException?.Message ?? "Unbekannter Fehler");
    }

    private async void ShowPlayerError(string message)
    {
        LoadingText.Text = "Vorschau nicht möglich";
        var open = await Dialogs.ConfirmAsync(
            "Vorschau nicht möglich",
            $"Der eingebaute Player kann diesen Clip nicht abspielen:\n{message}\n\nIm Standard-Player öffnen?",
            "Öffnen");
        if (open)
        {
            ShellFiles.OpenWithDefaultApp(_clip.Path);
        }
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e) => StopAtEnd();

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_timeline == null)
        {
            return;
        }

        // Beim Ziehen höchstens alle 25 ms wirklich springen, sonst kommt der Player nicht hinterher.
        if (_pendingSeek is { } pending)
        {
            _pendingSeek = null;
            Player.Position = pending;
        }

        if (_scrubbing)
        {
            return;
        }

        var pos = Position;
        if (_playing)
        {
            // Entfernte Teile und alles hinter dem Ende überspringen.
            if (PreviewCutsCheck.IsChecked == true)
            {
                var next = _timeline.NextPlayable(pos);
                if (next == null)
                {
                    StopAtEnd();
                    return;
                }

                if (next.Value - pos > TimeSpan.FromMilliseconds(40) && DateTime.UtcNow - _lastSeekAt > TimeSpan.FromMilliseconds(250))
                {
                    Seek(next.Value);
                    pos = next.Value;
                }
            }
            else if (pos >= _timeline.Duration)
            {
                StopAtEnd();
                return;
            }

            // Ton der Spuren am Bild ausrichten, wenn sie auseinanderlaufen.
            if (_audio != null && DateTime.UtcNow - _lastAudioSync > TimeSpan.FromMilliseconds(700)
                && DateTime.UtcNow - _lastSeekAt > TimeSpan.FromMilliseconds(400))
            {
                var drift = (_audio.Position - pos).Duration();
                if (drift > TimeSpan.FromMilliseconds(120))
                {
                    _audio.Play(pos);
                }

                _lastAudioSync = DateTime.UtcNow;
            }
        }

        TimelineView.Position = pos;
        UpdatePositionText(pos);
    }

    private void StopAtEnd()
    {
        SetPlaying(false);
        if (_timeline != null)
        {
            Seek(PreviewCutsCheck.IsChecked == true ? _timeline.Out : _timeline.Duration);
        }
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        if (playing)
        {
            Player.IsMuted = UsesTrackAudio;
            Player.Play();
            _audio?.Play(Position);
            _lastAudioSync = DateTime.UtcNow;
        }
        else
        {
            Player.Pause();
            Player.IsMuted = true;
            _audio?.Stop();
        }

        PlayButton.Icon = new SymbolIcon { Symbol = playing ? SymbolRegular.Pause24 : SymbolRegular.Play24, Filled = true };
    }

    private void TogglePlay()
    {
        if (_timeline == null)
        {
            return;
        }

        if (!_playing && PreviewCutsCheck.IsChecked == true)
        {
            // Vor dem Anfang oder in einem entfernten Teil: zum nächsten behaltenen Teil springen.
            // Am Ende angekommen: von vorne (ab dem Anfang des Schnitts) starten.
            var next = _timeline.NextPlayable(Position);
            Seek(next ?? _timeline.In);
        }

        SetPlaying(!_playing);
    }

    private void Seek(TimeSpan t)
    {
        if (_timeline == null)
        {
            return;
        }

        t = t < TimeSpan.Zero ? TimeSpan.Zero : t > _timeline.Duration ? _timeline.Duration : t;
        _lastSeekAt = DateTime.UtcNow;
        Player.Position = t;
        TimelineView.Position = t;
        UpdatePositionText(t);
        if (_playing && _audio != null)
        {
            _audio.Play(t);
            _lastAudioSync = DateTime.UtcNow;
        }
    }

    /// <summary>Springen beim Ziehen auf der Zeitleiste: Anzeige sofort, Player gebremst (siehe Timer).</summary>
    private void ScrubTo(TimeSpan t)
    {
        if (_timeline == null)
        {
            return;
        }

        t = t < TimeSpan.Zero ? TimeSpan.Zero : t > _timeline.Duration ? _timeline.Duration : t;
        _pendingSeek = t;
        _lastSeekAt = DateTime.UtcNow;
        TimelineView.Position = t;
        UpdatePositionText(t);
    }

    private void Step(TimeSpan delta) => Seek(Position + delta);

    private void Timeline_ScrubbingChanged(object? sender, bool scrubbing)
    {
        _scrubbing = scrubbing;
        if (scrubbing)
        {
            _resumeAfterScrub = _playing;
            if (_playing)
            {
                SetPlaying(false);
            }
        }
        else
        {
            // Letzte Position sicher übernehmen, bevor es weitergeht.
            if (_pendingSeek is { } pending)
            {
                _pendingSeek = null;
                Player.Position = pending;
            }

            if (_resumeAfterScrub)
            {
                SetPlaying(true);
            }
        }
    }

    private void Timeline_Changed(object? sender, EventArgs e)
    {
        foreach (var row in _trackRows)
        {
            row.SyncFromTimeline();
        }

        UpdateTexts();
        SaveEdits();
    }

    private void SaveEdits()
    {
        if (_timeline == null)
        {
            return;
        }

        try
        {
            App.Db.SaveEdit(_clip.Path, _timeline.IsEdited ? _timeline.ToState() : null);
            EditsSaved?.Invoke(this, _clip.Path);
        }
        catch (Exception ex)
        {
            Log.Error($"Schnitte für \"{_clip.Path}\" konnten nicht gespeichert werden", ex);
        }
    }

    private void UpdateTexts()
    {
        if (_timeline == null)
        {
            return;
        }

        UndoButton.IsEnabled = _timeline.CanUndo;
        var removed = _timeline.Segments.Count(s => s.Removed);
        ResultText.Text = _timeline.IsEdited
            ? $"Ergebnis: {TimeText.Format(_timeline.OutputDuration)} von {TimeText.Format(_timeline.Duration)}"
              + (removed > 0 ? $" · {removed} {(removed == 1 ? "Stelle" : "Stellen")} rausgeschnitten" : string.Empty)
            : "Noch nichts geschnitten";
        UpdatePositionText(Position);
    }

    private void UpdatePositionText(TimeSpan pos)
    {
        if (_timeline != null)
        {
            PositionText.Text = $"{TimeText.Format(pos)} / {TimeText.Format(_timeline.Duration)}";
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_timeline == null || e.OriginalSource is System.Windows.Controls.TextBox)
        {
            return;
        }

        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var frame = TimeSpan.FromSeconds(1 / _fps);
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Space:
            case Key.K:
                TogglePlay();
                break;
            case Key.Left:
                Step(TimeSpan.FromSeconds(shift ? -5 : -1));
                break;
            case Key.Right:
                Step(TimeSpan.FromSeconds(shift ? 5 : 1));
                break;
            case Key.OemComma:
                SetPlaying(false);
                Step(-frame);
                break;
            case Key.OemPeriod:
                SetPlaying(false);
                Step(frame);
                break;
            case Key.Home:
                Seek(_timeline.In);
                break;
            case Key.End:
                Seek(_timeline.Out);
                break;
            case Key.I:
                _timeline.SetIn(Position);
                break;
            case Key.O:
                _timeline.SetOut(Position);
                break;
            case Key.S when !ctrl:
                _timeline.Split(Position);
                break;
            case Key.Delete:
            case Key.Back:
                _timeline.ToggleRemovedAt(Position);
                break;
            case Key.Z when ctrl:
                _timeline.Undo();
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private void Video_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => TogglePlay();

    private void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Player != null)
        {
            Player.Volume = e.NewValue;
        }

        if (_audio != null)
        {
            _audio.Volume = (float)e.NewValue;
        }
    }

    private void SetIn_Click(object sender, RoutedEventArgs e) => _timeline?.SetIn(Position);

    private void SetOut_Click(object sender, RoutedEventArgs e) => _timeline?.SetOut(Position);

    private void Split_Click(object sender, RoutedEventArgs e) => _timeline?.Split(Position);

    private void Remove_Click(object sender, RoutedEventArgs e) => _timeline?.ToggleRemovedAt(Position);

    private void Undo_Click(object sender, RoutedEventArgs e) => _timeline?.Undo();

    private void Reset_Click(object sender, RoutedEventArgs e) => _timeline?.Reset();

    private void Export_Click(object sender, RoutedEventArgs e) => OpenExport(saveMode: false);

    /// <summary>Menü nach dem Aufziehen eines Bereichs in einer Spur.</summary>
    private void ShowRangeMenu(int track, TimeSpan start, TimeSpan end)
    {
        if (track < 0)
        {
            return;
        }

        var name = track < _trackRows.Count ? _trackRows[track].Name : $"Spur {track + 1}";
        var length = TimeText.Format(end - start);
        ShowMenu(
            ($"Nur in \"{name}\" stumm schalten ({length})", Wpf.Ui.Controls.SymbolRegular.SpeakerMute24, () => _timeline?.MuteTrackRange(track, start, end)),
            ($"Aus dem ganzen Clip rausschneiden ({length})", Wpf.Ui.Controls.SymbolRegular.Cut24, () => _timeline?.RemoveRange(start, end)),
            ("Ab hier abspielen", Wpf.Ui.Controls.SymbolRegular.Play24, () =>
            {
                Seek(start);
                SetPlaying(true);
            }));
    }

    private static System.Windows.Media.Color TrackColorOf(int index) =>
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(App.Settings.TrackColor(index));

    private System.Windows.Controls.ContextMenu? _openMenu;

    private void ShowMenu(params (string Text, Wpf.Ui.Controls.SymbolRegular Icon, Action Action)[] items)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = TimelineView,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };
        foreach (var (text, icon, action) in items)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = text,
                Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = icon, FontSize = 16 },
            };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        // Nur das zuletzt geöffnete Menü darf die Auswahl wegnehmen (ein altes schließt sich evtl. später).
        _openMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openMenu, menu))
            {
                _openMenu = null;
                TimelineView.ClearSelection();
            }
        };
        menu.IsOpen = true;
    }

    private void TrackVolume_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) =>
        _timeline?.BeginTrackVolumeChange();

    private async void TrackName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || (sender as FrameworkElement)?.DataContext is not TrackRowViewModel row)
        {
            return;
        }

        e.Handled = true;
        var name = await Dialogs.PromptAsync(
            "Spur umbenennen",
            $"Name für Spur {row.Index + 1} (gilt für alle Clips):",
            row.Name,
            "Speichern");
        if (name == null)
        {
            return;
        }

        name = name.Trim();
        if (name.Length == 0)
        {
            App.Settings.TrackNames.Remove(row.Index);
        }
        else
        {
            App.Settings.TrackNames[row.Index] = name;
        }

        App.SaveSettings();
        row.Name = App.Settings.TrackName(row.Index, _info?.AudioTracks.ElementAtOrDefault(row.Index)?.Title, _trackRows.Count);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => OpenExport(saveMode: true);

    private void OpenExport(bool saveMode)
    {
        if (_timeline == null)
        {
            return;
        }

        SetPlaying(false);
        var dialog = new ExportWindow(_clip, _info, _timeline, saveMode, this) { Owner = this };
        dialog.ShowDialog();
        if (dialog.OriginalReplaced)
        {
            Close();
        }
    }

    /// <summary>
    /// Gibt die Datei frei (zum Ersetzen des Originals). Danach ist der Editor nicht mehr nutzbar;
    /// er wird nach dem Export-Dialog geschlossen.
    /// </summary>
    public void ReleaseClip()
    {
        _timer.Stop();
        _timeline!.Changed -= Timeline_Changed;
        _audio?.Dispose();
        _audio = null;
        Player.Stop();
        Player.Close();
        Player.Source = null;
    }

    /// <summary>Kacheln aktualisieren, nachdem sich gespeicherte Schnitte geändert haben.</summary>
    public static void NotifyEditsChanged(string path) => EditsSaved?.Invoke(null, path);

    private void OnClosed(object? sender, EventArgs e)
    {
        OpenWindows.Remove(_clip.Path);
        _timer.Stop();
        _audio?.Dispose();
        _audio = null;
        Player.Stop();
        Player.Close();
        Player.Source = null;
    }
}
