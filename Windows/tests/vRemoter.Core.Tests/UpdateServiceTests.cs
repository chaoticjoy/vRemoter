using System.Security.Cryptography;
using vRemoter.Core.Updates;
using Xunit;

namespace vRemoter.Core.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public void SemanticVersionComparisonHandlesPrereleaseAndPadding()
    {
        Assert.True(new AppVersion("v1.4.0") > new AppVersion("1.3.9"));
        Assert.True(new AppVersion("1.4.0-beta.2") > new AppVersion("1.4.0-beta.1"));
        Assert.True(new AppVersion("1.4.0") > new AppVersion("1.4.0-beta.9"));
        Assert.True(new AppVersion("1.4") == new AppVersion("1.4.0"));
    }

    [Fact]
    public async Task SelectsWindowsAssetAndIgnoresMacPackages()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, """
            [
              {
                "id": 2001,
                "tag_name": "v1.5.0",
                "name": "vRemoter 1.5.0",
                "body": "Windows and macOS dual release",
                "html_url": "https://github.com/ccc333cn/vRemoter/releases/tag/v1.5.0",
                "draft": false,
                "prerelease": false,
                "assets": [
                  {
                    "id": 1,
                    "name": "vRemoter-Xiaozu-1.5.0-AppleSilicon.pkg",
                    "browser_download_url": "https://example.invalid/app.pkg",
                    "digest": null
                  },
                  {
                    "id": 2,
                    "name": "vRemoter-Xiaozu-1.5.0-Windows-x64-Setup.exe",
                    "browser_download_url": "https://example.invalid/setup.exe",
                    "digest": "sha256:0000"
                  }
                ]
              }
            ]
            """);

            var service = new GitHubUpdateService(new Uri(tempFile).AbsoluteUri, "1.4.0");
            var result = await service.CheckForUpdateAsync();
            var available = Assert.IsType<UpdateCheckResult.Available>(result);
            Assert.Equal("vRemoter-Xiaozu-1.5.0-Windows-x64-Setup.exe", available.Release.WindowsInstallerAsset?.Name);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task VerifiesSha256AgainstFixtureDigest()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"vremoter-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            byte[] payload = "installer-binary-bytes"u8.ToArray();
            string goodDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            string srcFile = Path.Combine(tempDir, "source.exe");
            await File.WriteAllBytesAsync(srcFile, payload);

            var handler = new StubHttpHandler(payload);
            var service = new GitHubUpdateService("https://example.invalid/releases", "1.4.0", new HttpClient(handler));

            var release = new GitHubRelease(
                1, "v1.5.0", "vRemoter 1.5.0", null, "https://example.invalid", false, false, false,
                [new GitHubReleaseAsset(10, "vRemoter-Xiaozu-1.5.0-Windows-x64-Setup.exe", "https://example.invalid/setup.exe", goodDigest)]);

            string? downloaded = await service.DownloadInstallerAsync(release, Path.Combine(tempDir, "dl"));
            Assert.NotNull(downloaded);
            Assert.True(File.Exists(downloaded));

            var badRelease = release with
            {
                Assets = [new GitHubReleaseAsset(11, "bad.exe", "https://example.invalid/bad.exe", "sha256:" + new string('0', 64))]
            };
            await Assert.ThrowsAsync<UpdateIntegrityException>(() =>
                service.DownloadInstallerAsync(badRelease, Path.Combine(tempDir, "dl2")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class StubHttpHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            return Task.FromResult(response);
        }
    }
}

