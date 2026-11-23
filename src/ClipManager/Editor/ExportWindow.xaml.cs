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

/// <summary>Export-Dialog: Ziel wählen, exportieren, Ergebnis rausziehen/kopieren/speichern.</summary>
public partial class ExportWindow : FluentWindow
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private readonly ClipFile _clip;
    private readonly IReadOnlyList<TimeRange> _ranges;
    private readonly TimeSpan _outputDuration;
    private MediaInfo? _info;
    private VideoEncoder _encoder = VideoEncoder.Cpu;
    private CancellationTokenSource? _cts;
    private string? _outputPath;
    private Point _dragStart;

    public ExportWindow(ClipFile clip, MediaInfo? info, IReadOnlyList<TimeRange> ranges)
    {
        InitializeComponent();
        _clip = clip;
        _info = info;
        _ranges = ranges;
        _outputDuration = ranges.Aggregate(TimeSpan.Zero, (sum, r) => sum + r.Duration);

        var s = App.Settings;
        TargetCombo.ItemsSource = new[]
        {
            new TargetItem(DiscordTier.Free, $"Discord ohne Nitro (max. {s.LimitFor(DiscordTier.Free)} MB)"),
            new TargetItem(DiscordTier.NitroBasic, $"Discord Nitro Basic (max. {s.LimitFor(DiscordTier.NitroBasic)} MB)"),
            new TargetItem(DiscordTier.Nitro, $"Discord Nitro (max. {s.LimitFor(DiscordTier.Nitro)} MB)"),
            new TargetItem(null, "Volle Qualität als Kopie (ohne Größenlimit)"),
        };
        TargetCombo.DisplayMemberPath = nameof(TargetItem.Label);
        TargetCombo.SelectedIndex = (int)s.Tier;

        Loaded += async (_, _) => await PrepareAsync();
        Closing += (_, e) =>
        {
            if (_cts != null)
            {
                _cts.Cancel();
            }
        };
    }

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
        var tracks = ExportPlanner.DefaultAudioTracks(_info?.AudioTracks.Count ?? 1);
        var trackText = tracks.Count == 0 ? "ohne Ton" : $"Ton: Spur {string.Join(", ", tracks.Select(t => t + 1))} gemischt";
        PlanDetailText.Text = $"{plan.Describe()} · {trackText} · Kodiert mit {encoder}";

        WarningText.Visibility = plan.TooLongWarning ? Visibility.Visible : Visibility.Collapsed;
        WarningText.Text = "Der Clip ist für dieses Limit ziemlich lang, die Qualität wird schlecht. Kürzer schneiden hilft.";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
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
            output = limit == null
                ? ExportCommand.OutputPath(Path.GetDirectoryName(_clip.Path)!, _clip.Path, "Kopie")
                : ExportCommand.OutputPath(App.ExportFolder, _clip.Path, "Discord");
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
            ExportPlanner.DefaultAudioTracks(_info?.AudioTracks.Count ?? 1),
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
        FileNameText.Text = Path.GetFileName(_outputPath);
        DonePanel.Visibility = Visibility.Visible;
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
            FileName = Path.GetFileName(_outputPath),
            Filter = "MP4-Video|*.mp4",
            DefaultExt = ".mp4",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.Copy(_outputPath, dialog.FileName, overwrite: true);
            DoneText.Text = $"Gespeichert unter {dialog.FileName}";
        }
        catch (Exception ex)
        {
            await Dialogs.ErrorAsync($"Speichern fehlgeschlagen: {ex.Message}");
        }
    }

    private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_outputPath != null)
        {
            ShellFiles.ShowInExplorer(_outputPath);
        }
    }

    private sealed record TargetItem(DiscordTier? Tier, string Label);
}
