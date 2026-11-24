using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClipManager.Core;
using ClipManager.Core.Library;
using ClipManager.Core.Media;

namespace ClipManager.Views;

public partial class LibraryView : UserControl
{
    // Kachel 296 breit + 16 Abstand (siehe TileTemplate). Reihen pro Seite fest, Spalten je nach Fensterbreite,
    // damit die letzte Reihe voll ist.
    private const double TileSlotWidth = 312;
    private const int RowsPerPage = 5;
    private int _pageSize = 25;
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private int PageSize => _pageSize;

    // ffprobe-Aufrufe begrenzen, damit die Platte nicht überlastet wird.
    private static readonly SemaphoreSlim ProbeLimit = new(4);

    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly List<FileSystemWatcher> _watchers = new();

    private IReadOnlyList<ClipFile> _allClips = Array.Empty<ClipFile>();
    private IReadOnlyList<ClipFile> _filtered = Array.Empty<ClipFile>();
    private HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _edited = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, TimeSpan> _durations = new(StringComparer.OrdinalIgnoreCase);
    private List<ClipCategory> _categories = new();
    private Dictionary<long, HashSet<string>> _members = new();
    private string _signature = string.Empty;
    private int _page;
    private int _refreshVersion;
    private bool _updatingFilters;
    private CancellationTokenSource? _pageCts;
    private CancellationTokenSource? _analyzeCts;

    public LibraryView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            _resizeTimer.Stop();
            _resizeTimer.Start();
        };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            UpdatePageSize();
        };
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
        Editor.EditorView.EditsSaved += OnEditsSaved;
        Editor.EditorView.FavoriteChanged += OnFavoriteChanged;
        _longPressTimer.Tick += LongPress_Tick;
        PreviewMouseLeftButtonUp += (_, _) => EndDragSelect();
        FillShowCombo(null);
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
        var subfolders = App.Settings.ScanSubfolders;
        var (clips, favorites, durations, edited, names) = await Task.Run(() =>
            (ClipScanner.Scan(folders, exportFolder, subfolders), App.Db.GetFavorites(), App.Db.GetDurations(), App.Db.GetEditedPaths(), App.Db.GetDisplayNames()));
        LoadCategories();

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
        _edited = edited;
        _names = names;
        UpdateSummary(analyzed: null);
        ApplyFilter(resetPage: false);
        _ = AnalyzeAllAsync(clips);
    }

    // ---------- Anzeige ----------

    private object? ShowTag => (ShowCombo.SelectedItem as ComboBoxItem)?.Tag;

    /// <summary>Gewählte eigene Kategorie (oder null).</summary>
    private ClipCategory? CurrentCategory => ShowTag is long id ? _categories.FirstOrDefault(c => c.Id == id) : null;

    private LibraryFilter CurrentFilter() => new(
        FavoritesOnly: ShowTag is "fav",
        Edited: ShowTag switch
        {
            "edited" => EditedFilter.Edited,
            "unedited" => EditedFilter.Unedited,
            _ => EditedFilter.All,
        },
        OnlyPaths: ShowTag is long id ? _members.GetValueOrDefault(id) ?? new HashSet<string>() : null,
        Folder: (FolderCombo.SelectedItem as FolderItem)?.Path,
        Search: SearchBox.Text,
        Sort: SortCombo.SelectedIndex switch
        {
            1 => ClipSort.Oldest,
            2 => ClipSort.Longest,
            3 => ClipSort.Largest,
            4 => ClipSort.Name,
            5 => ClipSort.EditedFirst,
            _ => ClipSort.Newest,
        });

    private void ApplyFilter(bool resetPage)
    {
        _filtered = LibraryQuery.Apply(_allClips, CurrentFilter(), _favorites, _durations, _edited, _names);
        if (resetPage)
        {
            _page = 0;
        }

        _page = Math.Clamp(_page, 0, LibraryQuery.PageCount(_filtered.Count, PageSize) - 1);
        ShowPage();
    }

    /// <summary>Seitengröße an die Spaltenzahl anpassen; die Seite mit dem bisher ersten Clip bleibt sichtbar.</summary>
    private void UpdatePageSize()
    {
        // Breite für die Kacheln: Steuerelement minus Bildlaufleiste.
        var width = TileList.ActualWidth > 0 ? TileList.ActualWidth : ActualWidth - SystemParameters.VerticalScrollBarWidth;
        var size = LibraryQuery.PageSizeFor(width, TileSlotWidth, RowsPerPage);
        if (size == _pageSize)
        {
            return;
        }

        _page = LibraryQuery.PageAfterResize(_page, _pageSize, size);
        _pageSize = size;
        if (_filtered.Count > 0)
        {
            _page = Math.Clamp(_page, 0, LibraryQuery.PageCount(_filtered.Count, PageSize) - 1);
            ShowPage();
        }
    }

    private void ShowPage()
    {
        _pageCts?.Cancel();
        StopPreviews();

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
            .Select(c => new ClipTileViewModel(c, _favorites.Contains(c.Path), _edited.Contains(c.Path), _names.GetValueOrDefault(c.Path))
            {
                SelectionMode = _selecting,
                IsSelected = _selected.Contains(c.Path),
            })
            .ToList();

        TileList.ItemsSource = tiles;
        EmptyState.Visibility = Visibility.Collapsed;
        TileScroller.Visibility = Visibility.Visible;
        TileScroller.ScrollToTop();

        var pages = LibraryQuery.PageCount(_filtered.Count, PageSize);
        Pager.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageText.Text = $"von {pages}";
        PageBox.Text = (_page + 1).ToString();
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

        if (App.LocateFfmpeg() == null)
        {
            text += " · ⚠ FFmpeg fehlt, keine Vorschaubilder (siehe Einstellungen)";
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
        if (ffmpeg == null)
        {
            Log.Error("Keine Vorschaubilder: FFmpeg nicht gefunden");
        }

        await Task.WhenAll(tiles.Select(t => LoadTileAsync(t, ffmpeg, ct)));
    }

    private async Task LoadTileAsync(ClipTileViewModel tile, FfmpegPaths? ffmpeg, CancellationToken ct)
    {
        // Infos und Vorschaubild getrennt: scheitert eins, soll das andere trotzdem kommen.
        try
        {
            tile.Info = await GetMediaInfoAsync(tile.Clip, ffmpeg, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error($"Clip-Infos für \"{tile.Clip.Path}\" konnten nicht gelesen werden", ex);
        }

        if (ffmpeg == null || ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var thumb = await App.Thumbnails.GetOrCreateAsync(ffmpeg.Ffmpeg, tile.Clip, tile.Info?.Duration, ct);
            if (thumb != null && !ct.IsCancellationRequested)
            {
                tile.SetThumbnailFile(thumb);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"Vorschaubild für \"{tile.Clip.Path}\" konnte nicht angezeigt werden", ex);
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
        catch (Exception ex)
        {
            Log.Error("Analyse der Clips abgebrochen", ex);
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

    // ---------- Hover-Vorschau: Clip läuft klein und stumm in der Kachel ----------

    private DispatcherTimer? _hoverTimer;
    private Grid? _hoverGrid;
    private MediaElement? _hoverPlayer;

    private void Thumb_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_selecting || sender is not Grid grid || (grid.DataContext as ClipTileViewModel) is null)
        {
            return;
        }

        StopPreviews();
        _hoverGrid = grid;
        // Kurz warten, damit beim Drüberfahren mit der Maus nicht jede Kachel anfängt zu laden.
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer?.Stop();
            StartPreview(grid);
        };
        _hoverTimer.Start();
    }

    private void Thumb_MouseLeave(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _hoverGrid))
        {
            StopPreviews();
        }
    }

    private void StartPreview(Grid grid)
    {
        if (!ReferenceEquals(grid, _hoverGrid) || grid.DataContext is not ClipTileViewModel tile || !File.Exists(tile.Clip.Path))
        {
            return;
        }

        var player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            IsMuted = true,
            Volume = 0,
            Stretch = Stretch.UniformToFill,
            IsHitTestVisible = false,
            Opacity = 0,
        };
        player.MediaOpened += (_, _) => player.Opacity = 1;
        player.MediaEnded += (_, _) =>
        {
            player.Position = TimeSpan.Zero;
            player.Play();
        };
        player.MediaFailed += (_, _) => StopPreviews();
        player.Source = new Uri(tile.Clip.Path);

        // Über dem Vorschaubild, aber unter Dauer, Schere und Stern
        var index = Math.Min(2, grid.Children.Count);
        grid.Children.Insert(index, player);
        _hoverPlayer = player;
        player.Play();
    }

    /// <summary>Hält die Hover-Vorschau an und gibt die Datei frei.</summary>
    public void StopPreviews()
    {
        _hoverTimer?.Stop();
        _hoverTimer = null;
        if (_hoverPlayer is { } player)
        {
            player.Stop();
            player.Close();
            player.Source = null;
            (player.Parent as Panel)?.Children.Remove(player);
        }

        _hoverPlayer = null;
        _hoverGrid = null;
    }

    /// <summary>Clip neben <paramref name="path"/> in der aktuellen Reihenfolge (Filter + Sortierung), sonst null.</summary>
    public ClipFile? Neighbor(string path, int direction)
    {
        for (var i = 0; i < _filtered.Count; i++)
        {
            if (string.Equals(_filtered[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                var j = i + direction;
                return j >= 0 && j < _filtered.Count ? _filtered[j] : null;
            }
        }

        return null;
    }

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        StopPreviews();
        _longPressTimer.Stop();
        // Auswahl läuft (oder lange gedrückt): kein Editor öffnen.
        if (_dragSelecting || _selecting)
        {
            EndDragSelect();
            e.Handled = true;
            return;
        }

        if (TileOf(sender) is { } tile)
        {
            (Window.GetWindow(this) as MainWindow)?.OpenEditor(tile.Clip, tile.Info);
        }
    }

    /// <summary>Der Editor hat Schnitte gespeichert oder gelöscht: Schere auf der Kachel anpassen.</summary>
    private void OnEditsSaved(object? sender, string path)
    {
        var edited = App.Db.GetEdit(path) != null;
        if (edited)
        {
            _edited.Add(path);
        }
        else
        {
            _edited.Remove(path);
        }

        if (TileList.ItemsSource is IEnumerable<ClipTileViewModel> tiles)
        {
            foreach (var tile in tiles.Where(t => string.Equals(t.Clip.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                tile.IsEdited = edited;
            }
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

    /// <summary>Eigener Name nur fürs Programm: die Datei bleibt, wie sie ist.</summary>
    private async void MenuDisplayName_Click(object sender, RoutedEventArgs e)
    {
        if (TileOf(sender) is not { } tile)
        {
            return;
        }

        var name = await Dialogs.PromptAsync(
            "Namen im Programm ändern",
            $"So heißt der Clip nur hier in Clippy, die Datei bleibt \"{Path.GetFileName(tile.Clip.Path)}\". Leer lassen = Dateiname.",
            tile.DisplayName ?? tile.Clip.Name,
            "Speichern");
        if (name == null)
        {
            return;
        }

        name = name.Trim();
        var value = name.Length == 0 || name == tile.Clip.Name ? null : name;
        App.Db.SetDisplayName(tile.Clip.Path, value);
        if (value == null)
        {
            _names.Remove(tile.Clip.Path);
        }
        else
        {
            _names[tile.Clip.Path] = value;
        }

        tile.DisplayName = value;
    }

    private async void MenuRename_Click(object sender, RoutedEventArgs e)
    {
        StopPreviews();
        if (TileOf(sender) is not { } tile)
        {
            return;
        }

        var name = await Dialogs.PromptAsync("Datei umbenennen", "Neuer Dateiname:", tile.Clip.Name, "Umbenennen");
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
        StopPreviews();
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

    // ---------- Eigene Kategorien ----------

    private const string NewCategoryTag = "new";

    private void LoadCategories()
    {
        try
        {
            _categories = App.Db.GetCategories();
            _members = App.Db.GetCategoryMembers();
        }
        catch (Exception ex)
        {
            Log.Error("Kategorien konnten nicht geladen werden", ex);
        }

        FillShowCombo(ShowTag);
    }

    /// <summary>Filter-Liste: feste Einträge, darunter abgetrennt die eigenen Kategorien und "Neue Kategorie".</summary>
    private void FillShowCombo(object? select)
    {
        _updatingFilters = true;
        try
        {
            ShowCombo.Items.Clear();
            ShowCombo.Items.Add(new ComboBoxItem { Content = "Alle Clips", Tag = "all" });
            ShowCombo.Items.Add(new ComboBoxItem { Content = "★ Favoriten", Tag = "fav" });
            ShowCombo.Items.Add(new ComboBoxItem { Content = "✂ Geschnitten", Tag = "edited" });
            ShowCombo.Items.Add(new ComboBoxItem { Content = "Ungeschnitten", Tag = "unedited" });
            ShowCombo.Items.Add(new Separator());
            ShowCombo.Items.Add(new ComboBoxItem
            {
                Content = "EIGENE KATEGORIEN",
                IsEnabled = false,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
            });
            foreach (var category in _categories)
            {
                var count = _members.GetValueOrDefault(category.Id)?.Count ?? 0;
                ShowCombo.Items.Add(new ComboBoxItem { Content = $"🏷 {category.Name}  ({count})", Tag = category.Id });
            }

            ShowCombo.Items.Add(new ComboBoxItem { Content = "＋ Neue Kategorie …", Tag = NewCategoryTag });

            var match = ShowCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, select) && !Equals(i.Tag, NewCategoryTag));
            ShowCombo.SelectedItem = match ?? ShowCombo.Items[0];
        }
        finally
        {
            _updatingFilters = false;
        }

        UpdateCategoryButtons();
    }

    private void UpdateCategoryButtons()
    {
        var category = CurrentCategory;
        CategoryMenuButton.Visibility = category != null ? Visibility.Visible : Visibility.Collapsed;
        RemoveFromCategoryButton.Visibility = category != null ? Visibility.Visible : Visibility.Collapsed;
        if (category != null)
        {
            RemoveFromCategoryButton.Content = $"Aus „{category.Name}“ entfernen";
        }
    }

    private object? _lastShowTag = "all";

    private async void ShowCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingFilters || !IsLoaded)
        {
            return;
        }

        if (ShowTag is NewCategoryTag)
        {
            var created = await AskNewCategoryAsync();
            FillShowCombo(created?.Id ?? _lastShowTag);
        }

        _lastShowTag = ShowTag;
        UpdateCategoryButtons();
        ApplyFilter(resetPage: true);
    }

    /// <summary>Fragt nach einem Namen und legt die Kategorie an (null = abgebrochen).</summary>
    private async Task<ClipCategory?> AskNewCategoryAsync()
    {
        var name = await Dialogs.PromptAsync("Neue Kategorie", "Name der Kategorie (z.B. Aces, Lustig, Fails):", string.Empty, "Anlegen");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            var category = App.Db.AddCategory(name);
            _categories = App.Db.GetCategories();
            return category;
        }
        catch (Exception ex)
        {
            Log.Error("Kategorie konnte nicht angelegt werden", ex);
            await Dialogs.ErrorAsync(ex.Message);
            return null;
        }
    }

    private void CategoryMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentCategory is not { } category)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = CategoryMenuButton };
        var rename = new MenuItem { Header = "Umbenennen", Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Rename24 } };
        rename.Click += async (_, _) =>
        {
            var name = await Dialogs.PromptAsync("Kategorie umbenennen", "Neuer Name:", category.Name, "Umbenennen");
            if (name == null)
            {
                return;
            }

            if (!App.Db.RenameCategory(category.Id, name))
            {
                await Dialogs.ErrorAsync("Der Name ist leer oder schon vergeben.");
                return;
            }

            LoadCategories();
        };
        var delete = new MenuItem { Header = "Kategorie löschen", Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 } };
        delete.Click += async (_, _) =>
        {
            var ok = await Dialogs.ConfirmAsync(
                "Kategorie löschen",
                $"Kategorie „{category.Name}“ löschen? Die Clips selbst bleiben, sie sind nur nicht mehr in dieser Kategorie.",
                "Löschen");
            if (!ok)
            {
                return;
            }

            App.Db.DeleteCategory(category.Id);
            _categories = App.Db.GetCategories();
            _members = App.Db.GetCategoryMembers();
            FillShowCombo("all");
            ApplyFilter(resetPage: true);
        };
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    // ---------- Mehrfachauswahl ----------
    // Start über das Kachel-Menü ("Auswählen") oder lange gedrückt halten (0,5 s). Danach: klicken oder
    // gedrückt über Kacheln ziehen markiert (wie in der Handy-Galerie). Ein normaler Klick öffnet nie
    // aus Versehen eine Auswahl, weil erst das lange Halten den Modus startet.

    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _longPressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _selecting;
    private bool _dragSelecting;
    private bool _dragValue;
    private int _dragAnchor = -1;
    private Dictionary<string, bool> _dragBaseline = new(StringComparer.OrdinalIgnoreCase);
    private ClipTileViewModel? _pressTile;
    private Point _pressPoint;

    private List<ClipTileViewModel> PageTiles => (TileList.ItemsSource as IEnumerable<ClipTileViewModel>)?.ToList() ?? new List<ClipTileViewModel>();

    private void SetSelecting(bool on)
    {
        _selecting = on;
        if (!on)
        {
            _selected.Clear();
            EndDragSelect();
        }
        else
        {
            StopPreviews();
        }

        foreach (var tile in PageTiles)
        {
            tile.SelectionMode = on;
            tile.IsSelected = _selected.Contains(tile.Clip.Path);
        }

        SelectionBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        FilterBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        UpdateSelectionText();
    }

    private void UpdateSelectionText()
    {
        SelectionText.Text = _selected.Count == 1 ? "1 Clip ausgewählt" : $"{_selected.Count} Clips ausgewählt";
        AddToCategoryButton.IsEnabled = _selected.Count > 0;
        RemoveFromCategoryButton.IsEnabled = _selected.Count > 0;
        UpdateCategoryButtons();
    }

    private void SetSelected(ClipTileViewModel tile, bool selected)
    {
        tile.IsSelected = selected;
        if (selected)
        {
            _selected.Add(tile.Clip.Path);
        }
        else
        {
            _selected.Remove(tile.Clip.Path);
        }
    }

    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (TileOf(sender) is not { } tile)
        {
            return;
        }

        _pressTile = tile;
        _pressPoint = e.GetPosition(this);
        if (_selecting)
        {
            BeginDragSelect(tile, !tile.IsSelected);
            return;
        }

        // Außerhalb der Auswahl: erst nach langem Halten starten.
        _longPressTimer.Stop();
        _longPressTimer.Start();
    }

    private void LongPress_Tick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();
        if (_pressTile is not { } tile || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        // Maus inzwischen weit weg bewegt: war wohl kein Halten.
        if ((Mouse.GetPosition(this) - _pressPoint).Length > 12)
        {
            return;
        }

        SetSelecting(true);
        BeginDragSelect(tile, true);
    }

    private void BeginDragSelect(ClipTileViewModel tile, bool value)
    {
        var tiles = PageTiles;
        _dragAnchor = tiles.IndexOf(tile);
        _dragValue = value;
        _dragBaseline = tiles.ToDictionary(t => t.Clip.Path, t => t.IsSelected, StringComparer.OrdinalIgnoreCase);
        _dragSelecting = true;
        SetSelected(tile, value);
        UpdateSelectionText();
    }

    private void Tile_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_dragSelecting || e.LeftButton != MouseButtonState.Pressed || TileOf(sender) is not { } tile)
        {
            return;
        }

        var tiles = PageTiles;
        var current = tiles.IndexOf(tile);
        if (current < 0 || _dragAnchor < 0)
        {
            return;
        }

        // Alles zwischen Start und aktueller Kachel bekommt den Wert, der Rest wieder den Stand von vorher.
        var range = LibraryQuery.SelectionRange(_dragAnchor, current).ToHashSet();
        for (var i = 0; i < tiles.Count; i++)
        {
            var value = range.Contains(i) ? _dragValue : _dragBaseline.GetValueOrDefault(tiles[i].Clip.Path);
            SetSelected(tiles[i], value);
        }

        UpdateSelectionText();
    }

    private void EndDragSelect()
    {
        _longPressTimer.Stop();
        _dragSelecting = false;
        _dragAnchor = -1;
    }

    private void MenuSelect_Click(object sender, RoutedEventArgs e)
    {
        SetSelecting(true);
        if (TileOf(sender) is { } tile)
        {
            SetSelected(tile, true);
            UpdateSelectionText();
        }
    }

    private void SelectionDone_Click(object sender, RoutedEventArgs e) => SetSelecting(false);

    private void SelectPage_Click(object sender, RoutedEventArgs e)
    {
        foreach (var tile in PageTiles)
        {
            SetSelected(tile, true);
        }

        UpdateSelectionText();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        _selected.Clear();
        foreach (var tile in PageTiles)
        {
            tile.IsSelected = false;
        }

        UpdateSelectionText();
    }

    private void AddToCategory_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddToCategoryButton };
        foreach (var category in _categories)
        {
            var item = new MenuItem { Header = category.Name, Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Tag24 } };
            item.Click += async (_, _) => await AddSelectionToAsync(category);
            menu.Items.Add(item);
        }

        if (_categories.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        var create = new MenuItem { Header = "Neue Kategorie …", Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Add24 } };
        create.Click += async (_, _) =>
        {
            if (await AskNewCategoryAsync() is { } category)
            {
                await AddSelectionToAsync(category);
            }
        };
        menu.Items.Add(create);
        menu.IsOpen = true;
    }

    private async Task AddSelectionToAsync(ClipCategory category)
    {
        var paths = _selected.ToList();
        try
        {
            App.Db.SetInCategory(category.Id, paths, true);
        }
        catch (Exception ex)
        {
            Log.Error("Clips konnten nicht zur Kategorie hinzugefügt werden", ex);
            await Dialogs.ErrorAsync(ex.Message);
            return;
        }

        _members = App.Db.GetCategoryMembers();
        var keep = ShowTag;
        SetSelecting(false);
        FillShowCombo(keep);
        SummaryText.Text = paths.Count == 1
            ? $"1 Clip zu „{category.Name}“ hinzugefügt"
            : $"{paths.Count} Clips zu „{category.Name}“ hinzugefügt";
    }

    private async void RemoveFromCategory_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentCategory is not { } category)
        {
            return;
        }

        try
        {
            App.Db.SetInCategory(category.Id, _selected.ToList(), false);
        }
        catch (Exception ex)
        {
            Log.Error("Clips konnten nicht aus der Kategorie entfernt werden", ex);
            await Dialogs.ErrorAsync(ex.Message);
            return;
        }

        _members = App.Db.GetCategoryMembers();
        SetSelecting(false);
        FillShowCombo(category.Id);
        ApplyFilter(resetPage: false);
    }

    /// <summary>Tasten aus dem Hauptfenster, solange die Bibliothek angezeigt wird.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _selecting)
        {
            SetSelecting(false);
            e.Handled = true;
        }
    }

    // ---------- Seite eingeben ----------

    private void PageBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => PageBox.SelectAll();

    private void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var pages = LibraryQuery.PageCount(_filtered.Count, PageSize);
            if (LibraryQuery.ParsePageInput(PageBox.Text, pages) is { } page)
            {
                _page = page;
                ApplyFilter(resetPage: false);
            }
            else
            {
                PageBox.Text = (_page + 1).ToString();
            }

            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            PageBox.Text = (_page + 1).ToString();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void PageBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => PageBox.Text = (_page + 1).ToString();

    /// <summary>Im Editor wurde ein Clip zum Favoriten gemacht (oder nicht mehr).</summary>
    private void OnFavoriteChanged(object? sender, (string Path, bool Favorite) change)
    {
        if (change.Favorite)
        {
            _favorites.Add(change.Path);
        }
        else
        {
            _favorites.Remove(change.Path);
        }

        foreach (var tile in PageTiles.Where(t => string.Equals(t.Clip.Path, change.Path, StringComparison.OrdinalIgnoreCase)))
        {
            tile.IsFavorite = change.Favorite;
        }
    }

    private static ClipTileViewModel? TileOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ClipTileViewModel;

    private sealed record FolderItem(string? Path, string Label);
}
