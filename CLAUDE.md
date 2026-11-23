# Clip Manager – Übergabe für Agents

Diese Datei ist der Einstieg für jeden Agent (oder Menschen), der hier weiterarbeitet.
Sie beschreibt Ziel, Entscheidungen, Aufbau, Build und den Fahrplan. Bitte aktuell halten,
wenn sich etwas Wichtiges ändert (Status-Tabelle, neue Entscheidungen).

## Ziel

Windows-Programm für OBS-Clips: Clips aus einem oder mehreren Ordnern als Kacheln anzeigen,
anklicken, schneiden (Anfang/Ende, Stellen in der Mitte), Audiospuren einzeln bearbeiten
(auch einzelne Stellen stummschalten) und so exportieren, dass der Clip unter das
Discord-Upload-Limit passt. Favoriten, Löschen, Kopie, Umbenennen.
Erst für den Besitzer (Kev), später für andere ("für jedermann": beliebige Längen,
Auflösungen mindestens bis 1440p, beliebige Spuranzahl).

Aufnehmen macht OBS (Replay-Puffer), nicht dieses Programm.

## Über den Nutzer

- Kev schreibt Deutsch, locker. Antworten auf Deutsch, kurz, ohne Fachchinesisch.
- Kev programmiert nicht selbst. Er testet die fertige .exe und mergt PRs per Klick.
- Alle Texte in der Oberfläche sind Deutsch. Code-Kommentare auch Deutsch.

## Feste Entscheidungen

| Thema | Entscheidung | Warum |
| --- | --- | --- |
| Sprache/UI | C# / .NET 8, WPF mit [WPF-UI](https://github.com/lepoco/wpfui) **3.0.5** (dunkles Fluent-Design) | Ausgereift für Windows, Video + mehrere Spuren stabiler als Web-Technik (Electron/Tauri), Rust-UI zu unreif |
| Video/Audio | FFmpeg + ffprobe als externe Programme (Ordner `ffmpeg/` neben der exe) | Schnell, jedes Format, GPU-Encoder |
| GPU | NVIDIA NVENC und AMD AMF, Fallback CPU (libx264) | Kev hat NVIDIA |
| Daten | SQLite (`Microsoft.Data.Sqlite`) für Favoriten und Clip-Infos, JSON für Einstellungen | |
| Originale | Werden nur auf ausdrücklichen Wunsch ersetzt ("Original ersetzen" im Export-/Speichern-Dialog, altes Original → Papierkorb). Sonst: Schnitte werden nur gemerkt, Export erzeugt neue Datei | Sicherheit; Kev wollte Ersetzen/Kopie/Speichern unter (23.11.2026) |
| Discord-Limits | Stufen Ohne Nitro / Nitro Basic / Nitro, Standard 20 / 50 / 500 MB, in den Einstellungen änderbar | Kev hat 20 MB ohne Nitro bestätigt (23.11.2026). Alte Einstellungen mit 10 MB werden beim Start auf 20 umgestellt (`SettingsStore.Migrate`) |
| Export-Format | H.264 + AAC in MP4 | Spielt in Discord überall direkt ab |
| Export-Ablage | Export landet erst in `%TEMP%\ClipManager\Export` (für Reinziehen/Kopieren), wird erst per Knopf gespeichert | Kev wollte keine doppelten Dateien |
| Updates | GitHub Releases, Update-Knopf im Programm | Kev soll nichts manuell runterladen |
| Vorschau im Editor | Bild: WPF `MediaElement` (stumm). Ton: Spuren per FFmpeg als WAV in den Cache, mit NAudio gemischt (`TrackPlayer`), Video ist der Takt, Ton wird bei >120 ms Abweichung nachgezogen | Hört genau, was exportiert wird. Bis die WAVs fertig sind, spielt MediaElement Spur 1 |

Falls MediaElement bei manchen Formaten (MKV, HEVC) nicht reicht: LibVLC/mpv nur fürs Bild.

## Aufbau

```
src/ClipManager.Core/     Logik ohne Oberfläche (net8.0, läuft auch unter Linux, hat Tests)
  Settings/               AppSettings, SettingsStore (JSON), DiscordTier
  Editing/                EditTimeline (Anfang/Ende, Schnitte, entfernte Teile, Spuren, Rückgängig), TrackEdit, RangeSet, ClipEditState (JSON), TimeText
  Library/                ClipScanner, ClipFile, LibraryDb (SQLite), LibraryQuery (Filter/Sort/Seiten), ClipFileOps (Kopie/Umbenennen)
  Media/                  FfmpegLocator, FfmpegProbe (Version + GPU-Test), MediaInfo (ffprobe), ThumbnailCache, FilmstripCache (Zeitleiste), AudioTrackCache + Waveform (Spuren als WAV), ProcessRunner
  Export/                 ExportPlanner (Bitrate/Auflösung aus Limit), ExportCommand (FFmpeg-Filtergraph), Exporter (Lauf, Fortschritt, Größen-Check + Wiederholung)
  Updates/                UpdateChecker (GitHub-API), UpdateScript (cmd zum Ersetzen + Neustart)
src/ClipManager/          WPF-Oberfläche (net8.0-windows)
  App.xaml(.cs)           Start, globale Dienste (Settings, Db, Thumbnails)
  MainWindow              Seitenleiste (Bibliothek, Einstellungen, Update-Knopf)
  Views/LibraryView       Kacheln, Seiten, Filter, Menü, Ordner-Überwachung, Klick öffnet Editor
  Editor/EditorWindow     Editor-Fenster: Player, Tastenkürzel, speichert Schnitte automatisch in der DB
  Editor/TimelineControl  Selbst gezeichnete Zeitleiste (Filmstreifen, Griffe, Schnitte, Abspielkopf, Spur-Zeilen).
                          Bedienung: in Spur ziehen = Auswahl → Menü (stumm / rausschneiden), Ränder von stummen und
                          rausgeschnittenen Stellen ziehbar, Klick auf so eine Stelle = Menü (wieder laut / reinnehmen)
  Editor/ExportWindow     Export-/Speichern-Dialog: Ziel (Discord-Stufe / volle Qualität), Fortschritt, Rausziehen, Kopieren, Original ersetzen / Kopie / Speichern unter
  Editor/TrackPlayer      NAudio: Spuren gemischt abspielen (an/aus, Lautstärke, stumme Stellen)
  Editor/TrackRowViewModel  Spur-Kopf (Name, Haken, Lautstärke); Namen global in settings.json (TrackNames)
  Views/SettingsView      Ordner, Discord, Tonspur-Namen (+/–), Export-Ordner, Updates, FFmpeg/GPU
  AppUpdater, ShellFiles, Dialogs, FolderPicker
tests/ClipManager.Core.Tests/   xUnit-Tests für Core
.github/workflows/build.yml     Windows-Build, Tests, FFmpeg dazu, Artifact, Release auf main
```

Grundregel: Alles, was sich ohne Fenster testen lässt, gehört nach `ClipManager.Core` und bekommt Tests.

## Bauen und Testen

- Tests: `dotnet test tests/ClipManager.Core.Tests` (geht unter Linux und Windows).
- Die WPF-App baut nur mit dem Windows-Desktop-SDK. Ubuntus `dotnet-sdk-8.0` (Source-Build) hat es **nicht**,
  deshalb in Cloud-Containern nur Core + Tests bauen. Die App wird vom GitHub-Workflow auf `windows-latest` gebaut.
  Jede Änderung an XAML/WPF also per PR pushen und den Build abwarten.
- `EnableWindowsTargeting` ist gesetzt, hilft aber nur mit Microsofts SDK.
- Klicktests macht Kev mit dem Artifact oder dem Update-Knopf. Bitte nach UI-Änderungen um Screenshots bitten.

## Versionen, Releases, Updates

- Version = `0.<GitHub-Laufnummer>.0`, gesetzt im Workflow (`APP_VERSION`). Lokal gebaut ist es `0.0.0`.
- Jeder Push auf `main` erstellt ein Release `v0.N.0` mit `ClipManager-win-x64.zip` (Programm + `ffmpeg/`).
- Das Programm fragt beim Start `releases/latest` ab. Ist eine neuere Version da, erscheint links unten
  "Update auf …". Ablauf: ZIP nach `%TEMP%\ClipManagerUpdate` laden, entpacken, `update.cmd` starten,
  Programm beenden; das Skript wartet, kopiert per robocopy über den Programmordner und startet neu.
- Voraussetzung: Repository ist öffentlich (sonst braucht die API einen Token) und der Programmordner ist beschreibbar.

## Daten auf dem Rechner des Nutzers

| Was | Wo |
| --- | --- |
| Einstellungen | `%AppData%\ClipManager\settings.json` |
| Favoriten, Clip-Infos, Schnitte (Tabelle `edits`) | `%AppData%\ClipManager\library.db` |
| Vorschaubilder, Filmstreifen | `%LocalAppData%\ClipManager\thumbs\` |
| Tonspuren als WAV (Cache, 3 Tage) | `%LocalAppData%\ClipManager\audio\` |
| Zwischen-Exporte (2 Tage) | `%TEMP%\ClipManager\Export\` |
| Fehler-Log | `%LocalAppData%\ClipManager\log.txt` |
| Exporte (Standard) | `Videos\Clip Manager Exporte` |

## Kevs OBS-Setup (Referenz für Tests und Spuren-Namen)

- Clip-Ordner: `F:\OBS Clips`, Format Hybrid MP4, NVIDIA NVENC H.264, CQP 20, P5, Replay-Puffer mit Hotkey.
- Spuren: **1** alles gemischt, **2** Spiel (Spielaufnahme mit "Audio erfassen"), **3** Mikro, **4** Discord, **5** Spotify.
- Spur 1 ist der Mix. Im Editor/Export sollen standardmäßig die Einzelspuren (2–5) genutzt werden, Spur 1 nur,
  wenn es keine Einzelspuren gibt, sonst ist alles doppelt.

## Fahrplan

| Etappe | Inhalt | Status |
| --- | --- | --- |
| 1 | Grundgerüst, Einstellungen, FFmpeg- und GPU-Erkennung | fertig (PR #1) |
| 2 | Bibliothek: Kacheln, Vorschaubilder, Dauer, Favoriten, Seiten, Suche/Sortierung, Umbenennen/Kopie/Löschen, Ordner-Überwachung | fertig (PR #2) |
| – | Update-Knopf, Releases, diese Übergabe-Datei | fertig (in PR #2) |
| 3 | Editor Teil 1: Player, Zeitleiste mit Vorschaubildern, Anfang/Ende kürzen, Stellen in der Mitte rausschneiden, Tastenkürzel | fertig (PR #4, Fixes in PR #5) |
| 4 | Export: Discord-Stufen, Bitrate-Rechnung, GPU-Encoder, Fortschritt, fertigen Clip per Drag-and-Drop aus dem Programm ziehen, "Speichern unter" | fertig (PR #6, #7) |
| 5 | Editor Teil 2: Audiospuren einzeln (Lautstärke, stumm, stumme Bereiche), Vorschau hört genau, was exportiert wird; Speichern (ersetzen/Kopie) | fertig (PR #8); Schneiden per Ziehen + Spur-Namen in den Einstellungen in PR #9 |
| 6 | Feinschliff: Hover-Vorschau auf Kacheln, Design, Fehlerfälle | offen |
| 7 | Für andere: Installer, Ersteinrichtung, ggf. Code-Signatur | offen |

### Etappe 3/5: Editor (Plan)

- Video im Player stumm abspielen (mpv per `libmpv` oder LibVLCSharp), Audiospuren separat (NAudio) synchron dazu mischen.
  Alternative, falls Sync zu fummelig: Spuren per FFmpeg als WAV in einen Cache extrahieren und mit NAudio abspielen.
- Bearbeitung als Datenmodell speichern (z.B. JSON in der DB): Liste behaltener Bereiche (`[start, end]`),
  pro Spur Lautstärke, stumm, Liste stummer Bereiche. Original nie anfassen.
- "Als Kopie speichern" = Export in voller Qualität als neue Datei neben dem Original.

### Etappe 4: Export-Logik (Plan)

Videobitrate aus Zielgröße, mit 5 % Puffer für den Container:

```
Videobitrate [kbit/s] = Zielgröße [MB] * 8192 * 0,95 / Dauer [s] - Audiobitrate [kbit/s]
```

Beispiel: 30 s, 10 MB, Audio 128 kbit/s → ca. 2.460 kbit/s Video.

Auflösung automatisch anpassen (Startwerte, mit echten Clips nachjustieren):

| Videobitrate | Auflösung | FPS |
| --- | --- | --- |
| ab ca. 8.000 kbit/s | Original (z.B. 1440p) | Original |
| ab ca. 4.000 kbit/s | 1080p | Original |
| ab ca. 2.000 kbit/s | 720p | max. 60 |
| ab ca. 1.000 kbit/s | 720p | 30 |
| darunter | 480p + Hinweis "Clip kürzen" | 30 |

- Encoder: `h264_nvenc` / `h264_amf` / `libx264`, je nach `FfmpegProbe`-Ergebnis. Zwei Durchgänge bzw. VBV-Grenzen, damit die Größe stimmt.
- Nach dem Export echte Dateigröße prüfen; zu groß → mit ~5–8 % weniger Bitrate wiederholen.
- Audio: aktive Spuren mit Lautstärken und stummen Bereichen per `amix`/`volume`-Filter zu einer Spur mischen, AAC 128 kbit/s (bei sehr knappem Limit 96).
- Schnitte mit `trim`/`atrim` + `concat` im Filtergraph.
- Exporte landen im Export-Ordner (wird von der Bibliothek ausgeblendet). "Volle Qualität" landet als "(Kopie)" neben dem Original.
- Umgesetzt (PR #6): GPU-Export schlägt fehl → automatisch einmal mit CPU. Tonspuren vorerst nach Standardregel
  (`ExportPlanner.DefaultAudioTracks`), Auswahl pro Spur kommt mit Etappe 5. Kopieren-Knopf legt die Datei in die
  Zwischenablage (in Discord Strg+V).

## Arbeitsweise

- Pro Etappe ein Branch und ein PR mit deutscher Beschreibung. Kev mergt selbst.
- Vor dem Push: `dotnet test tests/ClipManager.Core.Tests`. Danach den Windows-Build im PR abwarten und Fehler selbst beheben.
- Keine Tests abschalten, um grün zu werden.
- Plan-Dokument mit allen Hintergründen: https://claude.ai/artifact/1vdhpZmk1wUYrr7aWvDRom (nur für Kev sichtbar).
