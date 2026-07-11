# Privacy

Playlist DL has no telemetry, advertising, analytics, or remote account service.

App stores settings and the most recent resumable job under `%LOCALAPPDATA%\PlaylistDL`. This includes source link or manifest path, track completion state, manual source URLs, output directory, download quality, file organization, concurrency, and optional cookie-file path. Cookie contents are not copied or logged.

Download providers receive normal requests needed to resolve metadata and media. The on-demand update button makes an unauthenticated request to GitHub's public latest-release API; it sends no playlist, track, cookie, or download-history data.

Deleting `%LOCALAPPDATA%\PlaylistDL` removes settings and extracted helper tools. Downloaded music remains in user-selected output directory.
