using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ClipManager.Core.Library;

namespace ClipManager.Views;

public partial class LibraryView : UserControl
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private int _refreshVersion;

    public LibraryView()
    {
        InitializeComponent();
    }

    public async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        var folders = App.Settings.ClipFolders.ToList();

        if (folders.Count == 0)
        {
            ShowEmpty("Noch keine Clip-Ordner", "Füge den Ordner hinzu, in den OBS deine Clips speichert.");
            SummaryText.Text = string.Empty;
            return;
        }

        SummaryText.Text = "Suche Clips …";
        var exportFolder = App.ExportFolder;
        var clips = await Task.Run(() => ClipScanner.Scan(folders, exportFolder));

        // Falls inzwischen schon neu geladen wurde, dieses Ergebnis verwerfen.
        if (version != _refreshVersion)
        {
            return;
        }

        var missing = folders.Count(f => !Directory.Exists(f));
        var folderWord = folders.Count == 1 ? "Ordner" : "Ordnern";
        SummaryText.Text = $"{clips.Count} Clips in {folders.Count} {folderWord}"
            + (missing > 0 ? $" · {missing} Ordner nicht gefunden" : string.Empty);

        if (clips.Count == 0)
        {
            ShowEmpty("Keine Clips gefunden", "In deinen Clip-Ordnern liegen noch keine Videos.");
            return;
        }

        ClipList.ItemsSource = clips.Select(ToRow).ToList();
        EmptyState.Visibility = Visibility.Collapsed;
        ClipList.Visibility = Visibility.Visible;
    }

    private void ShowEmpty(string title, string hint)
    {
        EmptyTitle.Text = title;
        EmptyHint.Text = hint;
        ClipList.ItemsSource = null;
        ClipList.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    private static ClipRow ToRow(ClipFile clip) => new(
        clip.Name,
        Path.GetDirectoryName(clip.Path) ?? clip.RootFolder,
        $"{clip.ModifiedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", German)}   ·   {FormatSize(clip.SizeBytes)}");

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? (bytes / (1024d * 1024 * 1024)).ToString("0.0", German) + " GB"
            : (bytes / (1024d * 1024)).ToString("0.0", German) + " MB";

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderPicker.AddClipFolders() > 0)
        {
            await RefreshAsync();
        }
    }

    private sealed record ClipRow(string Name, string Folder, string Details);
}
