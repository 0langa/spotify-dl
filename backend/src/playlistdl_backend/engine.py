from __future__ import annotations

import logging
import os
import sys
import threading
import time
import uuid
from collections.abc import Callable
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

from spotdl.download.downloader import Downloader
from spotdl.download.progress_handler import ProgressHandler, SongTracker
from spotdl.types.album import Album
from spotdl.types.playlist import Playlist
from spotdl.types.song import Song
from spotdl.utils.spotify import SpotifyClient

from playlistdl_backend.manifest import load_manifest
from playlistdl_backend.models import PlaylistDto, TrackDto
from playlistdl_backend.playlist_file import sanitize_filename, write_m3u8

logger = logging.getLogger(__name__)

EventSink = Callable[[dict[str, Any]], None]

_SOURCE_TYPES = ("playlist", "album", "track")

SUPPORTED_FORMATS = ("mp3", "m4a", "opus", "flac", "wav", "ogg")

NAMING_PRESETS = {
    "position_artist_title": "{list-position} - {artist} - {title}.{output-ext}",
    "artist_title": "{artist} - {title}.{output-ext}",
    "album_track_title": "{album-artist}/{album}/{track-number} - {title}.{output-ext}",
}

# Failure taxonomy: map raw spotDL/yt-dlp error text onto actionable classes.
_FAILURE_PATTERNS: tuple[tuple[str, tuple[str, ...]], ...] = (
    (
        "youtube_blocked",
        (
            "sign in to confirm",
            "429",
            "too many requests",
            "read timed out",
            "urlopen error",
            "http error 403",
            "unable to download webpage",
        ),
    ),
    (
        "network",
        (
            "max retries exceeded",
            "connecttimeout",
            "failed to complete request",
            "connection refused",
            "getaddrinfo failed",
            "ssl",
        ),
    ),
    (
        "no_match",
        (
            "no results found",
            "lookuperror",
            "no song matches",
        ),
    ),
    (
        "convert_error",
        (
            "ffmpeg",
            "conversion",
            "convert",
        ),
    ),
)

_FAILURE_PRIORITY = ("youtube_blocked", "network", "no_match", "convert_error", "unknown")

RETRYABLE_FAILURE_CLASSES = frozenset({"youtube_blocked", "network"})

FAILURE_HINTS = {
    "youtube_blocked": (
        "YouTube is rate-limiting or bot-checking this network. "
        "Add a browser cookie file under Settings → YouTube cookies, "
        "lower concurrency, or retry in a few minutes."
    ),
    "network": (
        "The downloader could not reach the network. Check connectivity and "
        "any antivirus/firewall rules for this app, then run a diagnosis."
    ),
    "no_match": (
        "No matching YouTube source was found for some tracks. "
        "Use the per-track Source button to pick an exact video."
    ),
    "convert_error": (
        "Audio conversion failed. Verify the bundled FFmpeg is intact "
        "or try a different output format."
    ),
    "unknown": "Some tracks failed to download. Retry the failed tracks to try again.",
}


_RETRY_BACKOFF_SECONDS = 8.0

_DIAGNOSE_ENDPOINTS = (
    "https://open.spotify.com/",
    "https://music.youtube.com/",
    "https://www.youtube.com/",
)


def _default_probe(url: str) -> tuple[bool, str]:
    import requests

    try:
        response = requests.get(url, timeout=8, allow_redirects=True)
        return True, f"HTTP {response.status_code}"
    except requests.RequestException as exc:
        return False, str(exc)


def _parse_duration_seconds(item: dict[str, Any]) -> int:
    value = item.get("duration_seconds")
    if isinstance(value, int):
        return value
    text = str(item.get("duration") or "")
    parts = text.split(":")
    if not all(part.strip().isdigit() for part in parts if part):
        return 0
    seconds = 0
    try:
        for part in parts:
            seconds = seconds * 60 + int(part)
    except ValueError:
        return 0
    return seconds


def _song_from_ytmusic(item: dict[str, Any], position: int) -> Song | None:
    """Build a complete spotDL Song from one ytmusicapi song result."""
    candidate = _candidate_from_result(item)
    if candidate is None:
        return None
    artists = candidate["artists"] or ["Unknown artist"]
    thumbnails = item.get("thumbnails") or []
    last_thumbnail = thumbnails[-1] if thumbnails else None
    cover_url = last_thumbnail.get("url") if isinstance(last_thumbnail, dict) else None
    return Song(
        name=candidate["title"],
        artists=artists,
        artist=artists[0],
        genres=[],
        disc_number=1,
        disc_count=1,
        album_name=candidate["album"] or "",
        album_artist=artists[0],
        duration=candidate["duration_seconds"],
        year=0,
        date="",
        track_number=position,
        tracks_count=0,
        song_id=f"ytm-{candidate['url'].rsplit('=', 1)[-1]}",
        explicit=False,
        publisher="",
        url=candidate["url"],
        isrc="",
        cover_url=cover_url,
        copyright_text=None,
        list_name="",
        list_url=str(Path()),
        list_position=position,
        list_length=0,
        album_id="",
        artist_id="",
        album_type="",
        download_url=candidate["url"],
    )


def _candidate_from_result(item: dict[str, Any]) -> dict[str, Any] | None:
    """Normalize one ytmusicapi search result into a source candidate."""
    if not isinstance(item, dict):
        return None
    video_id = item.get("videoId")
    title = item.get("title")
    if not video_id or not title:
        return None
    artists = [
        str(artist.get("name"))
        for artist in item.get("artists") or []
        if isinstance(artist, dict) and artist.get("name")
    ]
    album = item.get("album")
    album_name = album.get("name") if isinstance(album, dict) else None
    result_type = str(item.get("resultType") or "")
    host = "music.youtube.com" if result_type == "song" else "www.youtube.com"
    return {
        "url": f"https://{host}/watch?v={video_id}",
        "title": str(title),
        "artists": artists,
        "album": album_name,
        "duration_seconds": _parse_duration_seconds(item),
        "result_type": result_type or "video",
    }


def rank_candidates(
    candidates: list[dict[str, Any]], target_duration_seconds: int
) -> list[dict[str, Any]]:
    """Order candidates by duration proximity; songs win ties over videos."""
    for candidate in candidates:
        duration = candidate.get("duration_seconds") or 0
        candidate["duration_delta_seconds"] = (
            duration - target_duration_seconds if target_duration_seconds and duration else None
        )
    def sort_key(candidate: dict[str, Any]) -> tuple[int, int]:
        delta = candidate.get("duration_delta_seconds")
        distance = abs(delta) if delta is not None else 10_000
        type_rank = 0 if candidate.get("result_type") == "song" else 1
        return (distance, type_rank)

    return sorted(candidates, key=sort_key)


def classify_failure(error_text: str | None) -> str:
    """Bucket raw downloader error text into an actionable failure class."""
    lowered = (error_text or "").lower()
    for failure_class, needles in _FAILURE_PATTERNS:
        if any(needle in lowered for needle in needles):
            return failure_class
    return "unknown"


def dominant_failure_class(classes: list[str]) -> str | None:
    """Pick the most actionable class across all failed tracks."""
    present = set(classes)
    for candidate in _FAILURE_PRIORITY:
        if candidate in present:
            return candidate
    return None


def validate_source_url(url: str) -> str:
    """Validate and normalize a user-selected YouTube source URL."""
    value = url.strip()
    parsed = urlparse(value)
    host = (parsed.hostname or "").lower()
    if parsed.scheme != "https" or not (
        host == "youtu.be" or host == "youtube.com" or host.endswith(".youtube.com")
    ):
        raise ValueError("Manual sources must be HTTPS YouTube or YouTube Music URLs")
    return value


def effective_bitrate(audio_format: str, bitrate: str | None) -> str | None:
    """Map the UI bitrate choice onto spotDL's bitrate option per format."""
    if audio_format == "mp3":
        return bitrate or "0"
    if audio_format in ("m4a", "opus"):
        # Source audio is already AAC/Opus; copy the stream instead of re-encoding.
        return "disable"
    # Lossless targets (flac/wav) and ogg re-encode with converter defaults.
    return None


def build_output_paths(
    output_dir: str,
    collection_name: str,
    naming_preset: str,
    create_source_folder: bool,
) -> tuple[Path, str]:
    """Return the collection root and spotDL output template."""
    if naming_preset not in NAMING_PRESETS:
        raise ValueError("Unknown file naming preset")
    root = Path(output_dir).expanduser().resolve()
    if create_source_folder:
        root /= sanitize_filename(collection_name)
    return root, str(root / NAMING_PRESETS[naming_preset])


def classify_spotify_url(url: str) -> str:
    """Return source type for an open.spotify.com URL, or raise ValueError."""
    parsed = urlparse(url)
    if parsed.scheme not in ("http", "https") or not parsed.hostname:
        raise ValueError("Paste a valid Spotify playlist, album, or track URL")
    host = parsed.hostname.lower()
    if host != "spotify.com" and not host.endswith(".spotify.com"):
        raise ValueError("Paste a valid Spotify playlist, album, or track URL")
    segments = [segment for segment in parsed.path.split("/") if segment]
    # Locale-prefixed links look like /intl-de/track/<id>.
    if segments and segments[0].startswith("intl-"):
        segments = segments[1:]
    if len(segments) >= 2 and segments[0] in _SOURCE_TYPES and segments[1]:
        return segments[0]
    raise ValueError("Unsupported Spotify link. Use a playlist, album, or track URL")


class Engine:
    def __init__(self, emit: EventSink) -> None:
        self._emit = emit
        self._spotify_initialized = False
        self._songs: dict[str, list[Song]] = {}
        self._names: dict[str, str] = {}
        self._cancel = threading.Event()

    def _ensure_spotify(self) -> None:
        if self._spotify_initialized:
            return
        SpotifyClient.init(
            client_id="",
            client_secret="",
            no_cache=True,
            use_official_api=False,
        )
        self._spotify_initialized = True

    def resolve(self, url: str) -> PlaylistDto:
        source_type = classify_spotify_url(url)
        self._ensure_spotify()
        name, description, owner, cover_url, songs = self._fetch_source(source_type, url)
        playlist_id = uuid.uuid4().hex
        self._songs[playlist_id] = songs
        self._names[playlist_id] = name
        tracks = [self._track_dto(song, index + 1) for index, song in enumerate(songs)]
        return PlaylistDto(
            id=playlist_id,
            name=name,
            description=description,
            owner=owner,
            cover_url=cover_url,
            source_url=url,
            source_type=source_type,
            tracks=tracks,
        )

    def resolve_search(
        self, query: str, limit: int = 12, client: Any | None = None
    ) -> PlaylistDto:
        """Resolve free text into downloadable tracks via YouTube Music.

        Spotify-independent by design: this path keeps working when the
        unofficial Spotify resolver breaks.
        """
        text = query.strip()
        if not text:
            raise ValueError("Type an artist, title, or both to search")
        if client is None:
            from ytmusicapi import YTMusic

            client = YTMusic()
        raw = client.search(text, filter="songs", limit=limit)
        songs: list[Song] = []
        seen: set[str] = set()
        for item in raw:
            song = _song_from_ytmusic(item, len(songs) + 1)
            if song is None or song.download_url in seen:
                continue
            seen.add(song.download_url or "")
            songs.append(song)
            if len(songs) >= limit:
                break
        if not songs:
            raise ValueError(f"No songs found for '{text}'")
        for song in songs:
            song.list_length = len(songs)
        playlist_id = uuid.uuid4().hex
        self._songs[playlist_id] = songs
        self._names[playlist_id] = text
        tracks = [self._track_dto(song, index + 1) for index, song in enumerate(songs)]
        return PlaylistDto(
            id=playlist_id,
            name=text,
            description="YouTube Music search results",
            owner="Search",
            cover_url="",
            source_url=f"search:{text}",
            source_type="search",
            tracks=tracks,
        )

    def import_manifest(self, path: str) -> PlaylistDto:
        name, songs = load_manifest(path)
        playlist_id = uuid.uuid4().hex
        self._songs[playlist_id] = songs
        self._names[playlist_id] = name
        tracks = [self._track_dto(song, index + 1) for index, song in enumerate(songs)]
        return PlaylistDto(
            id=playlist_id,
            name=name,
            description="Imported track manifest",
            owner="Local file",
            cover_url="",
            source_url=str(Path(path).expanduser().resolve()),
            source_type="import",
            tracks=tracks,
        )

    @staticmethod
    def _fetch_source(source_type: str, url: str) -> tuple[str, str, str, str, list[Song]]:
        if source_type == "playlist":
            metadata, songs = Playlist.get_metadata(url)
            return (
                str(metadata.get("name") or "Spotify playlist"),
                str(metadata.get("description") or ""),
                str(metadata.get("author_name") or ""),
                str(metadata.get("cover_url") or ""),
                songs,
            )
        if source_type == "album":
            metadata, songs = Album.get_metadata(url)
            artist = metadata.get("artist") or {}
            cover_url = next((song.cover_url for song in songs if song.cover_url), "")
            return (
                str(metadata.get("name") or "Spotify album"),
                "",
                str(artist.get("name") or ""),
                str(cover_url or ""),
                songs,
            )
        song = Song.from_url(url)
        return (
            song.name,
            "",
            song.artist or (song.artists[0] if song.artists else ""),
            str(song.cover_url or ""),
            [song],
        )

    @staticmethod
    def _track_dto(song: Song, fallback_position: int) -> TrackDto:
        return TrackDto(
            id=song.song_id or song.url or uuid.uuid4().hex,
            position=song.list_position or fallback_position,
            title=song.name,
            artists=list(song.artists),
            album=song.album_name or "",
            duration_seconds=song.duration,
            cover_url=song.cover_url,
            spotify_url=song.url,
            isrc=song.isrc,
        )

    def cancel(self) -> None:
        self._cancel.set()

    @staticmethod
    def search_sources(
        title: str,
        artist: str,
        duration_seconds: int = 0,
        limit: int = 8,
        client: Any | None = None,
    ) -> list[dict[str, Any]]:
        """Search YouTube Music for ranked source candidates for one track."""
        if client is None:
            from ytmusicapi import YTMusic

            client = YTMusic()
        query = f"{artist} {title}".strip()
        if not query:
            raise ValueError("Search needs a title or artist")
        raw: list[dict[str, Any]] = []
        for search_filter in ("songs", "videos"):
            try:
                raw.extend(client.search(query, filter=search_filter, limit=limit))
            except Exception:  # noqa: BLE001 - provider variance
                logger.exception("ytmusicapi search failed for filter %s", search_filter)
        candidates = []
        seen: set[str] = set()
        for item in raw:
            candidate = _candidate_from_result(item)
            if candidate is None or candidate["url"] in seen:
                continue
            seen.add(candidate["url"])
            candidates.append(candidate)
        return rank_candidates(candidates, duration_seconds)[:limit]

    @staticmethod
    def ensure_runtime() -> None:
        """Load provider runtime resources omitted easily by freezer recipes."""
        from ytmusicapi import YTMusic

        YTMusic()

    def ensure_startable(self, playlist_id: str, audio_format: str) -> None:
        """Validate a start request synchronously so errors precede job_started."""
        if audio_format not in SUPPORTED_FORMATS:
            raise ValueError(
                f"Unsupported audio format: {audio_format}. "
                f"Supported formats: {', '.join(SUPPORTED_FORMATS)}"
            )
        if playlist_id not in self._songs:
            raise ValueError("Unknown or expired playlist id")

    def download(
        self,
        playlist_id: str,
        output_dir: str,
        bitrate: str = "0",
        threads: int = 2,
        cookie_file: str | None = None,
        track_ids: list[str] | None = None,
        audio_format: str = "mp3",
        write_m3u: bool = False,
        source_overrides: dict[str, str] | None = None,
        naming_preset: str = "position_artist_title",
        create_source_folder: bool = True,
        throttle_seconds: float = 0.0,
        retries: int = 1,
        ytdlp_args: str | None = None,
        embed_lyrics: bool = False,
    ) -> None:
        if audio_format not in SUPPORTED_FORMATS:
            raise ValueError(
                f"Unsupported audio format: {audio_format}. "
                f"Supported formats: {', '.join(SUPPORTED_FORMATS)}"
            )
        songs = self._songs.get(playlist_id)
        if songs is None:
            raise ValueError("Unknown or expired playlist id")
        if track_ids:
            selected = set(track_ids)
            songs = [song for song in songs if (song.song_id or song.url) in selected]
            if not songs:
                raise ValueError("No requested tracks exist in this playlist")
        if source_overrides:
            known_ids = {song.song_id or song.url for song in songs}
            unknown_ids = set(source_overrides) - known_ids
            if unknown_ids:
                raise ValueError("A manual source refers to a track outside this job")
            for song in songs:
                track_id = song.song_id or song.url
                if track_id in source_overrides:
                    song.download_url = validate_source_url(source_overrides[track_id])
        for song in songs:
            # spotDL 4.5 writes ISRC unconditionally for MP3; Mutagen rejects None.
            if song.isrc is None:
                song.isrc = ""

        output, output_template = build_output_paths(
            output_dir,
            self._names.get(playlist_id) or "playlist",
            naming_preset,
            create_source_folder,
        )
        output.mkdir(parents=True, exist_ok=True)
        self._cancel.clear()

        settings: dict[str, Any] = {
            "audio_providers": ["youtube-music", "youtube"],
            "lyrics_providers": ["genius", "azlyrics", "musixmatch"] if embed_lyrics else [],
            "format": audio_format,
            "bitrate": effective_bitrate(audio_format, bitrate),
            "threads": max(1, min(threads, 4)),
            "output": output_template,
            "overwrite": "skip",
            "scan_for_songs": True,
            "restrict": "none",
            "simple_tui": True,
            "cookie_file": cookie_file,
            "yt_dlp_args": (ytdlp_args or "").strip() or None,
            "ffmpeg": os.environ.get("PLAYLISTDL_FFMPEG", "ffmpeg"),
        }
        downloader = Downloader(settings)
        downloader.progress_handler.close()
        downloader.progress_handler = ProgressHandler(
            simple_tui=True,
            update_callback=self._on_progress,
        )
        downloader.progress_handler.set_songs(songs)

        results_by_id: dict[str, dict[str, Any]] = {}
        pending = list(songs)
        for attempt in range(max(0, retries) + 1):
            cancelled = self._run_attempt(
                downloader, pending, results_by_id, threads, throttle_seconds
            )
            if cancelled:
                self._emit({"type": "job_cancelled"})
                return
            pending = [
                song
                for song in pending
                if (record := results_by_id.get(song.song_id or song.url)) is not None
                and not record["success"]
                and record["error_class"] in RETRYABLE_FAILURE_CLASSES
            ]
            if not pending or attempt >= max(0, retries):
                break
            if self._wait_cancellable(_RETRY_BACKOFF_SECONDS * (attempt + 1)):
                self._emit({"type": "job_cancelled"})
                return

        downloader.progress_handler.close()
        results = [
            results_by_id[track_id]
            for song in songs
            if (track_id := song.song_id or song.url) in results_by_id
        ]
        failure_class = dominant_failure_class(
            [record["error_class"] for record in results if not record["success"]]
        )
        m3u_path = (
            self._write_playlist_file(playlist_id, output, songs, results) if write_m3u else None
        )
        self._emit(
            {
                "type": "job_completed",
                "results": results,
                "m3u_path": str(m3u_path) if m3u_path else None,
                "failure_class": failure_class,
                "failure_hint": FAILURE_HINTS.get(failure_class) if failure_class else None,
            }
        )

    def _run_attempt(
        self,
        downloader: Downloader,
        songs: list[Song],
        results_by_id: dict[str, dict[str, Any]],
        threads: int,
        throttle_seconds: float,
    ) -> bool:
        """Download one pass over songs; returns True if cancelled."""
        batch_size = max(1, min(threads, 4))
        for offset in range(0, len(songs), batch_size):
            if self._cancel.is_set():
                return True
            if offset and throttle_seconds > 0 and self._wait_cancellable(throttle_seconds):
                return True
            batch = songs[offset : offset + batch_size]
            errors_before = len(downloader.errors)
            for resolved_song, path in downloader.download_multiple_songs(batch):
                track_id = resolved_song.song_id or resolved_song.url
                results_by_id[track_id] = {
                    "track_id": track_id,
                    "path": str(path) if path else None,
                    "success": path is not None,
                    "error": None,
                    "error_class": None,
                }
            new_errors = list(downloader.errors[errors_before:])
            self._attribute_errors(batch, new_errors, results_by_id)
        return False

    @staticmethod
    def _attribute_errors(
        batch: list[Song],
        new_errors: list[str],
        results_by_id: dict[str, dict[str, Any]],
    ) -> None:
        """Match spotDL error strings to failed songs, by display name when possible."""
        failed = [
            record
            for song in batch
            if (record := results_by_id.get(song.song_id or song.url)) is not None
            and not record["success"]
        ]
        songs_by_id = {song.song_id or song.url: song for song in batch}
        unmatched = list(new_errors)
        for record in failed:
            song = songs_by_id[record["track_id"]]
            display_name = getattr(song, "display_name", None) or song.name
            match = next(
                (text for text in unmatched if display_name and display_name in text), None
            )
            if match is not None:
                unmatched.remove(match)
                record["error"] = match
        leftover = " | ".join(unmatched) if unmatched else None
        for record in failed:
            if record["error"] is None and leftover:
                record["error"] = leftover
            record["error_class"] = classify_failure(record["error"])

    def _wait_cancellable(self, seconds: float) -> bool:
        """Sleep in small slices; returns True if cancelled meanwhile."""
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if self._cancel.is_set():
                return True
            time.sleep(min(0.2, max(0.0, deadline - time.monotonic())))
        return self._cancel.is_set()

    def diagnose(self, probe: Callable[[str], tuple[bool, str]] | None = None) -> dict[str, Any]:
        """Probe provider endpoints so users can see exactly what is blocked."""
        if probe is None:
            probe = _default_probe
        checks = []
        for url in _DIAGNOSE_ENDPOINTS:
            started = time.monotonic()
            ok, detail = probe(url)
            checks.append(
                {
                    "url": url,
                    "ok": ok,
                    "detail": detail,
                    "elapsed_ms": int((time.monotonic() - started) * 1000),
                }
            )
        return {
            "backend_path": sys.executable,
            "frozen": bool(getattr(sys, "frozen", False)),
            "checks": checks,
        }

    def _write_playlist_file(
        self,
        playlist_id: str,
        output: Path,
        songs: list[Song],
        results: list[dict[str, Any]],
    ) -> Path | None:
        path_by_id = {
            result["track_id"]: result["path"]
            for result in results
            if result["success"] and result["path"]
        }
        ordered = [
            path_by_id[track_id]
            for song in songs
            if (track_id := song.song_id or song.url) in path_by_id
        ]
        if not ordered:
            return None
        try:
            return write_m3u8(output, self._names.get(playlist_id) or "playlist", ordered)
        except OSError:
            logger.exception("Failed to write m3u8 playlist file")
            return None

    def _on_progress(self, tracker: SongTracker, message: str) -> None:
        self._emit(
            {
                "type": "track_progress",
                "track_id": tracker.song.song_id or tracker.song.url,
                "progress": int(tracker.progress),
                "status": message,
                "path": tracker.path,
            }
        )
