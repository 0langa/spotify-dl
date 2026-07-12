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
    bool EmbedLyrics)
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
        settings.EmbedLyrics);
}

public sealed record QueuedJob(
    string PlaylistId,
    string Name,
    string OutputDirectory,
    IReadOnlyList<TrackItem> Tracks,
    QueuedJobSettings Settings);

/// <summary>In-memory FIFO of download jobs executed one after another.</summary>
public sealed class DownloadQueue
{
    private readonly Queue<QueuedJob> _jobs = new();

    public int Count => _jobs.Count;

    public bool IsEmpty => _jobs.Count == 0;

    public void Enqueue(QueuedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Tracks.Count == 0)
        {
            throw new ArgumentException("A queued job needs at least one track", nameof(job));
        }

        _jobs.Enqueue(job);
    }

    public QueuedJob? DequeueNext() => _jobs.Count > 0 ? _jobs.Dequeue() : null;

    public void Clear() => _jobs.Clear();

    public IReadOnlyList<string> PendingNames() => _jobs.Select(job => job.Name).ToList();
}
