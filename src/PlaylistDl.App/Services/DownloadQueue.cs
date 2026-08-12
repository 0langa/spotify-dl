using PlaylistDl.App.Models;

namespace PlaylistDl.App.Services;

/// <summary>Settings frozen at enqueue time so later edits do not change queued work.</summary>
public sealed record QueuedJobSettings(
    string Format,
    string Bitrate,
    int Threads,
    string? CookieFile,
    bool WriteM3u,
    string NamingPreset,
    bool CreateSourceFolder,
    int ThrottleSeconds,
    string? YtDlpArgs,
    bool EmbedLyrics,
    string DuplicatePolicy = "download",
    bool NormalizeLoudness = false,
    bool VerifyDownloads = true)
{
    public static QueuedJobSettings From(AppSettings settings) => new(
        settings.Format,
        settings.Bitrate,
        settings.Threads,
        settings.CookieFile,
        settings.WriteM3u,
        settings.NamingPreset,
        settings.CreateSourceFolder,
        settings.ThrottleSeconds,
        settings.YtDlpArgs,
        settings.EmbedLyrics,
        settings.DuplicatePolicy,
        settings.NormalizeLoudness,
        settings.VerifyDownloads);
}

/// <summary>
/// One pending job. <see cref="Snapshot"/> is the durable part: it survives a restart and
/// carries the per-track state needed to re-resolve the source and restore the selection.
/// The live fields are only set while the resolved source is still in the backend session.
/// </summary>
public sealed record QueuedJob(
    string Name,
    string SourceUrl,
    string SourceType,
    string OutputDirectory,
    QueuedJobSettings Settings,
    SavedJob Snapshot)
{
    public string? PlaylistId { get; init; }

    public IReadOnlyList<TrackItem> AllTracks { get; init; } = [];

    public IReadOnlyList<TrackItem> Tracks { get; init; } = [];

    /// <summary>Tracks this job will download, counted from whichever form is available.</summary>
    public int SelectedCount => Tracks.Count > 0
        ? Tracks.Count
        : Snapshot.Tracks.Count(track => track.IsSelected && !track.IsComplete);

    /// <summary>True once the backend session that resolved this job is gone.</summary>
    public bool NeedsResolve => PlaylistId is null || Tracks.Count == 0;
}

/// <summary>Result of one finished queue job, kept for the end-of-run report.</summary>
public sealed record QueueJobSummary(
    string Name,
    int Succeeded,
    int Failed,
    string? FailureHint,
    string? Error)
{
    public string Outcome => Error is not null
        ? "Could not run"
        : Failed == 0 ? $"{Succeeded} saved" : $"{Succeeded} saved, {Failed} failed";
}

public static class SavedJobSnapshot
{
    public static SavedJob Create(
        string sourceUrl,
        string sourceName,
        string sourceType,
        string outputDirectory,
        IEnumerable<TrackItem> tracks) => new()
        {
            SourceUrl = sourceUrl,
            SourceName = sourceName,
            SourceType = sourceType,
            OutputDirectory = outputDirectory,
            Tracks = tracks.Select(track => new SavedTrack
            {
                Id = track.Id,
                SpotifyUrl = track.SpotifyUrl,
                IsSelected = track.IsSelected,
                // A failed attempt is processed (Progress=100) but must remain resumable.
                IsComplete = track.Status == "Done",
                OutputPath = track.OutputPath,
                SourceOverride = track.SourceOverride,
                LastError = track.ErrorText,
            }).ToList(),
        };
}

/// <summary>Ordered list of download jobs executed one after another.</summary>
public sealed class DownloadQueue
{
    private readonly List<QueuedJob> _jobs = [];

    /// <summary>Raised whenever the pending jobs change, so they can be persisted.</summary>
    public event EventHandler? Changed;

    public int Count => _jobs.Count;

    public bool IsEmpty => _jobs.Count == 0;

    public IReadOnlyList<QueuedJob> Items => _jobs;

    public void Enqueue(QueuedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.SelectedCount == 0)
        {
            throw new ArgumentException("A queued job needs at least one track", nameof(job));
        }

        _jobs.Add(job);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the queue, e.g. with jobs restored from disk at startup.</summary>
    public void Replace(IEnumerable<QueuedJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        _jobs.Clear();
        _jobs.AddRange(jobs.Where(job => job.SelectedCount > 0));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public QueuedJob? DequeueNext()
    {
        if (_jobs.Count == 0)
        {
            return null;
        }

        var job = _jobs[0];
        _jobs.RemoveAt(0);
        Changed?.Invoke(this, EventArgs.Empty);
        return job;
    }

    /// <summary>Puts a job back at the head, e.g. when a run is paused before it ran.</summary>
    public void Requeue(QueuedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _jobs.Insert(0, job);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Strips the live backend session from every job, keeping the durable part.</summary>
    /// <remarks>Used when the backend restarts: the sources are resolved again on demand.</remarks>
    public void InvalidateSessions()
    {
        if (_jobs.Count == 0)
        {
            return;
        }

        for (var index = 0; index < _jobs.Count; index++)
        {
            _jobs[index] = _jobs[index] with { PlaylistId = null, AllTracks = [], Tracks = [] };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replaces the durable snapshot of every job whose source <paramref name="lookup"/>
    /// returns a newer entry for, e.g. after the library repaired it.
    /// </summary>
    /// <remarks>
    /// The live session is dropped with it, because the queued selection is derived from
    /// the snapshot when the job runs.
    /// </remarks>
    /// <returns>How many queued jobs were refreshed.</returns>
    public int RefreshSnapshots(Func<string, SavedJob?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var refreshed = 0;
        for (var index = 0; index < _jobs.Count; index++)
        {
            if (lookup(_jobs[index].SourceUrl) is not { } snapshot)
            {
                continue;
            }

            _jobs[index] = _jobs[index] with
            {
                Snapshot = snapshot,
                PlaylistId = null,
                AllTracks = [],
                Tracks = [],
            };
            refreshed++;
        }

        if (refreshed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return refreshed;
    }

    /// <summary>Moves one job by <paramref name="offset"/> places; returns its new index.</summary>
    public int Move(int index, int offset)
    {
        if (index < 0 || index >= _jobs.Count)
        {
            return index;
        }

        var target = Math.Clamp(index + offset, 0, _jobs.Count - 1);
        if (target == index)
        {
            return index;
        }

        var job = _jobs[index];
        _jobs.RemoveAt(index);
        _jobs.Insert(target, job);
        Changed?.Invoke(this, EventArgs.Empty);
        return target;
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _jobs.Count)
        {
            return;
        }

        _jobs.RemoveAt(index);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_jobs.Count == 0)
        {
            return;
        }

        _jobs.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<string> PendingNames() => _jobs.Select(job => job.Name).ToList();
}
