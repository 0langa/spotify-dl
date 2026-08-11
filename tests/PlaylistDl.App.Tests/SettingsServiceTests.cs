using PlaylistDl.App.Models;
using PlaylistDl.App.Services;
using Xunit;

namespace PlaylistDl.App.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "playlistdl-tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private SettingsService Service => new(SettingsPath);

    [Fact]
    public void SaveThenLoadRoundTripsEverySetting()
    {
        Service.Save(new AppSettings
        {
            Format = "flac",
            Bitrate = "320k",
            Threads = 3,
            CookieFile = @"C:\cookies.txt",
            NamingPreset = "artist_title",
            ThrottleSeconds = 2,
            EmbedLyrics = true,
            AutoUpdateCheck = false,
        });

        var loaded = Service.Load();

        Assert.Equal("flac", loaded.Format);
        Assert.Equal("320k", loaded.Bitrate);
        Assert.Equal(3, loaded.Threads);
        Assert.Equal(@"C:\cookies.txt", loaded.CookieFile);
        Assert.Equal("artist_title", loaded.NamingPreset);
        Assert.Equal(2, loaded.ThrottleSeconds);
        Assert.True(loaded.EmbedLyrics);
        Assert.False(loaded.AutoUpdateCheck);
    }

    [Fact]
    public void MissingSettingsFileLoadsDefaults()
    {
        var loaded = Service.Load();

        Assert.Equal("mp3", loaded.Format);
        Assert.True(loaded.AutoUpdateCheck);
    }

    [Fact]
    public void CorruptSettingsAreKeptAsideInsteadOfBeingOverwritten()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{not json");

        var loaded = Service.Load();

        Assert.Equal("mp3", loaded.Format);
        Assert.False(File.Exists(SettingsPath));
        Assert.True(File.Exists(SettingsPath + ".corrupt"));
        Assert.Equal("{not json", File.ReadAllText(SettingsPath + ".corrupt"));
    }

    [Fact]
    public async Task ALockedSettingsFileIsRetriedAndKeepsTheStoredValues()
    {
        Service.Save(new AppSettings { Format = "opus" });

        var exclusive = new FileStream(
            SettingsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        // Released while Load is still retrying, exactly like a virus scanner
        // holding the file open for a moment.
        var release = Task.Run(async () =>
        {
            await Task.Delay(80);
            await exclusive.DisposeAsync();
        });

        var loaded = Service.Load();
        await release;

        Assert.Equal("opus", loaded.Format);
    }

    [Fact]
    public void SettingsThatStayLockedAreCopiedAsideBeforeDefaultsReplaceThem()
    {
        Service.Save(new AppSettings { Format = "flac" });
        var service = new SettingsService(SettingsPath);

        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var loaded = service.Load();

            Assert.Equal("mp3", loaded.Format);
            Assert.True(service.LastLoadFailed);
        }

        service.Save(new AppSettings { Format = "mp3" });

        Assert.False(service.LastLoadFailed);
        Assert.True(File.Exists(SettingsPath + ".unreadable"));
        Assert.Contains("flac", File.ReadAllText(SettingsPath + ".unreadable"));
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
