# Playlist DL roadmap

Current baseline: v1.6.1. Completed milestones live in [CHANGELOG.md](CHANGELOG.md).

## Next priorities

1. Verified in-app updater
   - Download release executable and `SHA256SUMS.txt`.
   - Verify SHA-256 before launching a small swap helper.
   - Preserve rollback path and never replace a running binary in place.

2. Official Spotify API escape hatch
   - Optional user-supplied client credentials.
   - Keep unofficial public-link resolver as zero-setup default.
   - Store secrets using Windows Credential Manager, never settings JSON.

3. Audio finishing
   - Optional ReplayGain or FFmpeg loudness normalization.
   - Cover-art column and richer metadata inspection.
   - Drag-and-drop Spotify links and manifest files.

4. Library and queue depth
   - Cross-job duplicate detection with explicit skip/copy/hardlink policy.
   - Persistent pending queue across app restarts.
   - Better queue reordering and per-job failure summary.

5. Distribution trust
   - Authenticode signing when a trusted certificate is available.
   - Keep checksum verification, frozen-backend lifecycle smoke, Spotify resolver smoke, and malware scan in release CI.

## Standing release gates

- `uv run --project backend ruff check backend`
- `uv run --project backend pytest`
- `dotnet build PlaylistDl.slnx --configuration Release`
- `dotnet test PlaylistDl.slnx --configuration Release --no-build`
- `./scripts/verify-release.ps1`
- `./scripts/smoke-backend-lifecycle.ps1`
- `./scripts/smoke-frozen-backend.ps1`

Live-download E2E uses public-domain or permissively licensed media only. Current smoke input: NASA JPL Mars wind recording. Public Spotify playlists may be resolved for metadata-only testing.
