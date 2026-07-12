using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class SpotifyInputTests
{
    [Theory]
    [InlineData(
        "thttps://open.spotify.com/playlist/0hGKH8nRTU2JYvwX7nt0j0?si=abc",
        "https://open.spotify.com/playlist/0hGKH8nRTU2JYvwX7nt0j0?si=abc")]
    [InlineData(
        "Listen: https://open.spotify.com/intl-de/track/abc123)",
        "https://open.spotify.com/intl-de/track/abc123")]
    public void ExtractsSpotifyUrlFromPastedText(string input, string expected)
    {
        Assert.True(SpotifyInput.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("https://evilspotify.com/playlist/abc")]
    [InlineData("https://open.spotify.com/artist/abc")]
    [InlineData("not a link")]
    public void RejectsUnsupportedOrSpoofedLinks(string input)
    {
        Assert.False(SpotifyInput.TryNormalize(input, out _));
    }
}
