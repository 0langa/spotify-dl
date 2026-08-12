using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlaylistDl.App.Services;

/// <summary>One downloadable file published with a release.</summary>
public sealed record ReleaseAsset(string Name, Uri Url, long Size);

public sealed record UpdateResult(
    Version Version,
    string Tag,
    Uri ReleasePage,
    ReleaseAsset? Executable = null,
    ReleaseAsset? Checksums = null)
{
    /// <summary>True when the release ships both the executable and its checksums.</summary>
    public bool CanInstall => Executable is not null && Checksums is not null;
}

public sealed class UpdateService
{
    private static readonly Uri LatestReleaseEndpoint = new(
        "https://api.github.com/repos/0langa/spotify-dl/releases/latest");
    private readonly HttpClient _client;

    public UpdateService(HttpClient? client = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PlaylistDL", "1.0"));
        }
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!_client.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"))
        {
            _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        }
    }

    public async Task<UpdateResult?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync(LatestReleaseEndpoint, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(body, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("GitHub returned an empty release response.");
        var latest = ParseVersion(release.TagName);
        if (latest <= currentVersion || !TryParseReleasePage(release.HtmlUrl, out var page))
        {
            return null;
        }

        return new UpdateResult(
            latest,
            release.TagName,
            page,
            SelectAsset(release.Assets, "PlaylistDL.exe"),
            SelectAsset(release.Assets, "SHA256SUMS.txt"));
    }

    /// <summary>Only an https github.com download URL is ever used for an update.</summary>
    public static ReleaseAsset? SelectAsset(IReadOnlyList<GitHubAsset>? assets, string name)
    {
        foreach (var asset in assets ?? [])
        {
            if (!string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(asset.Name) != asset.Name)
            {
                continue;
            }

            if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var url) ||
                url.Scheme != Uri.UriSchemeHttps ||
                !(url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                    url.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
                    url.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return new ReleaseAsset(asset.Name, url, asset.Size);
        }

        return null;
    }

    /// <summary>Only an https github.com release page is ever handed to the shell.</summary>
    public static bool TryParseReleasePage(string? value, out Uri page)
    {
        page = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps ||
            !(candidate.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                candidate.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        page = candidate;
        return true;
    }

    /// <summary>Gate for the silent startup check: enabled and not checked within ~a day.</summary>
    public static bool ShouldAutoCheck(bool enabled, DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc) =>
        enabled && (lastCheckUtc is null || nowUtc - lastCheckUtc >= TimeSpan.FromHours(20));

    public static Version ParseVersion(string tag)
    {
        var value = tag.Trim().TrimStart('v', 'V');
        var prerelease = value.IndexOfAny(['-', '+']);
        if (prerelease >= 0)
        {
            value = value[..prerelease];
        }
        return Version.TryParse(value, out var version)
            ? version
            : throw new FormatException($"Unsupported release version: {tag}");
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; init; } = [];
    }

    public sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; init; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; init; }
    }
}
