using Microsoft.Win32;

namespace ClipManager;

internal static class FolderPicker
{
    /// <summary>Lässt den Nutzer Clip-Ordner auswählen und speichert neue in den Einstellungen.</summary>
    /// <returns>Anzahl neu hinzugefügter Ordner.</returns>
    public static int AddClipFolders()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Clip-Ordner auswählen (dort, wo OBS speichert)",
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return 0;
        }

        var added = dialog.FolderNames.Count(App.Settings.AddClipFolder);
        if (added > 0)
        {
            App.SaveSettings();
        }

        return added;
    }
}
