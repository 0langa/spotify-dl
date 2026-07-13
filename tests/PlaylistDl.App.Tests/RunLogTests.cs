using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class RunLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"playlistdl-log-tests-{Guid.NewGuid():N}");

    [Fact]
    public void WritesTimestampedDiagnosticsAndStructuredTrackResults()
    {
        var log = new RunLog(_directory, new DateTimeOffset(2026, 7, 13, 20, 0, 0, TimeSpan.Zero));

        log.Write("backend", "provider warning");
        log.Write("track", "FAILED id-1 [no_match] No results found");

        var text = File.ReadAllText(log.Path);
        Assert.Contains("provider warning", text);
        Assert.Contains("FAILED id-1 [no_match] No results found", text);
        Assert.Contains("2026-07-13T20:00:00", text);
    }

    [Fact]
    public void LockedLogNeverStopsApplicationWork()
    {
        var log = new RunLog(_directory);
        using var lockStream = new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.None);

        var exception = Record.Exception(() => log.Write("backend", "diagnostic"));

        Assert.Null(exception);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
