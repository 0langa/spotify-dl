using PlaylistDl.App.Models;
using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class ManualSourcesTests
{
    private static SavedJob Saved(params SavedTrack[] tracks) => new()
    {
        SourceUrl = "https://open.spotify.com/playlist/mix",
        Tracks = [.. tracks],
    };

    [Fact]
    public void AFreshAnalysisGetsBackTheSourcesPickedByHand()
    {
        var saved = Saved(
            new SavedTrack
            {
                Id = "a",
                SpotifyUrl = "https://open.spotify.com/track/a",
                IsComplete = true,
                SourceOverride = "https://music.youtube.com/watch?v=picked-a",
            },
            new SavedTrack { Id = "b", SourceOverride = "https://www.youtube.com/watch?v=picked-b" },
            new SavedTrack { Id = "c" });
        var fresh = new[]
        {
            new TrackItem { Id = "a", SpotifyUrl = "https://open.spotify.com/track/a" },
            new TrackItem { Id = "b" },
            new TrackItem { Id = "c" },
            new TrackItem { Id = "new" },
        };

        var applied = SavedJobSnapshot.ApplyManualSources(fresh, saved);

        Assert.Equal(2, applied);
        Assert.Equal("https://music.youtube.com/watch?v=picked-a", fresh[0].SourceOverride);
        Assert.Equal("https://www.youtube.com/watch?v=picked-b", fresh[1].SourceOverride);
        Assert.Null(fresh[2].SourceOverride);
        Assert.Null(fresh[3].SourceOverride);
        // Only the choice comes back; the fresh analysis still starts the track over.
        Assert.Equal("Ready", fresh[0].Status);
        Assert.True(fresh[0].IsSelected);
    }

    [Fact]
    public void MatchesByTrackUrlWhenTheIdChanged()
    {
        var saved = Saved(new SavedTrack
        {
            Id = "old-key",
            SpotifyUrl = "https://open.spotify.com/track/a",
            SourceOverride = "https://youtu.be/picked",
        });
        var fresh = new[] { new TrackItem { Id = "a", SpotifyUrl = "https://open.spotify.com/track/a" } };

        Assert.Equal(1, SavedJobSnapshot.ApplyManualSources(fresh, saved));
        Assert.Equal("https://youtu.be/picked", fresh[0].SourceOverride);
    }

    [Fact]
    public void ATrackThatAlreadyHasASourceKeepsIt()
    {
        var saved = Saved(new SavedTrack { Id = "a", SourceOverride = "https://youtu.be/old" });
        var fresh = new[] { new TrackItem { Id = "a", SourceOverride = "https://youtu.be/current" } };

        Assert.Equal(0, SavedJobSnapshot.ApplyManualSources(fresh, saved));
        Assert.Equal("https://youtu.be/current", fresh[0].SourceOverride);
    }

    [Fact]
    public void NothingSavedChangesNothing()
    {
        var fresh = new[] { new TrackItem { Id = "a" } };

        Assert.Equal(0, SavedJobSnapshot.ApplyManualSources(fresh, null));
        Assert.Null(fresh[0].SourceOverride);
    }
}
