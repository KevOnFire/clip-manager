using ClipManager.Core.Settings;

namespace ClipManager.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_HaveDiscordLimits()
    {
        var s = new AppSettings();
        Assert.Equal(DiscordTier.Free, s.Tier);
        Assert.Equal(10, s.LimitFor(DiscordTier.Free));
        Assert.Equal(50, s.LimitFor(DiscordTier.NitroBasic));
        Assert.Equal(500, s.LimitFor(DiscordTier.Nitro));
    }

    [Fact]
    public void LimitFor_FallsBackOnInvalidValue()
    {
        var s = new AppSettings();
        s.TierLimitsMb[DiscordTier.Free] = 0;
        s.TierLimitsMb.Remove(DiscordTier.Nitro);
        Assert.Equal(10, s.LimitFor(DiscordTier.Free));
        Assert.Equal(500, s.LimitFor(DiscordTier.Nitro));
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
