# Clip Manager

Windows-Programm, um OBS-Clips anzuschauen, zu schneiden und passend zum Discord-Upload-Limit zu exportieren.

## Programm herunterladen

1. Rechts auf **Releases** klicken und beim neuesten Release **ClipManager-win-x64.zip** herunterladen.
2. ZIP entpacken, z.B. auf den Desktop oder nach Dokumente (nicht nach "Programme", sonst klappen Updates nicht).
3. `ClipManager.exe` starten. Windows fragt beim ersten Mal evtl. nach ("Windows hat den PC geschützt"): **Weitere Informationen → Trotzdem ausführen**.

Der Ordner `ffmpeg` muss neben der `ClipManager.exe` liegen bleiben. Neue Versionen installierst du danach direkt im Programm: links unten erscheint **Update auf …**.

## OBS-Empfehlung

- Aufnahmeformat **Hybrid MP4** (ab OBS 30) oder **MKV**.
- Spuren: 1 = alles gemischt, 2 = Spiel, 3 = Mikro, 4 = Discord.

## Stand

| Etappe | Inhalt | Status |
| --- | --- | --- |
| 1 | Grundgerüst, Einstellungen, FFmpeg- und GPU-Erkennung | fertig |
| 2 | Bibliothek mit Kacheln, Vorschaubildern, Favoriten | fertig |
| 3 | Editor: kürzen, Mitte rausschneiden | offen |
| 4 | Export fürs Discord-Limit, Drag-and-Drop | offen |
| 5 | Audiospuren einzeln bearbeiten | offen |
| 6 | Feinschliff | offen |
| 7 | Installer und Updates | offen |

## Für Entwickler

Ausführliche Übergabe für Agents und Entwickler: [CLAUDE.md](CLAUDE.md).


- C# / .NET 8, WPF mit [WPF-UI](https://github.com/lepoco/wpfui), FFmpeg als externes Programm.
- `src/ClipManager.Core`: Logik ohne Oberfläche (läuft auch unter Linux, hat Tests).
- `src/ClipManager`: WPF-Oberfläche (nur Windows).
- Tests: `dotnet test tests/ClipManager.Core.Tests`
