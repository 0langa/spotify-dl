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
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(
            directory,
            $"playlistdl-{started:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        File.WriteAllText(
            Path,
            $"{started:O} [app] Playlist DL session started{Environment.NewLine}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        PruneOldLogs(directory, started);
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
