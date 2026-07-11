using System.Text.Json.Serialization;

namespace PlaylistDl.App.Models;

public sealed class PlaylistInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("owner")]
    public string Owner { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("source_url")]
    public string SourceUrl { get; init; } = string.Empty;

    [JsonPropertyName("tracks")]
    public List<TrackItem> Tracks { get; init; } = [];
}
