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
