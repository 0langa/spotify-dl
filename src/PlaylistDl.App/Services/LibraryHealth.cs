using System.IO;
using PlaylistDl.App.Models;

namespace PlaylistDl.App.Services;

/// <summary>What the disk says about one track the library believes is downloaded.</summary>
public enum TrackFileState
{
    Present,
    Missing,
    Empty,
    Moved,
}

public sealed record TrackFileReport(SavedTrack Track, TrackFileState State, string? FoundPath);

/// <summary>Health of one saved job's downloaded files.</summary>
public sealed record LibraryHealthReport(
    SavedJob Job,
    IReadOnlyList<TrackFileReport> Tracks)
{
    public int Present => Tracks.Count(track => track.State == TrackFileState.Present);

    public int Missing => Tracks.Count(track =>
        track.State is TrackFileState.Missing or TrackFileState.Empty);

    public int Moved => Tracks.Count(track => track.State == TrackFileState.Moved);

    public bool IsHealthy => Missing == 0 && Moved == 0;

    public string Summary => Tracks.Count == 0
        ? "Nothing downloaded yet"
        : IsHealthy
            ? $"{Present} files present"
            : $"{Present} present · {Missing} missing · {Moved} moved";
}

/// <summary>
/// Compares what the job library believes it downloaded against what is on disk.
/// </summary>
/// <remarks>
/// The library records a path once and trusts it forever. Moving the music folder,
/// deleting a few tracks, or letting another tool rename files leaves those tracks
/// marked complete, so Resume and Sync skip them and cross-job reuse points at files
/// that are gone.
/// </remarks>
public sealed class LibraryHealthScanner
{
    private readonly LibraryStore _library;

    public LibraryHealthScanner(LibraryStore library) => _library = library;

    public IReadOnlyList<LibraryHealthReport> ScanAll(string? searchRoot = null) =>
        [.. _library.List().Select(job => Scan(job, searchRoot))];

    /// <summary>
    /// Classifies every completed track of one job. When <paramref name="searchRoot"/> is
    /// given, a missing file whose name turns up beneath it counts as moved.
    /// </summary>
    public LibraryHealthReport Scan(SavedJob job, string? searchRoot = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        var byName = BuildNameIndex(searchRoot);
        var reports = new List<TrackFileReport>();
        foreach (var track in job.Tracks)
        {
            if (!track.IsComplete || string.IsNullOrWhiteSpace(track.OutputPath))
            {
                continue;
            }

            reports.Add(Classify(track, byName));
        }

        return new LibraryHealthReport(job, reports);
    }

    private static TrackFileReport Classify(
        SavedTrack track,
        IReadOnlyDictionary<string, string>? byName)
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

            var name = Path.GetFileName(path);
            if (byName is not null && name.Length > 0 && byName.TryGetValue(name, out var found))
            {
                return new TrackFileReport(track, TrackFileState.Moved, found);
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

    /// <summary>Indexes audio files under a folder by name so moved files can be found.</summary>
    private static Dictionary<string, string>? BuildNameIndex(string? searchRoot)
    {
        if (string.IsNullOrWhiteSpace(searchRoot) || !Directory.Exists(searchRoot))
        {
            return null;
        }

        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
            {
                index.TryAdd(Path.GetFileName(file), file);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A partial index still lets some files be recovered.
        }

        return index;
    }

    /// <summary>Points the library at files that turned up in a new location.</summary>
    /// <returns>How many track paths were rewritten.</returns>
    public int Relocate(LibraryHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var moved = 0;
        foreach (var track in report.Tracks)
        {
            if (track.State != TrackFileState.Moved || track.FoundPath is null)
            {
                continue;
            }

            track.Track.OutputPath = track.FoundPath;
            moved++;
        }

        if (moved > 0)
        {
            _library.Save(report.Job);
        }

        return moved;
    }

    /// <summary>
    /// Clears the completed flag of tracks whose file is gone, so Resume and Sync offer
    /// them again instead of skipping them forever.
    /// </summary>
    /// <returns>How many tracks were reopened.</returns>
    public int ForgetMissing(LibraryHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var reopened = 0;
        foreach (var track in report.Tracks)
        {
            if (track.State is not (TrackFileState.Missing or TrackFileState.Empty))
            {
                continue;
            }

            track.Track.IsComplete = false;
            track.Track.IsSelected = true;
            track.Track.OutputPath = null;
            reopened++;
        }

        if (reopened > 0)
        {
            _library.Save(report.Job);
        }

        return reopened;
    }
}
