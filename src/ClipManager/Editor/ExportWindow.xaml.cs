using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ClipManager.Core;
using ClipManager.Core.Editing;
using ClipManager.Core.Export;
using ClipManager.Core.Library;
using ClipManager.Core.Media;
using ClipManager.Core.Settings;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace ClipManager.Editor;

/// <summary>
/// Export-Dialog: Ziel wählen, exportieren, Ergebnis rausziehen/kopieren. Der Export liegt erst nur
/// zwischengespeichert; gespeichert wird erst auf Wunsch (Original ersetzen, Kopie, Speichern unter).
/// </summary>
public partial class ExportWindow : FluentWindow
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private readonly ClipFile _clip;
    private readonly EditTimeline _timeline;
    private readonly EditorView _editor;
    private readonly IReadOnlyList<TimeRange> _ranges;
    private readonly IReadOnlyList<TrackEdit> _audio;
    private readonly TimeSpan _outputDuration;
    private string? _savedPath;
    private MediaInfo? _info;
    private VideoEncoder _encoder = VideoEncoder.Cpu;
    private CancellationTokenSource? _cts;
    private string? _outputPath;
    private Point _dragStart;

    private readonly bool _quickReplace;

    /// <param name="quickReplace">Schnellspeichern: sofort in voller Qualität exportieren und das Original ohne Nachfrage ersetzen.</param>
    public ExportWindow(ClipFile clip, MediaInfo? info, EditTimeline timeline, bool saveMode, EditorView editor, bool quickReplace = false)
    {
        InitializeComponent();
        _clip = clip;
        _info = info;
        _timeline = timeline;
        _editor = editor;
        _ranges = timeline.KeptRanges;
        _audio = timeline.AudibleTracks.ToList();
        _outputDuration = _ranges.Aggregate(TimeSpan.Zero, (sum, r) => sum + r.Duration);

        var s = App.Settings;
        TargetCombo.ItemsSource = new[]
        {
            new TargetItem(DiscordTier.Free, $"Discord ohne Nitro (max. {s.LimitFor(DiscordTier.Free)} MB)"),
            new TargetItem(DiscordTier.NitroBasic, $"Discord Nitro Basic (max. {s.LimitFor(DiscordTier.NitroBasic)} MB)"),
            new TargetItem(DiscordTier.Nitro, $"Discord Nitro (max. {s.LimitFor(DiscordTier.Nitro)} MB)"),
            new TargetItem(null, "Volle Qualität als Kopie (ohne Größenlimit)"),
        };
        TargetCombo.DisplayMemberPath = nameof(TargetItem.Label);
        TargetCombo.SelectedIndex = saveMode ? 3 : (int)s.Tier;
        if (saveMode)
        {
            Title = "Speichern";
            StartButton.Content = "Fertig machen";
        }

        _quickReplace = quickReplace;
        if (quickReplace)
        {
            Title = "Schnellspeichern";
        }

        Loaded += async (_, _) =>
        {
            await PrepareAsync();
            if (_quickReplace && StartButton.IsEnabled)
            {
                await RunExportAsync();
            }
        };
        Closing += (_, e) =>
        {
            if (_cts != null)
            {
                _cts.Cancel();
            }
        };
    }

    /// <summary>True, wenn das Original ersetzt wurde. Der Editor schließt sich dann.</summary>
    public bool OriginalReplaced { get; private set; }

    private double? LimitMb => TargetCombo.SelectedItem is TargetItem { Tier: { } tier } ? App.Settings.LimitFor(tier) : null;

    private async Task PrepareAsync()
    {
        var ffmpeg = App.LocateFfmpeg();
        if (ffmpeg == null)
        {
            PlanText.Text = "FFmpeg wurde nicht gefunden. Ohne FFmpeg kann nicht exportiert werden (siehe Einstellungen).";
            return;
        }

        PlanDetailText.Text = "Prüfe Clip und Grafikkarte …";
        try
        {
            if (_info == null && ffmpeg.Ffprobe != null)
            {
                _info = await new MediaInfoReader(App.ProcessRunner).ReadAsync(ffmpeg.Ffprobe, _clip.Path);
            }

            var status = await App.GetFfmpegStatusAsync();
            _encoder = status.Nvidia == GpuEncoderState.Available ? VideoEncoder.Nvidia
                : status.Amd == GpuEncoderState.Available ? VideoEncoder.Amd
                : VideoEncoder.Cpu;
        }
        catch (Exception ex)
        {
            Log.Error("Export-Vorbereitung fehlgeschlagen", ex);
        }

        StartButton.IsEnabled = true;
        UpdatePlan();
    }

    private void TargetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdatePlan();

    private void UpdatePlan()
    {
        if (!IsLoaded)
        {
            return;
        }

        var height = _info?.Height ?? 1080;
        var fps = _info?.Fps ?? 60;
        var plan = LimitMb is { } limit
            ? ExportPlanner.Plan(limit, _outputDuration, height, fps)
            : Exporter.FullQualityPlan(height, fps);
        var estimateMb = (plan.VideoKbps + plan.AudioKbps) * _outputDuration.TotalSeconds / 8192;

        PlanText.Text = $"Länge {TimeText.Format(_outputDuration)} · {plan.Height}p{Math.Round(plan.Fps)} · ca. {estimateMb.ToString("0.0", German)} MB";
        var encoder = _encoder switch
        {
            VideoEncoder.Nvidia => "NVIDIA-Grafikkarte",
            VideoEncoder.Amd => "AMD-Grafikkarte",
            _ => "Prozessor (langsamer)",
        };
        var trackText = _audio.Count == 0
            ? "ohne Ton"
            : "Ton: " + string.Join(", ", _audio.Select(t => App.Settings.TrackName(t.Index, _info?.AudioTracks.ElementAtOrDefault(t.Index)?.Title, _timeline.Tracks.Count)));
        PlanDetailText.Text = $"{plan.Describe()} · {trackText} · Kodiert mit {encoder}";

        WarningText.Visibility = plan.TooLongWarning ? Visibility.Visible : Visibility.Collapsed;
        WarningText.Text = "Der Clip ist für dieses Limit ziemlich lang, die Qualität wird schlecht. Kürzer schneiden hilft.";
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunExportAsync();

    private async Task RunExportAsync()
    {
        var ffmpeg = App.LocateFfmpeg();
        if (ffmpeg == null)
        {
            return;
        }

        var limit = LimitMb;
        string output;
        try
        {
            Directory.CreateDirectory(App.ExportTempFolder);
            output = ExportCommand.OutputPath(App.ExportTempFolder, _clip.Path, limit == null ? "Schnitt" : "Discord");
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync($"Zielordner nicht nutzbar: {ex.Message}");
            return;
        }

        SetupPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        var progress = new Progress<ExportProgress>(p =>
        {
            ProgressBarControl.Value = p.Fraction;
            ProgressText.Text = p.Text;
        });

        var request = new ExportRequest(
            ffmpeg.Ffmpeg,
            _clip.Path,
            output,
            _ranges,
            _audio,
            _info?.Height ?? 1080,
            _info?.Fps ?? 60,
            _encoder,
            limit);

        ExportResult result;
        try
        {
            result = await Task.Run(() => new Exporter().RunAsync(request, progress, _cts.Token));
            if (!result.Success && result.OutputPath == null && _encoder != VideoEncoder.Cpu)
            {
                // Grafikkarte hat gestreikt: einmal mit dem Prozessor probieren.
                Log.Info("GPU-Export fehlgeschlagen, versuche CPU");
                _encoder = VideoEncoder.Cpu;
                ProgressText.Text = "Grafikkarte klappt nicht, versuche es mit dem Prozessor …";
                result = await Task.Run(() => new Exporter().RunAsync(request with { Encoder = VideoEncoder.Cpu }, progress, _cts.Token));
            }
        }
        catch (OperationCanceledException)
        {
            _cts = null;
            ProgressPanel.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Export fehlgeschlagen", ex);
            result = new ExportResult(false, null, 0, null, ex.Message, 0);
        }

        _cts = null;
        ProgressPanel.Visibility = Visibility.Collapsed;
        if (result.OutputPath == null)
        {
            SetupPanel.Visibility = Visibility.Visible;
            await Dialogs.ErrorAsync($"Export fehlgeschlagen.\n\n{Shorten(result.Error)}");
            return;
        }

        _outputPath = result.OutputPath;
        var sizeText = (result.Bytes / (1024d * 1024)).ToString("0.0", German) + " MB";
        DoneTitle.Text = result.Success ? "Fertig!" : "Fertig, aber etwas zu groß";
        DoneText.Text = result.Success
            ? $"{sizeText}{(limit is { } l ? $" (Limit {l:0} MB)" : string.Empty)} · {result.Plan?.Describe()}"
            : $"{sizeText}. {result.Error}";
        if (limit != null)
        {
            DoneText.Text += " · Zum Speichern unten wählen, wohin.";
        }
        FileNameText.Text = Path.GetFileName(_outputPath);
        DonePanel.Visibility = Visibility.Visible;

        if (_quickReplace && result.Success)
        {
            await ReplaceOriginalAsync(confirm: false);
        }
    }

    private static string Shorten(string? text)
    {
        text ??= "Unbekannter Fehler";
        return text.Length > 600 ? text[..600] + " …" : text;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void DragCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    private void DragCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _outputPath == null || !File.Exists(_outputPath))
        {
            return;
        }

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(DataFormats.FileDrop, new[] { _outputPath });
        DragDrop.DoDragDrop(DragCard, data, DragDropEffects.Copy);
    }

    private void CopyFile_Click(object sender, RoutedEventArgs e)
    {
        if (_outputPath == null)
        {
            return;
        }

        var files = new System.Collections.Specialized.StringCollection { _outputPath };
        Clipboard.SetFileDropList(files);
        DoneText.Text = "Kopiert. In Discord einfach Strg+V drücken.";
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_outputPath == null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(_clip.Path) + ".mp4",
            InitialDirectory = Directory.Exists(App.ExportFolder) ? App.ExportFolder : Path.GetDirectoryName(_clip.Path),
            Filter = "MP4-Video|*.mp4",
            DefaultExt = ".mp4",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(_clip.Path), StringComparison.OrdinalIgnoreCase))
        {
            await Dialogs.ErrorAsync("Um das Original zu überschreiben, nimm bitte \"Original ersetzen\". Dann landet das alte im Papierkorb.");
            return;
        }

        await SaveToAsync(dialog.FileName);
    }

    private async void SaveCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_outputPath != null)
        {
            await SaveToAsync(ExportCommand.OutputPath(Path.GetDirectoryName(_clip.Path)!, _clip.Path, "Kopie"));
        }
    }

    private async Task SaveToAsync(string target)
    {
        try
        {
            await Task.Run(() => File.Copy(_outputPath!, target, overwrite: true));
            _savedPath = target;
            ShowInExplorerButton.Visibility = Visibility.Visible;
            DoneText.Text = $"Gespeichert: {target}";
        }
        catch (Exception ex)
        {
            Log.Error($"Speichern nach \"{target}\" fehlgeschlagen", ex);
            await Dialogs.ErrorAsync($"Speichern fehlgeschlagen: {ex.Message}");
        }
    }

    private async void Replace_Click(object sender, RoutedEventArgs e) => await ReplaceOriginalAsync(confirm: true);

    private async Task ReplaceOriginalAsync(bool confirm)
    {
        if (_outputPath == null)
        {
            return;
        }

        var original = _clip.Path;
        var target = Path.ChangeExtension(original, ".mp4");
        if (!string.Equals(target, original, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
        {
            await Dialogs.ErrorAsync($"Es gibt schon eine Datei \"{Path.GetFileName(target)}\". Nimm lieber \"Als Kopie speichern\".");
            return;
        }

        var warning = LimitMb != null
            ? "\n\nAchtung: Das ist die verkleinerte Discord-Version. Für volle Qualität im Editor auf \"Speichern\" gehen."
            : string.Empty;
        var ok = !confirm || await Dialogs.ConfirmAsync(
            "Original ersetzen?",
            $"Das Original kommt in den Papierkorb (dort kannst du es zurückholen) und der geschnittene Clip nimmt seinen Platz ein.{warning}",
            "Ersetzen");
        if (!ok)
        {
            return;
        }

        // Der Editor muss die Datei loslassen, sonst kann Windows sie nicht verschieben.
        _editor.ReleaseClip();
        try
        {
            await MoveToRecycleBinWithRetryAsync(original);
            await Task.Run(() => File.Copy(_outputPath, target, overwrite: false));
        }
        catch (Exception ex)
        {
            Log.Error($"Ersetzen von \"{original}\" fehlgeschlagen", ex);
            await Dialogs.ErrorAsync($"Ersetzen hat nicht geklappt: {ex.Message}\n\nFalls das Original schon im Papierkorb ist, kannst du es dort wiederherstellen. Der neue Clip liegt noch hier: {_outputPath}");
            OriginalReplaced = true;
            Close();
            return;
        }

        try
        {
            // Die Schnitte sind jetzt im Clip drin, also nichts mehr gespeichert lassen.
            App.Db.SaveEdit(original, null);
            if (!string.Equals(target, original, StringComparison.OrdinalIgnoreCase))
            {
                App.Db.MovePath(original, target);
            }

            EditorView.NotifyEditsChanged(original);
        }
        catch (Exception ex)
        {
            Log.Error("Datenbank nach dem Ersetzen nicht aktualisiert", ex);
        }

        OriginalReplaced = true;
        Close();
    }

    private static async Task MoveToRecycleBinWithRetryAsync(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ShellFiles.MoveToRecycleBin(path);
                return;
            }
            catch (IOException) when (attempt < 8)
            {
                // Der Player braucht manchmal einen Moment, bis die Datei wirklich frei ist.
                await Task.Delay(400);
            }
        }
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if ((_savedPath ?? _outputPath) is { } path)
        {
            ShellFiles.ShowInExplorer(path);
        }
    }

    private sealed record TargetItem(DiscordTier? Tier, string Label);
}
