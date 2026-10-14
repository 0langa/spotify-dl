using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class SpotifyInputTests
{
    [Theory]
    [InlineData(
        "thttps://open.spotify.com/playlist/0hGKH8nRTU2JYvwX7nt0j0?si=abc",
        "https://open.spotify.com/playlist/0hGKH8nRTU2JYvwX7nt0j0")]
    [InlineData(
        "Listen: https://open.spotify.com/intl-de/track/abc123)",
        "https://open.spotify.com/track/abc123")]
    public void ExtractsSpotifyUrlFromPastedText(string input, string expected)
    {
        Assert.True(SpotifyInput.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=1a2b3c4d5e6f4a7b")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=ffffffffffff&pt=xyz")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?utm_source=copy-link&utm_medium=copy-link")]
    [InlineData("https://open.spotify.com/intl-fr/playlist/37i9dQZF1DXcBWIGoYBM5M?si=x")]
    [InlineData("http://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M/")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M#tracks")]
    [InlineData("https://spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M")]
    public void EveryShareLinkOfOnePlaylistMapsToOneUrl(string input)
    {
        Assert.True(SpotifyInput.TryNormalize(input, out var actual));
        Assert.Equal("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", actual);
        Assert.Equal(LibraryStore.KeyFor("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M"), LibraryStore.KeyFor(actual));
    }

    [Fact]
    public void KeepsTheCaseOfTheId()
    {
        Assert.True(SpotifyInput.TryNormalize("https://open.spotify.com/album/4aawyAB9vmqN3uQ7FjRGTy?si=Q", out var actual));
        Assert.Equal("https://open.spotify.com/album/4aawyAB9vmqN3uQ7FjRGTy", actual);
    }

    [Theory]
    [InlineData("https://evilspotify.com/playlist/abc")]
    [InlineData("https://open.spotify.com/artist/abc")]
    [InlineData("not a link")]
    public void RejectsUnsupportedOrSpoofedLinks(string input)
    {
        Assert.False(SpotifyInput.TryNormalize(input, out _));
    }

    [Theory]
    [InlineData("https://open.spotify.com/track/abc123?si=q", "https://open.spotify.com/track/abc123")]
    [InlineData("https://open.spotify.com/track/abc123", "https://open.spotify.com/track/abc123")]
    [InlineData("search:artist title", "search:artist title")]
    [InlineData("search:remix of https://open.spotify.com/track/abc123?si=q", "search:remix of https://open.spotify.com/track/abc123?si=q")]
    [InlineData(@"C:\Music\manifest.csv", @"C:\Music\manifest.csv")]
    [InlineData("https://example.com/playlist/abc?si=q", "https://example.com/playlist/abc?si=q")]
    public void CanonicalSourceRewritesOnlySpotifyLinks(string stored, string expected)
    {
        Assert.Equal(expected, SpotifyInput.CanonicalSource(stored));
    }
}
