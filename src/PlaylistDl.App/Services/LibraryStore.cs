using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlaylistDl.App.Models;

namespace PlaylistDl.App.Services;

/// <summary>Persists every resolved job as one JSON file so past work stays reopenable.</summary>
public sealed class LibraryStore
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _directory;

    public LibraryStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlaylistDL",
            "jobs");
    }

    public static string KeyFor(string sourceUrl) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl.Trim())))[..16]
            .ToLowerInvariant();

    public IReadOnlyList<SavedJob> List()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(_directory, "*.json");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var jobs = new List<SavedJob>();
        foreach (var file in files)
        {
            try
            {
                var job = JsonSerializer.Deserialize<SavedJob>(File.ReadAllText(file), _jsonOptions);
                if (job is not null && !string.IsNullOrWhiteSpace(job.SourceUrl))
                {
                    jobs.Add(job);
                }
            }
            catch (JsonException)
            {
                // A corrupt entry must not take the whole library down.
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return jobs.OrderByDescending(job => job.UpdatedAt).ToList();
    }

    /// <summary>
    /// Maps track ids and Spotify URLs onto files earlier jobs already downloaded, so a
    /// new job can reuse them instead of downloading the same audio twice.
    /// </summary>
    /// <remarks>
    /// The source being downloaded is skipped, because reusing a job's own earlier output
    /// would defeat a deliberate re-download of that source. Track ids are not excluded:
    /// the same Spotify track keeps its id across sources, which is exactly the match the
    /// backend looks for. Paths are not checked here; the backend verifies that each file
    /// still exists before it reuses one.
    /// </remarks>
    public Dictionary<string, string> DownloadedFiles(string? currentSourceUrl)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var job in List())
        {
            if (!string.IsNullOrWhiteSpace(currentSourceUrl) &&
                string.Equals(job.SourceUrl, currentSourceUrl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var track in job.Tracks)
            {
                if (!track.IsComplete || string.IsNullOrWhiteSpace(track.OutputPath))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(track.Id))
                {
                    files.TryAdd(track.Id, track.OutputPath);
                }
                if (!string.IsNullOrEmpty(track.SpotifyUrl))
                {
                    files.TryAdd(track.SpotifyUrl, track.OutputPath);
                }
            }
        }

        return files;
    }

    public SavedJob? Load(string sourceUrl)
    {
        var path = PathFor(sourceUrl);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SavedJob>(File.ReadAllText(path), _jsonOptions)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(SavedJob job)
    {
        if (string.IsNullOrWhiteSpace(job.SourceUrl))
        {
            throw new ArgumentException("Job needs a source URL", nameof(job));
        }

        Directory.CreateDirectory(_directory);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        var path = PathFor(job.SourceUrl);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(job, _jsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    /// <summary>
    /// Saves progress from the download grid without losing the per-source preferences
    /// the grid never carries.
    /// </summary>
    /// <remarks>
    /// The grid snapshot is built from the track list alone, so writing it as-is would
    /// clear "keep in sync" and its last check on every checkpoint.
    /// </remarks>
    public void SaveProgress(SavedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (Load(job.SourceUrl) is { } stored)
        {
            job.AutoSync = stored.AutoSync;
            job.LastAutoSyncUtc = stored.LastAutoSyncUtc;
        }
        else if (File.Exists(PathFor(job.SourceUrl)))
        {
            // The entry is there but could not be deserialized. Progress is still worth
            // saving, but the preferences are read straight out of the text first, so a
            // reader that trips over one bad field does not silently switch auto-sync off.
            ReadAutoSyncPreferences(PathFor(job.SourceUrl), job);
        }

        Save(job);
    }

    private static void ReadAutoSyncPreferences(string path, SavedJob job)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("autoSync", out var autoSync) &&
                autoSync.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                job.AutoSync = autoSync.GetBoolean();
            }

            if (document.RootElement.TryGetProperty("lastAutoSyncUtc", out var lastCheck) &&
                lastCheck.ValueKind == JsonValueKind.String &&
                lastCheck.TryGetDateTimeOffset(out var when))
            {
                job.LastAutoSyncUtc = when;
            }
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Nothing readable to keep; the save below still preserves the download work.
        }
    }

    /// <summary>
    /// Stamps when auto-sync last looked at a source, without touching anything else.
    /// </summary>
    /// <remarks>
    /// UpdatedAt is deliberately left alone: an unattended check is not a change to the
    /// job, and bumping it would reorder the library list and claim work that never
    /// happened.
    /// </remarks>
    /// <returns>False when there was no entry to stamp, or it could not be written.</returns>
    public bool RecordAutoSyncCheck(string sourceUrl, DateTimeOffset when)
    {
        var job = Load(sourceUrl);
        if (job is null)
        {
            return false;
        }

        job.LastAutoSyncUtc = when;
        var path = PathFor(sourceUrl);
        var temporaryPath = path + ".tmp";
        try
        {
            // The same write-and-swap Save uses: this is the app's only unattended,
            // repeating write, so a torn one must not be able to destroy the entry.
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(job, _jsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Delete(string sourceUrl)
    {
        var path = PathFor(sourceUrl);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>One-time import of the 1.2.x single last-job file into the library.</summary>
    /// <remarks>
    /// Only runs before the library exists. Re-running it would resurrect entries the
    /// user deleted, because the last-job file still points at the deleted source.
    /// </remarks>
    public void MigrateFromLastJob(SavedJob? lastJob)
    {
        if (lastJob is null || string.IsNullOrWhiteSpace(lastJob.SourceUrl) ||
            Directory.Exists(_directory))
        {
            return;
        }

        if (Load(lastJob.SourceUrl) is null)
        {
            var preservedTimestamp = lastJob.UpdatedAt;
            Save(lastJob);
            RewriteTimestamp(lastJob.SourceUrl, preservedTimestamp);
        }
    }

    private void RewriteTimestamp(string sourceUrl, DateTimeOffset updatedAt)
    {
        var job = Load(sourceUrl);
        if (job is null)
        {
            return;
        }

        job.UpdatedAt = updatedAt;
        File.WriteAllText(PathFor(sourceUrl), JsonSerializer.Serialize(job, _jsonOptions));
    }

    private string PathFor(string sourceUrl) => Path.Combine(_directory, KeyFor(sourceUrl) + ".json");
}
