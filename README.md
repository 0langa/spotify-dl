# Playlist DL

Windows personal-use playlist downloader. Paste Spotify playlist URL, inspect matches, download source audio through spotDL/yt-dlp, convert to MP3, and retain playlist metadata.

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

- Spotify public playlist resolution through spotDL experimental resolver
- YouTube Music/YouTube matching and fallback
- Per-track and overall progress, duplicate scanning, retry by rerunning, batch cancellation
- MP3 V0 default, 320 kbps option, Windows-compatible ID3 tags and cover art
- Optional YouTube cookie file for authenticated/Premium formats
- One downloadable self-contained Windows x64 executable

## Known limitations

- Public Spotify resolution uses experimental unofficial SpotAPI/spotipyFree path and may break after platform changes.
- Cancellation takes effect after currently active download batch finishes.
- Match selection is automatic in v1; wrong or missing matches require rerunning through spotDL/manual tooling.
- Release executable is unsigned and can trigger Windows SmartScreen unknown-publisher warning.

## Release build

```powershell
./scripts/build-release.ps1 -Version 1.0.0
```

Build freezes Python backend, bundles local `ffmpeg`, `ffprobe`, and `deno`, verifies helper hashes at runtime, then publishes `artifacts/release/PlaylistDL.exe`. Released executable extracts versioned helpers under `%LOCALAPPDATA%\PlaylistDL\tools` on first use.

## License

GPL-3.0. See [third-party notices](THIRD_PARTY_NOTICES.md) and [privacy policy](PRIVACY.md).
