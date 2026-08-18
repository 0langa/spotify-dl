using PlaylistDl.App.Models;
using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class DownloadQueueTests
{
    internal static QueuedJob Job(string name, params TrackItem[] tracks)
    {
        var all = tracks.Length > 0 ? tracks : [new TrackItem { Id = "t1" }];
        var url = $"https://open.spotify.com/playlist/{name}";
        return new QueuedJob(
            Name: name,
            SourceUrl: url,
            SourceType: "playlist",
            OutputDirectory: @"C:\music",
            Settings: QueuedJobSettings.From(new AppSettings()),
            Snapshot: SavedJobSnapshot.Create(url, name, "playlist", @"C:\music", all))
        {
            PlaylistId = Guid.NewGuid().ToString("N"),
            AllTracks = all,
            Tracks = [.. all.Where(track => track.IsSelected)],
        };
    }

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
    public void RefreshSnapshotsReplacesRepairedJobsAndDropsTheirSession()
    {
        var queue = new DownloadQueue();
        queue.Enqueue(Job("first"));
        queue.Enqueue(Job("second"));
        var repairedUrl = queue.Items[0].SourceUrl;
        var changes = 0;
        queue.Changed += (_, _) => changes++;
        var repaired = new SavedJob
        {
            SourceUrl = repairedUrl,
            SourceName = "first",
            OutputDirectory = @"C:\music",
            Tracks = [new SavedTrack { Id = "t1", IsSelected = true, IsComplete = false }],
        };

        var refreshed = queue.RefreshSnapshots(
            source => source == repairedUrl ? repaired : null);

        Assert.Equal(1, refreshed);
        Assert.Equal(1, changes);
        Assert.Same(repaired, queue.Items[0].Snapshot);
        // The resolved session belongs to the pre-repair selection and has to be redone.
        Assert.True(queue.Items[0].NeedsResolve);
        Assert.False(queue.Items[1].NeedsResolve);
    }

    [Fact]
    public void RefreshSnapshotsKeepsTheSelectionTheJobWasQueuedWith()
    {
        var queue = new DownloadQueue();
        queue.Enqueue(Job(
            "mix",
            new TrackItem { Id = "t1", IsSelected = true },
            new TrackItem { Id = "t2", IsSelected = false }));
        var url = queue.Items[0].SourceUrl;
        var repaired = new SavedJob
        {
            SourceUrl = url,
            SourceName = "mix",
            OutputDirectory = @"C:\elsewhere",
            Tracks =
            [
                new SavedTrack { Id = "t1", IsSelected = true, IsComplete = true, OutputPath = @"C:\music\t1.mp3" },
                new SavedTrack { Id = "t2", IsSelected = true, IsComplete = false },
                new SavedTrack { Id = "t3", IsSelected = true, IsComplete = false },
            ],
        };

        Assert.Equal(1, queue.RefreshSnapshots(source => source == url ? repaired : null));

        var snapshot = queue.Items[0].Snapshot;
        // The repaired completion state is taken over...
        Assert.True(snapshot.Tracks.Single(track => track.Id == "t1").IsComplete);
        // ...but a track the user deselected before queuing stays deselected.
        Assert.False(snapshot.Tracks.Single(track => track.Id == "t2").IsSelected);
        // A track this job never had is not added to it by a repair.
        Assert.False(snapshot.Tracks.Single(track => track.Id == "t3").IsSelected);
        // The queued job keeps its own output folder.
        Assert.Equal(@"C:\music", snapshot.OutputDirectory);
    }

    [Fact]
    public void RefreshSnapshotsStoresTheRepairEvenWhenNothingIsLeftToDownload()
    {
        var queue = new DownloadQueue();
        queue.Enqueue(Job("mix", new TrackItem { Id = "t1", IsSelected = true }));
        var url = queue.Items[0].SourceUrl;
        var repaired = new SavedJob
        {
            SourceUrl = url,
            SourceName = "mix",
            OutputDirectory = @"C:\music",
            Tracks = [new SavedTrack { Id = "t1", IsSelected = true, IsComplete = true }],
        };

        Assert.Equal(1, queue.RefreshSnapshots(source => source == url ? repaired : null));

        // Keeping the pre-repair snapshot would let the run write it back over the repair.
        Assert.Same(repaired, queue.Items[0].Snapshot);
        Assert.Equal(0, queue.Items[0].SelectedCount);
    }

    [Fact]
    public void ATrackTheRepairReopenedIsTakenBackIntoTheQueuedJob()
    {
        var queue = new DownloadQueue();
        queue.Enqueue(Job(
            "mix",
            new TrackItem { Id = "t1", IsSelected = true },
            new TrackItem { Id = "done", IsSelected = false, Status = "Done" }));
        var url = queue.Items[0].SourceUrl;
        var repaired = new SavedJob
        {
            SourceUrl = url,
            SourceName = "mix",
            OutputDirectory = @"C:\music",
            Tracks =
            [
                new SavedTrack { Id = "t1", IsSelected = true, IsComplete = false },
                // The library check found its file gone and reopened it.
                new SavedTrack { Id = "done", IsSelected = true, IsComplete = false },
            ],
        };

        Assert.Equal(1, queue.RefreshSnapshots(source => source == url ? repaired : null));

        var snapshot = queue.Items[0].Snapshot;
        Assert.True(snapshot.Tracks.Single(track => track.Id == "done").IsSelected);
        Assert.Equal(2, queue.Items[0].SelectedCount);
    }

    [Fact]
    public void RefreshSnapshotsLeavesTheQueueAloneWhenNothingWasRepaired()
    {
        var queue = new DownloadQueue();
        queue.Enqueue(Job("first"));
        var before = queue.Items[0];
        var changes = 0;
        queue.Changed += (_, _) => changes++;

        Assert.Equal(0, queue.RefreshSnapshots(_ => null));
        Assert.Same(before, queue.Items[0]);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void RemoveSourceDropsEveryPendingJobForThatSource()
    {
        var queue = new DownloadQueue();
        var repeated = Job("first");
        queue.Enqueue(repeated);
        queue.Enqueue(Job("second"));
        // The same source can be queued twice, e.g. by hand and then by auto-sync.
        queue.Enqueue(repeated with { Name = "first again" });
        var changes = 0;
        queue.Changed += (_, _) => changes++;

        Assert.Equal(2, queue.RemoveSource(repeated.SourceUrl.ToUpperInvariant()));
        Assert.Equal(1, changes);
        Assert.Equal(["second"], queue.PendingNames());
        // Nothing to remove must not raise a change.
        Assert.Equal(0, queue.RemoveSource(repeated.SourceUrl));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ReplaceKeepsAnAutoSyncJobButDropsAFinishedOne()
    {
        var queue = new DownloadQueue();
        var autoSync = Job("kept in sync", new TrackItem { Id = "a", Status = "Done" }) with
        {
            ResolveSelection = true,
            PlaylistId = null,
            AllTracks = [],
            Tracks = [],
        };
        var finished = Job("finished", new TrackItem { Id = "b", Status = "Done" }) with
        {
            PlaylistId = null,
            AllTracks = [],
            Tracks = [],
        };
        Assert.Equal(0, autoSync.SelectedCount);
        Assert.Equal(0, finished.SelectedCount);

        queue.Replace([autoSync, finished]);

        // The auto-sync job decides its tracks when it runs, so it must survive the filter.
        Assert.Equal(["kept in sync"], queue.PendingNames());
    }

    [Fact]
    public void RejectsEmptyTrackList()
    {
        var queue = new DownloadQueue();
        var empty = Job("empty", new TrackItem { Id = "t1", IsSelected = false });

        Assert.Throws<ArgumentException>(() => queue.Enqueue(empty));
    }

    [Fact]
    public void MoveReordersPendingJobsAndClampsAtTheEnds()
    {
        var queue = new DownloadQueue();
        queue.Enqueue(Job("first"));
        queue.Enqueue(Job("second"));
        queue.Enqueue(Job("third"));

        Assert.Equal(0, queue.Move(1, -1));
        Assert.Equal(["second", "first", "third"], queue.PendingNames());
        Assert.Equal(2, queue.Move(1, 5));
        Assert.Equal(["second", "third", "first"], queue.PendingNames());
        Assert.Equal(0, queue.Move(0, -1));
        Assert.Equal(["second", "third", "first"], queue.PendingNames());
    }

    [Fact]
    public void RemoveAtDropsOneJobAndIgnoresBadIndexes()
    {
        var queue = new DownloadQueue();
        queue.Enqueue(Job("first"));
        queue.Enqueue(Job("second"));

        queue.RemoveAt(5);
        Assert.Equal(2, queue.Count);
        queue.RemoveAt(0);
        Assert.Equal(["second"], queue.PendingNames());
    }

    [Fact]
    public void ChangesAreAnnouncedSoTheQueueCanBePersisted()
    {
        var queue = new DownloadQueue();
        var changes = 0;
        queue.Changed += (_, _) => changes++;

        queue.Enqueue(Job("first"));
        queue.Enqueue(Job("second"));
        queue.Move(0, 1);
        queue.RemoveAt(0);
        queue.DequeueNext();
        queue.Clear();

        // Enqueue, enqueue, move, remove, dequeue: the final clear had nothing left to do.
        Assert.Equal(5, changes);
    }

    [Fact]
    public void RestoredJobsCountSelectionFromTheSnapshot()
    {
        var job = Job(
            "restored",
            new TrackItem { Id = "a" },
            new TrackItem { Id = "b" },
            new TrackItem { Id = "c", Status = "Done" });
        var withoutLiveTracks = job with { PlaylistId = null, AllTracks = [], Tracks = [] };

        Assert.True(withoutLiveTracks.NeedsResolve);
        Assert.Equal(2, withoutLiveTracks.SelectedCount);
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

    [Fact]
    public void SavedSnapshotKeepsFullSourceAndQueuedCompletion()
    {
        var completed = new TrackItem
        {
            Id = "done",
            SpotifyUrl = "https://open.spotify.com/track/done",
            Status = "Done",
            Progress = 100,
        };
        var pending = new TrackItem
        {
            Id = "pending",
            SpotifyUrl = "https://open.spotify.com/track/pending",
        };
        var failed = new TrackItem
        {
            Id = "failed",
            SpotifyUrl = "https://open.spotify.com/track/failed",
            Status = "Failed",
            Progress = 100,
            ErrorText = "Age-restricted source",
        };
        var job = Job("source") with
        {
            AllTracks = [completed, pending, failed],
            Tracks = [completed],
        };

        var saved = SavedJobSnapshot.Create(
            job.SourceUrl,
            job.Name,
            job.SourceType,
            job.OutputDirectory,
            job.AllTracks);

        Assert.Equal(job.SourceUrl, saved.SourceUrl);
        Assert.Equal(3, saved.Tracks.Count);
        Assert.True(saved.Tracks.Single(track => track.Id == "done").IsComplete);
        Assert.False(saved.Tracks.Single(track => track.Id == "pending").IsComplete);
        Assert.False(saved.Tracks.Single(track => track.Id == "failed").IsComplete);
        Assert.Equal("Age-restricted source", saved.Tracks.Single(track => track.Id == "failed").LastError);
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
