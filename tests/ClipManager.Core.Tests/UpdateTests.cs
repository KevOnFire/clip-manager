using ClipManager.Core.Updates;

namespace ClipManager.Core.Tests;

public class UpdateTests
{
    private const string ReleaseJson = """
    {
      "tag_name": "v0.14.0",
      "draft": false,
      "body": "Etappe 3",
      "assets": [
        { "name": "something-else.zip", "browser_download_url": "https://x/other.zip", "size": 1 },
        { "name": "ClipManager-win-x64.zip", "browser_download_url": "https://github.com/KevOnFire/clip-manager/releases/download/v0.14.0/ClipManager-win-x64.zip", "size": 123456 }
      ]
    }
    """;

    [Fact]
    public void ParseRelease_FindsZipAsset()
    {
        var release = UpdateChecker.ParseRelease(ReleaseJson)!;
        Assert.Equal(new Version(0, 14, 0, 0), release.Version);
        Assert.Equal("v0.14.0", release.Tag);
        Assert.EndsWith("/v0.14.0/ClipManager-win-x64.zip", release.DownloadUrl);
        Assert.Equal(123456, release.SizeBytes);
        Assert.Equal("Etappe 3", release.Notes);
    }

    [Fact]
    public void ParseRelease_WithoutAssetOrBroken_ReturnsNull()
    {
        Assert.Null(UpdateChecker.ParseRelease("""{ "tag_name": "v1.0.0", "assets": [] }"""));
        Assert.Null(UpdateChecker.ParseRelease("""{ "tag_name": "kaputt", "assets": [] }"""));
        Assert.Null(UpdateChecker.ParseRelease(ReleaseJson.Replace("\"draft\": false", "\"draft\": true")));
        Assert.Null(UpdateChecker.ParseRelease("<html>"));
    }

    [Theory]
    [InlineData("v0.14.0", "0.13.0.0", true)]
    [InlineData("v0.14.0", "0.14.0.0", false)]
    [InlineData("0.14", "0.14.0", false)]
    [InlineData("v0.9.0", "0.14.0", false)]
    [InlineData("v1.0.0", "0.99.0", true)]
    [InlineData("v0.15.0-beta", "0.14.0", true)]
    public void IsNewer(string available, string current, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(UpdateChecker.ParseVersion(available)!, UpdateChecker.ParseVersion(current)!));
    }

    [Fact]
    public void Script_WaitsCopiesAndRestarts()
    {
        var script = UpdateScript.Build(4242, @"C:\Temp\staging\", @"C:\Users\Kev\Desktop\ClipManager", "ClipManager.exe");
        Assert.Contains("PID eq 4242", script);
        Assert.Contains(@"robocopy ""C:\Temp\staging"" ""C:\Users\Kev\Desktop\ClipManager"" /E", script);
        Assert.Contains(@"start """" ""C:\Users\Kev\Desktop\ClipManager\ClipManager.exe""", script);
        Assert.Contains("\r\n", script);
    }
}
