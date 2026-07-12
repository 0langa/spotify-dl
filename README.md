# Playlist DL

Windows personal-use playlist downloader. Paste a Spotify playlist, album, or track URL, inspect and select tracks, download source audio through spotDL/yt-dlp, convert to your chosen format, and retain playlist metadata.

> [!WARNING]
> This project is unofficial and not affiliated with Spotify, YouTube, Google, or spotDL. Platform terms can prohibit automated retrieval and audio extraction even for personal use. Users must determine whether each download is permitted where they live and by relevant service terms. No rights to hosted media are granted by this software.

## Current development setup

Requirements: Windows 10 22H2+ x64, .NET 10 SDK, uv, FFmpeg, Deno.

```powershell
uv sync --project backend --extra dev
uv run --project backend pytest
dotnet build PlaylistDl.slnx
dotnet run --project src/PlaylistDl.App/PlaylistDl.App.csproj
```

UI launches backend through `uv` during development. Set `PLAYLISTDL_BACKEND_PATH` to a frozen backend executable to override this.

## Features

- Spotify public playlist, album, and single-track resolution through spotDL experimental resolver
- CSV and JSON track-manifest import, including common Exportify columns
- YouTube Music/YouTube matching and fallback
- Per-track manual YouTube source override for correcting a weak or wrong automatic match
- Per-track selection with select-all and live filtering by title, artist, or album
- Per-track and overall progress, duplicate scanning, batch cancellation
- Per-track Done/Failed results with failure reasons, one-click retry, and automatic backoff retry for rate-limit failures
- Failure banner with actionable guidance plus built-in network diagnosis that reveals antivirus/firewall per-app blocks
- Optional download pacing and advanced yt-dlp argument passthrough
- Restart-safe last-job resume with completed tracks and match overrides restored
- Output formats: MP3 (V0 default, 320 kbps option), M4A, Opus, FLAC, OGG, WAV; Windows-compatible tags and cover art
- Configurable source folders and filename layouts, including album/track folder organization
- Optional .m3u8 playlist export preserving track order, plus Open folder shortcut
- Optional YouTube cookie file for authenticated/Premium formats
- On-demand update check against published GitHub releases
- One downloadable self-contained Windows x64 executable

### Track manifest format

Use **Import CSV/JSON** when Spotify resolution is unavailable or when metadata comes from another source. Every row requires a title and artist. Supported common fields include album, duration, Spotify URL/URI, ISRC, cover URL, year, release date, and track number.

Minimal CSV:

```csv
title,artist,album,duration_seconds
Song One,Artist One,Album One,185
```

Minimal JSON:

```json
{
  "name": "My tracks",
  "tracks": [
    { "title": "Song One", "artist": "Artist One", "album": "Album One", "duration_seconds": 185 }
  ]
}
```

## Known limitations

- Public Spotify resolution uses experimental unofficial SpotAPI/spotipyFree path and may break after platform changes.
- Cancellation takes effect after currently active download batch finishes.
- Automatic download matching still picks a single candidate; the per-track Source dialog shows ranked candidates when you want to choose.
- The candidate search and update check need direct network access; strict per-app firewalls can block them (use the in-app Diagnose button).
- Release executable is unsigned and can trigger Windows SmartScreen unknown-publisher warning.

## Release build

```powershell
./scripts/build-release.ps1 -Version 1.2.0
```

Build freezes Python backend, bundles local `ffmpeg`, `ffprobe`, and `deno`, verifies helper hashes at runtime, then publishes `artifacts/release/PlaylistDL.exe`. Released executable extracts versioned helpers under `%LOCALAPPDATA%\PlaylistDL\tools` on first use.

## License

GPL-3.0. See [third-party notices](THIRD_PARTY_NOTICES.md) and [privacy policy](PRIVACY.md).
