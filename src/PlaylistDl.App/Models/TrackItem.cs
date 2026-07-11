using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace PlaylistDl.App.Models;

public sealed class TrackItem : INotifyPropertyChanged
{
    private int _progress;
    private string _status = "Ready";
    private bool _isSelected = true;
    private string? _outputPath;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("position")]
    public int Position { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("artists")]
    public List<string> Artists { get; init; } = [];

    [JsonIgnore]
    public string ArtistText => string.Join(", ", Artists);

    [JsonPropertyName("album")]
    public string Album { get; init; } = string.Empty;

    [JsonPropertyName("duration_seconds")]
    public int DurationSeconds { get; init; }

    [JsonIgnore]
    public string DurationText => TimeSpan.FromSeconds(DurationSeconds).ToString(@"m\:ss");

    [JsonPropertyName("spotify_url")]
    public string SpotifyUrl { get; init; } = string.Empty;

    public int Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    [JsonIgnore]
    public string? OutputPath
    {
        get => _outputPath;
        set => SetField(ref _outputPath, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
