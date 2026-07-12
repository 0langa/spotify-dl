using PlaylistDl.App.Models;
using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class DownloadQueueTests
{
    private static QueuedJob Job(string name) => new(
        PlaylistId: Guid.NewGuid().ToString("N"),
        Name: name,
        OutputDirectory: @"C:\music",
        Tracks: [new TrackItem { Id = "t1" }],
        Settings: QueuedJobSettings.From(new AppSettings()));

    [Fact]
    public void FifoOrderAndCounts()
    {
        var queue = new DownloadQueue();
        queue.Enqueue(Job("first"));
        queue.Enqueue(Job("second"));

        Assert.Equal(2, queue.Count);
        Assert.Equal(["first", "second"], queue.PendingNames());
        Assert.Equal("first", queue.DequeueNext()!.Name);
        Assert.Equal("second", queue.DequeueNext()!.Name);
        Assert.Null(queue.DequeueNext());
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void RejectsEmptyTrackList()
    {
        var queue = new DownloadQueue();
        var empty = Job("empty") with { Tracks = [] };

        Assert.Throws<ArgumentException>(() => queue.Enqueue(empty));
    }

    [Fact]
    public void ClearDropsEverything()
    {
        var queue = new DownloadQueue();
        queue.Enqueue(Job("one"));
        queue.Clear();

        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void SettingsSnapshotIsFrozenAtEnqueueTime()
    {
        var settings = new AppSettings { Format = "mp3" };
        var snapshot = QueuedJobSettings.From(settings);
        settings.Format = "flac";

        Assert.Equal("mp3", snapshot.Format);
        Assert.Equal("flac", settings.Format);
    }
}

public sealed class UpdateAutoCheckGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ChecksWhenEnabledAndNeverCheckedBefore()
    {
        Assert.True(UpdateService.ShouldAutoCheck(true, null, Now));
    }

    [Fact]
    public void SkipsWhenDisabledOrCheckedRecently()
    {
        Assert.False(UpdateService.ShouldAutoCheck(false, null, Now));
        Assert.False(UpdateService.ShouldAutoCheck(true, Now.AddHours(-2), Now));
    }

    [Fact]
    public void ChecksAgainAfterTheDailyWindow()
    {
        Assert.True(UpdateService.ShouldAutoCheck(true, Now.AddHours(-21), Now));
    }
}
