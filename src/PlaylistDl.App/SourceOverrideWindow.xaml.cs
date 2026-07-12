using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using PlaylistDl.App.Models;

namespace PlaylistDl.App;

public sealed record SourceCandidate(
    string Url,
    string Title,
    string Subtitle,
    string TypeLabel,
    string DurationLabel);

public partial class SourceOverrideWindow : Window
{
    private readonly Func<Task<JsonElement>>? _search;
    private bool _clearRequested;

    public SourceOverrideWindow(TrackItem track, Func<Task<JsonElement>>? search = null)
    {
        InitializeComponent();
        _search = search;
        TrackTitle.Text = $"{track.Title} — {track.ArtistText}";
        SourceUrlBox.Text = track.SourceOverride ?? string.Empty;
        SourceUrlBox.Focus();
        SourceUrlBox.SelectAll();
        Loaded += async (_, _) => await LoadCandidatesAsync();
    }

    public string? SourceUrl { get; private set; }

    private async Task LoadCandidatesAsync()
    {
        if (_search is null)
        {
            SearchStatus.Text = "Automatic candidate search unavailable.";
            return;
        }

        try
        {
            var response = await _search();
            var candidates = ParseCandidates(response);
            CandidateList.ItemsSource = candidates;
            SearchStatus.Text = candidates.Count == 0
                ? "No candidates found — paste a URL manually."
                : $"{candidates.Count} candidates ranked by duration match. Double-click to use one.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SearchStatus.Text = $"Candidate search failed: {ex.Message}";
        }
    }

    public static List<SourceCandidate> ParseCandidates(JsonElement response)
    {
        var parsed = new List<SourceCandidate>();
        if (!response.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
        {
            return parsed;
        }

        foreach (var item in candidates.EnumerateArray())
        {
            var url = ReadString(item, "url");
            var title = ReadString(item, "title");
            if (url is null || title is null)
            {
                continue;
            }

            var artists = item.TryGetProperty("artists", out var artistsElement) &&
                artistsElement.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", artistsElement.EnumerateArray()
                        .Where(artist => artist.ValueKind == JsonValueKind.String)
                        .Select(artist => artist.GetString()))
                    : string.Empty;
            var album = ReadString(item, "album");
            var subtitle = string.IsNullOrEmpty(album) ? artists : $"{artists} · {album}";
            var isSong = ReadString(item, "result_type") == "song";
            var duration = item.TryGetProperty("duration_seconds", out var durationElement) &&
                durationElement.ValueKind == JsonValueKind.Number
                    ? durationElement.GetInt32()
                    : 0;
            var durationLabel = duration > 0 ? TimeSpan.FromSeconds(duration).ToString(@"m\:ss") : "?";
            if (item.TryGetProperty("duration_delta_seconds", out var delta) &&
                delta.ValueKind == JsonValueKind.Number)
            {
                var deltaSeconds = delta.GetInt32();
                durationLabel += deltaSeconds == 0 ? " (exact)" : $" ({deltaSeconds:+0;-0}s)";
            }

            parsed.Add(new SourceCandidate(url, title, subtitle, isSong ? "Music" : "Video", durationLabel));
        }

        return parsed;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void CandidateList_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is SourceCandidate candidate)
        {
            SourceUrlBox.Text = candidate.Url;
            ValidationText.Text = string.Empty;
        }
    }

    private void CandidateList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CandidateList.SelectedItem is SourceCandidate)
        {
            SaveButton_Click(sender, e);
        }
    }

    private void UseAutomaticButton_Click(object sender, RoutedEventArgs e)
    {
        _clearRequested = true;
        SourceUrl = null;
        DialogResult = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var value = SourceUrlBox.Text.Trim();
        if (value.Length > 0 && !IsSupportedSource(value))
        {
            ValidationText.Text = "Use an https://youtube.com, https://music.youtube.com, or https://youtu.be URL.";
            return;
        }

        SourceUrl = _clearRequested || value.Length == 0 ? null : value;
        DialogResult = true;
    }

    internal static bool IsSupportedSource(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
    }
}
