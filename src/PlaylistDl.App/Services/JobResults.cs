using System.Text.Json;

namespace PlaylistDl.App.Services;

public sealed record DownloadResult(
    string TrackId,
    string? Path,
    bool Success,
    string? Error = null,
    string? ErrorClass = null);

public sealed record JobFailureSummary(string? FailureClass, string? FailureHint);

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
            var trackId = ReadString(item, "track_id");
            if (string.IsNullOrEmpty(trackId))
            {
                continue;
            }

            var success = item.TryGetProperty("success", out var successElement) &&
                successElement.ValueKind == JsonValueKind.True;
            parsed.Add(new DownloadResult(
                trackId,
                ReadString(item, "path"),
                success,
                ReadString(item, "error"),
                ReadString(item, "error_class")));
        }

        return parsed;
    }

    public static JobFailureSummary ParseFailure(JsonElement message) => new(
        ReadString(message, "failure_class"),
        ReadString(message, "failure_hint"));

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
