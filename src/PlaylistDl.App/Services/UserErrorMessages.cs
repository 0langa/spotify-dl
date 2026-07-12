namespace PlaylistDl.App.Services;

public static class UserErrorMessages
{
    private static readonly string[] NetworkFailureMarkers =
    [
        "connecttimeout",
        "connection timed out",
        "failed to establish a new connection",
        "name resolution",
        "temporary failure in name resolution",
        "connection refused",
        "network is unreachable",
    ];

    public static string ForSourceResolution(Exception exception, bool spotifySource)
    {
        var detail = exception.Message;
        if (NetworkFailureMarkers.Any(marker => detail.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            var provider = spotifySource ? "Spotify" : "YouTube Music";
            return $"{provider} could not be reached from Playlist DL. " +
                   "An antivirus, VPN, or firewall may be blocking the app's extracted backend. " +
                   "Run Diagnose, then allow the backend path shown there.";
        }

        if (detail.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase))
        {
            return "YouTube is rate-limiting or bot-checking this connection. " +
                   "Add browser cookies under Settings, lower concurrency, or retry later.";
        }

        return detail;
    }
}
