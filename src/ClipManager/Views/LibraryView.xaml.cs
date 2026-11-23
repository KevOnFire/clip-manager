using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClipManager.Core.Library;
using ClipManager.Core.Media;

namespace ClipManager.Views;

public partial class LibraryView : UserControl
{
    private const int PageSize = 24;

    // ffprobe-Aufrufe begrenzen, damit die Platte nicht überlastet wird.
    private static readonly SemaphoreSlim ProbeLimit = new(4);

    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly List<FileSystemWatcher> _watchers = new();

    private IReadOnlyList<ClipFile> _allClips = Array.Empty<ClipFile>();
    private IReadOnlyList<ClipFile> _filtered = Array.Empty<ClipFile>();
    private HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, TimeSpan> _durations = new(StringComparer.OrdinalIgnoreCase);
    private string _signature = string.Empty;
    private int _page;
    private int _refreshVersion;
    private bool _updatingFilters;
    private CancellationTokenSource? _pageCts;
    private CancellationTokenSource? _analyzeCts;

    public LibraryView()
    {
        InitializeComponent();
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter(resetPage: true);
        };
        _watchTimer.Tick += async (_, _) =>
        {
            _watchTimer.Stop();
            await RefreshAsync(onlyIfChanged: true);
        };
    }

    /// <summary>Liest die Clip-Ordner neu ein.</summary>
    public async Task RefreshAsync(bool onlyIfChanged = false)
    {
        var version = ++_refreshVersion;
        var folders = App.Settings.ClipFolders.ToList();
        UpdateFolderCombo(folders);
        UpdateWatchers(folders);

        if (folders.Count == 0)
        {
            _allClips = Array.Empty<ClipFile>();
            _signature = string.Empty;
            SummaryText.Text = string.Empty;
            ApplyFilter(resetPage: true);
            return;
        }

        if (!onlyIfChanged)
        {
            SummaryText.Text = "Suche Clips …";
        }

        var exportFolder = App.ExportFolder;
        var (clips, favorites, durations) = await Task.Run(() =>
            (ClipScanner.Scan(folders, exportFolder), App.Db.GetFavorites(), App.Db.GetDurations()));

        if (version != _refreshVersion)
        {
            return;
        }

        var signature = string.Join('\n', clips.Select(c => $"{c.Path}|{c.SizeBytes}|{c.ModifiedUtc.Ticks}"));
        if (onlyIfChanged && signature == _signature)
        {
            return;
        }

        _signature = signature;
        _allClips = clips;
        _favorites = favorites;
        _durations = durations;
        UpdateSummary(analyzed: null);
        ApplyFilter(resetPage: false);
        _ = AnalyzeAllAsync(clips);
    }

    // ---------- Anzeige ----------

    private LibraryFilter CurrentFilter() => new(
        FavoritesOnly: ShowCombo.SelectedIndex == 1,
        Folder: (FolderCombo.SelectedItem as FolderItem)?.Path,
        Search: SearchBox.Text,
        Sort: SortCombo.SelectedIndex switch
        {
            1 => ClipSort.Oldest,
            2 => ClipSort.Longest,
            3 => ClipSort.Largest,
            4 => ClipSort.Name,
            _ => ClipSort.Newest,
        });

    private void ApplyFilter(bool resetPage)
    {
        _filtered = LibraryQuery.Apply(_allClips, CurrentFilter(), _favorites, _durations);
        if (resetPage)
        {
            _page = 0;
        }

        _page = Math.Clamp(_page, 0, LibraryQuery.PageCount(_filtered.Count, PageSize) - 1);
        ShowPage();
    }

    private void ShowPage()
    {
        _pageCts?.Cancel();

        if (App.Settings.ClipFolders.Count == 0)
        {
            ShowEmpty("Noch keine Clip-Ordner", "Füge den Ordner hinzu, in den OBS deine Clips speichert.");
            return;
        }

        if (_allClips.Count == 0)
        {
            ShowEmpty("Keine Clips gefunden", "In deinen Clip-Ordnern liegen noch keine Videos.");
            return;
        }

        if (_filtered.Count == 0)
        {
            ShowEmpty("Nichts gefunden", "Kein Clip passt zu deinem Filter.");
            return;
        }

        var tiles = LibraryQuery.Page(_filtered, _page, PageSize)
            .Select(c => new ClipTileViewModel(c, _favorites.Contains(c.Path)))
            .ToList();

        TileList.ItemsSource = tiles;
        EmptyState.Visibility = Visibility.Collapsed;
        TileScroller.Visibility = Visibility.Visible;
        TileScroller.ScrollToTop();

        var pages = LibraryQuery.PageCount(_filtered.Count, PageSize);
        Pager.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text = $"Seite {_page + 1} von {pages}";
        PrevButton.IsEnabled = _page > 0;
        NextButton.IsEnabled = _page < pages - 1;

        var cts = new CancellationTokenSource();
        _pageCts = cts;
        _ = LoadTilesAsync(tiles, cts.Token);
    }

    private void ShowEmpty(string title, string hint)
    {
        EmptyTitle.Text = title;
        EmptyHint.Text = hint;
        TileList.ItemsSource = null;
        TileScroller.Visibility = Visibility.Collapsed;
        Pager.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void UpdateSummary(int? analyzed)
    {
        var folders = App.Settings.ClipFolders;
        var missing = folders.Count(f => !Directory.Exists(f));
        var text = $"{_allClips.Count} Clips in {folders.Count} {(folders.Count == 1 ? "Ordner" : "Ordnern")}";
        if (missing > 0)
        {
            text += $" · {missing} Ordner nicht gefunden";
        }

        if (analyzed is { } done)
        {
            text += $" · analysiere {done} …";
        }

        SummaryText.Text = text;
    }

    private void UpdateFolderCombo(IReadOnlyList<string> folders)
    {
        var selected = (FolderCombo.SelectedItem as FolderItem)?.Path;
        var items = new List<FolderItem> { new(null, "Alle Ordner") };
        items.AddRange(folders.Select(f => new FolderItem(f, Path.GetFileName(f) is { Length: > 0 } n ? n : f)));

        _updatingFilters = true;
        try
        {
            FolderCombo.ItemsSource = items;
            FolderCombo.DisplayMemberPath = nameof(FolderItem.Label);
            FolderCombo.SelectedItem = items.FirstOrDefault(i => i.Path != null && AppSettingsPathEquals(i.Path, selected)) ?? items[0];
            FolderCombo.Visibility = folders.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _updatingFilters = false;
        }
    }

    private static bool AppSettingsPathEquals(string a, string? b) =>
        b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---------- Vorschaubilder und Infos ----------

    private async Task LoadTilesAsync(IReadOnlyList<ClipTileViewModel> tiles, CancellationToken ct)
    {
        var ffmpeg = App.LocateFfmpeg();
        await Task.WhenAll(tiles.Select(t => LoadTileAsync(t, ffmpeg, ct)));
    }

    private async Task LoadTileAsync(ClipTileViewModel tile, FfmpegPaths? ffmpeg, CancellationToken ct)
    {
        try
        {
            tile.Info = await GetMediaInfoAsync(tile.Clip, ffmpeg, ct);
            if (ffmpeg == null || ct.IsCancellationRequested)
            {
                return;
            }

            var thumb = await App.Thumbnails.GetOrCreateAsync(ffmpeg.Ffmpeg, tile.Clip, tile.Info?.Duration, ct);
            if (thumb != null && !ct.IsCancellationRequested)
            {
                tile.SetThumbnailFile(thumb);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Eine kaputte oder gerade geschriebene Datei soll nicht die ganze Seite stören.
        }
    }

    private async Task<MediaInfo?> GetMediaInfoAsync(ClipFile clip, FfmpegPaths? ffmpeg, CancellationToken ct)
    {
        var cached = await Task.Run(() => App.Db.GetMediaInfo(clip), ct);
        if (cached != null || ffmpeg?.Ffprobe == null)
        {
            return cached;
        }

        await ProbeLimit.WaitAsync(ct);
        try
        {
            var info = await new MediaInfoReader(App.ProcessRunner).ReadAsync(ffmpeg.Ffprobe, clip.Path, ct);
            if (info != null && info.Duration > TimeSpan.Zero)
            {
                await Task.Run(() => App.Db.SaveMediaInfo(clip, info), CancellationToken.None);
                _durations[clip.Path] = info.Duration;
            }

            return info;
        }
        finally
        {
            ProbeLimit.Release();
        }
    }

    /// <summary>Liest im Hintergrund die Länge aller Clips, damit "Längste zuerst" funktioniert.</summary>
    private async Task AnalyzeAllAsync(IReadOnlyList<ClipFile> clips)
    {
        _analyzeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _analyzeCts = cts;

        var ffmpeg = App.LocateFfmpeg();
        if (ffmpeg?.Ffprobe == null)
        {
            return;
        }

        var missing = clips.Where(c => !_durations.ContainsKey(c.Path)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var done = 0;
        try
        {
            foreach (var chunk in missing.Chunk(4))
            {
                await Task.WhenAll(chunk.Select(c => GetMediaInfoAsync(c, ffmpeg, cts.Token)));
                done += chunk.Length;
                UpdateSummary(analyzed: null);
                SummaryText.Text += $" · Clip-Infos {done}/{missing.Count}";
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        if (!cts.IsCancellationRequested)
        {
            UpdateSummary(analyzed: null);
            if (CurrentFilter().Sort == ClipSort.Longest)
            {
                ApplyFilter(resetPage: false);
            }
        }
    }

    // ---------- Ordner überwachen ----------

    private void UpdateWatchers(IReadOnlyList<string> folders)
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                };
                watcher.Created += OnFolderChanged;
                watcher.Deleted += OnFolderChanged;
                watcher.Changed += OnFolderChanged;
                watcher.Renamed += OnFolderChanged;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // Ordner nicht überwachbar (z.B. Netzlaufwerk): dann eben nur per "Aktualisieren".
            }
        }
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e)
    {
        var relevant = ClipScanner.VideoExtensions.Contains(Path.GetExtension(e.FullPath))
            || (e is RenamedEventArgs r && ClipScanner.VideoExtensions.Contains(Path.GetExtension(r.OldFullPath)));
        if (!relevant)
        {
            return;
        }

        // Kommt aus einem Hintergrund-Thread; Timer neu starten = warten, bis OBS fertig geschrieben hat.
        Dispatcher.BeginInvoke(() =>
        {
            _watchTimer.Stop();
            _watchTimer.Start();
        });
    }

    // ---------- Bedienung ----------

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderPicker.AddClipFolders() > 0)
        {
            await RefreshAsync();
        }
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingFilters && IsLoaded)
        {
            ApplyFilter(resetPage: true);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        _page--;
        ApplyFilter(resetPage: false);
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        _page++;
        ApplyFilter(resetPage: false);
    }

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        // Der Editor kommt in Etappe 3. Bis dahin öffnet ein Klick den Clip im Standard-Player.
        if (TileOf(sender) is { } tile)
        {
            ShellFiles.OpenWithDefaultApp(tile.Clip.Path);
        }
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        if (TileOf(sender) is not { } tile)
        {
            return;
        }

        tile.IsFavorite = !tile.IsFavorite;
        App.Db.SetFavorite(tile.Clip.Path, tile.IsFavorite);
        if (tile.IsFavorite)
        {
            _favorites.Add(tile.Clip.Path);
        }
        else
        {
            _favorites.Remove(tile.Clip.Path);
            if (CurrentFilter().FavoritesOnly)
            {
                ApplyFilter(resetPage: false);
            }
        }
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button && FindResource("TileMenu") is ContextMenu menu)
        {
            menu.DataContext = button.DataContext;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void MenuPlay_Click(object sender, RoutedEventArgs e)
    {
        if (TileOf(sender) is { } tile)
        {
            ShellFiles.OpenWithDefaultApp(tile.Clip.Path);
        }
    }

    private void MenuExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (TileOf(sender) is { } tile)
        {
            ShellFiles.ShowInExplorer(tile.Clip.Path);
        }
    }

    private async void MenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (TileOf(sender) is not { } tile)
        {
            return;
        }

        var name = await Dialogs.PromptAsync("Clip umbenennen", "Neuer Name:", tile.Name, "Umbenennen");
        if (name == null)
        {
            return;
        }

        if (ClipFileOps.ValidateName(name) is { } problem)
        {
            await Dialogs.ErrorAsync(problem);
            return;
        }

        try
        {
            var oldPath = tile.Clip.Path;
            var newPath = ClipFileOps.Rename(oldPath, name);
            App.Db.MovePath(oldPath, newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await Dialogs.ErrorAsync(ex.Message);
        }

        await RefreshAsync();
    }

    private async void MenuCopy_Click(object sender, RoutedEventArgs e)
    {
        if (TileOf(sender) is not { } tile)
        {
            return;
        }

        try
        {
            SummaryText.Text = "Kopiere …";
            await Task.Run(() => ClipFileOps.CreateCopy(tile.Clip.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ErrorAsync(ex.Message);
        }

        await RefreshAsync();
    }

    private async void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (TileOf(sender) is not { } tile)
        {
            return;
        }

        var ok = await Dialogs.ConfirmAsync(
            "Clip löschen?",
            $"\"{tile.Name}\" wird in den Papierkorb verschoben. Von dort kannst du ihn wiederherstellen.",
            "Löschen");
        if (!ok)
        {
            return;
        }

        try
        {
            ShellFiles.MoveToRecycleBin(tile.Clip.Path);
            App.Db.RemovePath(tile.Clip.Path);
        }
        catch (IOException ex)
        {
            await Dialogs.ErrorAsync(ex.Message);
        }

        await RefreshAsync();
    }

    private static ClipTileViewModel? TileOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ClipTileViewModel;

    private sealed record FolderItem(string? Path, string Label);
}
