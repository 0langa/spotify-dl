# Changelog

## Unreleased

- Replaced raw provider timeout traces during resolve/search with concise antivirus, VPN, firewall, and Diagnose guidance.
- Added release checksum/signature verification, Microsoft Defender scanning when available, and a repeated frozen-backend shutdown smoke gate.
- Promoted the current development roadmap into the tracked repository.

## 1.6.0 - 2026-07-12

- Added a download queue: line up several playlists, albums, searches, or imports (each with the settings active at add time) and run them back to back with one click.
- Queue progress swaps the track list per job, cancellation clears the remaining queue, and the completion alert fires once at the end.
- Added a silent daily update check on startup (Settings toggle) that turns the update button into a download badge when a newer release exists.

## 1.5.0 - 2026-07-12

- Added free-text search: type an artist and title instead of a Spotify URL and download straight from ranked YouTube Music song results — a fully Spotify-independent intake that keeps working if the experimental resolver breaks.
- Search jobs are first-class: saved to the library, resumable, and syncable like any other source.
- Added optional lyrics embedding (public providers) into downloaded audio tags.
- Spotify resolution failures now show the guidance banner (search, manifest import, network diagnosis) instead of only an error dialog.

## 1.4.0 - 2026-07-12

- Added a job library: every resolved playlist, album, track, or import is remembered under a new Library button with progress counts and timestamps.
- Added one-click Sync per saved playlist: re-reads the source and selects only new or unfinished tracks for download, reporting how many new tracks appeared.
- Added Open/Delete management for saved jobs; deleting a job never touches downloaded files.
- The existing single last-job resume is migrated into the library automatically and keeps working unchanged.

## 1.3.1 - 2026-07-12

- Added ranked source candidates to the per-track Source dialog: YouTube Music/YouTube results ordered by duration match, with one click or double-click to apply, replacing blind manual URL hunting.
- Added a quick audio-format selector to the main window that stays in sync with Settings.
- Added a completion alert (sound + taskbar flash) when a download job finishes while the window is in the background.
- Backend gained a search_sources command (deduped songs+videos search with duration-delta ranking).

## 1.3.0 - 2026-07-12

- Added per-track failure reasons: failed rows now explain themselves on hover instead of a bare `Failed`.
- Added failure classification with an actionable in-app banner, including cookie-file guidance when YouTube rate-limits or bot-checks the network.
- Added a network diagnosis button that probes Spotify/YouTube endpoints from the backend and names the executing binary path, so antivirus/firewall per-app blocks become visible.
- Added automatic backoff retry for rate-limit and network failures (matching failures are never blind-retried).
- Added optional download pacing (delay between batches) to reduce rate-limiting on large playlists.
- Added an advanced yt-dlp arguments setting for power-user unblocking (player clients, PO tokens) without a new release.
- Fixed the backend lingering after app close: shutdown now stops the backend read loop and the app closes stdin as a second EOF-based exit path.
- Fixed the UI hanging forever on "Resolving playlist…" when the backend process dies mid-request.

## 1.2.1 - 2026-07-12

- Fixed all Spotify resolution in standalone builds by replacing frozen curl transport with a requests-based compatible transport.
- Added provider error details so connection failures no longer collapse to `Failed to complete request.`
- Added 1,200-track resolver coverage and bulk WPF collection updates for large playlists.
- Fixed imported-manifest downloads trying to re-resolve synthetic Spotify IDs.
- Fixed MP3 tagging failure when ISRC metadata is absent.
- Added release-gating frozen-backend Spotify smoke test.
- Verified real download, MP3 conversion, and title/artist/album tags using NASA JPL public media.

## 1.2.0 - 2026-07-12

- Added restart-safe last-job persistence and resume; completed tracks and manual choices are restored after reopening the app.
- Added per-track YouTube/YouTube Music source overrides while retaining Spotify/imported metadata for tags.
- Added CSV and JSON track-manifest import, including Exportify-style column names, as a Spotify-independent metadata source.
- Added configurable file organization: source-named folders and three safe filename/folder layouts.
- Added an on-demand in-app check for newer published GitHub releases.
- Added focused persistence, manifest, source validation, output layout, and update comparison regression tests.

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
