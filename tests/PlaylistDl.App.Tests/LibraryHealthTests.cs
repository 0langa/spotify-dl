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

    private string WriteFile(string relativePath, string content = "audio")
    {
        var path = Path.Combine(MusicDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string MusicPath(string relativePath) => Path.Combine(MusicDirectory, relativePath);

    private SavedJob Job(params SavedTrack[] tracks)
    {
        Directory.CreateDirectory(MusicDirectory);
        return new SavedJob
        {
            SourceUrl = "https://open.spotify.com/playlist/one",
            SourceName = "My Mix",
            OutputDirectory = MusicDirectory,
            Tracks = [.. tracks],
        };
    }

    private static SavedTrack Complete(string id, string? path) => new()
    {
        Id = id,
        SpotifyUrl = $"https://open.spotify.com/track/{id}",
        IsComplete = true,
        IsSelected = false,
        OutputPath = path,
    };

    [Fact]
    public void PresentFilesAreReportedHealthy()
    {
        var job = Job(Complete("a", WriteFile("a.mp3")), Complete("b", WriteFile("b.mp3")));

        var report = Scanner.Scan(job);

        Assert.True(report.IsHealthy);
        Assert.Equal(2, report.Present);
        Assert.Equal("2 files present", report.Summary);
    }

    [Fact]
    public void MissingAndEmptyFilesAreFound()
    {
        var job = Job(
            Complete("present", WriteFile("present.mp3")),
            Complete("gone", MusicPath("gone.mp3")),
            Complete("empty", WriteFile("empty.mp3", string.Empty)));

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
            Complete("done", WriteFile("done.mp3")));

        Assert.Single(Scanner.Scan(job).Tracks);
    }

    [Fact]
    public void ATrackMarkedDoneWithNoFileRecordedCountsAsMissing()
    {
        var job = Job(Complete("nopath", null));

        var report = Scanner.Scan(job);

        Assert.Equal(TrackFileState.Missing, report.Tracks.Single().State);
        Assert.Equal(1, report.Missing);
    }

    [Fact]
    public void AFileMovedIntoASubfolderIsRecognized()
    {
        var moved = WriteFile(Path.Combine("Albums", "song.mp3"));
        var job = Job(Complete("a", MusicPath("song.mp3")));

        var report = Scanner.Scan(job);

        Assert.Equal(1, report.Moved);
        Assert.Equal(moved, report.Tracks.Single().FoundPath);
    }

    [Fact]
    public void ANameThatOccursTwiceIsNeverRelocated()
    {
        WriteFile(Path.Combine("Album A", "01 - Intro.mp3"));
        WriteFile(Path.Combine("Album B", "01 - Intro.mp3"));
        var job = Job(Complete("a", MusicPath("01 - Intro.mp3")));

        var report = Scanner.Scan(job);

        // Two candidates means the right one cannot be told apart from another album's.
        Assert.Equal(0, report.Moved);
        Assert.Equal(TrackFileState.Missing, report.Tracks.Single().State);
    }

    [Fact]
    public void AFileAnotherSavedJobStillUsesIsNotAdopted()
    {
        var other = WriteFile(Path.Combine("Other job", "song.mp3"));
        Store.Save(new SavedJob
        {
            SourceUrl = "https://open.spotify.com/album/two",
            SourceName = "Album",
            OutputDirectory = MusicDirectory,
            Tracks = [Complete("b", other)],
        });
        var job = Job(Complete("a", MusicPath("song.mp3")));

        Assert.Equal(TrackFileState.Missing, Scanner.Scan(job).Tracks.Single().State);
    }

    [Fact]
    public void TwoMissingTracksNeverShareOneFile()
    {
        WriteFile(Path.Combine("Moved", "song.mp3"));
        Directory.CreateDirectory(MusicPath("Second"));
        var job = Job(
            Complete("a", MusicPath("song.mp3")),
            Complete("b", MusicPath(Path.Combine("Second", "song.mp3"))));

        var report = Scanner.Scan(job);

        Assert.Equal(1, report.Moved);
        Assert.Equal(1, report.Missing);
    }

    [Fact]
    public void RelocateRewritesThePathsAndPersistsThem()
    {
        var moved = WriteFile(Path.Combine("Moved", "song.mp3"));
        var job = Job(Complete("a", MusicPath("song.mp3")));
        Store.Save(job);

        var relocated = Scanner.Relocate(Scanner.Scan(job));

        Assert.Equal(1, relocated);
        var reloaded = Store.Load(job.SourceUrl)!;
        Assert.Equal(moved, reloaded.Tracks.Single().OutputPath);
        // The rewritten library now reports healthy.
        Assert.True(Scanner.Scan(reloaded).IsHealthy);
    }

    [Fact]
    public void ARepairIsAppliedToTheJobAsItIsOnDiskRightNow()
    {
        var moved = WriteFile(Path.Combine("Moved", "song.mp3"));
        var job = Job(
            Complete("a", MusicPath("song.mp3")),
            new SavedTrack { Id = "b", IsComplete = false });
        Store.Save(job);
        var report = Scanner.Scan(job);

        // A download finishes while the check is open.
        var meanwhile = Store.Load(job.SourceUrl)!;
        var second = meanwhile.Tracks.Single(track => track.Id == "b");
        second.IsComplete = true;
        second.OutputPath = WriteFile("later.mp3");
        Store.Save(meanwhile);

        Assert.Equal(1, Scanner.Relocate(report));

        var reloaded = Store.Load(job.SourceUrl)!;
        Assert.Equal(moved, reloaded.Tracks.Single(track => track.Id == "a").OutputPath);
        // The newer completion must survive the repair.
        var later = reloaded.Tracks.Single(track => track.Id == "b");
        Assert.True(later.IsComplete);
        Assert.Equal(MusicPath("later.mp3"), later.OutputPath);
    }

    [Fact]
    public void ATrackThatFinishedAgainWhileTheCheckRanIsLeftAlone()
    {
        var job = Job(Complete("gone", MusicPath("gone.mp3")));
        Store.Save(job);
        var report = Scanner.Scan(job);

        var meanwhile = Store.Load(job.SourceUrl)!;
        meanwhile.Tracks.Single().OutputPath = WriteFile("recovered.mp3");
        Store.Save(meanwhile);

        Assert.Equal(0, Scanner.ForgetMissing(report));
        Assert.True(Store.Load(job.SourceUrl)!.Tracks.Single().IsComplete);
    }

    [Fact]
    public void ARepairDoesNotRecreateAJobThatWasDeleted()
    {
        var job = Job(Complete("gone", MusicPath("gone.mp3")));
        Store.Save(job);
        var report = Scanner.Scan(job);

        Store.Delete(job.SourceUrl);

        Assert.Equal(0, Scanner.ForgetMissing(report));
        Assert.Null(Store.Load(job.SourceUrl));
    }

    [Fact]
    public void ForgetMissingReopensOnlyTheTracksWhoseFileIsGone()
    {
        var job = Job(
            Complete("present", WriteFile("present.mp3")),
            Complete("gone", MusicPath("gone.mp3")));
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
    public void AnEmptyLeftoverFileIsDeletedWhenItsTrackIsReopened()
    {
        var empty = WriteFile("empty.mp3", string.Empty);
        var job = Job(Complete("empty", empty));
        Store.Save(job);

        Assert.Equal(1, Scanner.ForgetMissing(Scanner.Scan(job)));

        // The downloader skips a track whose output file already exists, so the truncated
        // file has to go or the re-download can never replace it.
        Assert.False(File.Exists(empty));
    }

    [Fact]
    public void AnUnreachableFolderIsNotReportedAsDeletedAndCannotBeReopened()
    {
        var offline = Path.Combine(_root, "offline");
        var job = new SavedJob
        {
            SourceUrl = "https://open.spotify.com/playlist/one",
            SourceName = "My Mix",
            OutputDirectory = offline,
            Tracks =
            [
                Complete("a", Path.Combine(offline, "a.mp3")),
                Complete("b", Path.Combine(offline, "b.mp3")),
            ],
        };
        Store.Save(job);

        var report = Scanner.Scan(job);

        Assert.Equal(2, report.Unreachable);
        Assert.Equal(0, report.Missing);
        Assert.False(report.IsHealthy);
        Assert.False(report.CanReopenMissing);
        Assert.Equal(0, Scanner.ForgetMissing(report));
        Assert.All(Store.Load(job.SourceUrl)!.Tracks, track => Assert.True(track.IsComplete));
    }

    [Fact]
    public void EveryJobIsCheckedAgainstItsOwnOutputFolder()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Directory.CreateDirectory(Path.Combine(first, "Moved"));
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "Moved", "song.mp3"), "audio");
        Store.Save(new SavedJob
        {
            SourceUrl = "https://open.spotify.com/playlist/one",
            SourceName = "First",
            OutputDirectory = first,
            Tracks = [Complete("a", Path.Combine(first, "song.mp3"))],
        });
        Store.Save(new SavedJob
        {
            SourceUrl = "https://open.spotify.com/album/two",
            SourceName = "Second",
            OutputDirectory = second,
            Tracks = [Complete("b", Path.Combine(second, "missing.mp3"))],
        });

        var reports = Scanner.ScanAll();

        Assert.Equal(2, reports.Count);
        // Check all must see the moved file, exactly as Check files does for one job.
        Assert.Single(reports, report => report.Moved == 1);
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
        var job = Job(Complete("a", WriteFile("a.mp3")));
        Store.Save(job);
        var entryPath = Path.Combine(LibraryDirectory, LibraryStore.KeyFor(job.SourceUrl) + ".json");
        var before = File.GetLastWriteTimeUtc(entryPath);

        var report = Scanner.Scan(job);

        Assert.Equal(0, Scanner.Relocate(report));
        Assert.Equal(0, Scanner.ForgetMissing(report));
        Assert.Equal(before, File.GetLastWriteTimeUtc(entryPath));
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
