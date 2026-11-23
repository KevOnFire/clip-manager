using System.Globalization;

namespace ClipManager.Core.Editing;

/// <summary>Zeitangaben für die Oberfläche, z.B. "0:42.3" oder "1:02:05.0".</summary>
public static class TimeText
{
    public static string Format(TimeSpan t, bool tenths = true)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        // Abschneiden statt runden, damit 0:59.96 nicht als 1:00.0 erscheint, obwohl der Clip 0:59 lang ist.
        var totalTenths = (long)(t.TotalMilliseconds / 100);
        var hours = totalTenths / 36000;
        var minutes = totalTenths / 600 % 60;
        var seconds = totalTenths / 10 % 60;
        var fraction = totalTenths % 10;

        var main = hours > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", hours, minutes, seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", minutes, seconds);
        return tenths ? $"{main}.{fraction}" : main;
    }
}
