using PlaylistDl.App.Models;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class TrackItemTests
{
    [Fact]
    public void ComputedDisplayFieldsAreStable()
    {
        var track = new TrackItem
        {
            Artists = ["Artist One", "Artist Two"],
            DurationSeconds = 185,
        };

        Assert.Equal("Artist One, Artist Two", track.ArtistText);
        Assert.Equal("3:05", track.DurationText);
    }

    [Fact]
    public void TracksStartSelectedAndNotifyOnSelectionChange()
    {
        var track = new TrackItem();
        Assert.True(track.IsSelected);

        string? changedProperty = null;
        track.PropertyChanged += (_, args) => changedProperty = args.PropertyName;
        track.IsSelected = false;

        Assert.False(track.IsSelected);
        Assert.Equal(nameof(TrackItem.IsSelected), changedProperty);
    }

    [Fact]
    public void ManualSourceChangesItsDisplayLabel()
    {
        var track = new TrackItem();
        Assert.Equal("Auto", track.SourceLabel);

        track.SourceOverride = "https://youtu.be/example";

        Assert.Equal("Manual", track.SourceLabel);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59, "0:59")]
    [InlineData(185, "3:05")]
    [InlineData(3600, "1:00:00")]
    [InlineData(4205, "1:10:05")]
    public void DurationTextKeepsWholeHours(int seconds, string expected)
    {
        Assert.Equal(expected, new TrackItem { DurationSeconds = seconds }.DurationText);
    }
}
