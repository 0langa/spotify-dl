using System.IO;
using System.Text;

namespace PlaylistDl.App.Services;

/// <summary>Small local session log for provider diagnostics and final track results.</summary>
public sealed class RunLog
{
    private readonly object _writeLock = new();
    private readonly Func<DateTimeOffset> _clock;

    public RunLog(string? directory = null, DateTimeOffset? fixedTimestamp = null)
    {
        _clock = fixedTimestamp is null ? () => DateTimeOffset.Now : () => fixedTimestamp.Value;
        var started = _clock();
        directory ??= System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlaylistDL",
            "logs");
        var fileName = $"playlistdl-{started:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log";
        Path = TryInitialize(directory, fileName, started)
            ?? TryInitialize(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PlaylistDL", "logs"),
                fileName,
                started)
            ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);
    }

    public string Path { get; }

    public void Write(string category, string message)
    {
        var singleLine = message.Replace("\r", " ").Replace("\n", " | ");
        var line = $"{_clock():O} [{category}] {singleLine}{Environment.NewLine}";
        lock (_writeLock)
        {
            try
            {
                File.AppendAllText(Path, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Provider work must continue when a log is temporarily locked or disk is full.
            }
            catch (UnauthorizedAccessException)
            {
                // Provider work must continue when security software changes file access.
            }
        }
    }

    private static string? TryInitialize(
        string directory,
        string fileName,
        DateTimeOffset started)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, fileName);
            File.WriteAllText(
                path,
                $"{started:O} [app] Playlist DL session started{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            PruneOldLogs(directory, started);
            return path;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void PruneOldLogs(string directory, DateTimeOffset now)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "playlistdl-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < now.UtcDateTime.AddDays(-14))
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
            // Logging must never block application startup.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep running when retention cleanup is denied.
        }
    }
}
