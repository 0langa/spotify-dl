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
            if (File.Exists(backend) && VerifyManifest(target))
            {
                return backend;
            }

            Directory.CreateDirectory(target);
            using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
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

            if (!File.Exists(backend) || !VerifyManifest(target))
            {
                throw new InvalidDataException("Embedded tool bundle failed integrity validation.");
            }

            return backend;
        }
    }

    private static bool VerifyManifest(string directory)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
            {
                var name = file.GetProperty("name").GetString();
                var expected = file.GetProperty("sha256").GetString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(expected))
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
        catch (JsonException)
        {
            return false;
        }
    }
}
