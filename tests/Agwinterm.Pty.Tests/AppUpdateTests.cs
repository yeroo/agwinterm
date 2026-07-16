using Agwinterm.Pty;

namespace Agwinterm.Pty.Tests;

public class AppUpdateTests
{
    private const string LocalAppData = @"C:\Users\x\AppData\Local";

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\agwinterm\Agwinterm.Win32.exe", UpdateChannel.Installed)]
    [InlineData(@"C:\Users\x\AppData\Local\PROGRAMS\AGWINTERM\Agwinterm.Win32.exe", UpdateChannel.Installed)]   // case-insensitive
    [InlineData(@"C:\Users\x\scoop\apps\agwinterm\current\agwinterm.exe", UpdateChannel.PackageManager)]
    [InlineData(@"C:\ProgramData\chocolatey\lib\agwinterm\tools\agwinterm.exe", UpdateChannel.PackageManager)]
    [InlineData(@"D:\tools\agwinterm.exe", UpdateChannel.Portable)]
    [InlineData(@"C:\Users\x\Downloads\agwinterm-portable-0.14.1-win-x64.exe", UpdateChannel.Portable)]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\agwinterm-fork\agwinterm.exe", UpdateChannel.Portable)]     // prefix, not our dir
    public void DetectChannel_ClassifiesByExePath(string exe, UpdateChannel expected)
        => Assert.Equal(expected, AppUpdate.DetectChannel(exe, LocalAppData));

    private const string ReleaseJson = """
        {
          "tag_name": "v0.14.1",
          "assets": [
            { "name": "agwinterm-portable-0.14.1-win-x64.exe",
              "browser_download_url": "https://github.com/yeroo/agwinterm/releases/download/v0.14.1/agwinterm-portable-0.14.1-win-x64.exe",
              "digest": "sha256:06cb165d5aaf9088c2b44d8a3fd99e620477a07a198858ec923486b5697449ef" },
            { "name": "agwinterm-setup-0.14.1.exe",
              "browser_download_url": "https://github.com/yeroo/agwinterm/releases/download/v0.14.1/agwinterm-setup-0.14.1.exe",
              "digest": "sha256:167cb39f16dbf3fcd6623a9c79107b16bd0a3285d01094745d562e77ae7271fe" }
          ]
        }
        """;

    [Fact]
    public void ParseRelease_ReadsVersionAssetsAndDigests()
    {
        var rel = AppUpdate.ParseRelease(ReleaseJson);
        Assert.NotNull(rel);
        Assert.Equal("0.14.1", rel!.Version);   // tag v-prefix stripped
        Assert.Equal(2, rel.Assets.Count);
        Assert.Equal("06cb165d5aaf9088c2b44d8a3fd99e620477a07a198858ec923486b5697449ef",
            rel.Assets[0].Sha256);              // "sha256:" prefix stripped
    }

    [Fact]
    public void ParseRelease_FailsClosed()
    {
        Assert.Null(AppUpdate.ParseRelease("not json"));
        Assert.Null(AppUpdate.ParseRelease("{\"tag_name\":\"nightly\"}"));            // unparseable version
        Assert.Null(AppUpdate.ParseRelease("{\"tag_name\":\"v1.0.0\"}"));             // no assets array
        // Unknown digest algorithm → asset kept but digest dropped (download will refuse it).
        var rel = AppUpdate.ParseRelease(
            "{\"tag_name\":\"v1.0.0\",\"assets\":[{\"name\":\"agwinterm-setup-1.0.0.exe\",\"browser_download_url\":\"https://x/y.exe\",\"digest\":\"sha512:abc\"}]}");
        Assert.NotNull(rel);
        Assert.Null(rel!.Assets[0].Sha256);
    }

    [Fact]
    public void PickAsset_MatchesChannelToArtifact()
    {
        var rel = AppUpdate.ParseRelease(ReleaseJson)!;
        Assert.Equal("agwinterm-setup-0.14.1.exe", AppUpdate.PickAsset(rel, UpdateChannel.Installed)!.Name);
        Assert.Equal("agwinterm-portable-0.14.1-win-x64.exe", AppUpdate.PickAsset(rel, UpdateChannel.Portable)!.Name);
        Assert.Null(AppUpdate.PickAsset(rel, UpdateChannel.PackageManager));   // managers own their files
    }

    [Fact]
    public async Task DownloadVerified_LocalSeam_AcceptsGoodRejectsBadDigest()
    {
        string dir = Path.Combine(Path.GetTempPath(), "agwinterm-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "payload.bin");
            await File.WriteAllBytesAsync(src, new byte[] { 1, 2, 3, 4 });
            string good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3, 4 })).ToLowerInvariant();

            string dst = Path.Combine(dir, "out.bin");
            Assert.True(await AppUpdate.DownloadVerifiedAsync(new ReleaseAsset("payload.bin", src, good), dst));
            Assert.True(File.Exists(dst));

            string dst2 = Path.Combine(dir, "out2.bin");
            Assert.False(await AppUpdate.DownloadVerifiedAsync(new ReleaseAsset("payload.bin", src, new string('0', 64)), dst2));
            Assert.False(File.Exists(dst2));                       // mismatch → deleted

            Assert.False(await AppUpdate.DownloadVerifiedAsync(new ReleaseAsset("payload.bin", src, null), Path.Combine(dir, "out3.bin")));   // no digest → fail closed
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void HelperScript_SwapsSafely()
    {
        string s = AppUpdate.HelperScript;
        Assert.Contains("Wait-Process", s);                        // never swap under a live process
        Assert.Contains("/VERYSILENT", s);                         // installed channel: silent per-user setup
        Assert.Contains("$Exe + '.old'", s);                       // portable channel: rename-aside, not overwrite
        Assert.Contains("Move-Item $old $Exe", s);                 // rollback path restores the app on failure
        Assert.Contains("Start-Process $Exe", s);                  // relaunch the same path either way
    }
}
