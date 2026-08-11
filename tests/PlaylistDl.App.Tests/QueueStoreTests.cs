using PlaylistDl.App.Models;
using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class QueueStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "playlistdl-tests", Guid.NewGuid().ToString("N"));

    private string QueuePath => Path.Combine(_directory, "queue.json");

    private QueueStore Store => new(QueuePath);

    private static QueuedJob Job(string name, params TrackItem[] tracks) =>
        DownloadQueueTests.Job(name, tracks);

    [Fact]
    public void PendingJobsSurviveARestart()
    {
        var job = Job(
            "My Mix",
            new TrackItem { Id = "a", SpotifyUrl = "https://open.spotify.com/track/a" },
            new TrackItem { Id = "b", SpotifyUrl = "https://open.spotify.com/track/b" });

        Store.Save([job]);
        var restored = Store.Load();

        Assert.Single(restored);
        Assert.Equal("My Mix", restored[0].Name);
        Assert.Equal(job.SourceUrl, restored[0].SourceUrl);
        Assert.Equal(job.OutputDirectory, restored[0].OutputDirectory);
        Assert.Equal(job.Settings, restored[0].Settings);
        Assert.Equal(2, restored[0].SelectedCount);
        // The backend session is gone after a restart, so the source must be resolved again.
        Assert.True(restored[0].NeedsResolve);
        Assert.Null(restored[0].PlaylistId);
    }

    [Fact]
    public void CompletedTracksAreNotQueuedAgain()
    {
        var job = Job(
            "Partly done",
            new TrackItem { Id = "a", Status = "Done", Progress = 100 },
            new TrackItem { Id = "b" });

        Store.Save([job]);

        Assert.Equal(1, Store.Load()[0].SelectedCount);
    }

    [Fact]
    public void AJobWithNothingLeftToDownloadIsDropped()
    {
        var job = Job("Finished", new TrackItem { Id = "a", Status = "Done", Progress = 100 });

        Store.Save([job]);

        Assert.Empty(Store.Load());
    }

    [Fact]
    public void QueueOrderIsPreserved()
    {
        Store.Save([Job("first"), Job("second"), Job("third")]);

        Assert.Equal(["first", "second", "third"], Store.Load().Select(job => job.Name));
    }

    [Fact]
    public void MissingOrUnreadableQueueLoadsEmptyInsteadOfThrowing()
    {
        Assert.Empty(Store.Load());

        Directory.CreateDirectory(_directory);
        File.WriteAllText(QueuePath, "{not json");
        Assert.Empty(Store.Load());
    }

    [Fact]
    public void AQueueFromAnUnknownVersionIsIgnored()
    {
        Directory.CreateDirectory(_directory);
        var job = """
        {"version":99,"jobs":[{"name":"x","sourceUrl":"https://open.spotify.com/playlist/x",
        "tracks":[{"id":"a","isSelected":true,"isComplete":false}]}]}
        """;
        File.WriteAllText(QueuePath, job);

        // The same document at the supported version must load, so the version gate is
        // what rejects it here rather than the document being unusable.
        Assert.Empty(Store.Load());
        File.WriteAllText(QueuePath, job.Replace("\"version\":99", "\"version\":1"));
        Assert.Single(Store.Load());
    }

    [Fact]
    public void InvalidatedSessionsStillPersistTheDurableJob()
    {
        var queue = new DownloadQueue();
        queue.Enqueue(Job("live", new TrackItem { Id = "a" }));
        queue.InvalidateSessions();

        Store.Save(queue.Items);
        var restored = Store.Load();

        Assert.Single(restored);
        Assert.True(restored[0].NeedsResolve);
        Assert.Equal(1, restored[0].SelectedCount);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
