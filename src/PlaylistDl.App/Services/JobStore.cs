using System.IO;
using System.Text.Json;
using PlaylistDl.App.Models;

namespace PlaylistDl.App.Services;

public sealed class JobStore
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _jobPath;

    public JobStore(string? jobPath = null)
    {
        _jobPath = jobPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlaylistDL",
            "last-job.json");
    }

    public SavedJob? Load()
    {
        try
        {
            return File.Exists(_jobPath)
                ? JsonSerializer.Deserialize<SavedJob>(File.ReadAllText(_jobPath), _jsonOptions)
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
    }

    public void Save(SavedJob job)
    {
        var directory = Path.GetDirectoryName(_jobPath)
            ?? throw new InvalidOperationException("Job storage directory is unavailable.");
        Directory.CreateDirectory(directory);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        var temporaryPath = _jobPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(job, _jsonOptions));
        File.Move(temporaryPath, _jobPath, overwrite: true);
    }
}
