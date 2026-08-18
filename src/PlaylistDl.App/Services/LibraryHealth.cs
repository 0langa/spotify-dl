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
    bool ScanComplete,
    bool RootAvailable = true,
    string? Root = null)
{
    public int Present => Tracks.Count(track => track.State == TrackFileState.Present);

    public int Missing => Tracks.Count(track =>
        track.State is TrackFileState.Missing or TrackFileState.Empty);

    public int Moved => Tracks.Count(track => track.State == TrackFileState.Moved);

    public int Unreachable => Tracks.Count(track => track.State == TrackFileState.Unreachable);

    public bool IsHealthy => Missing == 0 && Moved == 0 && Unreachable == 0;

    /// <summary>
    /// Whether reopening the missing tracks is safe. An output folder that is offline or
    /// that could only be read in part makes every unseen file look deleted, and reopening
    /// those tracks would throw away paths to files that are still there. A single track
    /// pointing outside a readable output folder — cross-job reuse under the "skip"
    /// duplicate policy records the other job's path — says nothing about the rest, so it
    /// is left unreachable on its own instead of freezing the whole job.
    /// </summary>
    public bool CanReopenMissing => ScanComplete && (RootAvailable || Unreachable == 0);

    public string Summary
    {
        get
        {
            if (Tracks.Count == 0)
            {
                return "Nothing downloaded yet";
            }

            if (IsHealthy && ScanComplete)
            {
                return Files(Present) + " present";
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
            if (Unreachable > 0)
            {
                parts.Add($"{Unreachable} in a folder that is not available");
            }

            return string.Join(" · ", parts) +
                (ScanComplete ? string.Empty : " (folder read only in part)");
        }
    }

    /// <summary>"1 file" or "3 files"; the one-file case is the common one for a repair.</summary>
    public static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    /// <summary>"1 track" or "3 tracks".</summary>
    public static string Tracked(int count) => count == 1 ? "1 track" : $"{count} tracks";
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
    /// <summary>One output folder as this scan sees it.</summary>
    private sealed record FolderIndex(
        Dictionary<string, string?>? ByName,
        bool Complete,
        bool RootAvailable,
        string? Root);

    private readonly LibraryStore _library;
    private readonly Func<string, FolderWalk> _walk;

    public LibraryHealthScanner(LibraryStore library)
        : this(library, WalkFiles)
    {
    }

    /// <summary>Test seam for folders that cannot be read in full.</summary>
    internal LibraryHealthScanner(LibraryStore library, Func<string, FolderWalk> walk)
    {
        _library = library;
        _walk = walk;
    }

    /// <summary>Checks every saved job against its own output folder.</summary>
    /// <remarks>
    /// The claimed paths and the folder indexes are built once and shared, because saved
    /// jobs normally sit under one music folder and would otherwise be walked once per job.
    /// </remarks>
    public IReadOnlyList<LibraryHealthReport> ScanAll()
    {
        var jobs = _library.List();
        var claimed = ClaimedPaths(jobs);
        var folders = new Dictionary<string, FolderIndex>(StringComparer.OrdinalIgnoreCase);
        // One adopted set for the whole run: a file one job takes is not free for the next.
        var adopted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return [.. jobs.Select(job => Scan(job, null, claimed, folders, adopted))];
    }

    /// <summary>
    /// Classifies every completed track of one job. A missing file whose name turns up
    /// exactly once beneath <paramref name="searchRoot"/> (the job's output folder unless
    /// another root is given) and that no other saved job claims counts as moved.
    /// </summary>
    public LibraryHealthReport Scan(SavedJob job, string? searchRoot = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        return Scan(
            job,
            searchRoot,
            ClaimedPaths(_library.List()),
            new Dictionary<string, FolderIndex>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private LibraryHealthReport Scan(
        SavedJob job,
        string? searchRoot,
        IReadOnlySet<string> claimed,
        Dictionary<string, FolderIndex> folders,
        HashSet<string> adopted)
    {
        var root = string.IsNullOrWhiteSpace(searchRoot) ? job.OutputDirectory : searchRoot;
        var folder = IndexFor(root, folders);
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

            reports.Add(Classify(track, folder, job.OutputDirectory, claimed, adopted));
        }

        return new LibraryHealthReport(
            job, reports, folder.Complete, folder.RootAvailable, folder.Root);
    }

    private FolderIndex IndexFor(string? root, Dictionary<string, FolderIndex> folders)
    {
        var key = string.IsNullOrWhiteSpace(root) ? string.Empty : Canonical(root);
        if (folders.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var folder = BuildNameIndex(root);
        folders[key] = folder;
        return folder;
    }

    private TrackFileReport Classify(
        SavedTrack track,
        FolderIndex folder,
        string? home,
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
            // renamed parent as well. When the folder the file was recorded in is not
            // available, the file is not gone, so it must not be reported as deleted.
            // Inside a folder that reads fine, a vanished subfolder is an ordinary
            // reorganization.
            var directory = Path.GetDirectoryName(path);
            var parentMissing = !string.IsNullOrEmpty(directory) && !Directory.Exists(directory);
            var unreachable = parentMissing &&
                (!folder.RootAvailable || !IsUnder(path, folder.Root));

            // A file that can be found in the folder being searched is repairable no matter
            // what happened to the path it was recorded under, which is what makes a moved
            // music folder fixable at all — but only for a file the job recorded in its own
            // output folder. A file recorded elsewhere, which cross-job reuse under the
            // "skip" duplicate policy does, whose folder is merely offline is left
            // unreachable: a same-named stray under this root would otherwise be adopted
            // over a file that is still there.
            if (!unreachable || IsUnder(path, home))
            {
                var candidate = FindMoved(path, folder, claimed, adopted);
                if (candidate is not null)
                {
                    adopted.Add(candidate);
                    return new TrackFileReport(track, TrackFileState.Moved, candidate);
                }
            }

            if (unreachable)
            {
                return new TrackFileReport(track, TrackFileState.Unreachable, null);
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
    /// guessed at. A folder that could not be read in full proves no name unique, so it
    /// adopts nothing at all.
    /// </remarks>
    private static string? FindMoved(
        string path,
        FolderIndex folder,
        IReadOnlySet<string> claimed,
        IReadOnlySet<string> adopted)
    {
        var name = Path.GetFileName(path);
        if (!folder.Complete || folder.ByName is null || name.Length == 0 ||
            !folder.ByName.TryGetValue(name, out var candidate) || candidate is null)
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
    private static IReadOnlySet<string> ClaimedPaths(IEnumerable<SavedJob> jobs)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            foreach (var track in job.Tracks)
            {
                if (track.IsComplete && !string.IsNullOrWhiteSpace(track.OutputPath))
                {
                    claimed.Add(Canonical(track.OutputPath));
                }
            }
        }

        return claimed;
    }

    /// <summary>What one folder walk saw, and whether it saw all of it.</summary>
    internal sealed record FolderWalk(List<string> Files, bool Complete);

    /// <summary>Walks one folder tree, one directory at a time.</summary>
    /// <remarks>
    /// The recursive enumeration overload is not used: capping its recursion depth would
    /// drop files without saying so, and leaving it uncapped follows junctions, which .NET
    /// does not check for cycles. Reparse points are skipped instead and reported as an
    /// incomplete walk, so the caller never treats a folder it did not fully see as one
    /// where a missing file is proof of deletion.
    /// </remarks>
    internal static FolderWalk WalkFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            // A folder that cannot be read must be reported, not silently skipped: an
            // unseen file would otherwise be offered for deletion as missing.
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.None,
        };
        var files = new List<string>();
        var complete = true;
        var pending = new Stack<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Canonical(root) };
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            files.AddRange(Directory.EnumerateFiles(directory, "*", options));
            foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
            {
                if (new DirectoryInfo(child).LinkTarget is not null)
                {
                    // Following it risks a cycle, so this part of the tree stays unseen.
                    complete = false;
                    continue;
                }

                if (seen.Add(Canonical(child)))
                {
                    pending.Push(child);
                }
            }
        }

        return new FolderWalk(files, complete);
    }

    /// <summary>Indexes files under a folder by name; a repeated name is stored as ambiguous.</summary>
    private FolderIndex BuildNameIndex(string? searchRoot)
    {
        if (string.IsNullOrWhiteSpace(searchRoot))
        {
            return new FolderIndex(null, true, false, null);
        }

        var root = Canonical(searchRoot);
        if (!Directory.Exists(root))
        {
            // The folder itself is gone; its tracks are classified unreachable below.
            return new FolderIndex(null, true, false, root);
        }

        var index = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        try
        {
            var walk = _walk(root);
            complete = walk.Complete;
            foreach (var file in walk.Files)
            {
                var name = Path.GetFileName(file);
                if (!index.TryAdd(name, Canonical(file)))
                {
                    index[name] = null;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Nothing is adopted and nothing is reopened from a partial read; the caller
            // reports that the folder was only seen in part.
            complete = false;
        }

        return new FolderIndex(index, complete, true, root);
    }

    /// <summary>Points the library at files that turned up in a new location.</summary>
    /// <returns>How many track paths were rewritten.</returns>
    public int Relocate(LibraryHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.ScanComplete)
        {
            return 0;
        }

        // The scan may be minutes old, so the file is checked again before the library is
        // pointed at it.
        var moved = report.Tracks
            .Where(track => track.State == TrackFileState.Moved && StillUsable(track.FoundPath))
            .ToList();
        if (moved.Count == 0)
        {
            return 0;
        }

        return Apply(
            report.Job.SourceUrl,
            moved,
            (track, found) => track.OutputPath = found,
            job =>
            {
                // The whole folder was moved and found again somewhere else: the job has to
                // point at the new one, or the next download writes to a folder that is gone.
                if (report.Root is not null && !Directory.Exists(job.OutputDirectory))
                {
                    job.OutputDirectory = report.Root;
                }
            }).Count;
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

        // The scan may be minutes old and a probe can fail for a moment, so every file is
        // checked again right before its only record of the path is dropped.
        var root = report.Root is not null && Directory.Exists(report.Root) ? report.Root : null;
        var gone = report.Tracks
            .Where(track => track.State is TrackFileState.Missing or TrackFileState.Empty)
            .Where(track => ConfirmedGone(track.Track.OutputPath, root))
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

        // Only the leftovers of tracks that were really reopened: a track the repair left
        // alone still points at its file.
        foreach (var track in reopened.Where(track => track.State == TrackFileState.Empty))
        {
            RemoveEmptyFile(track.FoundPath);
        }

        return reopened.Count;
    }

    /// <summary>Whether the file is provably not usable right now.</summary>
    /// <remarks>
    /// A file whose own folder is gone as well may only be offline, unless the output
    /// folder the scan walked is right there: then the folder was reorganized and the
    /// recorded path really is dead. This must agree with how the scan classified it.
    /// </remarks>
    private static bool ConfirmedGone(string? path, string? availableRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        try
        {
            var file = new FileInfo(path);
            if (file.Exists)
            {
                return file.Length == 0;
            }

            var directory = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(directory) || Directory.Exists(directory) ||
                IsUnder(path, availableRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            return false;
        }
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
    private List<TrackFileReport> Apply(
        string sourceUrl,
        IEnumerable<TrackFileReport> repairs,
        Action<SavedTrack, string?> repair,
        Action<SavedJob>? adjustJob = null)
    {
        var applied = new List<TrackFileReport>();
        var fresh = _library.Load(sourceUrl);
        if (fresh is null)
        {
            // The job was deleted while the check was running; do not recreate it.
            return applied;
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
                byPath.TryAdd(Canonical(track.OutputPath), track);
            }
        }

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
                byPath.TryGetValue(Canonical(saved.OutputPath), out current);
            }

            // A track that finished again while the check ran is left alone unless its
            // file is the one the repair is about.
            if (current is null || !current.IsComplete || !SamePath(current.OutputPath, saved.OutputPath))
            {
                continue;
            }

            repair(current, report.FoundPath);
            applied.Add(report);
        }

        if (applied.Count > 0)
        {
            adjustJob?.Invoke(fresh);
            _library.Save(fresh);
        }

        return applied;
    }

    /// <summary>Whether a file the scan found is still there and still has content.</summary>
    private static bool StillUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length > 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            return false;
        }
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

    /// <summary>
    /// The library stores whatever path the backend produced and the folder is enumerated
    /// from whatever the user typed, so both are compared in one canonical form.
    /// </summary>
    private static string Canonical(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or
            IOException or SecurityException)
        {
            return path;
        }
    }

    private static bool SamePath(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)
            ? string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right)
            : string.Equals(Canonical(left), Canonical(right), StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnder(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var full = Canonical(path);
        var prefix = Canonical(root);
        if (Path.EndsInDirectorySeparator(prefix))
        {
            // A drive root such as "D:\" keeps its separator; appending another matches nothing.
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return full.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(prefix + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
