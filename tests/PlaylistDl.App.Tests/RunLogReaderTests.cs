using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class RunLogReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"playlistdl-reader-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ReadsLogWhileWriterKeepsFileOpen()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "current.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using (var textWriter = new StreamWriter(writer, leaveOpen: true))
        {
            textWriter.Write("exact provider failure");
        }

        Assert.Equal("exact provider failure", RunLogReader.Read(path));
    }

    [Fact]
    public void MissingLogReturnsUsefulEmptyState()
    {
        var text = RunLogReader.Read(Path.Combine(_directory, "missing.log"));

        Assert.Contains("No run log exists yet", text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
