using System.Text.Json;
using ClipManager.Core.Editing;
using ClipManager.Core.Media;
using Microsoft.Data.Sqlite;

namespace ClipManager.Core.Library;

/// <summary>
/// Kleine SQLite-Datenbank für Favoriten, zwischengespeicherte Clip-Infos und Schnitte.
/// Videodateien selbst werden nie angefasst.
/// </summary>
public sealed class LibraryDb
{
    private readonly string _connectionString;

    public LibraryDb(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = filePath, Pooling = true }.ToString();
        Execute("""
            CREATE TABLE IF NOT EXISTS favorites (
                path TEXT PRIMARY KEY COLLATE NOCASE
            );
            CREATE TABLE IF NOT EXISTS media_info (
                path TEXT PRIMARY KEY COLLATE NOCASE,
                size INTEGER NOT NULL,
                mtime INTEGER NOT NULL,
                duration_ms INTEGER NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL,
                fps REAL NOT NULL,
                audio_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS edits (
                path TEXT PRIMARY KEY COLLATE NOCASE,
                state_json TEXT NOT NULL,
                updated INTEGER NOT NULL
            );
            """);
    }

    public HashSet<string> GetFavorites()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM favorites";
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public void SetFavorite(string path, bool favorite) =>
        Execute(
            favorite
                ? "INSERT OR IGNORE INTO favorites (path) VALUES ($path)"
                : "DELETE FROM favorites WHERE path = $path",
            ("$path", path));

    /// <summary>Gibt gespeicherte Infos zurück, aber nur wenn Größe und Datum noch passen.</summary>
    public MediaInfo? GetMediaInfo(ClipFile clip)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT duration_ms, width, height, fps, audio_json FROM media_info
            WHERE path = $path AND size = $size AND mtime = $mtime
            """;
        command.Parameters.AddWithValue("$path", clip.Path);
        command.Parameters.AddWithValue("$size", clip.SizeBytes);
        command.Parameters.AddWithValue("$mtime", clip.ModifiedUtc.Ticks);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var audio = JsonSerializer.Deserialize<List<AudioTrackInfo>>(reader.GetString(4)) ?? new List<AudioTrackInfo>();
        return new MediaInfo(
            TimeSpan.FromMilliseconds(reader.GetInt64(0)),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetDouble(3),
            audio);
    }

    /// <summary>Alle gespeicherten Dauern, für das Sortieren nach Länge.</summary>
    public Dictionary<string, TimeSpan> GetDurations()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, duration_ms FROM media_info";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            result[reader.GetString(0)] = TimeSpan.FromMilliseconds(reader.GetInt64(1));
        }

        return result;
    }

    public void SaveMediaInfo(ClipFile clip, MediaInfo info) =>
        Execute(
            """
            INSERT INTO media_info (path, size, mtime, duration_ms, width, height, fps, audio_json)
            VALUES ($path, $size, $mtime, $duration, $width, $height, $fps, $audio)
            ON CONFLICT(path) DO UPDATE SET
                size = excluded.size, mtime = excluded.mtime, duration_ms = excluded.duration_ms,
                width = excluded.width, height = excluded.height, fps = excluded.fps, audio_json = excluded.audio_json
            """,
            ("$path", clip.Path),
            ("$size", clip.SizeBytes),
            ("$mtime", clip.ModifiedUtc.Ticks),
            ("$duration", (long)info.Duration.TotalMilliseconds),
            ("$width", info.Width),
            ("$height", info.Height),
            ("$fps", info.Fps),
            ("$audio", JsonSerializer.Serialize(info.AudioTracks)));

    /// <summary>Gespeicherte Schnitte eines Clips oder null.</summary>
    public ClipEditState? GetEdit(string path)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json FROM edits WHERE path = $path";
        command.Parameters.AddWithValue("$path", path);
        if (command.ExecuteScalar() is not string json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ClipEditState>(json);
        }
        catch (JsonException ex)
        {
            Log.Error($"Gespeicherte Schnitte für \"{path}\" sind kaputt und werden ignoriert", ex);
            return null;
        }
    }

    /// <summary>Speichert die Schnitte. Null oder "nichts geändert" löscht den Eintrag.</summary>
    public void SaveEdit(string path, ClipEditState? state)
    {
        if (state == null)
        {
            Execute("DELETE FROM edits WHERE path = $path", ("$path", path));
            return;
        }

        Execute(
            """
            INSERT INTO edits (path, state_json, updated) VALUES ($path, $json, $updated)
            ON CONFLICT(path) DO UPDATE SET state_json = excluded.state_json, updated = excluded.updated
            """,
            ("$path", path),
            ("$json", JsonSerializer.Serialize(state)),
            ("$updated", DateTime.UtcNow.Ticks));
    }

    /// <summary>Alle Clips, für die Schnitte gespeichert sind (für das Schere-Symbol auf der Kachel).</summary>
    public HashSet<string> GetEditedPaths()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM edits";
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>Nach dem Umbenennen: Favorit und Infos wandern mit.</summary>
    public void MovePath(string oldPath, string newPath) =>
        Execute(
            """
            DELETE FROM favorites WHERE path = $new;
            DELETE FROM media_info WHERE path = $new;
            DELETE FROM edits WHERE path = $new;
            UPDATE favorites SET path = $new WHERE path = $old;
            UPDATE media_info SET path = $new WHERE path = $old;
            UPDATE edits SET path = $new WHERE path = $old;
            """,
            ("$old", oldPath),
            ("$new", newPath));

    /// <summary>Nach dem Löschen: Einträge entfernen.</summary>
    public void RemovePath(string path) =>
        Execute(
            "DELETE FROM favorites WHERE path = $path; DELETE FROM media_info WHERE path = $path; DELETE FROM edits WHERE path = $path;",
            ("$path", path));

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
