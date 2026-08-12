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
    public void AGoneReusedFileFromAnotherJobDoesNotFreezeTheRestOfTheJob()
    {
        // Duplicate policy "skip" records the other job's path verbatim, so a job can
        // point outside its own output folder. That folder being deleted for good must
        // not hide or block the repairs this job's own missing files still need.
        var otherJob = Path.Combine(_root, "other-job");
        var job = Job(
            Complete("reused", Path.Combine(otherJob, "reused.mp3")),
            Complete("gone", MusicPath("gone.mp3")),
            Complete("here", WriteFile("here.mp3")));
        Store.Save(job);

        var report = Scanner.Scan(job);

        Assert.Equal(1, report.Unreachable);
        Assert.Equal(1, report.Missing);
        Assert.Contains("1 missing or empty", report.Summary);
        Assert.True(report.CanReopenMissing);

        Assert.Equal(1, Scanner.ForgetMissing(report));
        var saved = Store.Load(job.SourceUrl)!;
        Assert.False(saved.Tracks.Single(track => track.Id == "gone").IsComplete);
        // The unreachable path is left alone: nothing proves that file is deleted.
        Assert.True(saved.Tracks.Single(track => track.Id == "reused").IsComplete);
        Assert.True(saved.Tracks.Single(track => track.Id == "here").IsComplete);
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
    public void AFolderThatCouldNotBeReadInFullAdoptsNothingAndReopensNothing()
    {
        WriteFile(Path.Combine("Moved", "song.mp3"));
        var job = Job(
            Complete("a", MusicPath("song.mp3")),
            Complete("gone", MusicPath("gone.mp3")));
        Store.Save(job);
        var scanner = new LibraryHealthScanner(Store, _ => PartialWalk());

        var report = scanner.Scan(job);

        Assert.False(report.ScanComplete);
        Assert.False(report.CanReopenMissing);
        // A name can only be proven unique by a folder that was read in full.
        Assert.Equal(0, report.Moved);
        Assert.Equal(0, scanner.Relocate(report));
        Assert.Equal(0, scanner.ForgetMissing(report));
        Assert.Contains("read only in part", report.Summary);
        Assert.All(Store.Load(job.SourceUrl)!.Tracks, track => Assert.True(track.IsComplete));
    }

    private static LibraryHealthScanner.FolderWalk PartialWalk() =>
        new(["first.mp3"], Complete: false);

    [Fact]
    public void ASubfolderRemovedInsideAReadableRootIsRepairableRatherThanUnreachable()
    {
        var moved = WriteFile(Path.Combine("Now here", "song.mp3"));
        var job = Job(Complete("a", MusicPath(Path.Combine("Was here", "song.mp3"))));

        var report = Scanner.Scan(job);

        // The output folder reads fine, so a folder the user reorganized is not "offline".
        Assert.Equal(0, report.Unreachable);
        Assert.Equal(1, report.Moved);
        Assert.Equal(moved, report.Tracks.Single().FoundPath);
        Assert.True(report.CanReopenMissing);
    }

    [Fact]
    public void AFileThatCameBackBeforeTheRepairRanIsNotReopened()
    {
        var job = Job(Complete("gone", MusicPath("gone.mp3")));
        Store.Save(job);
        var report = Scanner.Scan(job);

        // The probe failed for a moment; the file is there when the repair runs.
        WriteFile("gone.mp3");

        Assert.Equal(0, Scanner.ForgetMissing(report));
        Assert.True(Store.Load(job.SourceUrl)!.Tracks.Single().IsComplete);
    }

    [Fact]
    public void PathsAreComparedInOneCanonicalForm()
    {
        var file = WriteFile(Path.Combine("Sub", "song.mp3"));
        var job = Job(Complete("a", Path.Combine(MusicDirectory, "Sub", ".", "song.mp3")));

        var report = Scanner.Scan(job);

        Assert.Equal(TrackFileState.Present, report.Tracks.Single().State);
        Assert.True(report.IsHealthy);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void AFileAnotherJobClaimsInADifferentlyWrittenPathIsNotAdopted()
    {
        var other = WriteFile(Path.Combine("Other", "song.mp3"));
        Store.Save(new SavedJob
        {
            SourceUrl = "https://open.spotify.com/album/two",
            SourceName = "Album",
            OutputDirectory = MusicDirectory,
            Tracks = [Complete("b", Path.Combine(MusicDirectory, "Other", ".", "song.mp3"))],
        });
        var job = Job(Complete("a", MusicPath("song.mp3")));

        Assert.Equal(TrackFileState.Missing, Scanner.Scan(job).Tracks.Single().State);
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void AFolderTheUserReorganizedInsideAReadableRootIsActuallyReopened()
    {
        // The scan calls this missing rather than unreachable, so the repair has to agree.
        var job = Job(Complete("gone", MusicPath(Path.Combine("Old album", "song.mp3"))));
        Store.Save(job);

        var report = Scanner.Scan(job);

        Assert.Equal(TrackFileState.Missing, report.Tracks.Single().State);
        Assert.Equal(1, Scanner.ForgetMissing(report));
        Assert.False(Store.Load(job.SourceUrl)!.Tracks.Single().IsComplete);
    }

    [Fact]
    public void CheckAllNeverHandsOneFileToTwoJobs()
    {
        var moved = WriteFile(Path.Combine("Moved", "song.mp3"));
        foreach (var (url, name) in new[]
                 {
                     ("https://open.spotify.com/playlist/one", "First"),
                     ("https://open.spotify.com/album/two", "Second"),
                 })
        {
            Store.Save(new SavedJob
            {
                SourceUrl = url,
                SourceName = name,
                OutputDirectory = MusicDirectory,
                Tracks = [Complete(name, MusicPath("song.mp3"))],
            });
        }

        var reports = Scanner.ScanAll();

        Assert.Equal(1, reports.Sum(report => report.Moved));
        Assert.Equal(1, reports.Sum(report => report.Missing));
        Assert.Single(
            reports.SelectMany(report => report.Tracks),
            track => track.FoundPath == moved);
    }

    [Fact]
    public void AnAdoptedFileThatDisappearedAfterTheScanIsNotWrittenToTheLibrary()
    {
        var moved = WriteFile(Path.Combine("Moved", "song.mp3"));
        var job = Job(Complete("a", MusicPath("song.mp3")));
        Store.Save(job);
        var report = Scanner.Scan(job);

        File.Delete(moved);

        Assert.Equal(0, Scanner.Relocate(report));
        Assert.Equal(MusicPath("song.mp3"), Store.Load(job.SourceUrl)!.Tracks.Single().OutputPath);
    }

    [Fact]
    public void AnEmptyLeftoverIsKeptWhenItsTrackWasNotReopened()
    {
        var reopened = MusicPath("gone.mp3");
        var leftover = WriteFile("empty.mp3", string.Empty);
        var job = Job(Complete("gone", reopened), Complete("empty", leftover));
        Store.Save(job);
        var report = Scanner.Scan(job);

        // The empty track finished again with a different file while the check was open.
        var meanwhile = Store.Load(job.SourceUrl)!;
        meanwhile.Tracks.Single(track => track.Id == "empty").OutputPath = WriteFile("fixed.mp3");
        Store.Save(meanwhile);

        Assert.Equal(1, Scanner.ForgetMissing(report));
        // Its leftover belongs to a track the repair left alone, so it stays.
        Assert.True(File.Exists(leftover));
    }

    [Fact]
    public void FilesUnderALinkedFolderAreNeverAdopted()
    {
        var target = Path.Combine(_root, "outside");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "song.mp3"), "audio");
        Directory.CreateDirectory(MusicDirectory);
        var link = MusicPath("linked");
        Assert.True(TryLink(link, target), "no symlink or junction could be created");
        var job = Job(Complete("a", MusicPath("song.mp3")));

        var report = Scanner.Scan(job);

        Assert.Equal(0, report.Moved);
        Assert.Equal(TrackFileState.Missing, report.Tracks.Single().State);
        // The part of the tree behind the link was not read, so nothing may be reopened.
        Assert.False(report.ScanComplete);
        Assert.False(report.CanReopenMissing);
        Assert.True(File.Exists(Path.Combine(link, "song.mp3")));
    }

    /// <summary>Links a folder, as a symlink or else as a junction.</summary>
    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", ["/c", "mklink", "/J", link, target])
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            process?.WaitForExit(10_000);
            return Directory.Exists(link);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public void ATrackWithoutAnIdIsStillMatchedByItsUrlWhenTheRepairIsApplied()
    {
        var job = Job();
        job.Tracks.Add(new SavedTrack
        {
            Id = string.Empty,
            SpotifyUrl = "https://open.spotify.com/track/only-url",
            IsComplete = true,
            OutputPath = MusicPath("gone.mp3"),
        });
        Store.Save(job);

        Assert.Equal(1, Scanner.ForgetMissing(Scanner.Scan(job)));
        Assert.False(Store.Load(job.SourceUrl)!.Tracks.Single().IsComplete);
    }

    [Theory]
    [InlineData(@"C:\Music", @"C:\Music\a.mp3", true)]
    [InlineData(@"C:\Music\", @"C:\Music\Album\a.mp3", true)]
    [InlineData(@"C:\Music", @"C:\Music2\a.mp3", false)]
    [InlineData(@"C:\", @"C:\Music\a.mp3", true)]
    [InlineData(@"C:\", @"D:\Music\a.mp3", false)]
    [InlineData(null, @"C:\Music\a.mp3", false)]
    public void APathIsUnderAFolderIncludingADriveRoot(string? folder, string path, bool expected)
    {
        // A drive root keeps its trailing separator, so the prefix test has to allow for it
        // or every job whose output folder is a drive root is frozen.
        Assert.Equal(expected, LibraryHealthScanner.IsUnder(path, folder));
    }

    [Fact]
    public void AMovedOutputFolderIsFoundAgainAndTheJobFollowsIt()
    {
        // The whole music folder was moved: from the recorded path nothing can be found.
        var gone = Path.Combine(_root, "old-music");
        var moved = Path.Combine(_root, "new-music");
        Directory.CreateDirectory(Path.Combine(moved, "Album"));
        File.WriteAllText(Path.Combine(moved, "Album", "song.mp3"), "audio");
        var job = new SavedJob
        {
            SourceUrl = "https://open.spotify.com/playlist/one",
            SourceName = "My Mix",
            OutputDirectory = gone,
            Tracks = [Complete("a", Path.Combine(gone, "Album", "song.mp3"))],
        };
        Store.Save(job);

        var blind = Scanner.Scan(job);
        Assert.Equal(1, blind.Unreachable);
        Assert.False(blind.RootAvailable);

        // Pointed at the folder the user found, the same check repairs the job.
        var report = Scanner.Scan(job, moved);

        Assert.Equal(1, report.Moved);
        Assert.Equal(1, Scanner.Relocate(report));
        var reloaded = Store.Load(job.SourceUrl)!;
        Assert.Equal(Path.Combine(moved, "Album", "song.mp3"), reloaded.Tracks.Single().OutputPath);
        // The job also has to point at the new folder, or the next download writes nowhere.
        Assert.Equal(moved, reloaded.OutputDirectory);
    }

    [Fact]
    public void AnAvailableOutputFolderIsNotRewrittenByARelocation()
    {
        var moved = WriteFile(Path.Combine("Moved", "song.mp3"));
        var job = Job(Complete("a", MusicPath("song.mp3")));
        Store.Save(job);

        Assert.Equal(1, Scanner.Relocate(Scanner.Scan(job)));

        var reloaded = Store.Load(job.SourceUrl)!;
        Assert.Equal(MusicDirectory, reloaded.OutputDirectory);
        Assert.Equal(moved, reloaded.Tracks.Single().OutputPath);
    }

    [Fact]
    public void AFileOutsideAnAvailableRootIsNotReopenedByTheReProbe()
    {
        // Recorded under the "skip" duplicate policy: the file lives in another job's
        // folder, which is gone. The scan calls that unreachable and the repair must agree.
        var elsewhere = Path.Combine(_root, "other-job");
        var job = Job(
            Complete("reused", Path.Combine(elsewhere, "song.mp3")),
            Complete("gone", MusicPath("gone.mp3")));
        Store.Save(job);

        var report = Scanner.Scan(job);

        Assert.Equal(1, report.Unreachable);
        Assert.Equal(1, Scanner.ForgetMissing(report));
        var reloaded = Store.Load(job.SourceUrl)!;
        Assert.True(reloaded.Tracks.Single(track => track.Id == "reused").IsComplete);
        Assert.False(reloaded.Tracks.Single(track => track.Id == "gone").IsComplete);
    }

    [Fact]
    public void CountsStayVisibleWhenSomeFilesAreInAnUnavailableFolder()
    {
        var offline = Path.Combine(_root, "offline");
        var job = new SavedJob
        {
            SourceUrl = "https://open.spotify.com/playlist/one",
            SourceName = "My Mix",
            OutputDirectory = offline,
            Tracks =
            [
                Complete("away", Path.Combine(offline, "a.mp3")),
                Complete("nopath", null),
            ],
        };

        var summary = Scanner.Scan(job).Summary;

        Assert.Contains("1 missing or empty", summary);
        Assert.Contains("1 in a folder that is not available", summary);
    }

    [Fact]
    public void OneFileReadsAsOneFile()
    {
        var job = Job(Complete("a", WriteFile("a.mp3")));

        Assert.Equal("1 file present", Scanner.Scan(job).Summary);
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
