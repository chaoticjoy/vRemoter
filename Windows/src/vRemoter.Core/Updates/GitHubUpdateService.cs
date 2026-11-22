using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace vRemoter.Core.Updates;

public sealed record GitHubReleaseAsset(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
    [property: JsonPropertyName("digest")] string? Digest);

public sealed record GitHubRelease(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("driver_update_required")] bool? DriverUpdateRequired,
    [property: JsonPropertyName("assets")] List<GitHubReleaseAsset> Assets)
{
    public string DisplayVersion =>
        TagName.StartsWith('v') || TagName.StartsWith('V') ? TagName[1..] : TagName;

    /// <summary>
    /// Selects the Windows x64 installer (.exe) first, falling back to Windows .zip.
    /// Never selects macOS .pkg or .dmg assets.
    /// </summary>
    public GitHubReleaseAsset? WindowsInstallerAsset
    {
        get
        {
            var exe = Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                ?? Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            var zip = Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("Windows", StringComparison.OrdinalIgnoreCase));
            return exe ?? zip;
        }
    }
}

public abstract record UpdateCheckResult
{
    public sealed record UpToDate : UpdateCheckResult;
    public sealed record Available(GitHubRelease Release) : UpdateCheckResult;
}

public sealed class UpdateSourceUnavailableException : Exception
{
    public UpdateSourceUnavailableException() : base("Update source unavailable (404)") { }
}

public sealed class UpdateIntegrityException : Exception
{
    public UpdateIntegrityException(string message) : base(message) { }
}

public sealed class GitHubUpdateService
{
    public const string DefaultReleasesUrl = "https://api.github.com/repos/ccc333cn/vRemoter/releases";

    private readonly Uri _releasesUri;
    private readonly string _currentVersion;
    private readonly HttpClient _http;

    public GitHubUpdateService(string configuredUrl, string currentVersion, HttpClient? httpClient = null)
    {
        _currentVersion = currentVersion;
        string? envOverride = Environment.GetEnvironmentVariable("VREMOTER_RELEASES_URL");
        string effective = !string.IsNullOrWhiteSpace(envOverride) ? envOverride : configuredUrl;
        _releasesUri = new Uri(effective, UriKind.Absolute);
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        string json;
        if (_releasesUri.IsFile)
        {
            json = await File.ReadAllTextAsync(_releasesUri.LocalPath, ct);
        }
        else
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _releasesUri);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.UserAgent.ParseAdd($"vRemoter/{_currentVersion}");

            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                throw new UpdateSourceUnavailableException();
            }
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync(ct);
        }

        var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json) ?? [];
        var current = new AppVersion(_currentVersion);
        bool acceptsPrereleases = _currentVersion.Contains('-', StringComparison.Ordinal);

        var newest = releases
            .Where(r => !r.Draft && (acceptsPrereleases || !r.Prerelease))
            .OrderByDescending(r => new AppVersion(r.TagName))
            .FirstOrDefault();

        if (newest is null || new AppVersion(newest.TagName) <= current)
        {
            return new UpdateCheckResult.UpToDate();
        }
        return new UpdateCheckResult.Available(newest);
    }

    public async Task<string?> DownloadInstallerAsync(
        GitHubRelease release,
        string? destinationDirectory = null,
        CancellationToken ct = default)
    {
        var asset = release.WindowsInstallerAsset;
        if (asset is null) return null;

        string targetDir = destinationDirectory
            ?? Path.Combine(Path.GetTempPath(), "vRemoter-Updates");
        Directory.CreateDirectory(targetDir);
        string destPath = Path.Combine(targetDir, asset.Name);
        string tempPath = destPath + ".download";

        using var req = new HttpRequestMessage(HttpMethod.Get, asset.BrowserDownloadUrl);
        req.Headers.Accept.ParseAdd("application/octet-stream");
        req.Headers.UserAgent.ParseAdd($"vRemoter/{_currentVersion}");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using (var remoteStream = await resp.Content.ReadAsStreamAsync(ct))
        await using (var fileStream = File.Create(tempPath))
        {
            await remoteStream.CopyToAsync(fileStream, ct);
        }

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tempPath, destPath);

        if (!string.IsNullOrWhiteSpace(asset.Digest) &&
            asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            string expected = asset.Digest["sha256:".Length..].Trim().ToLowerInvariant();
            await using var readStream = File.OpenRead(destPath);
            byte[] hash = await SHA256.HashDataAsync(readStream, ct);
            string actual = Convert.ToHexString(hash).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                try { File.Delete(destPath); } catch { }
                throw new UpdateIntegrityException($"SHA-256 mismatch: expected {expected}, got {actual}");
            }
        }

        return destPath;
    }
}

