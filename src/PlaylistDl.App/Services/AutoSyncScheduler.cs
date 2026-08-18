using System.IO;
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

    /// <summary>
    /// Whether a source can be checked unattended at all.
    /// </summary>
    /// <remarks>
    /// An imported manifest is a local file that may be gone or on a drive that is not
    /// plugged in. A free-text search is a picker, not a list: re-running it later returns
    /// whatever the provider ranks highest that day, which is not what the user chose. And
    /// a job whose output folder has disappeared would have its tree recreated somewhere
    /// the user cannot see. None of the three is safe to act on with nobody watching.
    /// </remarks>
    public static bool CanAutoSync(SavedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(job.SourceUrl) ||
            string.Equals(job.SourceType, "import", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(job.SourceType, "search", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(job.OutputDirectory) && FolderExists(job.OutputDirectory);
    }

    private static bool FolderExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

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
                .Where(job => job.AutoSync && CanAutoSync(job))
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
    /// <returns>
    /// False when the entry is gone or could not be written. The caller must not queue a
    /// source it could not stamp: it would come back due on every tick.
    /// </returns>
    public static bool MarkChecked(LibraryStore library, string sourceUrl, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(library);
        // Stamped on the entry as it is on disk right now, and without touching UpdatedAt:
        // the sync itself rewrites the same file when it finishes.
        return library.RecordAutoSyncCheck(sourceUrl, now);
    }
}
