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
/// Editor für einen Clip: Vorschau, Zeitleiste, Anfang/Ende, Stellen rausschneiden.
/// Die Vorschau nutzt vorerst den eingebauten Windows-Player (MediaElement) und spielt Spur 1 (den Mix).
/// Einzelne Spuren hören kommt in Etappe 5.
/// </summary>
public partial class EditorWindow : FluentWindow
{
    private static readonly Dictionary<string, EditorWindow> OpenWindows = new(StringComparer.OrdinalIgnoreCase);

    private readonly ClipFile _clip;
    private readonly TimeSpan? _knownDuration;
    private readonly DispatcherTimer _timer;
    private EditTimeline? _timeline;
    private bool _playing;
    private bool _scrubbing;
    private bool _resumeAfterScrub;
    private double _fps = 60;
    private TimeSpan _lastSeek = TimeSpan.MinValue;
    private TimeSpan? _pendingSeek;

    private EditorWindow(ClipFile clip, MediaInfo? info)
    {
        InitializeComponent();
        _clip = clip;
        _knownDuration = info?.Duration;
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

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        LoadingText.Visibility = Visibility.Collapsed;
        var duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : _knownDuration ?? TimeSpan.Zero;
        if (duration <= TimeSpan.Zero)
        {
            ShowPlayerError("Die Länge des Clips konnte nicht ermittelt werden.");
            return;
        }

        _timeline = new EditTimeline(duration);
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
        // Direkt ab dem Anfang des Schnitts abspielen.
        Seek(_timeline.In);
        _timer.Start();
        SetPlaying(true);
        _ = LoadFilmstripAsync(duration);
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

                if (next.Value - pos > TimeSpan.FromMilliseconds(40) && next.Value != _lastSeek)
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
            Player.IsMuted = false;
            Player.Play();
        }
        else
        {
            Player.Pause();
            Player.IsMuted = true;
        }

        PlayButton.Icon = new SymbolIcon { Symbol = playing ? SymbolRegular.Pause24 : SymbolRegular.Play24, Filled = true };
    }

    private void TogglePlay()
    {
        if (_timeline == null)
        {
            return;
        }

        if (!_playing && PreviewCutsCheck.IsChecked == true && _timeline.NextPlayable(Position) == null)
        {
            // Am Ende angekommen: von vorne (ab dem Anfang des Schnitts) starten.
            Seek(_timeline.In);
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
        _lastSeek = t;
        Player.Position = t;
        TimelineView.Position = t;
        UpdatePositionText(t);
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
        _lastSeek = t;
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
    }

    private void SetIn_Click(object sender, RoutedEventArgs e) => _timeline?.SetIn(Position);

    private void SetOut_Click(object sender, RoutedEventArgs e) => _timeline?.SetOut(Position);

    private void Split_Click(object sender, RoutedEventArgs e) => _timeline?.Split(Position);

    private void Remove_Click(object sender, RoutedEventArgs e) => _timeline?.ToggleRemovedAt(Position);

    private void Undo_Click(object sender, RoutedEventArgs e) => _timeline?.Undo();

    private void Reset_Click(object sender, RoutedEventArgs e) => _timeline?.Reset();

    private void OnClosed(object? sender, EventArgs e)
    {
        OpenWindows.Remove(_clip.Path);
        _timer.Stop();
        Player.Stop();
        Player.Close();
        Player.Source = null;
    }
}
