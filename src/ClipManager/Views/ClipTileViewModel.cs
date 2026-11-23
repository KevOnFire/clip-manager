using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipManager.Core.Library;
using ClipManager.Core.Media;

namespace ClipManager.Views;

/// <summary>Daten für eine Kachel in der Bibliothek.</summary>
public sealed class ClipTileViewModel : INotifyPropertyChanged
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private bool _isFavorite;
    private bool _isEdited;
    private ImageSource? _thumbnail;
    private MediaInfo? _info;

    public ClipTileViewModel(ClipFile clip, bool isFavorite, bool isEdited = false)
    {
        Clip = clip;
        _isFavorite = isFavorite;
        _isEdited = isEdited;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ClipFile Clip { get; }

    public string Name => Clip.Name;

    public string DateText => Clip.ModifiedUtc.ToLocalTime().ToString("dd.MM.yyyy · HH:mm", German);

    public string Details
    {
        get
        {
            var size = Clip.SizeBytes >= 1024L * 1024 * 1024
                ? (Clip.SizeBytes / (1024d * 1024 * 1024)).ToString("0.0", German) + " GB"
                : (Clip.SizeBytes / (1024d * 1024)).ToString("0", German) + " MB";
            if (_info is not { Height: > 0 } info)
            {
                return size;
            }

            var tracks = info.AudioTracks.Count == 1 ? "1 Spur" : $"{info.AudioTracks.Count} Spuren";
            return $"{info.Height}p{Math.Round(info.Fps)} · {tracks} · {size}";
        }
    }

    public string DurationText => _info is { } i && i.Duration > TimeSpan.Zero ? FormatDuration(i.Duration) : string.Empty;

    public bool HasDuration => DurationText.Length > 0;

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite != value)
            {
                _isFavorite = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Für diesen Clip sind Schnitte gespeichert (Schere auf der Kachel).</summary>
    public bool IsEdited
    {
        get => _isEdited;
        set
        {
            if (_isEdited != value)
            {
                _isEdited = value;
                OnPropertyChanged();
            }
        }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail != null;

    public MediaInfo? Info
    {
        get => _info;
        set
        {
            _info = value;
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(HasDuration));
            OnPropertyChanged(nameof(Details));
        }
    }

    /// <summary>Lädt das Bild so, dass die Datei danach nicht gesperrt bleibt.</summary>
    public void SetThumbnailFile(string path)
    {
        var bitmap = new BitmapImage();
        using (var stream = File.OpenRead(path))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = ThumbnailCache.Width;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }

        bitmap.Freeze();
        Thumbnail = bitmap;
    }

    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", German) : d.ToString(@"mm\:ss", German);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
