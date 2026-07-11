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
}
