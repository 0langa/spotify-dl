using System.IO;
using System.Text.Json;
using PlaylistDl.App.Models;

namespace PlaylistDl.App.Services;

/// <summary>Durable form of one pending job; the live resolved tracks are not persisted.</summary>
public sealed class QueuedJobRecord
{
    public string Name { get; set; } = string.Empty;

    public string SourceUrl { get; set; } = string.Empty;

    public string SourceType { get; set; } = "playlist";

    public string OutputDirectory { get; set; } = string.Empty;

    public QueuedJobSettings Settings { get; set; } =
        new("mp3", "0", 2, null, true, "position_artist_title", true, 0, null, false);

    public List<SavedTrack> Tracks { get; set; } = [];
}

/// <summary>Keeps the pending queue across restarts so queued work is never lost silently.</summary>
public sealed class QueueStore
{
    private const int SupportedVersion = 1;

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _queuePath;

    public QueueStore(string? queuePath = null)
    {
        _queuePath = queuePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlaylistDL",
            "queue.json");
    }

    public IReadOnlyList<QueuedJob> Load()
    {
        try
        {
            if (!File.Exists(_queuePath))
            {
                return [];
            }

            var document = JsonSerializer.Deserialize<QueueDocument>(
                File.ReadAllText(_queuePath),
                _jsonOptions);
            if (document is null || document.Version != SupportedVersion)
            {
                return [];
            }

            return document.Jobs
                .Where(record => !string.IsNullOrWhiteSpace(record.SourceUrl))
                .Select(ToJob)
                .Where(job => job.SelectedCount > 0)
                .ToList();
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A queue that cannot be read must not stop the app from starting.
            return [];
        }
    }

    public void Save(IReadOnlyList<QueuedJob> jobs)
    {
        var directory = Path.GetDirectoryName(_queuePath)
            ?? throw new InvalidOperationException("Queue storage directory is unavailable.");
        Directory.CreateDirectory(directory);
        var document = new QueueDocument
        {
            Version = SupportedVersion,
            Jobs = jobs.Select(ToRecord).ToList(),
        };
        var temporaryPath = _queuePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, _jsonOptions));
        File.Move(temporaryPath, _queuePath, overwrite: true);
    }

    private static QueuedJobRecord ToRecord(QueuedJob job) => new()
    {
        Name = job.Name,
        SourceUrl = job.SourceUrl,
        SourceType = job.SourceType,
        OutputDirectory = job.OutputDirectory,
        Settings = job.Settings,
        Tracks = job.Snapshot.Tracks,
    };

    private static QueuedJob ToJob(QueuedJobRecord record) => new(
        record.Name,
        record.SourceUrl,
        record.SourceType,
        record.OutputDirectory,
        record.Settings,
        new SavedJob
        {
            SourceUrl = record.SourceUrl,
            SourceName = record.Name,
            SourceType = record.SourceType,
            OutputDirectory = record.OutputDirectory,
            Tracks = record.Tracks,
        });

    private sealed class QueueDocument
    {
        public int Version { get; set; }

        public List<QueuedJobRecord> Jobs { get; set; } = [];
    }
}
