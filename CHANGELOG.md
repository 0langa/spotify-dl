# Changelog

## 1.1.0 - 2026-07-11

- Added Spotify album and single-track link support alongside playlists, including locale-prefixed URLs.
- Added per-track selection with select-all, plus live filtering by title, artist, or album.
- Added per-track download results (Done/Failed) and a Retry button that re-runs only failed tracks.
- Added audio format selection: MP3, M4A, Opus, FLAC, OGG, and WAV, with stream copy for M4A/Opus.
- Added optional .m3u8 playlist export preserving track order, and an Open folder button.
- Fixed backend stdio to always use UTF-8 so non-ASCII titles no longer corrupt the app protocol.
- Fixed start-request validation to report errors before a job is announced as started.

## 1.0.0 - 2026-07-11

- Added Windows WPF playlist downloader with Spotify public-link intake.
- Added YouTube Music/YouTube matching through pinned spotDL and yt-dlp backend.
- Added V0/320 kbps MP3 conversion, metadata, cover art, duplicate scanning, and ordered filenames.
- Added per-track/overall progress, two-worker default, cancellation between active batches, output picker, and optional cookie file.
- Added self-contained single-EXE packaging with embedded FFmpeg, FFprobe, Deno, backend integrity checks, checksums, CI, and release automation.
