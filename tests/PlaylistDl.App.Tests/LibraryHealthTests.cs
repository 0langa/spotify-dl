using PlaylistDl.App.Models;
using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class LibraryHealthTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "playlistdl-tests", Guid.NewGuid().ToString("N"));

    private string LibraryDirectory => Path.Combine(_root, "library");

    private string MusicDirectory => Path.Combine(_root, "music");

    private LibraryStore Store => new(LibraryDirectory);

    private LibraryHealthScanner Scanner => new(Store);

    private string WriteTrackFile(string name, string content = "audio")
    {
        Directory.CreateDirectory(MusicDirectory);
        var path = Path.Combine(MusicDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private SavedJob Job(params SavedTrack[] tracks) => new()
    {
        SourceUrl = "https://open.spotify.com/playlist/one",
        SourceName = "My Mix",
        OutputDirectory = MusicDirectory,
        Tracks = [.. tracks],
    };

    private static SavedTrack Complete(string id, string? path) => new()
    {
        Id = id,
        IsComplete = true,
        IsSelected = false,
        OutputPath = path,
    };

    [Fact]
    public void PresentFilesAreReportedHealthy()
    {
        var job = Job(Complete("a", WriteTrackFile("a.mp3")), Complete("b", WriteTrackFile("b.mp3")));

        var report = Scanner.Scan(job);

        Assert.True(report.IsHealthy);
        Assert.Equal(2, report.Present);
        Assert.Equal("2 files present", report.Summary);
    }

    [Fact]
    public void MissingAndEmptyFilesAreFound()
    {
        var job = Job(
            Complete("present", WriteTrackFile("present.mp3")),
            Complete("gone", Path.Combine(MusicDirectory, "gone.mp3")),
            Complete("empty", WriteTrackFile("empty.mp3", string.Empty)));

        var report = Scanner.Scan(job);

        Assert.False(report.IsHealthy);
        Assert.Equal(1, report.Present);
        Assert.Equal(2, report.Missing);
        Assert.Equal(
            TrackFileState.Missing,
            report.Tracks.Single(track => track.Track.Id == "gone").State);
        Assert.Equal(
            TrackFileState.Empty,
            report.Tracks.Single(track => track.Track.Id == "empty").State);
    }

    [Fact]
    public void UnfinishedTracksAreNotInspected()
    {
        var job = Job(
            new SavedTrack { Id = "pending", IsComplete = false, OutputPath = null },
            Complete("done", WriteTrackFile("done.mp3")));

        Assert.Single(Scanner.Scan(job).Tracks);
    }

    [Fact]
    public void AFileMovedIntoASubfolderIsRecognized()
    {
        var moved = Path.Combine(MusicDirectory, "Albums");
        Directory.CreateDirectory(moved);
        File.WriteAllText(Path.Combine(moved, "song.mp3"), "audio");
        var job = Job(Complete("a", Path.Combine(MusicDirectory, "song.mp3")));

        var report = Scanner.Scan(job, MusicDirectory);

        Assert.Equal(1, report.Moved);
        Assert.Equal(
            Path.Combine(moved, "song.mp3"),
            report.Tracks.Single().FoundPath);
    }

    [Fact]
    public void RelocateRewritesThePathsAndPersistsThem()
    {
        var moved = Path.Combine(MusicDirectory, "Moved");
        Directory.CreateDirectory(moved);
        File.WriteAllText(Path.Combine(moved, "song.mp3"), "audio");
        var job = Job(Complete("a", Path.Combine(MusicDirectory, "song.mp3")));
        Store.Save(job);

        var relocated = Scanner.Relocate(Scanner.Scan(job, MusicDirectory));

        Assert.Equal(1, relocated);
        var reloaded = Store.Load(job.SourceUrl)!;
        Assert.Equal(Path.Combine(moved, "song.mp3"), reloaded.Tracks.Single().OutputPath);
        // The rewritten library now reports healthy.
        Assert.True(Scanner.Scan(reloaded, MusicDirectory).IsHealthy);
    }

    [Fact]
    public void ForgetMissingReopensOnlyTheTracksWhoseFileIsGone()
    {
        var job = Job(
            Complete("present", WriteTrackFile("present.mp3")),
            Complete("gone", Path.Combine(MusicDirectory, "gone.mp3")));
        Store.Save(job);

        var reopened = Scanner.ForgetMissing(Scanner.Scan(job));

        Assert.Equal(1, reopened);
        var reloaded = Store.Load(job.SourceUrl)!;
        var gone = reloaded.Tracks.Single(track => track.Id == "gone");
        var present = reloaded.Tracks.Single(track => track.Id == "present");
        Assert.False(gone.IsComplete);
        Assert.True(gone.IsSelected);
        Assert.Null(gone.OutputPath);
        // A track whose file is still there must stay complete.
        Assert.True(present.IsComplete);
    }

    [Fact]
    public void ScanningEveryJobCoversTheWholeLibrary()
    {
        Store.Save(Job(Complete("a", WriteTrackFile("a.mp3"))));
        Store.Save(new SavedJob
        {
            SourceUrl = "https://open.spotify.com/album/two",
            SourceName = "Album",
            OutputDirectory = MusicDirectory,
            Tracks = [Complete("b", Path.Combine(MusicDirectory, "missing.mp3"))],
        });

        var reports = Scanner.ScanAll();

        Assert.Equal(2, reports.Count);
        Assert.Single(reports, report => report.IsHealthy);
        Assert.Single(reports, report => report.Missing == 1);
    }

    [Fact]
    public void AnUnreadablePathCountsAsMissingInsteadOfThrowing()
    {
        var job = Job(Complete("bad", "::not a path::"));

        Assert.Equal(TrackFileState.Missing, Scanner.Scan(job).Tracks.Single().State);
    }

    [Fact]
    public void NothingIsWrittenWhenThereIsNothingToRepair()
    {
        var job = Job(Complete("a", WriteTrackFile("a.mp3")));
        Store.Save(job);
        var before = File.GetLastWriteTimeUtc(
            Path.Combine(LibraryDirectory, LibraryStore.KeyFor(job.SourceUrl) + ".json"));

        var report = Scanner.Scan(job, MusicDirectory);

        Assert.Equal(0, Scanner.Relocate(report));
        Assert.Equal(0, Scanner.ForgetMissing(report));
        Assert.Equal(
            before,
            File.GetLastWriteTimeUtc(
                Path.Combine(LibraryDirectory, LibraryStore.KeyFor(job.SourceUrl) + ".json")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
