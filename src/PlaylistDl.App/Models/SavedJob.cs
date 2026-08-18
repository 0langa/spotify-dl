namespace PlaylistDl.App.Models;

public sealed class SavedJob
{
    public string SourceUrl { get; set; } = string.Empty;

    public string SourceName { get; set; } = string.Empty;

    public string SourceType { get; set; } = "playlist";

    public string OutputDirectory { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Check this source for new tracks on its own while the app is open.</summary>
    public bool AutoSync { get; set; }

    /// <summary>When auto-sync last checked this source, whatever it found.</summary>
    public DateTimeOffset? LastAutoSyncUtc { get; set; }

    public List<SavedTrack> Tracks { get; set; } = [];
}

public sealed class SavedTrack
{
    public string Id { get; set; } = string.Empty;

    public string SpotifyUrl { get; set; } = string.Empty;

    public bool IsSelected { get; set; } = true;

    public bool IsComplete { get; set; }

    public string? OutputPath { get; set; }

    public string? SourceOverride { get; set; }

    public string? LastError { get; set; }
}
