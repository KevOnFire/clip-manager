namespace ClipManager.Core.Editing;

/// <summary>Art eines Objekts, das ins Video gezeichnet wird.</summary>
public enum OverlayKind
{
    Box,
    Ellipse,
    Blur,
    Text,
    Pen,
    Image,
}

/// <summary>
/// Ein Objekt im Video (Box, Kreis, Unschärfe, Text, Stift-Zeichnung, Bild).
/// Ort und Größe sind Anteile des Bildes (0..1), damit es bei jeder Auflösung gleich aussieht.
/// Zeiten sind Sekunden im Original (wie Schnitte). Drehung in Grad um die Mitte.
/// Z: höher = weiter vorne.
/// </summary>
public sealed class OverlayItem
{
    public const double MinSize = 0.01;
    public const double MinBlur = 1;
    public const double MaxBlur = 40;
    public const double DefaultBlur = 12;
    public const double DefaultStroke = 0.006;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public OverlayKind Kind { get; set; }

    public double Start { get; set; }

    public double End { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double W { get; set; }

    public double H { get; set; }

    public double Rotation { get; set; }

    /// <summary>Farbe als "#RRGGBB".</summary>
    public string Color { get; set; } = "#FFFFFF";

    /// <summary>Box/Kreis: gefüllt (sonst nur Rand).</summary>
    public bool Fill { get; set; }

    /// <summary>Unschärfe-Stärke (1–40, bezogen auf ein 540 Pixel hohes Bild).</summary>
    public double Blur { get; set; } = DefaultBlur;

    public string? Text { get; set; }

    /// <summary>Bild: Pfad der Kopie im Programmordner (nicht das Original, das könnte verschwinden).</summary>
    public string? ImagePath { get; set; }

    /// <summary>Stift: Striche als Punktlisten [x, y], Anteile des ganzen Bildes.</summary>
    public List<List<double[]>>? Paths { get; set; }

    /// <summary>Strichstärke (Stift, Rand von Box/Kreis) als Anteil der Bildhöhe.</summary>
    public double Stroke { get; set; } = DefaultStroke;

    public int Z { get; set; }

    public TimeRange Range => new(TimeSpan.FromSeconds(Start), TimeSpan.FromSeconds(End));

    public bool IsVisibleAt(TimeSpan t) => t.TotalSeconds >= Start && t.TotalSeconds <= End;

    /// <summary>Kann gedreht werden (Stift-Zeichnungen nicht, die liegen übers ganze Bild).</summary>
    public bool CanRotate => Kind != OverlayKind.Pen;

    public OverlayItem Clone()
    {
        var copy = (OverlayItem)MemberwiseClone();
        copy.Paths = Paths?.Select(p => p.Select(pt => (double[])pt.Clone()).ToList()).ToList();
        return copy;
    }

    /// <summary>Werte in erlaubte Bereiche bringen (nach Laden oder Ändern).</summary>
    public void Normalize(double duration)
    {
        static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;

        Start = Math.Clamp(Finite(Start, 0), 0, Math.Max(0, duration));
        End = Math.Clamp(Finite(End, duration), 0, Math.Max(0, duration));
        if (End < Start)
        {
            (Start, End) = (End, Start);
        }

        if (End - Start < EditTimeline.MinSegment.TotalSeconds)
        {
            End = Math.Min(duration, Start + EditTimeline.MinSegment.TotalSeconds);
            Start = Math.Max(0, End - EditTimeline.MinSegment.TotalSeconds);
        }

        W = Math.Clamp(Finite(W, 0.2), MinSize, 4);
        H = Math.Clamp(Finite(H, 0.2), MinSize, 4);
        // Darf etwas über den Rand hinaus, aber nicht ganz verschwinden.
        X = Math.Clamp(Finite(X, 0), -W + MinSize, 1 - MinSize);
        Y = Math.Clamp(Finite(Y, 0), -H + MinSize, 1 - MinSize);
        Rotation = Finite(Rotation, 0) % 360;
        if (Rotation > 180)
        {
            Rotation -= 360;
        }
        else if (Rotation <= -180)
        {
            Rotation += 360;
        }

        if (!CanRotate)
        {
            Rotation = 0;
        }

        Blur = Math.Clamp(Finite(Blur, DefaultBlur), MinBlur, MaxBlur);
        Stroke = Math.Clamp(Finite(Stroke, DefaultStroke), 0.001, 0.05);
        Color = NormalizeColor(Color);
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id;
    }

    /// <summary>"#RRGGBB" in Großbuchstaben; alles andere wird weiß.</summary>
    public static string NormalizeColor(string? color)
    {
        if (color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit))
        {
            return color.ToUpperInvariant();
        }

        if (color is { Length: 9 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit))
        {
            // "#AARRGGBB" (WPF) → Deckkraft weglassen
            return ("#" + color[3..]).ToUpperInvariant();
        }

        return "#FFFFFF";
    }

    /// <summary>Kurzer Name für Leiste und Ebenen-Liste.</summary>
    public string Label => Kind switch
    {
        OverlayKind.Box => "Box",
        OverlayKind.Ellipse => "Kreis",
        OverlayKind.Blur => "Unschärfe",
        OverlayKind.Text => string.IsNullOrWhiteSpace(Text) ? "Text" : Text!.Trim(),
        OverlayKind.Pen => "Stift",
        OverlayKind.Image => "Bild",
        _ => "Objekt",
    };
}

/// <summary>Rechteck in ganzen Pixeln (für FFmpeg).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>Hilfen für Reihen in der Leiste und für den Export.</summary>
public static class OverlayLayout
{
    /// <summary>
    /// Verteilt Objekte auf Reihen, sodass sich in keiner Reihe zwei zeitlich überlappen.
    /// Gibt pro Id die Reihe (0 = oben) und die Anzahl der Reihen zurück.
    /// </summary>
    public static (Dictionary<string, int> Lanes, int Count) AssignLanes(IEnumerable<OverlayItem> items)
    {
        var lanes = new Dictionary<string, int>();
        var ends = new List<double>();
        foreach (var item in items.OrderBy(i => i.Start).ThenBy(i => i.End))
        {
            var lane = ends.FindIndex(end => end <= item.Start + 0.0001);
            if (lane < 0)
            {
                lane = ends.Count;
                ends.Add(0);
            }

            ends[lane] = item.End;
            lanes[item.Id] = lane;
        }

        return (lanes, ends.Count);
    }

    /// <summary>
    /// Umgebendes Rechteck des (gedrehten) Objekts in Pixeln, auf gerade Zahlen gerundet und aufs Bild begrenzt.
    /// Null, wenn nichts davon im Bild liegt.
    /// </summary>
    public static PixelRect? Bounds(OverlayItem item, int frameWidth, int frameHeight)
    {
        var w = item.W * frameWidth;
        var h = item.H * frameHeight;
        var cx = item.X * frameWidth + w / 2;
        var cy = item.Y * frameHeight + h / 2;
        var a = item.Rotation * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(a));
        var sin = Math.Abs(Math.Sin(a));
        var bw = w * cos + h * sin;
        var bh = w * sin + h * cos;

        // Stift: Rand der Linien mitnehmen
        var pad = item.Kind is OverlayKind.Pen or OverlayKind.Box or OverlayKind.Ellipse or OverlayKind.Text
            ? item.Stroke * frameHeight * 2
            : 0;
        var left = (int)Math.Floor((cx - bw / 2 - pad) / 2) * 2;
        var top = (int)Math.Floor((cy - bh / 2 - pad) / 2) * 2;
        var right = (int)Math.Ceiling((cx + bw / 2 + pad) / 2) * 2;
        var bottom = (int)Math.Ceiling((cy + bh / 2 + pad) / 2) * 2;
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(frameWidth - frameWidth % 2, right);
        bottom = Math.Min(frameHeight - frameHeight % 2, bottom);
        if (right - left < 2 || bottom - top < 2)
        {
            return null;
        }

        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>FFmpeg-Radius für boxblur bei dieser Bildhöhe, passend zu einem Bereich (FFmpeg erlaubt höchstens die halbe Kante pro Farbebene).</summary>
    public static int BlurRadius(double blur, int frameHeight, PixelRect area)
    {
        var radius = (int)Math.Round(Math.Clamp(blur, OverlayItem.MinBlur, OverlayItem.MaxBlur) * frameHeight / 540.0);
        // Farbebenen sind halb so groß (yuv420), daher /4.
        var max = Math.Min(area.Width, area.Height) / 4 - 1;
        return Math.Max(1, Math.Min(radius, Math.Max(1, max)));
    }
}
