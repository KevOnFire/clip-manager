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
            SubfoldersSwitch.IsChecked = s.ScanSubfolders;
            VideoColorsSwitch.IsChecked = s.CorrectVideoColors;
            ThemeSwitch.IsChecked = s.Theme.Enabled;
            ThemeAngleSlider.Value = s.Theme.Angle;
            ThemeStrengthSlider.Value = s.Theme.ValidStrength();
            ShowTheme();

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

    private void SubfoldersSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Settings.ScanSubfolders = SubfoldersSwitch.IsChecked == true;
        App.SaveSettings();
    }

    private void VideoColorsSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Settings.CorrectVideoColors = VideoColorsSwitch.IsChecked == true;
        App.SaveSettings();
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
        var colors = App.Settings.TrackColors;
        var enabled = App.Settings.TrackEnabledByDefault;
        var count = names.Keys.Concat(colors.Keys).Concat(enabled.Keys).DefaultIfEmpty(-1).Max() + 1;
        count = Math.Max(count, 1);
        var rule = Core.Export.ExportPlanner.DefaultAudioTracks(count);
        _trackNames = Enumerable.Range(0, count)
            .Select(i => new TrackNameRow(
                i,
                names.TryGetValue(i, out var n) ? n : string.Empty,
                colors.TryGetValue(i, out var c) && AppSettings.IsHexColor(c) ? c : null)
            {
                Enabled = enabled.TryGetValue(i, out var on) ? on : rule.Contains(i),
            })
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
        App.Settings.TrackColors = _trackNames
            .Where(r => r.Color != null)
            .ToDictionary(r => r.Index, r => r.Color!);
        App.Settings.TrackEnabledByDefault = _trackNames.ToDictionary(r => r.Index, r => r.Enabled);
        App.SaveSettings();
    }

    private void AddTrackName_Click(object sender, RoutedEventArgs e)
    {
        _trackNames.Add(new TrackNameRow(_trackNames.Count, string.Empty, null) { Enabled = true });
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

    private void TrackEnabled_Click(object sender, RoutedEventArgs e) => SaveTrackNames();

    /// <summary>Klick aufs Farbfeld: Menü mit Farben zur Auswahl.</summary>
    private void TrackColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TrackNameRow row } button)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var swatches = new WrapPanel { Width = 6 * 32 };
        foreach (var hex in AppSettings.Palette)
        {
            var swatch = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(3),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = hex,
                Template = SwatchTemplate(ToBrush(hex), hex == row.CurrentColor),
            };
            swatch.Click += (_, _) =>
            {
                row.Color = hex == AppSettings.DefaultTrackColor(row.Index) ? null : hex;
                menu.IsOpen = false;
                ShowTrackNames();
                SaveTrackNames();
            };
            swatches.Children.Add(swatch);
        }

        menu.Items.Add(new MenuItem { Header = swatches, StaysOpenOnClick = true });
        if (row.Color != null)
        {
            var reset = new MenuItem { Header = "Standardfarbe" };
            reset.Click += (_, _) =>
            {
                row.Color = null;
                ShowTrackNames();
                SaveTrackNames();
            };
            menu.Items.Add(reset);
        }

        menu.IsOpen = true;
    }

    // ---------- Design (Farbverlauf) ----------

    private static readonly string[] ThemePalette =
    {
        "#2B1055", "#3A1C71", "#41295A", "#7597DE", "#3F5EFB", "#0F2027", "#203A43", "#2C5364",
        "#0B3D2E", "#3A7D44", "#A8C686", "#FC466B", "#FF5FA2", "#D76D77", "#FFAF7B", "#6F0000",
        "#200122", "#F7971E", "#FFD200", "#00C9FF", "#92FE9D", "#8E2DE2", "#4A00E0", "#1A1A1A",
    };

    private System.Windows.Threading.DispatcherTimer? _themeSaveTimer;

    private void ThemeSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Settings.Theme.Enabled = ThemeSwitch.IsChecked == true;
        ThemeChanged(saveNow: true);
    }

    private void ThemeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded)
        {
            return;
        }

        App.Settings.Theme.Angle = Math.Round(ThemeAngleSlider.Value);
        App.Settings.Theme.Strength = Math.Round(ThemeStrengthSlider.Value, 2);
        ThemeChanged(saveNow: false);
    }

    /// <summary>Vorschau und Fenster sofort anpassen; beim Ziehen am Regler erst kurz danach speichern.</summary>
    private void ThemeChanged(bool saveNow)
    {
        ShowTheme();
        (Window.GetWindow(this) as MainWindow)?.ApplyTheme();
        _themeSaveTimer?.Stop();
        if (saveNow)
        {
            App.SaveSettings();
            return;
        }

        _themeSaveTimer ??= CreateThemeSaveTimer();
        _themeSaveTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer CreateThemeSaveTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            App.SaveSettings();
        };
        return timer;
    }

    private void ShowTheme()
    {
        var theme = App.Settings.Theme;
        ThemePanel.Visibility = theme.Enabled ? Visibility.Visible : Visibility.Collapsed;
        ThemePreview.Background = ThemeBrush.Create(theme);

        ThemePresetList.Children.Clear();
        foreach (var preset in ThemeSettings.Presets)
        {
            var button = new Button
            {
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = preset.Name,
                Template = PresetTemplate(ThemeBrush.Create(preset.Colors, preset.Angle), preset.Name),
            };
            button.Click += (_, _) =>
            {
                theme.Colors = preset.Colors.ToList();
                theme.Angle = preset.Angle;
                _loading = true;
                ThemeAngleSlider.Value = preset.Angle;
                _loading = false;
                ThemeChanged(saveNow: true);
            };
            ThemePresetList.Children.Add(button);
        }

        ThemeColorList.Children.Clear();
        var colors = theme.ValidColors().ToList();
        for (var i = 0; i < colors.Count; i++)
        {
            var index = i;
            var swatch = new Button
            {
                Width = 40,
                Height = 40,
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = colors[i],
                Template = SwatchTemplate(ToBrush(colors[i]), false),
            };
            swatch.Click += (_, _) => ShowThemeColorMenu(swatch, colors, index);
            ThemeColorList.Children.Add(swatch);
        }

        if (colors.Count < ThemeSettings.MaxColors)
        {
            var add = new Wpf.Ui.Controls.Button
            {
                Height = 40,
                Margin = new Thickness(0, 0, 8, 8),
                Content = "Farbe",
                Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Add24 },
                ToolTip = "Weitere Farbe zum Verlauf hinzufügen",
            };
            add.Click += (_, _) =>
            {
                colors.Add(colors[^1]);
                theme.Colors = colors;
                ThemeChanged(saveNow: true);
            };
            ThemeColorList.Children.Add(add);
        }
    }

    private void ShowThemeColorMenu(Button target, List<string> colors, int index)
    {
        var theme = App.Settings.Theme;
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var swatches = new WrapPanel { Width = 8 * 32 };
        foreach (var hex in ThemePalette)
        {
            var swatch = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(3),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = hex,
                Template = SwatchTemplate(ToBrush(hex), string.Equals(hex, colors[index], StringComparison.OrdinalIgnoreCase)),
            };
            swatch.Click += (_, _) =>
            {
                menu.IsOpen = false;
                colors[index] = hex;
                theme.Colors = colors;
                ThemeChanged(saveNow: true);
            };
            swatches.Children.Add(swatch);
        }

        menu.Items.Add(new MenuItem { Header = swatches, StaysOpenOnClick = true });
        var custom = new MenuItem { Header = "Eigene Farbe (Hex-Code) …" };
        custom.Click += async (_, _) =>
        {
            var text = await Dialogs.PromptAsync("Eigene Farbe", "Farbe als Hex-Code, z.B. #FF5FA2 (findest du in jedem Farbwähler im Internet):", colors[index], "Übernehmen");
            if (text == null)
            {
                return;
            }

            text = text.Trim();
            if (!text.StartsWith('#'))
            {
                text = "#" + text;
            }

            if (!AppSettings.IsHexColor(text))
            {
                await Dialogs.ErrorAsync("Das ist kein gültiger Hex-Code. Er sieht so aus: #RRGGBB, z.B. #FF5FA2.");
                return;
            }

            colors[index] = text.ToUpperInvariant();
            theme.Colors = colors;
            ThemeChanged(saveNow: true);
        };
        menu.Items.Add(custom);

        if (colors.Count > ThemeSettings.MinColors)
        {
            var remove = new MenuItem { Header = "Farbe entfernen" };
            remove.Click += (_, _) =>
            {
                colors.RemoveAt(index);
                theme.Colors = colors;
                ThemeChanged(saveNow: true);
            };
            menu.Items.Add(remove);
        }

        menu.IsOpen = true;
    }

    /// <summary>Vorlage: kleines Feld mit dem Verlauf und dem Namen darunter.</summary>
    private static ControlTemplate PresetTemplate(System.Windows.Media.Brush brush, string name)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        var swatch = new FrameworkElementFactory(typeof(Border));
        swatch.SetValue(Border.BackgroundProperty, brush);
        swatch.SetValue(Border.WidthProperty, 96.0);
        swatch.SetValue(Border.HeightProperty, 40.0);
        swatch.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        swatch.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        swatch.SetValue(Border.BorderBrushProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetValue(TextBlock.TextProperty, name);
        label.SetValue(TextBlock.FontSizeProperty, 11.0);
        label.SetValue(TextBlock.MarginProperty, new Thickness(0, 3, 0, 0));
        label.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.AppendChild(swatch);
        panel.AppendChild(label);
        return new ControlTemplate(typeof(Button)) { VisualTree = panel };
    }

    private static ControlTemplate SwatchTemplate(System.Windows.Media.Brush brush, bool selected)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, brush);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(selected ? 2 : 1));
        border.SetValue(
            Border.BorderBrushProperty,
            selected ? System.Windows.Media.Brushes.White : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private static System.Windows.Media.SolidColorBrush ToBrush(string hex)
    {
        var brush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>Eine Zeile in der Liste der Spur-Namen.</summary>
    public sealed class TrackNameRow
    {
        public TrackNameRow(int index, string name, string? color)
        {
            Index = index;
            Name = name;
            Color = color;
        }

        /// <summary>Spur ist beim Öffnen eines Clips an.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Eigene Farbe oder null (= Standardfarbe).</summary>
        public string? Color { get; set; }

        public string CurrentColor => Color ?? AppSettings.DefaultTrackColor(Index);

        public System.Windows.Media.Brush ColorBrush => ToBrush(CurrentColor);

        public int Index { get; }

        public string Name { get; set; }

        public bool IsLast { get; set; }

        public string Label => $"Spur {Index + 1}";

        public string Placeholder => Index == 0 ? "z.B. Alles" : $"Spur {Index + 1}";

        public Visibility RemoveVisibility => IsLast ? Visibility.Visible : Visibility.Hidden;
    }
}
