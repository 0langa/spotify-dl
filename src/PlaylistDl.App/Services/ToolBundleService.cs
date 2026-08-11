using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace PlaylistDl.App.Services;

public static class ToolBundleService
{
    private const string ResourceName = "PlaylistDl.Tools";
    private static readonly object Sync = new();

    public static string? TryResolveBackend()
    {
        lock (Sync)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var resource = assembly.GetManifestResourceStream(ResourceName);
            if (resource is null)
            {
                return null;
            }

            var version = assembly.GetName().Version?.ToString(3) ?? "dev";
            var target = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PlaylistDL",
                "tools",
                version);
            var backend = Path.Combine(target, "playlistdl-backend.exe");
            using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
            // The expected hashes come from the manifest inside the executable, never from
            // the extracted copy: an extracted manifest could be rewritten to match tampered
            // helpers in this user-writable folder.
            var expected = ReadEmbeddedManifest(archive);
            if (File.Exists(backend) && VerifyManifest(target, expected))
            {
                return backend;
            }

            Directory.CreateDirectory(target);
            var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Tool bundle contains an unsafe path.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            if (!File.Exists(backend) || !VerifyManifest(target, expected))
            {
                throw new InvalidDataException("Embedded tool bundle failed integrity validation.");
            }

            return backend;
        }
    }

    public static string? TryResolveBackendVersion()
    {
        if (TryResolveBackend() is null)
        {
            return null;
        }

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var resource = assembly.GetManifestResourceStream(ResourceName);
            if (resource is null)
            {
                return null;
            }

            using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
            using var document = JsonDocument.Parse(ReadEmbeddedManifest(archive));
            return document.RootElement.TryGetProperty("backend_version", out var version)
                ? version.GetString()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static string ReadEmbeddedManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("Embedded tool bundle has no manifest.");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool VerifyManifest(string directory, string manifestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
            {
                var name = file.GetProperty("name").GetString();
                var expected = file.GetProperty("sha256").GetString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(expected) ||
                    Path.GetFileName(name) != name)
                {
                    return false;
                }

                var path = Path.Combine(directory, name);
                if (!File.Exists(path))
                {
                    return false;
                }

                using var stream = File.OpenRead(path);
                var actual = Convert.ToHexString(SHA256.HashData(stream));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or KeyNotFoundException or
            UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
    }
}
