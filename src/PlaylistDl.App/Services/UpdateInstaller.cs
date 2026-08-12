using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace PlaylistDl.App.Services;

/// <summary>Raised when an update failed and the previous executable could not be restored.</summary>
public sealed class UpdateRollbackException(string retiredExecutable, Exception failure, Exception rollbackFailure)
    : IOException(
        $"The update failed ({failure.Message}) and the previous version could not be restored " +
        $"({rollbackFailure.Message}). The working version is still on disk as " +
        $"{Path.GetFileName(retiredExecutable)}; rename it back to " +
        $"{Path.GetFileNameWithoutExtension(retiredExecutable)} to keep using it.",
        failure)
{
    public string RetiredExecutable { get; } = retiredExecutable;
}

/// <summary>Outcome of preparing an update for installation.</summary>
public sealed record PreparedUpdate(string ExecutablePath, string Sha256, long Size);

/// <summary>
/// Downloads a published release, verifies it against the release checksums, and swaps it
/// in without ever writing over the running binary.
/// </summary>
/// <remarks>
/// Windows allows renaming a running executable but not overwriting it, so the swap moves
/// the current file aside, puts the verified one in its place, and restores the old file
/// if anything fails. The retired file is removed by the next start.
/// </remarks>
public sealed class UpdateInstaller
{
    private const string RetiredSuffix = ".previous";
    private const string PartialSuffix = ".part";
    // The published executable is ~150 MB; these bounds stop a hostile or broken endpoint
    // from streaming without end before the checksum can reject it.
    private const long MaxChecksumBytes = 1 << 20;
    private const long MaxExecutableBytes = 1L << 30;

    private readonly HttpClient _client;

    public UpdateInstaller(HttpClient? client = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>Parses a `sha256  filename` checksum listing.</summary>
    public static IReadOnlyDictionary<string, string> ParseChecksums(string text)
    {
        var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var entry = line.Trim();
            if (entry.Length == 0 || entry.StartsWith('#'))
            {
                continue;
            }

            var separator = entry.IndexOf(' ');
            if (separator <= 0)
            {
                continue;
            }

            var hash = entry[..separator].Trim();
            var name = entry[separator..].TrimStart(' ', '*').Trim();
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || name.Length == 0 ||
                Path.GetFileName(name) != name)
            {
                continue;
            }

            checksums[name] = hash;
        }

        return checksums;
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Downloads the release executable and keeps it only if its checksum matches.</summary>
    /// <exception cref="InvalidDataException">The release is unusable or fails verification.</exception>
    public async Task<PreparedUpdate> PrepareAsync(
        UpdateResult update,
        string targetDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (update.Executable is null || update.Checksums is null)
        {
            throw new InvalidDataException(
                "This release does not publish a verifiable executable. Open the release page instead.");
        }

        Directory.CreateDirectory(targetDirectory);
        var checksums = ParseChecksums(
            await ReadBoundedTextAsync(update.Checksums.Url, MaxChecksumBytes, cancellationToken));
        if (!checksums.TryGetValue(update.Executable.Name, out var expected))
        {
            throw new InvalidDataException(
                $"The release checksums do not cover {update.Executable.Name}.");
        }

        var target = Path.Combine(targetDirectory, update.Executable.Name);
        var partial = target + PartialSuffix;
        File.Delete(partial);
        try
        {
            await DownloadAsync(update.Executable, partial, progress, cancellationToken);

            var actual = ComputeSha256(partial);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The downloaded update did not match the published checksum and was discarded.");
            }

            File.Move(partial, target, overwrite: true);
            return new PreparedUpdate(target, actual, new FileInfo(target).Length);
        }
        catch
        {
            // Cancelled, failed, or rejected: never leave a partial download behind.
            TryDelete(partial);
            throw;
        }
    }

    private async Task<string> ReadBoundedTextAsync(
        Uri url,
        long limit,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit)
        {
            throw new InvalidDataException("The release checksum file is implausibly large.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new InvalidDataException("The release checksum file is implausibly large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task DownloadAsync(
        ReleaseAsset asset,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(
            asset.Url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        var limit = Math.Min(MaxExecutableBytes, total > 0 ? total * 2 : MaxExecutableBytes);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                copied += read;
                if (copied > limit)
                {
                    throw new InvalidDataException(
                        "The update download exceeded the size published with the release.");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (total > 0)
                {
                    progress?.Report(Math.Min(100d, copied * 100d / total));
                }
            }
        }
    }

    /// <summary>
    /// Puts the verified executable in place of the current one, keeping the current file
    /// as a rollback until it has been replaced successfully.
    /// </summary>
    public static void Swap(string currentExecutable, string verifiedExecutable)
    {
        var retired = currentExecutable + RetiredSuffix;
        File.Delete(retired);
        // Renaming the running binary is allowed; overwriting it is not.
        File.Move(currentExecutable, retired);
        try
        {
            File.Copy(verifiedExecutable, currentExecutable, overwrite: true);
        }
        catch (Exception failure)
        {
            try
            {
                File.Delete(currentExecutable);
                File.Move(retired, currentExecutable);
            }
            catch (Exception rollbackFailure)
            {
                // The rollback itself failed: say exactly where the working binary is
                // instead of letting the caller claim the old version was kept.
                throw new UpdateRollbackException(retired, failure, rollbackFailure);
            }

            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Leftovers are cleared by the startup cleanup.
        }
    }

    /// <summary>Removes the executable retired by a previous update, if it is still there.</summary>
    public static void CleanupRetired(string currentExecutable)
    {
        try
        {
            File.Delete(currentExecutable + RetiredSuffix);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The old binary is still locked; the next start removes it.
        }
    }

    /// <summary>Deletes update downloads left behind by an earlier session.</summary>
    public static void CleanupDownloads(string updatesRoot)
    {
        try
        {
            if (Directory.Exists(updatesRoot))
            {
                Directory.Delete(updatesRoot, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A locked download is retried on the next start.
        }
    }

    /// <summary>Starts the freshly installed executable so the user lands in the new version.</summary>
    /// <returns>False when the new version could not be started and must be opened manually.</returns>
    public static bool Relaunch(string executable)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = executable, UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException or
            IOException or UnauthorizedAccessException)
        {
            // The update is installed either way; the user can start it from the folder.
            return false;
        }
    }
}
