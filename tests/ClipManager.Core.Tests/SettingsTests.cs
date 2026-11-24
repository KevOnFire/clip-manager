using ClipManager.Core.Settings;

namespace ClipManager.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_HaveDiscordLimits()
    {
        var s = new AppSettings();
        Assert.Equal(DiscordTier.Free, s.Tier);
        Assert.Equal(20, s.LimitFor(DiscordTier.Free));
        Assert.Equal(50, s.LimitFor(DiscordTier.NitroBasic));
        Assert.Equal(500, s.LimitFor(DiscordTier.Nitro));
    }

    [Fact]
    public void LimitFor_FallsBackOnInvalidValue()
    {
        var s = new AppSettings();
        s.TierLimitsMb[DiscordTier.Free] = 0;
        s.TierLimitsMb.Remove(DiscordTier.Nitro);
        Assert.Equal(20, s.LimitFor(DiscordTier.Free));
        Assert.Equal(500, s.LimitFor(DiscordTier.Nitro));
    }

    [Fact]
    public void Load_OldDefaultTenMb_BecomesTwenty()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """{ "Tier": "Free", "TierLimitsMb": { "Free": 10, "NitroBasic": 50, "Nitro": 500 } }""");
        var s = new SettingsStore(path).Load();
        Assert.Equal(20, s.LimitFor(DiscordTier.Free));
        Assert.Equal(AppSettings.CurrentVersion, s.Version);
    }

    [Fact]
    public void Load_CustomLimit_IsKept()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """{ "TierLimitsMb": { "Free": 8 } }""");
        Assert.Equal(8, new SettingsStore(path).Load().LimitFor(DiscordTier.Free));

        // Nach der Umstellung bleibt auch ein bewusst gesetztes 10 MB erhalten.
        var store = new SettingsStore(path);
        var s = new AppSettings();
        s.TierLimitsMb[DiscordTier.Free] = 10;
        store.Save(s);
        Assert.Equal(10, store.Load().LimitFor(DiscordTier.Free));
    }

    [Fact]
    public void TrackName_OwnNameThenFileTitleThenNumber()
    {
        var s = new AppSettings();
        Assert.Equal("Spur 1 (Mix)", s.TrackName(0, null, 5));
        Assert.Equal("Spur 1", s.TrackName(0, null, 1));
        Assert.Equal("Spur 3", s.TrackName(2, "Track3", 5));
        Assert.Equal("Discord", s.TrackName(3, "Discord", 5));
        s.TrackNames[2] = "Mikro";
        Assert.Equal("Mikro", s.TrackName(2, "Track3", 5));
    }

    [Fact]
    public void AddClipFolder_IgnoresDuplicates()
    {
        using var dir = new TempDir();
        var s = new AppSettings();
        Assert.True(s.AddClipFolder(dir.Path));
        Assert.False(s.AddClipFolder(dir.Path + System.IO.Path.DirectorySeparatorChar));
        Assert.Single(s.ClipFolders);
        Assert.True(s.RemoveClipFolder(dir.Path));
        Assert.Empty(s.ClipFolders);
    }

    [Fact]
    public void Store_RoundTrips()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(System.IO.Path.Combine(dir.Path, "sub", "settings.json"));
        var s = new AppSettings { Tier = DiscordTier.NitroBasic, FfmpegPath = @"C:\ffmpeg\ffmpeg.exe" };
        s.AddClipFolder(dir.Path);
        s.TierLimitsMb[DiscordTier.Free] = 25;
        store.Save(s);

        var loaded = store.Load();
        Assert.Equal(DiscordTier.NitroBasic, loaded.Tier);
        Assert.Equal(25, loaded.LimitFor(DiscordTier.Free));
        Assert.Equal(s.ClipFolders, loaded.ClipFolders);
        Assert.Equal(s.FfmpegPath, loaded.FfmpegPath);
        Assert.Contains("\"NitroBasic\"", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Store_MissingFile_GivesDefaults()
    {
        using var dir = new TempDir();
        var loaded = new SettingsStore(System.IO.Path.Combine(dir.Path, "nope.json")).Load();
        Assert.Empty(loaded.ClipFolders);
    }

    [Fact]
    public void Store_CorruptFile_IsBackedUpAndDefaultsReturned()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ kaputt");
        var loaded = new SettingsStore(path).Load();
        Assert.Empty(loaded.ClipFolders);
        Assert.True(File.Exists(path + ".bak"));
    }
}

public class TrackColorTests
{
    [Fact]
    public void TrackColor_OwnColorOrDefault()
    {
        var s = new AppSettings();
        Assert.Equal(AppSettings.Palette[0], s.TrackColor(0));
        Assert.NotEqual(s.TrackColor(1), s.TrackColor(2));
        Assert.Equal(AppSettings.Palette[1], s.TrackColor(AppSettings.Palette.Count + 1));

        s.TrackColors[4] = "#1DB954";
        Assert.Equal("#1DB954", s.TrackColor(4));

        // Kaputte Werte aus der settings.json fallen auf den Standard zurück.
        s.TrackColors[3] = "grün";
        Assert.Equal(AppSettings.Palette[3], s.TrackColor(3));
    }

    [Fact]
    public void TrackColors_AreSaved()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var s = new AppSettings();
            s.TrackColors[2] = "#FF0000";
            store.Save(s);
            Assert.Equal("#FF0000", store.Load().TrackColor(2));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}

public class TrackDefaultTests
{
    [Fact]
    public void EnabledTracksFor_RuleAndOwnChoice()
    {
        var s = new AppSettings();
        Assert.Equal(new[] { 1, 2, 3, 4 }, s.EnabledTracksFor(5));
        Assert.Equal(new[] { 0 }, s.EnabledTracksFor(1));

        // Spotify (Spur 5) standardmäßig aus, Mix bleibt aus
        s.TrackEnabledByDefault[4] = false;
        s.TrackEnabledByDefault[0] = false;
        Assert.Equal(new[] { 1, 2, 3 }, s.EnabledTracksFor(5));

        // Clip mit nur einer Spur: nie ganz stumm
        Assert.Equal(new[] { 0 }, s.EnabledTracksFor(1));
    }

    [Fact]
    public void ExportZiel_WirdGemerkt()
    {
        var s = new AppSettings { Tier = DiscordTier.NitroBasic };
        Assert.Equal(1, s.ExportTargetIndex());              // noch nie exportiert: Stufe aus den Einstellungen
        s.RememberExportTarget(null);
        Assert.Equal(3, s.ExportTargetIndex());              // volle Qualität
        s.RememberExportTarget(DiscordTier.Nitro);
        Assert.Equal(2, s.ExportTargetIndex());

        // Übersteht Speichern/Laden als JSON
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(2, loaded.ExportTargetIndex());

        s.LastExportTarget = "Quatsch";
        Assert.Equal(1, s.ExportTargetIndex());
    }
}
