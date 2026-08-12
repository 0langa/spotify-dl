using System.IO;
using System.Security;
using PlaylistDl.App.Models;

namespace PlaylistDl.App.Services;

/// <summary>What the disk says about one track the library believes is downloaded.</summary>
public enum TrackFileState
{
    Present,
    Missing,
    Empty,
    Moved,

    /// <summary>The folder that should hold the file is not available right now.</summary>
    Unreachable,
}

public sealed record TrackFileReport(SavedTrack Track, TrackFileState State, string? FoundPath);

/// <summary>Health of one saved job's downloaded files.</summary>
public sealed record LibraryHealthReport(
    SavedJob Job,
    IReadOnlyList<TrackFileReport> Tracks,
    bool ScanComplete)
{
    public int Present => Tracks.Count(track => track.State == TrackFileState.Present);

    public int Missing => Tracks.Count(track =>
        track.State is TrackFileState.Missing or TrackFileState.Empty);

    public int Moved => Tracks.Count(track => track.State == TrackFileState.Moved);

    public int Unreachable => Tracks.Count(track => track.State == TrackFileState.Unreachable);

    public bool IsHealthy => Missing == 0 && Moved == 0 && Unreachable == 0;

    /// <summary>
    /// Whether reopening the missing tracks is safe. An unreachable folder or a folder that
    /// could only be read in part makes every unseen file look deleted, and reopening those
    /// tracks would throw away paths to files that are still there.
    /// </summary>
    public bool CanReopenMissing => ScanComplete && Unreachable == 0;

    public string Summary
    {
        get
        {
            if (Tracks.Count == 0)
            {
                return "Nothing downloaded yet";
            }

            if (Unreachable > 0)
            {
                return $"{Unreachable} files are in a folder that is not available right now";
            }

            var parts = new List<string> { $"{Present} present" };
            if (Missing > 0)
            {
                parts.Add($"{Missing} missing or empty");
            }
            if (Moved > 0)
            {
                parts.Add($"{Moved} moved");
            }

            return IsHealthy && ScanComplete
                ? $"{Present} files present"
                : string.Join(" · ", parts) + (ScanComplete ? string.Empty : " (folder read only in part)");
        }
    }
}

/// <summary>
/// Compares what the job library believes it downloaded against what is on disk.
/// </summary>
/// <remarks>
/// The library records a path once and trusts it forever. Moving the music folder,
/// deleting a few tracks, or letting another tool reorganize files leaves those tracks
/// marked complete, so Resume and Sync skip them and cross-job reuse points at files
/// that are gone.
/// </remarks>
public sealed class LibraryHealthScanner
{
    private readonly LibraryStore _library;

    public LibraryHealthScanner(LibraryStore library) => _library = library;

    /// <summary>Checks every saved job against its own output folder.</summary>
    public IReadOnlyList<LibraryHealthReport> ScanAll() => [.. _library.List().Select(job => Scan(job))];

    /// <summary>
    /// Classifies every completed track of one job. A missing file whose name turns up
    /// exactly once beneath <paramref name="searchRoot"/> (the job's output folder unless
    /// another root is given) and that no other saved job claims counts as moved.
    /// </summary>
    public LibraryHealthReport Scan(SavedJob job, string? searchRoot = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        var root = string.IsNullOrWhiteSpace(searchRoot) ? job.OutputDirectory : searchRoot;
        var (byName, scanComplete) = BuildNameIndex(root);
        var claimed = ClaimedPaths();
        var adopted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reports = new List<TrackFileReport>();
        foreach (var track in job.Tracks)
        {
            if (!track.IsComplete)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(track.OutputPath))
            {
                // Complete with no file recorded is exactly the damage this check exists for.
                reports.Add(new TrackFileReport(track, TrackFileState.Missing, null));
                continue;
            }

            reports.Add(Classify(track, byName, claimed, adopted));
        }

        return new LibraryHealthReport(job, reports, scanComplete);
    }

    private TrackFileReport Classify(
        SavedTrack track,
        IReadOnlyDictionary<string, string?>? byName,
        IReadOnlySet<string> claimed,
        HashSet<string> adopted)
    {
        var path = track.OutputPath!;
        try
        {
            var file = new FileInfo(path);
            if (file.Exists)
            {
                return file.Length == 0
                    ? new TrackFileReport(track, TrackFileState.Empty, path)
                    : new TrackFileReport(track, TrackFileState.Present, path);
            }

            // FileInfo.Exists is false for an unplugged drive, an offline share, and a
            // renamed parent as well. Those files are not gone, so they must not be
            // reported as deleted.
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                return new TrackFileReport(track, TrackFileState.Unreachable, null);
            }

            var candidate = FindMoved(path, byName, claimed, adopted);
            if (candidate is not null)
            {
                adopted.Add(candidate);
                return new TrackFileReport(track, TrackFileState.Moved, candidate);
            }

            return new TrackFileReport(track, TrackFileState.Missing, null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            // An unreadable path is as good as missing for everything the user can do next.
            return new TrackFileReport(track, TrackFileState.Missing, null);
        }
    }

    /// <summary>
    /// Finds the one file that can safely be treated as this track's, or null.
    /// </summary>
    /// <remarks>
    /// File names collide by design: every album has a "01 - Intro.mp3" and jobs share one
    /// output root, so a name that occurs more than once, that another saved job still
    /// points at, or that this scan already handed to another track is refused rather than
    /// guessed at.
    /// </remarks>
    private static string? FindMoved(
        string path,
        IReadOnlyDictionary<string, string?>? byName,
        IReadOnlySet<string> claimed,
        IReadOnlySet<string> adopted)
    {
        var name = Path.GetFileName(path);
        if (byName is null || name.Length == 0 ||
            !byName.TryGetValue(name, out var candidate) || candidate is null)
        {
            return null;
        }

        if (claimed.Contains(candidate) || adopted.Contains(candidate))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(candidate);
            return file.Exists && file.Length > 0 ? candidate : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Every file path the library still points at, so no file is adopted twice.</summary>
    private IReadOnlySet<string> ClaimedPaths()
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in _library.List())
        {
            foreach (var track in job.Tracks)
            {
                if (track.IsComplete && !string.IsNullOrWhiteSpace(track.OutputPath))
                {
                    claimed.Add(track.OutputPath);
                }
            }
        }

        return claimed;
    }

    /// <summary>Indexes files under a folder by name; a repeated name is stored as ambiguous.</summary>
    /// <returns>The index, and whether the whole folder could be read.</returns>
    private static (Dictionary<string, string?>? Index, bool Complete) BuildNameIndex(string? searchRoot)
    {
        if (string.IsNullOrWhiteSpace(searchRoot))
        {
            return (null, true);
        }

        if (!Directory.Exists(searchRoot))
        {
            // The folder itself is gone; its tracks are classified unreachable below.
            return (null, true);
        }

        var index = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                // A folder that cannot be read must be reported, not silently skipped:
                // an unseen file would otherwise be offered for deletion as missing.
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.System,
            };
            foreach (var file in Directory.EnumerateFiles(searchRoot, "*", options))
            {
                var name = Path.GetFileName(file);
                if (!index.TryAdd(name, file))
                {
                    index[name] = null;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // A partial index still relocates what it found, but the caller must know that
            // the rest of the folder was never seen.
            complete = false;
        }

        return (index, complete);
    }

    /// <summary>Points the library at files that turned up in a new location.</summary>
    /// <returns>How many track paths were rewritten.</returns>
    public int Relocate(LibraryHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var moved = report.Tracks
            .Where(track => track.State == TrackFileState.Moved && track.FoundPath is not null)
            .ToList();
        if (moved.Count == 0)
        {
            return 0;
        }

        return Apply(report.Job.SourceUrl, moved, (track, found) => track.OutputPath = found);
    }

    /// <summary>
    /// Clears the completed flag of tracks whose file is gone, so Resume and Sync offer
    /// them again instead of skipping them forever. An empty leftover file is deleted,
    /// because the downloader skips a track whose output file already exists.
    /// </summary>
    /// <returns>How many tracks were reopened.</returns>
    public int ForgetMissing(LibraryHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.CanReopenMissing)
        {
            // The scan could not see the whole folder, so "missing" is not established.
            return 0;
        }

        var gone = report.Tracks
            .Where(track => track.State is TrackFileState.Missing or TrackFileState.Empty)
            .ToList();
        if (gone.Count == 0)
        {
            return 0;
        }

        var reopened = Apply(report.Job.SourceUrl, gone, (track, _) =>
        {
            track.IsComplete = false;
            track.IsSelected = true;
            track.OutputPath = null;
        });

        if (reopened > 0)
        {
            foreach (var track in gone.Where(track => track.State == TrackFileState.Empty))
            {
                RemoveEmptyFile(track.FoundPath);
            }
        }

        return reopened;
    }

    /// <summary>
    /// Applies one repair to the job as it is on disk right now.
    /// </summary>
    /// <remarks>
    /// The report holds the job as it was when the scan started, which may be minutes old
    /// and may have been deleted or rewritten since. Writing that snapshot back would undo
    /// whatever landed in between, so the fresh entry is loaded and only the repaired
    /// tracks are touched.
    /// </remarks>
    private int Apply(
        string sourceUrl,
        IEnumerable<TrackFileReport> repairs,
        Action<SavedTrack, string?> repair)
    {
        var fresh = _library.Load(sourceUrl);
        if (fresh is null)
        {
            // The job was deleted while the check was running; do not recreate it.
            return 0;
        }

        var byId = new Dictionary<string, SavedTrack>(StringComparer.Ordinal);
        var byUrl = new Dictionary<string, SavedTrack>(StringComparer.Ordinal);
        var byPath = new Dictionary<string, SavedTrack>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in fresh.Tracks)
        {
            if (!string.IsNullOrEmpty(track.Id))
            {
                byId.TryAdd(track.Id, track);
            }
            if (!string.IsNullOrEmpty(track.SpotifyUrl))
            {
                byUrl.TryAdd(track.SpotifyUrl, track);
            }
            if (!string.IsNullOrWhiteSpace(track.OutputPath))
            {
                byPath.TryAdd(track.OutputPath, track);
            }
        }

        var applied = 0;
        foreach (var report in repairs)
        {
            var saved = report.Track;
            SavedTrack? current = null;
            if (!string.IsNullOrEmpty(saved.Id))
            {
                byId.TryGetValue(saved.Id, out current);
            }
            if (current is null && !string.IsNullOrEmpty(saved.SpotifyUrl))
            {
                byUrl.TryGetValue(saved.SpotifyUrl, out current);
            }
            if (current is null && !string.IsNullOrWhiteSpace(saved.OutputPath))
            {
                byPath.TryGetValue(saved.OutputPath, out current);
            }

            // A track that finished again while the check ran is left alone unless its
            // file is the one the repair is about.
            if (current is null || !current.IsComplete ||
                !string.Equals(current.OutputPath, saved.OutputPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            repair(current, report.FoundPath);
            applied++;
        }

        if (applied > 0)
        {
            _library.Save(fresh);
        }

        return applied;
    }

    private static void RemoveEmptyFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length == 0)
            {
                file.Delete();
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Leaving the empty file behind only means the check reports it again.
        }
    }
}
