# Privacy

Playlist DL has no telemetry, advertising, analytics, or remote account service.

App stores settings, resumable job library, retained session logs, and extracted helper tools under `%LOCALAPPDATA%\PlaylistDL`. Saved jobs include source link, search text, or manifest path; track completion and failure state; manual source URLs; output directory; and download choices. Settings include download quality, file organization, concurrency, update preference, alternate-backend path, and optional cookie-file path. Cookie contents are not copied or logged.

Download providers receive normal requests needed to resolve metadata and media. Enabled automatic update checks run at most once per 20 hours; manual checks can run any time. Both make an unauthenticated request to GitHub's public latest-release API and send no playlist, track, cookie, or download-history data.

Deleting `%LOCALAPPDATA%\PlaylistDL` removes settings and extracted helper tools. Downloaded music remains in user-selected output directory.
