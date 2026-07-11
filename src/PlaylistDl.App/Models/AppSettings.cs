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
}
