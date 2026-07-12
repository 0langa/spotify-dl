using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class UserErrorMessagesTests
{
    [Fact]
    public void SpotifyTimeoutBecomesActionableWithoutRawProviderTrace()
    {
        var error = new InvalidOperationException(
            "Failed to complete request. (HTTPSConnectionPool(host='open.spotify.com'): " +
            "Max retries exceeded with url: / (Caused by ConnectTimeoutError(Connection timed out)))");

        var message = UserErrorMessages.ForSourceResolution(error, spotifySource: true);

        Assert.Contains("Spotify could not be reached", message);
        Assert.Contains("Run Diagnose", message);
        Assert.DoesNotContain("HTTPSConnectionPool", message);
    }

    [Fact]
    public void YoutubeBotWallBecomesCookieGuidance()
    {
        var error = new InvalidOperationException("HTTP Error 429: Sign in to confirm you're not a bot");

        var message = UserErrorMessages.ForSourceResolution(error, spotifySource: false);

        Assert.Contains("rate-limiting", message);
        Assert.Contains("cookies", message);
    }

    [Fact]
    public void UnknownFailureKeepsOriginalMessage()
    {
        const string detail = "Provider returned an unfamiliar response.";

        Assert.Equal(
            detail,
            UserErrorMessages.ForSourceResolution(new InvalidOperationException(detail), spotifySource: true));
    }
}
