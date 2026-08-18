using PlaylistDl.App.Models;

namespace PlaylistDl.App.Services;

/// <summary>
/// Decides which saved jobs are due for an unattended sync.
/// </summary>
/// <remarks>
/// The scheduler owns the decision only; the work itself is the ordinary queue, so an
/// auto-sync run is exactly the run the user would start by hand. It never fires while
/// anything else is running, because a background re-resolve during a download would
/// compete for the same backend session.
/// </remarks>
public static class AutoSyncScheduler
{
    /// <summary>How many sources one tick may enqueue, so a stale library is not a burst.</summary>
    public const int MaxPerTick = 3;

    /// <summary>Intervals the user can pick, in minutes; 0 turns auto-sync off.</summary>
    public static readonly IReadOnlyList<int> Intervals = [0, 30, 60, 360, 1440];

    /// <summary>
    /// The jobs that should be synced now, oldest check first.
    /// </summary>
    /// <param name="jobs">Every saved job.</param>
    /// <param name="interval">How long a job may go unchecked; zero or less means off.</param>
    /// <param name="now">Current time, passed in so the decision is testable.</param>
    /// <param name="busySources">
    /// Sources that must be left alone: the one loaded in the window and everything already
    /// queued, so a source is never enqueued twice.
    /// </param>
    public static IReadOnlyList<SavedJob> Due(
        IEnumerable<SavedJob> jobs,
        TimeSpan interval,
        DateTimeOffset now,
        IReadOnlySet<string>? busySources = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (interval <= TimeSpan.Zero)
        {
            return [];
        }

        var busy = busySources ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. jobs
                .Where(job => job.AutoSync && !string.IsNullOrWhiteSpace(job.SourceUrl))
                .Where(job => !busy.Contains(job.SourceUrl))
                .Where(job => IsDue(job, interval, now))
                // A job that has never been checked sorts first, then the longest waiting.
                .OrderBy(job => job.LastAutoSyncUtc ?? DateTimeOffset.MinValue)
                .Take(MaxPerTick),
        ];
    }

    private static bool IsDue(SavedJob job, TimeSpan interval, DateTimeOffset now)
    {
        if (job.LastAutoSyncUtc is not { } last)
        {
            return true;
        }

        // A clock that moved backwards (time zone change, a restored machine) would
        // otherwise park a job until the original due time comes round again.
        return last > now || now - last >= interval;
    }

    /// <summary>Records that a source was checked, whatever the sync then found.</summary>
    public static void MarkChecked(LibraryStore library, string sourceUrl, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (library.Load(sourceUrl) is not { } job)
        {
            return;
        }

        // Written on the entry as it is on disk right now: the sync itself rewrites the
        // same file when it finishes, and the snapshot this started from may be older.
        job.LastAutoSyncUtc = now;
        library.Save(job);
    }
}
