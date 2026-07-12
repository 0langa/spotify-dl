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

    public bool WriteM3u { get; set; } = true;

    public string NamingPreset { get; set; } = "position_artist_title";

    public bool CreateSourceFolder { get; set; } = true;

    public int ThrottleSeconds { get; set; }

    public string? YtDlpArgs { get; set; }

    public bool EmbedLyrics { get; set; }

    public bool AutoUpdateCheck { get; set; } = true;

    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
}
