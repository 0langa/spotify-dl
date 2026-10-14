using System.Text.RegularExpressions;

namespace PlaylistDl.App.Services;

public static partial class SpotifyInput
{
    [GeneratedRegex(@"https?://[^\s<>\""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    /// <summary>
    /// Finds a Spotify playlist, album, or track link in pasted text and returns it in one
    /// canonical form.
    /// </summary>
    /// <remarks>
    /// The form is <c>https://open.spotify.com/{type}/{id}</c>. Spotify's share button adds a
    /// fresh <c>?si=</c> value every time, and links can carry other tracking parameters or a
    /// locale segment such as <c>/intl-de/</c>. None of that changes what the link points at,
    /// but the URL is the library key, so each variant used to become its own library entry
    /// and auto-sync candidate.
    /// </remarks>
    public static bool TryNormalize(string input, out string url)
    {
        url = string.Empty;
        var match = UrlPattern().Match(input.Trim());
        if (!match.Success)
        {
            return false;
        }

        var candidate = match.Value.TrimEnd('.', ',', ';', '!', ')', ']', '}', '>');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        if (!host.Equals("spotify.com", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".spotify.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var offset = segments.Length > 0 && segments[0].StartsWith("intl-", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
        if (segments.Length < offset + 2 ||
            segments[offset] is not ("playlist" or "album" or "track") ||
            string.IsNullOrWhiteSpace(segments[offset + 1]))
        {
            return false;
        }

        url = $"https://open.spotify.com/{segments[offset]}/{segments[offset + 1]}";
        return true;
    }

    /// <summary>
    /// Returns the canonical form of a stored Spotify source URL, and any other source (a
    /// search, an imported manifest) unchanged.
    /// </summary>
    /// <remarks>
    /// Only a value that is a Spotify link as a whole is rewritten, so a free-text search
    /// that happens to contain a link is never turned into that link.
    /// </remarks>
    public static string CanonicalSource(string sourceUrl)
    {
        var trimmed = sourceUrl.Trim();
        var link = UrlPattern().Match(trimmed);
        return link.Success && link.Index == 0 && link.Length == trimmed.Length &&
            TryNormalize(trimmed, out var canonical)
                ? canonical
                : sourceUrl;
    }
}
