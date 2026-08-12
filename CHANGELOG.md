# Changelog

## Unreleased

## 2.5.0 - 2026-08-12

### Added

- Library health check. Check files compares one saved job against the disk and reports how many files are present, missing, empty, or moved; Check all does the same for every saved job at once.
- Files that were moved or renamed under the job's output folder are found by name and the library is pointed at their new location, so they are not downloaded a second time.
- Tracks whose file is gone or empty can be marked unfinished again, which puts them back into Open, Sync, and Resume instead of being skipped forever.
- After reopening tracks the job can be opened straight away with exactly those tracks selected.

### Fixed

- Three 2.3.0 entries in this changelog were listed under Changed instead of Added.

## 2.4.0 - 2026-08-12

### Added

- Every saved file is verified before its track is reported as done. A download that is missing, empty, does not decode, or is clearly the wrong length is rejected with the reason, and the existing alternate-source recovery tries a different upload instead.
- Verification also covers files reused from the library by the skip, copy, and hard-link policies.
- Settings can turn verification off; the check is skipped automatically when no probe is available, so a missing helper never fails a download.
- A rejected file is deleted, because the downloader skips a track whose output file already exists and would otherwise hand the same broken file to every retry and every alternate source.
- A library file that fails verification is downloaded normally instead of being reused, and the skip policy never deletes the library's own file.
- A hand-picked source is checked for readability but not for length, so a deliberate live or extended version is accepted.

## 2.3.0 - 2026-08-12

### Added

- Optional official Spotify Web API resolution. Supply your own client ID and secret under Settings to resolve playlists, albums, and tracks through the official API when the zero-setup public resolver breaks after a platform change.
- Credentials are stored in Windows Credential Manager for the signed-in Windows account. Settings keeps only the on/off flag, and the backend receives the pair per request without storing, logging, or echoing it. Rejected credentials report what to check instead of the provider's raw message, which can quote the secret.
- Optional loudness normalization to the EBU R128 streaming target (-14 LUFS), applied while converting.
- Cover art column in the track list.
- Drag and drop a Spotify link or a CSV/JSON manifest onto the window to load it.

### Changed

- Switching credentials on or off replaces the running resolver in place, so the change takes effect on the next resolve without restarting the app.

### Fixed

- Track lengths of an hour or more are shown in full instead of dropping the hours.
- The job library labels a saved free-text search as Search instead of Playlist.

## 2.2.0 - 2026-08-12

### Added

- Verified in-app update. The Get button now downloads the published executable, checks it against the SHA256SUMS.txt of the same release, shows the verified digest, and installs it only after the checksum matches.
- The installer never writes over the running binary: the current executable is moved aside as `PlaylistDL.exe.previous`, the verified one takes its place, and the previous file is restored if anything fails. The next start removes it.

### Changed

- Helper sets extracted by earlier app versions under the local tools folder are removed once the current version's set is verified, and finished update downloads are cleared at startup.
- An update cannot be installed while a download or queue run is active.

## 2.1.0 - 2026-08-12

### Added

- Pending queue jobs are saved and restored across restarts. A restored job is resolved again before it runs, so it picks up new tracks and skips what is already downloaded.
- New Queue window: reorder waiting jobs, remove one, clear all, and read the per-job report of the last run.
- Per-job failure summary for queue runs, with the count saved, the count failed, and the actionable hint or error for each job.
- Optional cross-job duplicate handling. A track an earlier job already downloaded can be skipped, copied, or hard-linked into the new job's folder instead of downloaded again; hard links fall back to a copy across drives.

### Changed

- A failed, cancelled, or interrupted queue job no longer discards the jobs waiting behind it. The run stops, the reason is recorded in the queue report, and the remaining jobs stay queued.
- A queued source that can no longer be resolved is reported in the queue report and the queue continues with the next job.

## 2.0.1 - 2026-08-11

### Fixed

- Queue runs now become the current source, so saving, resuming, and one-click retry after a queued job address the job that actually ran instead of the previously analyzed source.
- Source intake (Analyze, Import, Library, Resume) is disabled while a download runs, so a running job can no longer be orphaned by loading another source.
- Playlist `.m3u8` export merges with the existing file instead of replacing it, so a retry, Sync, or partial selection no longer shrinks a complete playlist to the tracks of the last job. Entries stay in source order, listings whose file is gone are dropped, and a track downloaded again under another format or naming preset replaces its earlier entry instead of being listed twice.
- A track that a source lists twice now gets its own row, result, and output file instead of collapsing into one record that never completes.
- A cancellation sent while a job is still starting is no longer discarded by the worker thread.
- Backend shutdown waits for the download worker, so stopping the app or a job no longer leaves converter and downloader child processes behind.
- Network diagnosis and candidate search no longer block the backend request loop, so Cancel stays responsive while they run.
- A provider-wide YouTube block now trips a circuit breaker: retries and alternate-source searches stop, and the actionable hint appears during the job instead of hours later.
- Track manifests accept `mm:ss` and `h:mm:ss` durations, and an unparsable duration reports what is accepted.
- Long source names are clamped and reserved Windows device names escaped, so a long playlist title no longer fails every download with a path error.
- A locked `settings.json` is retried instead of silently resetting every setting, and an unreadable file is preserved as `settings.json.corrupt` rather than overwritten with defaults.
- Settings writes that fail with an I/O or access error report in the status bar instead of terminating the app.
- Deleting a job from the Library is no longer undone by the legacy last-job migration on the next start.
- Closing the window waits for the backend session to end instead of racing dispatcher shutdown.
- The update check only opens `https://github.com` release pages, and a malformed release URL is ignored.
- Helper-tool hashes are verified against the manifest embedded in the executable, so a rewritten manifest in the extracted tools folder can no longer certify tampered helpers.

### Changed

- Selecting all tracks, restoring a saved job, and per-track progress updates no longer rescan the whole track list, keeping thousand-track playlists responsive.
- Select all now reflects the filtered rows it applies to.
- Run logs record resolve, candidate, and job-completion summaries instead of whole payloads.
- Resolved sources are bounded to the 16 most recently used per backend session.
- The failure banner, and with it the Diagnose button, also appears when a free-text search fails.

### Release engineering

- CI and release workflows fail on any failing step; multi-command PowerShell steps no longer report success when only the last command passed.
- Backend tests run from an explicit path so the backend pytest configuration always applies.

## 2.0.0 - 2026-07-14

- Declared backend protocol 1 stable and reject alternate backends with a missing or incompatible protocol before requests can hang or corrupt UI state.
- Made backend restarts invalidate dead in-memory playlist and queue IDs, with saved jobs retained for explicit resume.
- Prevented backend start, cancellation, malformed-event, and local persistence failures from escaping WPF event handlers and crashing the app.
- Isolated every download attempt from retained source metadata, so cleared manual sources and retries no longer reuse mutations from earlier runs.
- Turned provider-omitted tracks into explicit failures and always close downloader progress workers after completion, cancellation, or failure.
- Made run logging fall back to temporary storage and made unreadable settings, jobs, libraries, and tool manifests degrade safely.
- Updated yt-dlp to 2026.07.04, including its latest security and YouTube extractor fixes.
- Added formatting, 80% Python coverage, protocol, dependency-advisory, and frozen-module gates; current suite covers 103 backend and 64 app tests.
- Pinned GitHub Actions and build tools, reruns full source verification during releases, cleans stale release output, requires exact project/tag version alignment, and checksums every published asset.

## 1.9.1 - 2026-07-14

- Detects saved alternate backends older than the bundled backend, rejects them before a job starts, and automatically falls back to the current bundled backend.
- Clears a rejected saved override after successful fallback and shows the repair in the main status bar.
- Records the backend version in the signed tool manifest so release and runtime version checks use the exact bundled executable.

## 1.9.0 - 2026-07-14

- Removed all Spotify requests from the download phase. Resolved metadata is normalized locally, eliminating anonymous-session collapse after hundreds of tracks without losing tags or cover art.
- Reduced single-track Spotify intake to one metadata request per attempt, avoiding redundant artist/album calls and retrying transient upstream failures.
- Expanded automatic recovery to age/sign-in-blocked sources and up to six duration-checked alternatives.
- Added title-only fallback searches and typo-tolerant core-title matching while retaining artist and duration safeguards against unrelated audio.
- Prevented different tracks with identical formatted names from silently sharing or overwriting one file; only collisions receive a stable short track-ID suffix.
- Prevented existing unrelated files from being reported as successful downloads when their formatted path matches a requested track.
- Hardened release builds against locked prior backend files and native-tool failures; smoke gates now verify the exact backend bundled into the standalone executable.

## 1.8.1 - 2026-07-14

- Replaced the external Notepad launch with a built-in run-log viewer, avoiding broken Notepad installations and Windows file-association prompts.
- Added live Refresh and Copy all controls while preserving selectable exact provider diagnostics and the retained log path.

## 1.8.0 - 2026-07-13

- Replaced spotDL's three-request per-track Spotify refresh with one resilient metadata request, retaining album, artist, date, track count, source URL, and cover art while reducing provider pressure and large-playlist runtime.
- Added bounded metadata retries and rolling-window isolation so an expired anonymous Spotify session or provider-pool exception cannot terminate all remaining tracks.
- Automatic alternate-source recovery now finishes inside each rolling window instead of waiting until every initial playlist track has run; early failures become final or recovered immediately.
- Added live exact per-track failure details: select a failed row to read and copy its error, or open the new retained session log directly in Notepad for provider diagnostics and track history.
- Added processed/saved/failed counts, measured tracks-per-minute, and ETA during downloads.
- Large jobs now checkpoint once per completed window, preserve partial results on cancellation, and keep processed failures resumable instead of marking them complete.
- Deduplicated repeated provider progress callbacks and stopped raw backend diagnostics from replacing useful job status text.

## 1.7.0 - 2026-07-13

- Added conservative automatic alternate-source recovery: failed or unavailable primary matches now try up to three strongly matching, duration-checked candidates sequentially.
- Fixed age-restricted or unavailable YouTube sources leaving recoverable tracks permanently failed when public alternate uploads exist.
- Improved large-playlist throughput with rolling work windows that keep the bounded 1–4 worker pool busy without increasing the configured concurrency.
- Improved per-track error attribution in concurrent batches so one source failure is not copied onto unrelated tracks.
- Failed-track reasons now survive restart and Library restore, with one-click retry restored alongside them.
- Added a custom Playlist DL app icon and logo to the executable, window, and project page.

## 1.6.3 - 2026-07-13

- Fixed Choose folder crashing when a saved output path used forward slashes; paths are now normalized before opening Windows Shell, with a safe fallback if Shell still rejects the initial folder.

## 1.6.2 - 2026-07-12

- Added a persistent alternate-backend picker for antivirus/VPN path blocks; changes activate without restarting the app.
- Fixed alternate backends without sibling FFmpeg files incorrectly overriding a working system FFmpeg.
- Made Settings fit smaller screens with a scrollable layout.
- Fixed queued jobs persisting completion against the wrong source or losing unselected tracks from Library history.
- Added immediate cancellation for blocked playlist resolution, search, sync, restore, and manifest import operations.
- Fixed partial track selection being cleared by the Select all checkbox; mixed selections now remain stable.
- Made alternate-backend paths pasteable and validate them before saving.

## 1.6.1 - 2026-07-12

- Replaced raw provider timeout traces during resolve/search with concise antivirus, VPN, firewall, and Diagnose guidance.
- Added release checksum/signature verification, Microsoft Defender scanning when available, and a repeated frozen-backend shutdown smoke gate.
- Updated GitHub Actions runtimes to current Node 24-based releases.
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
