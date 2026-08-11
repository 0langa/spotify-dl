using PlaylistDl.App.Models;
using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class LibraryStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "playlistdl-tests", Guid.NewGuid().ToString("N"));

    private LibraryStore Store => new(_directory);

    private static SavedJob Job(string url, string name = "Mix") => new()
    {
        SourceUrl = url,
        SourceName = name,
        SourceType = "playlist",
        Tracks =
        [
            new SavedTrack { Id = "a", IsComplete = true },
            new SavedTrack { Id = "b", IsComplete = false },
        ],
    };

    [Fact]
    public void SaveThenListRoundTrips()
    {
        Store.Save(Job("https://open.spotify.com/playlist/one", "First"));
        Store.Save(Job("https://open.spotify.com/playlist/two", "Second"));

        var jobs = Store.List();

        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, job => job.SourceName == "First");
        Assert.Contains(jobs, job => job.SourceName == "Second");
    }

    [Fact]
    public void SameSourceUrlOverwritesInsteadOfDuplicating()
    {
        var url = "https://open.spotify.com/playlist/one";
        Store.Save(Job(url, "Old name"));
        Store.Save(Job(url, "New name"));

        var jobs = Store.List();

        Assert.Single(jobs);
        Assert.Equal("New name", jobs[0].SourceName);
    }

    [Fact]
    public void LoadFindsJobByUrlAndDeleteRemovesIt()
    {
        var url = "https://open.spotify.com/album/x";
        Store.Save(Job(url));

        Assert.NotNull(Store.Load(url));
        Store.Delete(url);
        Assert.Null(Store.Load(url));
        Assert.Empty(Store.List());
    }

    [Fact]
    public void CorruptEntryIsSkippedNotFatal()
    {
        Store.Save(Job("https://open.spotify.com/playlist/good"));
        File.WriteAllText(Path.Combine(_directory, "broken.json"), "{not json");

        Assert.Single(Store.List());
    }

    [Fact]
    public void MigrationImportsLastJobOncePreservingTimestamp()
    {
        var lastJob = Job("https://open.spotify.com/playlist/legacy", "Legacy");
        lastJob.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-3);
        var originalTimestamp = lastJob.UpdatedAt;

        Store.MigrateFromLastJob(lastJob);
        var imported = Store.Load(lastJob.SourceUrl);
        Assert.NotNull(imported);
        Assert.Equal(originalTimestamp, imported.UpdatedAt);

        imported.SourceName = "Edited after import";
        Store.Save(imported);
        Store.MigrateFromLastJob(lastJob);

        Assert.Equal("Edited after import", Store.Load(lastJob.SourceUrl)!.SourceName);
    }

    [Fact]
    public void MigrationDoesNotResurrectAJobDeletedFromTheLibrary()
    {
        var lastJob = Job("https://open.spotify.com/playlist/legacy", "Legacy");
        Store.MigrateFromLastJob(lastJob);
        Assert.NotNull(Store.Load(lastJob.SourceUrl));

        Store.Delete(lastJob.SourceUrl);
        Store.MigrateFromLastJob(lastJob);

        Assert.Null(Store.Load(lastJob.SourceUrl));
        Assert.Empty(Store.List());
    }

    [Fact]
    public void KeyIsStableAndFilenameSafe()
    {
        var key = LibraryStore.KeyFor("https://open.spotify.com/playlist/one?si=abc");

        Assert.Equal(LibraryStore.KeyFor("https://open.spotify.com/playlist/one?si=abc"), key);
        Assert.Equal(16, key.Length);
        Assert.True(key.All(char.IsAsciiLetterOrDigit));
    }

    [Fact]
    public void DownloadedFilesIndexesCompletedTracksForReuse()
    {
        var job = new SavedJob
        {
            SourceUrl = "https://open.spotify.com/playlist/one",
            SourceName = "One",
            Tracks =
            [
                new SavedTrack
                {
                    Id = "done",
                    SpotifyUrl = "https://open.spotify.com/track/done",
                    IsComplete = true,
                    OutputPath = @"C:\music\One - A - Done.mp3",
                },
                new SavedTrack { Id = "unfinished", OutputPath = null, IsComplete = false },
                new SavedTrack { Id = "no-path", IsComplete = true, OutputPath = "  " },
            ],
        };
        Store.Save(job);

        var files = Store.DownloadedFiles(null);

        Assert.Equal(@"C:\music\One - A - Done.mp3", files["done"]);
        Assert.Equal(@"C:\music\One - A - Done.mp3", files["https://open.spotify.com/track/done"]);
        Assert.False(files.ContainsKey("unfinished"));
        Assert.False(files.ContainsKey("no-path"));
    }

    [Fact]
    public void DownloadedFilesSkipsTheSourceBeingDownloadedButKeepsOthers()
    {
        var url = "https://open.spotify.com/playlist/one";
        Store.Save(new SavedJob
        {
            SourceUrl = url,
            SourceName = "One",
            Tracks =
            [
                new SavedTrack { Id = "own", IsComplete = true, OutputPath = @"C:\music\own.mp3" },
            ],
        });
        Store.Save(new SavedJob
        {
            SourceUrl = "https://open.spotify.com/playlist/other",
            SourceName = "Other",
            Tracks =
            [
                new SavedTrack { Id = "shared", IsComplete = true, OutputPath = @"C:\music\shared.mp3" },
            ],
        });

        var files = Store.DownloadedFiles(url);

        // The same Spotify track keeps its id across sources, so ids must stay matchable.
        Assert.True(files.ContainsKey("shared"));
        Assert.False(files.ContainsKey("own"));
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
    }
}
