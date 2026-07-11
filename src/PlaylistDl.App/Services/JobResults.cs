using System.Text.Json;

namespace PlaylistDl.App.Services;

public sealed record DownloadResult(string TrackId, string? Path, bool Success);

public static class JobResults
{
    public static IReadOnlyList<DownloadResult> Parse(JsonElement message)
    {
        if (!message.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<DownloadResult>();
        foreach (var item in results.EnumerateArray())
        {
            var trackId = item.TryGetProperty("track_id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
            if (string.IsNullOrEmpty(trackId))
            {
                continue;
            }

            var path = item.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
                ? pathElement.GetString()
                : null;
            var success = item.TryGetProperty("success", out var successElement) &&
                successElement.ValueKind == JsonValueKind.True;
            parsed.Add(new DownloadResult(trackId, path, success));
        }

        return parsed;
    }
}
