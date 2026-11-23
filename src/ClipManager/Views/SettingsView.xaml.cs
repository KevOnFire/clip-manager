using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ClipManager.Core.Media;
using ClipManager.Core.Settings;
using ClipManager.Core.Updates;
using Microsoft.Win32;

namespace ClipManager.Views;

public partial class SettingsView : UserControl
{
    private bool _loading;
    private bool _ffmpegChecked;
    private ReleaseInfo? _release;

    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>Überträgt die gespeicherten Einstellungen in die Felder.</summary>
    public void Refresh()
    {
        _loading = true;
        try
        {
            var s = App.Settings;
            FolderList.ItemsSource = null;
            FolderList.ItemsSource = s.ClipFolders.ToList();

            FreeLimitBox.Text = s.LimitFor(DiscordTier.Free).ToString();
            BasicLimitBox.Text = s.LimitFor(DiscordTier.NitroBasic).ToString();
            NitroLimitBox.Text = s.LimitFor(DiscordTier.Nitro).ToString();
            FillTierCombo();
            FillTrackNames();

            ExportFolderText.Text = App.ExportFolder;
            ResetExportFolderButton.IsEnabled = !string.IsNullOrWhiteSpace(s.ExportFolder);
            if (_release == null)
            {
                UpdateStatusText.Text = $"Installiert: Version {AppUpdater.CurrentVersionText}";
            }
        }
        finally
        {
            _loading = false;
        }

        if (!_ffmpegChecked)
        {
            _ffmpegChecked = true;
            _ = CheckFfmpegAsync();
        }
    }

    private void FillTierCombo()
    {
        var s = App.Settings;
        TierCombo.ItemsSource = new[]
        {
            new TierItem(DiscordTier.Free, $"Kein Nitro ({s.LimitFor(DiscordTier.Free)} MB)"),
            new TierItem(DiscordTier.NitroBasic, $"Nitro Basic ({s.LimitFor(DiscordTier.NitroBasic)} MB)"),
            new TierItem(DiscordTier.Nitro, $"Nitro ({s.LimitFor(DiscordTier.Nitro)} MB)"),
        };
        TierCombo.DisplayMemberPath = nameof(TierItem.Label);
        TierCombo.SelectedIndex = (int)s.Tier;
    }

    private void TierCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TierCombo.SelectedItem is not TierItem item)
        {
            return;
        }

        App.Settings.Tier = item.Tier;
        App.SaveSettings();
    }

    private void LimitBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        Apply(FreeLimitBox, DiscordTier.Free);
        Apply(BasicLimitBox, DiscordTier.NitroBasic);
        Apply(NitroLimitBox, DiscordTier.Nitro);
        App.SaveSettings();

        _loading = true;
        try
        {
            FillTierCombo();
        }
        finally
        {
            _loading = false;
        }

        void Apply(TextBox box, DiscordTier tier)
        {
            if (int.TryParse(box.Text.Trim(), out var mb) && mb is > 0 and <= 100_000)
            {
                s.TierLimitsMb[tier] = mb;
            }

            box.Text = s.LimitFor(tier).ToString();
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderPicker.AddClipFolders() > 0)
        {
            Refresh();
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not string folder)
        {
            return;
        }

        App.Settings.RemoveClipFolder(folder);
        App.SaveSettings();
        Refresh();
    }

    private void ChangeExportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Export-Ordner auswählen" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        App.Settings.ExportFolder = dialog.FolderName;
        App.SaveSettings();
        Refresh();
    }

    private void ResetExportFolder_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.ExportFolder = null;
        App.SaveSettings();
        Refresh();
    }

    private void OpenExportFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = App.ExportFolder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.LogFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.LogFolder}\"") { UseShellExecute = true });
    }

    private async void CheckFfmpeg_Click(object sender, RoutedEventArgs e) => await CheckFfmpegAsync();

    private async Task CheckFfmpegAsync()
    {
        CheckFfmpegButton.IsEnabled = false;
        FfmpegStatusText.Text = "Wird geprüft …";
        FfmpegPathText.Text = string.Empty;
        NvidiaText.Text = "…";
        AmdText.Text = "…";

        try
        {
            var paths = App.LocateFfmpeg();
            var status = await new FfmpegProbe(App.ProcessRunner).CheckAsync(paths);

            FfmpegStatusText.Text = status.Found
                ? $"✓ Gefunden, Version {status.Version}"
                : App.RunsFromTempFolder
                    ? "✗ Das Programm wurde direkt aus der ZIP-Datei gestartet. Bitte die ZIP erst komplett entpacken (Rechtsklick → Alle extrahieren) und dann die ClipManager.exe im entpackten Ordner starten."
                    : $"✗ {status.Error} Im Ordner \"{App.AppDirectory}\" muss ein Unterordner \"ffmpeg\" mit ffmpeg.exe liegen.";
            FfmpegPathText.Text = status.Paths?.Ffmpeg ?? "–";
            NvidiaText.Text = Describe(status.Nvidia);
            AmdText.Text = Describe(status.Amd);
        }
        finally
        {
            CheckFfmpegButton.IsEnabled = true;
        }
    }

    private static string Describe(GpuEncoderState state) => state switch
    {
        GpuEncoderState.Available => "✓ Funktioniert",
        GpuEncoderState.Unavailable => "✗ Keine passende Grafikkarte gefunden",
        _ => "✗ Nicht in diesem FFmpeg enthalten",
    };

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Suche nach Updates …";
        try
        {
            _release = await AppUpdater.CheckAsync();
            UpdateStatusText.Text = _release == null
                ? $"Du hast die neueste Version ({AppUpdater.CurrentVersionText})."
                : $"Version {_release.Version.ToString(3)} ist verfügbar (installiert: {AppUpdater.CurrentVersionText}).";
            InstallUpdateButton.Visibility = _release == null ? Visibility.Collapsed : Visibility.Visible;
            (Application.Current.MainWindow as MainWindow)?.ShowUpdate(_release);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_release != null && Application.Current.MainWindow is MainWindow window)
        {
            await window.InstallUpdateAsync(_release);
        }
    }

    private sealed record TierItem(DiscordTier Tier, string Label);

    // ---------- Tonspur-Namen ----------

    private List<TrackNameRow> _trackNames = new();

    private void FillTrackNames()
    {
        var names = App.Settings.TrackNames;
        var count = Math.Max(names.Count == 0 ? 0 : names.Keys.Max() + 1, 1);
        _trackNames = Enumerable.Range(0, count)
            .Select(i => new TrackNameRow(i, names.TryGetValue(i, out var n) ? n : string.Empty))
            .ToList();
        ShowTrackNames();
    }

    private void ShowTrackNames()
    {
        for (var i = 0; i < _trackNames.Count; i++)
        {
            _trackNames[i].IsLast = i == _trackNames.Count - 1 && _trackNames.Count > 1;
        }

        TrackNameList.ItemsSource = null;
        TrackNameList.ItemsSource = _trackNames;
    }

    private void SaveTrackNames()
    {
        App.Settings.TrackNames = _trackNames
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .ToDictionary(r => r.Index, r => r.Name.Trim());
        App.SaveSettings();
    }

    private void AddTrackName_Click(object sender, RoutedEventArgs e)
    {
        _trackNames.Add(new TrackNameRow(_trackNames.Count, string.Empty));
        ShowTrackNames();
    }

    private void RemoveTrackName_Click(object sender, RoutedEventArgs e)
    {
        if (_trackNames.Count > 1)
        {
            _trackNames.RemoveAt(_trackNames.Count - 1);
            ShowTrackNames();
            SaveTrackNames();
        }
    }

    private void TrackName_LostFocus(object sender, RoutedEventArgs e) => SaveTrackNames();

    /// <summary>Eine Zeile in der Liste der Spur-Namen.</summary>
    public sealed class TrackNameRow
    {
        public TrackNameRow(int index, string name)
        {
            Index = index;
            Name = name;
        }

        public int Index { get; }

        public string Name { get; set; }

        public bool IsLast { get; set; }

        public string Label => $"Spur {Index + 1}";

        public string Placeholder => Index == 0 ? "z.B. Alles" : $"Spur {Index + 1}";

        public Visibility RemoveVisibility => IsLast ? Visibility.Visible : Visibility.Collapsed;
    }
}
