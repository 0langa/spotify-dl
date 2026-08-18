using PlaylistDl.App.Models;
using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class AutoSyncSchedulerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "playlistdl-tests", Guid.NewGuid().ToString("N"));

    private LibraryStore Store => new(_root);

    private static SavedJob Job(
        string name,
        bool autoSync = true,
        DateTimeOffset? lastSync = null) => new()
        {
            SourceUrl = $"https://open.spotify.com/playlist/{name}",
            SourceName = name,
            OutputDirectory = @"C:\music",
            AutoSync = autoSync,
            LastAutoSyncUtc = lastSync,
        };

    [Fact]
    public void ASourceThatWasNeverCheckedIsDue()
    {
        var due = AutoSyncScheduler.Due([Job("mix")], TimeSpan.FromHours(1), Now);

        Assert.Single(due);
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

        AutoSyncScheduler.MarkChecked(Store, job.SourceUrl, Now);

        var reloaded = Store.Load(job.SourceUrl)!;
        Assert.Equal(Now, reloaded.LastAutoSyncUtc);
        Assert.True(reloaded.AutoSync);
        Assert.Empty(AutoSyncScheduler.Due([reloaded], TimeSpan.FromHours(1), Now));
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
    public void MarkCheckedDoesNotRecreateADeletedJob()
    {
        var job = Job("mix");

        AutoSyncScheduler.MarkChecked(Store, job.SourceUrl, Now);

        Assert.Null(Store.Load(job.SourceUrl));
    }

    [Fact]
    public void EveryOfferedIntervalIsUsable()
    {
        // The settings list and the scheduler must agree about what "off" means.
        Assert.Equal(0, AutoSyncScheduler.Intervals[0]);
        Assert.All(AutoSyncScheduler.Intervals.Skip(1), minutes => Assert.True(minutes > 0));
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
