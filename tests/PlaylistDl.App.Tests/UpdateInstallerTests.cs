using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "playlistdl-tests", Guid.NewGuid().ToString("N"));

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("new release binary");

    private static string PayloadHash => Convert.ToHexString(SHA256.HashData(Payload));

    private string Dir
    {
        get
        {
            Directory.CreateDirectory(_directory);
            return _directory;
        }
    }

    [Fact]
    public void ChecksumsAreParsedAndUnsafeEntriesDropped()
    {
        var text = string.Join('\n',
            "# a comment",
            $"{PayloadHash.ToLowerInvariant()}  PlaylistDL.exe",
            "abc  TooShortHash.txt",
            $"{PayloadHash}  ..\\escape.exe",
            $"{PayloadHash}  sub/dir.exe",
            $"{PayloadHash} *SHA256SUMS.txt",
            string.Empty);

        var checksums = UpdateInstaller.ParseChecksums(text);

        Assert.Equal(PayloadHash, checksums["PlaylistDL.exe"], ignoreCase: true);
        Assert.True(checksums.ContainsKey("SHA256SUMS.txt"));
        Assert.False(checksums.ContainsKey("TooShortHash.txt"));
        Assert.DoesNotContain(checksums.Keys, name => name.Contains("escape", StringComparison.Ordinal));
        Assert.DoesNotContain(checksums.Keys, name => name.Contains('/'));
    }

    [Fact]
    public async Task AVerifiedDownloadIsKept()
    {
        using var client = new HttpClient(new ReleaseHandler(Payload, $"{PayloadHash}  PlaylistDL.exe\n"));
        var installer = new UpdateInstaller(client);

        var prepared = await installer.PrepareAsync(Update(), Dir);

        Assert.True(File.Exists(prepared.ExecutablePath));
        Assert.Equal(Payload.Length, prepared.Size);
        Assert.Equal(PayloadHash, prepared.Sha256, ignoreCase: true);
        Assert.Empty(Directory.GetFiles(Dir, "*.part"));
    }

    [Fact]
    public async Task ATamperedDownloadIsRejectedAndDeleted()
    {
        var wrongHash = new string('a', 64);
        using var client = new HttpClient(new ReleaseHandler(Payload, $"{wrongHash}  PlaylistDL.exe\n"));
        var installer = new UpdateInstaller(client);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => installer.PrepareAsync(Update(), Dir));

        Assert.Contains("checksum", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(Dir));
    }

    [Fact]
    public async Task ReleaseChecksumsThatDoNotCoverTheExecutableAreRejected()
    {
        using var client = new HttpClient(new ReleaseHandler(Payload, $"{PayloadHash}  OTHER.exe\n"));
        var installer = new UpdateInstaller(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.PrepareAsync(Update(), Dir));
        Assert.Empty(Directory.GetFiles(Dir));
    }

    [Fact]
    public async Task AReleaseWithoutTheExpectedAssetsCannotBeInstalled()
    {
        using var client = new HttpClient(new ReleaseHandler(Payload, string.Empty));
        var installer = new UpdateInstaller(client);
        var update = new UpdateResult(
            new Version(9, 9, 9),
            "v9.9.9",
            new Uri("https://github.com/0langa/spotify-dl/releases/tag/v9.9.9"));

        Assert.False(update.CanInstall);
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.PrepareAsync(update, Dir));
    }

    [Fact]
    public void SwapKeepsTheRunningBinaryUntilTheNewOneIsInPlace()
    {
        var current = Path.Combine(Dir, "PlaylistDL.exe");
        var verified = Path.Combine(Dir, "downloaded.exe");
        File.WriteAllText(current, "old");
        File.WriteAllText(verified, "new");

        UpdateInstaller.Swap(current, verified);

        Assert.Equal("new", File.ReadAllText(current));
        // The retired binary stays as the rollback until the next start removes it.
        Assert.Equal("old", File.ReadAllText(current + ".previous"));

        UpdateInstaller.CleanupRetired(current);
        Assert.False(File.Exists(current + ".previous"));
    }

    [Fact]
    public void AFailedSwapRestoresTheCurrentVersion()
    {
        var current = Path.Combine(Dir, "PlaylistDL.exe");
        File.WriteAllText(current, "old");

        Assert.ThrowsAny<IOException>(
            () => UpdateInstaller.Swap(current, Path.Combine(Dir, "missing.exe")));

        Assert.Equal("old", File.ReadAllText(current));
        Assert.False(File.Exists(current + ".previous"));
    }

    [Fact]
    public void ALockedExecutableFailsBeforeAnythingIsMoved()
    {
        var current = Path.Combine(Dir, "PlaylistDL.exe");
        File.WriteAllText(current, "old");
        using var block = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.ThrowsAny<IOException>(
            () => UpdateInstaller.Swap(current, Path.Combine(Dir, "downloaded.exe")));

        // The rename never happened, so the running binary is untouched at its own path.
        Assert.False(File.Exists(current + ".previous"));
    }

    [Fact]
    public void AFailedRollbackTellsTheUserWhichFileStillWorks()
    {
        // The rollback only fails when a second fault hits the restore itself; the
        // message is what the user has to act on, so it names the file to rename back.
        var retired = Path.Combine(Dir, "PlaylistDL.exe.previous");

        var failure = new UpdateRollbackException(
            retired,
            new IOException("copy failed"),
            new IOException("restore failed"));

        Assert.Contains("PlaylistDL.exe.previous", failure.Message, StringComparison.Ordinal);
        Assert.Contains("PlaylistDL.exe", failure.Message, StringComparison.Ordinal);
        Assert.Contains("copy failed", failure.Message, StringComparison.Ordinal);
        Assert.Contains("restore failed", failure.Message, StringComparison.Ordinal);
        Assert.Equal(retired, failure.RetiredExecutable);
    }

    [Fact]
    public async Task ACancelledDownloadDoesNotLeaveAFileBehind()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(
            new ReleaseHandler(Payload, $"{PayloadHash}  PlaylistDL.exe\n", cancellation));
        var installer = new UpdateInstaller(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => installer.PrepareAsync(Update(), Dir, null, cancellation.Token));

        Assert.False(File.Exists(Path.Combine(Dir, "PlaylistDL.exe")));
    }

    [Fact]
    public void CleanupLeavesTheCurrentExecutableAlone()
    {
        var current = Path.Combine(Dir, "PlaylistDL.exe");
        File.WriteAllText(current, "current");

        UpdateInstaller.CleanupRetired(current);

        Assert.Equal("current", File.ReadAllText(current));
        Assert.False(File.Exists(current + ".previous"));
    }

    [Fact]
    public void FinishedDownloadsAreClearedAtStartup()
    {
        var updates = Path.Combine(Dir, "updates");
        Directory.CreateDirectory(Path.Combine(updates, "v9.9.9"));
        File.WriteAllText(Path.Combine(updates, "v9.9.9", "PlaylistDL.exe"), "downloaded");

        UpdateInstaller.CleanupDownloads(updates);
        Assert.False(Directory.Exists(updates));

        // Running it again on a folder that is already gone must stay silent.
        UpdateInstaller.CleanupDownloads(updates);
    }

    [Fact]
    public async Task AnImplausiblyLargeChecksumFileIsRejected()
    {
        var oversized = new string('a', 2 * 1024 * 1024);
        using var client = new HttpClient(new ReleaseHandler(Payload, oversized));
        var installer = new UpdateInstaller(client);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => installer.PrepareAsync(Update(), Dir));

        Assert.Contains("large", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(Dir));
    }

    [Fact]
    public async Task ADownloadLargerThanThePublishedAssetIsRejected()
    {
        var bloated = new byte[Payload.Length * 4];
        using var client = new HttpClient(new ReleaseHandler(bloated, $"{PayloadHash}  PlaylistDL.exe\n"));
        var installer = new UpdateInstaller(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.PrepareAsync(Update(), Dir));
        Assert.Empty(Directory.GetFiles(Dir));
    }

    [Fact]
    public async Task AFailedDownloadLeavesNoPartialFile()
    {
        using var client = new HttpClient(new FailingHandler($"{PayloadHash}  PlaylistDL.exe\n"));
        var installer = new UpdateInstaller(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => installer.PrepareAsync(Update(), Dir));

        Assert.Empty(Directory.GetFiles(Dir));
    }

    private static UpdateResult Update() => new(
        new Version(9, 9, 9),
        "v9.9.9",
        new Uri("https://github.com/0langa/spotify-dl/releases/tag/v9.9.9"),
        new ReleaseAsset("PlaylistDL.exe", new Uri("https://github.com/0langa/spotify-dl/releases/download/v9.9.9/PlaylistDL.exe"), Payload.Length),
        new ReleaseAsset("SHA256SUMS.txt", new Uri("https://github.com/0langa/spotify-dl/releases/download/v9.9.9/SHA256SUMS.txt"), 128));

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

    private sealed class FailingHandler(string checksums) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var isChecksums = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(
                isChecksums ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(isChecksums ? checksums : "unavailable", Encoding.UTF8),
            });
        }
    }

    private sealed class ReleaseHandler(
        byte[] payload,
        string checksums,
        CancellationTokenSource? cancelBeforeExecutable = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("github.com", request.RequestUri?.Host);
            var isChecksums = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal);
            if (!isChecksums)
            {
                cancelBeforeExecutable?.Cancel();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = isChecksums
                    ? new StringContent(checksums, Encoding.UTF8)
                    : new ByteArrayContent(payload),
            });
        }
    }
}

public sealed class ReleaseAssetSelectionTests
{
    [Fact]
    public void OnlyHttpsGitHubAssetsWithAnExactNameAreUsed()
    {
        var assets = new List<UpdateService.GitHubAsset>
        {
            new()
            {
                Name = "PlaylistDL.exe",
                DownloadUrl = "http://github.com/0langa/spotify-dl/releases/download/v1/PlaylistDL.exe",
                Size = 10,
            },
            new()
            {
                Name = "PlaylistDL.exe",
                DownloadUrl = "https://evil.example.com/PlaylistDL.exe",
                Size = 10,
            },
            new()
            {
                Name = "PlaylistDL.exe",
                DownloadUrl = "https://github.com/0langa/spotify-dl/releases/download/v1/PlaylistDL.exe",
                Size = 42,
            },
        };

        var selected = UpdateService.SelectAsset(assets, "PlaylistDL.exe");

        Assert.NotNull(selected);
        Assert.Equal("github.com", selected.Url.Host);
        Assert.Equal(42, selected.Size);
        Assert.Null(UpdateService.SelectAsset(assets, "SHA256SUMS.txt"));
        Assert.Null(UpdateService.SelectAsset(null, "PlaylistDL.exe"));
    }
}
