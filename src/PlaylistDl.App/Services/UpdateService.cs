using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlaylistDl.App.Services;

public sealed record UpdateResult(Version Version, string Tag, Uri ReleasePage);

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

        return new UpdateResult(latest, release.TagName, page);
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
    }
}
