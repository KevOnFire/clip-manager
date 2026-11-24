namespace ClipManager.Core.Settings;

/// <summary>Größe und Lage des Hauptfensters beim Beenden, damit es beim nächsten Start genauso aufgeht.</summary>
public sealed class WindowBounds
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }

    /// <summary>
    /// Passt die gespeicherte Lage an den sichtbaren Bildschirmbereich an (z.B. wenn ein zweiter Monitor
    /// fehlt). Gibt null zurück, wenn die Werte unbrauchbar sind – dann Standardgröße nehmen.
    /// </summary>
    public WindowBounds? FitInto(double screenLeft, double screenTop, double screenWidth, double screenHeight, double minWidth, double minHeight)
    {
        if (!(Width > 0) || !(Height > 0) || double.IsNaN(Left) || double.IsNaN(Top) || screenWidth <= 0 || screenHeight <= 0)
        {
            return null;
        }

        var width = Math.Clamp(Width, Math.Min(minWidth, screenWidth), screenWidth);
        var height = Math.Clamp(Height, Math.Min(minHeight, screenHeight), screenHeight);
        var left = Math.Clamp(Left, screenLeft, screenLeft + screenWidth - width);
        var top = Math.Clamp(Top, screenTop, screenTop + screenHeight - height);
        return new WindowBounds { Left = left, Top = top, Width = width, Height = height, Maximized = Maximized };
    }
}
