using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClipManager.Core.Editing;

namespace ClipManager.Editor;

/// <summary>Kopf einer Spur-Zeile im Editor: Name, an/aus, Lautstärke.</summary>
public sealed class TrackRowViewModel : INotifyPropertyChanged
{
    private readonly EditTimeline _timeline;
    private string _name;
    private bool _enabled;
    private double _volume;
    private bool _syncing;

    public TrackRowViewModel(EditTimeline timeline, int index, string name, System.Windows.Media.Color color)
    {
        ColorBrush = new System.Windows.Media.SolidColorBrush(color);
        ColorBrush.Freeze();
        _timeline = timeline;
        Index = index;
        _name = name;
        SyncFromTimeline();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }

    /// <summary>Farbe der Spur (wie die Wellenform).</summary>
    public System.Windows.Media.SolidColorBrush ColorBrush { get; }

    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            OnPropertyChanged();
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            OnPropertyChanged();
            if (!_syncing)
            {
                _timeline.SetTrackEnabled(Index, value);
            }
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            // Nahe 100 % einrasten, damit man leicht wieder auf "original" kommt.
            if (Math.Abs(value - 1) < 0.04)
            {
                value = 1;
            }

            if (Math.Abs(_volume - value) < 0.0001)
            {
                return;
            }

            _volume = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
            if (!_syncing)
            {
                _timeline.SetTrackVolume(Index, value, recordUndo: false);
            }
        }
    }

    public string VolumeText => $"{Math.Round(_volume * 100)} %";

    /// <summary>Werte aus der Zeitleiste übernehmen (z.B. nach Rückgängig), ohne neue Änderungen auszulösen.</summary>
    public void SyncFromTimeline()
    {
        if (Index >= _timeline.Tracks.Count)
        {
            return;
        }

        _syncing = true;
        try
        {
            var track = _timeline.Tracks[Index];
            Enabled = track.Enabled;
            Volume = track.Volume;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
