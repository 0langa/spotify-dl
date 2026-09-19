# Playlist DL roadmap

Baseline: v2.6.0. Completed milestones in [CHANGELOG.md](CHANGELOG.md).

## Finished-for-now baseline

2.0.1 closed every defect from full audit of 2.0.0 code. 2.1.0 completed library and queue depth milestone (persistent, reorderable queue, per-job failure reports, cross-job duplicate handling). 2.2.0 added verified in-app updater. 2.3.0 added optional official Spotify Web API path plus audio-finishing work (loudness normalization, cover art, drag and drop). 2.4.0 verifies every saved file before a track counts as done. 2.5.0 added library health check and reconciliation (missing, empty, moved, unavailable files, including music folder moved wholesale). 2.6.0 added scheduled auto-sync for sources marked to keep in sync. Windows personal-use scope stays feature-complete. Maintenance priorities: provider compatibility, security updates, bug fixes, preservation of release gates. Network providers are external dependencies; Playlist DL cannot guarantee their availability.

## Optional future work

1. Distribution trust
   - Authenticode signing when trusted certificate available.
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

Live-download E2E uses public-domain or permissively licensed media only. Smoke input: NASA JPL Mars wind recording. Public Spotify playlists may be resolved for metadata-only testing.
