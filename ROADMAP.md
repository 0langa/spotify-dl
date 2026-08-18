# Playlist DL roadmap

Current baseline: v2.6.0. Completed milestones live in [CHANGELOG.md](CHANGELOG.md).

## Finished-for-now baseline

Version 2.0.1 closed every defect found by a full audit of the 2.0.0 code, 2.1.0 completed the library and queue depth milestone (persistent, reorderable queue, per-job failure reports, cross-job duplicate handling), 2.2.0 added the verified in-app updater, and 2.3.0 added the optional official Spotify Web API path together with the audio-finishing work (loudness normalization, cover art, drag and drop). Three capabilities followed: 2.4.0 verifies every saved file before a track counts as done, 2.5.0 added the library health check and reconciliation (missing, empty, moved, and unavailable files, including a music folder that was moved wholesale), and 2.6.0 added scheduled auto-sync for sources marked to keep in sync. The Windows personal-use scope stays feature-complete. Maintenance should prioritize provider compatibility, security updates, bug fixes, and preservation of release gates. Network providers remain external dependencies; their availability cannot be guaranteed by Playlist DL.

## Optional future work

1. Distribution trust
   - Authenticode signing when a trusted certificate is available.
   - Keep checksum verification, frozen-backend lifecycle smoke, Spotify resolver smoke, and malware scan in release CI.

## Standing release gates

- `uv run --project backend --extra dev ruff check backend`
- `uv run --project backend --extra dev ruff format --check backend`
- `uv run --project backend --extra dev python -m pytest backend/tests --cov=playlistdl_backend --cov-fail-under=80`
- `./scripts/audit-python-dependencies.ps1`
- `dotnet format PlaylistDl.slnx --verify-no-changes`
- `dotnet build PlaylistDl.slnx --configuration Release`
- `dotnet test PlaylistDl.slnx --configuration Release --no-build`
- `./scripts/verify-release.ps1`
- `./scripts/smoke-backend-lifecycle.ps1`
- `./scripts/smoke-frozen-backend.ps1`

Live-download E2E uses public-domain or permissively licensed media only. Current smoke input: NASA JPL Mars wind recording. Public Spotify playlists may be resolved for metadata-only testing.
