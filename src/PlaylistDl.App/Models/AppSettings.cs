using System.IO;

namespace PlaylistDl.App.Models;

public sealed class AppSettings
{
    public string OutputDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "Playlist DL");

    public string Format { get; set; } = "mp3";

    public string Bitrate { get; set; } = "0";

    public int Threads { get; set; } = 2;

    public string? CookieFile { get; set; }

    public string? BackendExecutable { get; set; }

    public bool WriteM3u { get; set; } = true;

    public string NamingPreset { get; set; } = "position_artist_title";

    public bool CreateSourceFolder { get; set; } = true;

    public int ThrottleSeconds { get; set; }

    public string? YtDlpArgs { get; set; }

    public bool EmbedLyrics { get; set; }

    /// <summary>Normalize output loudness to the EBU R128 streaming target.</summary>
    public bool NormalizeLoudness { get; set; }

    /// <summary>Check every saved file before a track is reported as done.</summary>
    public bool VerifyDownloads { get; set; } = true;

    /// <summary>What to do when a track was already downloaded by an earlier job.</summary>
    /// <remarks>One of download, skip, copy, hardlink.</remarks>
    public string DuplicatePolicy { get; set; } = "download";

    public bool AutoUpdateCheck { get; set; } = true;

    /// <summary>How often sources marked "keep in sync" are checked; 0 turns it off.</summary>
    public int AutoSyncMinutes { get; set; }

    /// <summary>Use the official Spotify Web API with credentials from Credential Manager.</summary>
    /// <remarks>Only this flag is stored here; the credentials never enter this file.</remarks>
    public bool UseOfficialSpotifyApi { get; set; }

    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
}
