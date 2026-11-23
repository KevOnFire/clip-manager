# Clip Manager

Windows-Programm, um OBS-Clips anzuschauen, zu schneiden und passend zum Discord-Upload-Limit zu exportieren.

## Programm herunterladen

1. Oben auf **Actions** klicken und den neuesten grünen Lauf von **Build** öffnen.
2. Ganz unten unter **Artifacts** auf **ClipManager-win-x64** klicken. Es lädt eine ZIP-Datei herunter.
3. ZIP entpacken (z.B. nach `C:\Programme\ClipManager` oder auf den Desktop).
4. `ClipManager.exe` starten. Windows fragt beim ersten Mal evtl. nach ("Windows hat den PC geschützt"): **Weitere Informationen → Trotzdem ausführen**.

Der Ordner `ffmpeg` muss neben der `ClipManager.exe` liegen bleiben.

## OBS-Empfehlung

- Aufnahmeformat **Hybrid MP4** (ab OBS 30) oder **MKV**.
- Spuren: 1 = alles gemischt, 2 = Spiel, 3 = Mikro, 4 = Discord.

## Stand

| Etappe | Inhalt | Status |
| --- | --- | --- |
| 1 | Grundgerüst, Einstellungen, FFmpeg- und GPU-Erkennung | in Arbeit |
| 2 | Bibliothek mit Kacheln, Vorschaubildern, Favoriten | offen |
| 3 | Editor: kürzen, Mitte rausschneiden | offen |
| 4 | Export fürs Discord-Limit, Drag-and-Drop | offen |
| 5 | Audiospuren einzeln bearbeiten | offen |
| 6 | Feinschliff | offen |
| 7 | Installer und Updates | offen |

## Für Entwickler

- C# / .NET 8, WPF mit [WPF-UI](https://github.com/lepoco/wpfui), FFmpeg als externes Programm.
- `src/ClipManager.Core`: Logik ohne Oberfläche (läuft auch unter Linux, hat Tests).
- `src/ClipManager`: WPF-Oberfläche (nur Windows).
- Tests: `dotnet test tests/ClipManager.Core.Tests`
