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

    private const string Canonical = "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M";

    /// <summary>Writes an entry the way an older version saved it, timestamp included.</summary>
    private void WriteEntry(SavedJob job)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, LibraryStore.KeyFor(job.SourceUrl) + ".json"),
            System.Text.Json.JsonSerializer.Serialize(
                job, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void AShareLinkEntryMovesToTheCanonicalLinkWithEverythingItHeld()
    {
        var shareLink = Canonical + "?si=1a2b3c4d5e6f4a7b";
        var updated = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var job = Job(shareLink, "Mix");
        job.UpdatedAt = updated;
        job.AutoSync = true;
        job.Tracks[1].SourceOverride = "https://music.youtube.com/watch?v=pick";
        WriteEntry(job);

        Assert.Equal(1, Store.NormalizeSourceUrls());

        Assert.Null(Store.Load(shareLink));
        var moved = Store.Load(Canonical);
        Assert.NotNull(moved);
        Assert.Equal(Canonical, moved.SourceUrl);
        Assert.Equal(updated, moved.UpdatedAt);
        Assert.True(moved.AutoSync);
        Assert.Equal("https://music.youtube.com/watch?v=pick", moved.Tracks[1].SourceOverride);
        Assert.Single(Store.List());
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void NormalizingTwiceChangesNothingTheSecondTime()
    {
        WriteEntry(Job(Canonical + "?si=a"));

        Assert.Equal(1, Store.NormalizeSourceUrls());
        Assert.Equal(0, Store.NormalizeSourceUrls());
        Assert.Single(Store.List());
    }

    [Fact]
    public void OfSeveralShareLinksTheNewestTakesTheCanonicalLinkAndTheRestStay()
    {
        var older = Job(Canonical + "?si=old", "Older");
        older.UpdatedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = Job(Canonical + "?si=new", "Newer");
        newer.UpdatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        WriteEntry(older);
        WriteEntry(newer);

        Assert.Equal(1, Store.NormalizeSourceUrls());

        Assert.Equal("Newer", Store.Load(Canonical)?.SourceName);
        // Nothing is merged or deleted: the other entry still opens under its own link.
        Assert.Equal("Older", Store.Load(Canonical + "?si=old")?.SourceName);
        Assert.Equal(2, Store.List().Count);
    }

    [Fact]
    public void AnEntryAlreadyUnderTheCanonicalLinkIsNeverReplaced()
    {
        var current = Job(Canonical, "Current");
        current.UpdatedAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var shareLink = Job(Canonical + "?si=x", "Share link");
        shareLink.UpdatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        WriteEntry(current);
        WriteEntry(shareLink);

        Assert.Equal(0, Store.NormalizeSourceUrls());

        Assert.Equal("Current", Store.Load(Canonical)?.SourceName);
        Assert.Equal("Share link", Store.Load(Canonical + "?si=x")?.SourceName);
    }

    [Fact]
    public void SearchesAndImportsAreNeverMoved()
    {
        WriteEntry(Job("search:artist title"));
        WriteEntry(Job(@"C:\Music\list.csv"));

        Assert.Equal(0, Store.NormalizeSourceUrls());
        Assert.NotNull(Store.Load("search:artist title"));
        Assert.NotNull(Store.Load(@"C:\Music\list.csv"));
    }

    [Fact]
    public void AStoredShareLinkFollowsItsMovedEntry()
    {
        WriteEntry(Job(Canonical + "?si=a"));
        WriteEntry(Job("https://open.spotify.com/album/kept?si=1"));
        WriteEntry(Job("https://open.spotify.com/album/kept", "Taken"));
        Store.NormalizeSourceUrls();

        Assert.Equal(Canonical, Store.CurrentSourceUrl(Canonical + "?si=a"));
        // An entry left in place keeps its own link, so its jobs keep saving into it.
        Assert.Equal(
            "https://open.spotify.com/album/kept?si=1",
            Store.CurrentSourceUrl("https://open.spotify.com/album/kept?si=1"));
        // A link the library never saw is canonical from the start.
        Assert.Equal(
            "https://open.spotify.com/track/new",
            Store.CurrentSourceUrl("https://open.spotify.com/track/new?si=z"));
        Assert.Equal("search:x", Store.CurrentSourceUrl("search:x"));
    }

    [Fact]
    public void AQueuedJobFollowsItsMovedEntry()
    {
        WriteEntry(Job(Canonical + "?si=a"));
        Store.NormalizeSourceUrls();
        var queued = DownloadQueueTests.Job("ignored") with { SourceUrl = Canonical + "?si=a" };
        queued.Snapshot.SourceUrl = Canonical + "?si=a";

        var followed = Store.FollowSource(queued);

        Assert.Equal(Canonical, followed.SourceUrl);
        Assert.Equal(Canonical, followed.Snapshot.SourceUrl);
        Assert.Same(followed, Store.FollowSource(followed));
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
