using System.IO;
using System.Text.Json;
using PlaylistDl.App.Models;

namespace PlaylistDl.App.Services;

public sealed class SettingsService
{
    private const int ReadAttempts = 5;
    private const int ReadRetryDelayMilliseconds = 120;

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlaylistDL",
            "settings.json");
    }

    /// <summary>True when the last load fell back to defaults with a settings file present.</summary>
    public bool LastLoadFailed { get; private set; }

    public AppSettings Load()
    {
        LastLoadFailed = false;
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        // Security software and backup agents lock this file briefly. Falling straight
        // back to defaults would silently reset every setting and then persist that
        // reset on the next save, so a transient lock is retried first.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), _jsonOptions)
                    ?? new AppSettings();
            }
            catch (JsonException)
            {
                PreserveUnreadableSettings();
                return new AppSettings();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts - 1)
                {
                    // The stored settings exist but stayed unreadable. Saving now would
                    // replace them with defaults, so the next save keeps a copy first.
                    LastLoadFailed = true;
                    return new AppSettings();
                }

                Thread.Sleep(ReadRetryDelayMilliseconds);
            }
        }
    }

    /// <summary>Keeps a corrupt settings file instead of overwriting it with defaults.</summary>
    private void PreserveUnreadableSettings()
    {
        try
        {
            var backup = _settingsPath + ".corrupt";
            File.Move(_settingsPath, backup, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Defaults still load when the unreadable file cannot be set aside.
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("Settings directory is unavailable.");
        Directory.CreateDirectory(directory);
        PreserveUnreadableSettingsBeforeOverwrite();
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, _jsonOptions));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
        LastLoadFailed = false;
    }

    /// <summary>Copies settings that could not be read before defaults replace them.</summary>
    private void PreserveUnreadableSettingsBeforeOverwrite()
    {
        if (!LastLoadFailed || !File.Exists(_settingsPath))
        {
            return;
        }

        try
        {
            File.Copy(_settingsPath, _settingsPath + ".unreadable", overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Still unreadable; the save proceeds so the app stays usable.
        }
    }
}
