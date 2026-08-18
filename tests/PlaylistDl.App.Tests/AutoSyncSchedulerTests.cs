using PlaylistDl.App.Models;
using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class AutoSyncSchedulerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "playlistdl-tests", Guid.NewGuid().ToString("N"));

    private string LibraryDirectory => Path.Combine(_root, "library");

    private string MusicDirectory => Path.Combine(_root, "music");

    private LibraryStore Store => new(LibraryDirectory);

    private SavedJob Job(
        string name,
        bool autoSync = true,
        DateTimeOffset? lastSync = null,
        string sourceType = "playlist",
        string? outputDirectory = null)
    {
        Directory.CreateDirectory(MusicDirectory);
        return new SavedJob
        {
            SourceUrl = $"https://open.spotify.com/playlist/{name}",
            SourceName = name,
            SourceType = sourceType,
            OutputDirectory = outputDirectory ?? MusicDirectory,
            AutoSync = autoSync,
            LastAutoSyncUtc = lastSync,
        };
    }

    [Fact]
    public void ASourceThatWasNeverCheckedIsDue()
    {
        Assert.Single(AutoSyncScheduler.Due([Job("mix")], TimeSpan.FromHours(1), Now));
    }

    [Fact]
    public void ASourceCheckedInsideTheIntervalIsNotDue()
    {
        var jobs = new[] { Job("mix", lastSync: Now - TimeSpan.FromMinutes(59)) };

        Assert.Empty(AutoSyncScheduler.Due(jobs, TimeSpan.FromHours(1), Now));
    }

    [Fact]
    public void ASourceCheckedLongerAgoThanTheIntervalIsDue()
    {
        var jobs = new[] { Job("mix", lastSync: Now - TimeSpan.FromMinutes(61)) };

        Assert.Single(AutoSyncScheduler.Due(jobs, TimeSpan.FromHours(1), Now));
    }

    [Fact]
    public void SourcesWithoutTheFlagAreNeverPicked()
    {
        var jobs = new[] { Job("off", autoSync: false), Job("on") };

        Assert.Equal(["on"], AutoSyncScheduler.Due(jobs, TimeSpan.FromHours(1), Now)
            .Select(job => job.SourceName));
    }

    [Fact]
    public void AZeroIntervalTurnsAutoSyncOff()
    {
        Assert.Empty(AutoSyncScheduler.Due([Job("mix")], TimeSpan.Zero, Now));
        Assert.Empty(AutoSyncScheduler.Due([Job("mix")], TimeSpan.FromMinutes(-5), Now));
    }

    [Fact]
    public void ASourceThatIsQueuedOrLoadedIsLeftAlone()
    {
        var jobs = new[] { Job("queued"), Job("free") };
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "https://open.spotify.com/playlist/queued",
        };

        Assert.Equal(["free"], AutoSyncScheduler.Due(jobs, TimeSpan.FromHours(1), Now, busy)
            .Select(job => job.SourceName));
    }

    [Fact]
    public void AnImportedManifestIsNeverSyncedUnattended()
    {
        // Its "source" is a local file that may be gone or on a drive that is not plugged in.
        var jobs = new[] { Job("manifest", sourceType: "import") };

        Assert.False(AutoSyncScheduler.CanAutoSync(jobs[0]));
        Assert.Empty(AutoSyncScheduler.Due(jobs, TimeSpan.FromHours(1), Now));
    }

    [Fact]
    public void ASourceWhoseOutputFolderIsGoneIsSkipped()
    {
        // Downloading would recreate the tree somewhere the user cannot see.
        var jobs = new[] { Job("away", outputDirectory: Path.Combine(_root, "unplugged")) };

        Assert.False(AutoSyncScheduler.CanAutoSync(jobs[0]));
        Assert.Empty(AutoSyncScheduler.Due(jobs, TimeSpan.FromHours(1), Now));
    }

    [Fact]
    public void TheLongestWaitingSourcesGoFirstAndOneTickIsBounded()
    {
        var jobs = new[]
        {
            Job("recent", lastSync: Now - TimeSpan.FromHours(2)),
            Job("older", lastSync: Now - TimeSpan.FromHours(5)),
            Job("oldest", lastSync: Now - TimeSpan.FromDays(3)),
            Job("never"),
            Job("also-old", lastSync: Now - TimeSpan.FromHours(9)),
        };

        var due = AutoSyncScheduler.Due(jobs, TimeSpan.FromHours(1), Now);

        Assert.Equal(AutoSyncScheduler.MaxPerTick, due.Count);
        Assert.Equal(["never", "oldest", "also-old"], due.Select(job => job.SourceName));
    }

    [Fact]
    public void AClockThatMovedBackwardsDoesNotParkASource()
    {
        // A restored machine or a time zone change must not stall syncing until the
        // original due time comes round again.
        var jobs = new[] { Job("mix", lastSync: Now + TimeSpan.FromDays(2)) };

        Assert.Single(AutoSyncScheduler.Due(jobs, TimeSpan.FromHours(1), Now));
    }

    [Fact]
    public void ASourceWithoutAUrlIsIgnored()
    {
        var jobs = new[] { new SavedJob { SourceUrl = "  ", AutoSync = true } };

        Assert.Empty(AutoSyncScheduler.Due(jobs, TimeSpan.FromHours(1), Now));
    }

    [Fact]
    public void MarkCheckedStampsTheEntryOnDiskAndStopsItBeingDue()
    {
        var job = Job("mix");
        Store.Save(job);

        Assert.True(AutoSyncScheduler.MarkChecked(Store, job.SourceUrl, Now));

        var reloaded = Store.Load(job.SourceUrl)!;
        Assert.Equal(Now, reloaded.LastAutoSyncUtc);
        Assert.True(reloaded.AutoSync);
        Assert.Empty(AutoSyncScheduler.Due([reloaded], TimeSpan.FromHours(1), Now));
    }

    [Fact]
    public void MarkCheckedLeavesTheJobsOwnUpdatedTimeAlone()
    {
        var job = Job("mix");
        Store.Save(job);
        var updatedAt = Store.Load(job.SourceUrl)!.UpdatedAt;

        AutoSyncScheduler.MarkChecked(Store, job.SourceUrl, Now);

        // A check that found nothing is not a change to the job, so it must not float to
        // the top of the library list or claim a fresh update.
        Assert.Equal(updatedAt, Store.Load(job.SourceUrl)!.UpdatedAt);
    }

    [Fact]
    public void MarkCheckedWritesTheEntryAsItIsOnDiskNow()
    {
        var job = Job("mix");
        Store.Save(job);

        // The library changed after the scheduler read it.
        var meanwhile = Store.Load(job.SourceUrl)!;
        meanwhile.Tracks.Add(new SavedTrack { Id = "new", IsSelected = true });
        Store.Save(meanwhile);

        AutoSyncScheduler.MarkChecked(Store, job.SourceUrl, Now);

        var reloaded = Store.Load(job.SourceUrl)!;
        Assert.Equal(Now, reloaded.LastAutoSyncUtc);
        Assert.Single(reloaded.Tracks);
    }

    [Fact]
    public void MarkCheckedReportsFailureForAJobThatIsGone()
    {
        var job = Job("mix");

        Assert.False(AutoSyncScheduler.MarkChecked(Store, job.SourceUrl, Now));
        Assert.Null(Store.Load(job.SourceUrl));
    }

    [Fact]
    public void AFullyDownloadedSourceCanStillBeQueuedForAutoSync()
    {
        // The steady state of a synced source: every track complete. The queue has to take
        // it, because whether there is anything new is only known once it is re-resolved.
        var job = Job("mix");
        job.Tracks.Add(new SavedTrack { Id = "done", IsComplete = true, IsSelected = false });
        var queue = new DownloadQueue();
        var queued = new QueuedJob(
            job.SourceName,
            job.SourceUrl,
            job.SourceType,
            job.OutputDirectory,
            QueuedJobSettings.From(new AppSettings()),
            job)
        {
            ResolveSelection = true,
        };

        queue.Enqueue(queued);

        Assert.Equal(0, queued.SelectedCount);
        Assert.Single(queue.Items);
        // The same job without the flag is still refused, so nothing else got looser.
        Assert.Throws<ArgumentException>(() => queue.Enqueue(queued with { ResolveSelection = false }));
    }

    [Fact]
    public void SavingProgressKeepsTheKeepInSyncFlagAndTheLastCheck()
    {
        var job = Job("mix");
        Store.Save(job);
        AutoSyncScheduler.MarkChecked(Store, job.SourceUrl, Now);

        // What the download grid writes back knows nothing about either field.
        Store.SaveProgress(SavedJobSnapshot.Create(
            job.SourceUrl,
            job.SourceName,
            job.SourceType,
            job.OutputDirectory,
            [new TrackItem { Id = "one", Status = "Done" }]));

        var reloaded = Store.Load(job.SourceUrl)!;
        Assert.True(reloaded.AutoSync);
        Assert.Equal(Now, reloaded.LastAutoSyncUtc);
        Assert.Single(reloaded.Tracks);
    }

    [Fact]
    public void TurningKeepInSyncOffIsNotUndoneBySavingProgress()
    {
        var job = Job("mix", autoSync: false);
        Store.Save(job);

        Store.SaveProgress(SavedJobSnapshot.Create(
            job.SourceUrl, job.SourceName, job.SourceType, job.OutputDirectory, []));

        Assert.False(Store.Load(job.SourceUrl)!.AutoSync);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
