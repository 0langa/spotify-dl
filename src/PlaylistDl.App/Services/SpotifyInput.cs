using System.Text.RegularExpressions;

namespace PlaylistDl.App.Services;

public static partial class SpotifyInput
{
    [GeneratedRegex(@"https?://[^\s<>\""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

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

        url = uri.AbsoluteUri;
        return true;
    }
}
