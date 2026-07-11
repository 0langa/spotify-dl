using PlaylistDl.App.Models;
using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class JobStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"playlistdl-tests-{Guid.NewGuid():N}");

    [Fact]
    public void RoundTripsAResumableJob()
    {
        var path = Path.Combine(_directory, "job.json");
        var store = new JobStore(path);
        store.Save(new SavedJob
        {
            SourceUrl = "https://open.spotify.com/playlist/example",
            SourceName = "Road trip",
            SourceType = "import",
            OutputDirectory = @"C:\Music",
            Tracks =
            [
                new SavedTrack
                {
                    Id = "track-1",
                    SpotifyUrl = "https://open.spotify.com/track/one",
                    IsComplete = true,
                    OutputPath = @"C:\Music\song.mp3",
                },
            ],
        });

        var restored = store.Load();

        Assert.NotNull(restored);
        Assert.Equal("Road trip", restored.SourceName);
        Assert.Equal("import", restored.SourceType);
        Assert.True(restored.Tracks.Single().IsComplete);
        Assert.Equal(@"C:\Music\song.mp3", restored.Tracks.Single().OutputPath);
    }

    [Fact]
    public void InvalidJsonIsIgnored()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "job.json");
        File.WriteAllText(path, "not json");

        Assert.Null(new JobStore(path).Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
