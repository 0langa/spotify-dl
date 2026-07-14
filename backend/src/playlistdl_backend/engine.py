# ruff: noqa: I001 -- spotDL bootstrap must run before importing spotDL.
from __future__ import annotations

import copy
import importlib.util
import logging
import os
import re
import sys
import threading
import time
import uuid
from collections.abc import Callable
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

from playlistdl_backend import spotdl_bootstrap as _spotdl_bootstrap  # noqa: F401

import spotdl.download.downloader as spotdl_downloader_module
from spotdl.download.downloader import Downloader
from spotdl.download.progress_handler import ProgressHandler, SongTracker
from spotdl.types.album import Album
from spotdl.types.playlist import Playlist
from spotdl.types.song import Song
from spotdl.utils.formatter import create_file_name as spotdl_create_file_name
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
            "please sign in",
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
        "metadata_session",
        (
            "could not get session",
            "reinitializing song",
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
    (
        "source_unavailable",
        (
            "audioprovidererror: yt-dlp download error",
            "video unavailable",
            "this video is unavailable",
            "private video",
        ),
    ),
)

_FAILURE_PRIORITY = (
    "youtube_blocked",
    "network",
    "metadata_session",
    "no_match",
    "source_unavailable",
    "convert_error",
    "unknown",
)

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
    "metadata_session": (
        "Spotify metadata session expired while preparing a track. "
        "Progress was saved; resume the job to retry unfinished tracks."
    ),
    "no_match": (
        "No sufficiently close YouTube source was found for some tracks. "
        "Use the per-track Source button to pick an exact video."
    ),
    "source_unavailable": (
        "The best source was unavailable and no safe alternate succeeded. "
        "Retry, add YouTube cookies in Settings for age-restricted videos, "
        "or use the per-track Source button."
    ),
    "convert_error": (
        "Audio conversion failed. Verify the bundled FFmpeg is intact "
        "or try a different output format."
    ),
    "unknown": "Some tracks failed to download. Retry the failed tracks to try again.",
}


_RETRY_BACKOFF_SECONDS = 8.0
_FALLBACK_PAUSE_SECONDS = 0.5
_SPOTIFY_RESOLVE_RETRY_SECONDS = 1.0
_FALLBACK_FAILURE_CLASSES = frozenset({"no_match", "source_unavailable", "youtube_blocked"})
_IDENTITY_STOPWORDS = frozenset(
    {
        "a",
        "an",
        "and",
        "audio",
        "edit",
        "feat",
        "featuring",
        "ft",
        "lyrics",
        "official",
        "remaster",
        "remastered",
        "the",
        "version",
        "video",
    }
)

_DIAGNOSE_ENDPOINTS = (
    "https://open.spotify.com/",
    "https://music.youtube.com/",
    "https://www.youtube.com/",
)

_EXCLUDED_SERVER_MODULES = ("fastapi", "starlette", "uvicorn")


def find_bundled_server_modules() -> list[str]:
    """Return HTTP-server dependencies that must stay outside frozen desktop builds."""
    return [name for name in _EXCLUDED_SERVER_MODULES if importlib.util.find_spec(name) is not None]


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


def _identity_tokens(value: str) -> set[str]:
    return {
        token
        for token in re.findall(r"[\w]+", value.casefold(), flags=re.UNICODE)
        if len(token) > 1 and token not in _IDENTITY_STOPWORDS
    }


def _compact_identity(value: str) -> str:
    return "".join(character for character in value.casefold() if character.isalnum())


def _core_title(value: str) -> str:
    """Return title portion before common version/remix qualifiers."""
    return _compact_identity(re.split(r"\s+-\s+|[\(\[\{]", value, maxsplit=1)[0])


def candidate_is_relevant(
    candidate: dict[str, Any],
    title: str,
    artists: list[str],
    duration_seconds: int,
) -> bool:
    """Reject fallback candidates unless duration and musical identity both agree."""
    candidate_duration = int(candidate.get("duration_seconds") or 0)
    if duration_seconds and candidate_duration:
        tolerance = max(15, round(duration_seconds * 0.10))
        if abs(candidate_duration - duration_seconds) > tolerance:
            return False

    target_title = _identity_tokens(title)
    candidate_title_text = str(candidate.get("title") or "")
    candidate_artist_text = " ".join(str(value) for value in candidate.get("artists") or [])
    candidate_identity = _identity_tokens(f"{candidate_title_text} {candidate_artist_text}")
    if not target_title:
        return False
    title_overlap = len(target_title & candidate_identity) / len(target_title)

    candidate_compact = _compact_identity(f"{candidate_title_text} {candidate_artist_text}")
    artist_match = any(
        (compact := _compact_identity(artist))
        and len(compact) >= 3
        and compact in candidate_compact
        for artist in artists
    )
    exact_title = _compact_identity(title) == _compact_identity(candidate_title_text)
    target_core = _core_title(title)
    candidate_core = _core_title(candidate_title_text)
    core_match = bool(
        artist_match
        and target_core
        and candidate_core
        and (
            target_core in candidate_core
            or candidate_core in target_core
            or _edit_similarity(target_core, candidate_core) >= 0.82
        )
    )
    strong_overlap = title_overlap >= 0.85 and (len(target_title) >= 2 or exact_title)
    return core_match or (title_overlap >= 0.60 and (artist_match or exact_title or strong_overlap))


def _edit_similarity(left: str, right: str) -> float:
    """Small dependency-free edit similarity for misspelled provider titles."""
    if left == right:
        return 1.0
    if not left or not right:
        return 0.0
    previous = list(range(len(right) + 1))
    for left_index, left_character in enumerate(left, start=1):
        current = [left_index]
        for right_index, right_character in enumerate(right, start=1):
            current.append(
                min(
                    current[-1] + 1,
                    previous[right_index] + 1,
                    previous[right_index - 1] + (left_character != right_character),
                )
            )
        previous = current
    distance = previous[-1]
    return 1.0 - distance / max(len(left), len(right))


def _source_identity(url: str | None) -> str:
    if not url:
        return ""
    parsed = urlparse(url)
    if parsed.hostname == "youtu.be":
        return parsed.path.strip("/")
    query = dict(part.split("=", 1) for part in parsed.query.split("&") if "=" in part)
    return query.get("v", url)


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


def normalize_song_for_download(song: Song) -> None:
    """Make non-Spotify/search songs safe for matching and metadata embedding."""
    if getattr(song, "genres", None) is None:
        song.genres = []
    if getattr(song, "disc_number", None) is None:
        song.disc_number = 1
    if getattr(song, "disc_count", None) is None:
        song.disc_count = 1
    if getattr(song, "tracks_count", None) is None:
        song.tracks_count = 0
    if getattr(song, "track_number", None) is None:
        song.track_number = getattr(song, "list_position", None) or 0
    if getattr(song, "album_id", None) is None:
        song.album_id = ""
    if getattr(song, "album_name", None) is None:
        song.album_name = ""
    if getattr(song, "album_artist", None) is None:
        song.album_artist = song.artist or (song.artists[0] if song.artists else "")
    if getattr(song, "publisher", None) is None:
        song.publisher = ""
    if getattr(song, "date", None) is None:
        song.date = ""
    if getattr(song, "year", None) is None:
        song.year = 0
    # spotDL 4.5 writes ISRC unconditionally for MP3; Mutagen rejects None.
    if getattr(song, "isrc", None) is None:
        song.isrc = ""


def song_from_spotify_track_response(raw_track: dict[str, Any], source_url: str) -> Song:
    """Build a track without spotDL's extra artist and album API requests."""
    if not raw_track.get("name") or not raw_track.get("duration_ms"):
        raise ValueError(f"Spotify track is unavailable: {source_url}")
    artists_meta = raw_track.get("artists") or []
    artists = [str(artist.get("name")) for artist in artists_meta if artist.get("name")]
    album = raw_track.get("album") or {}
    album_artists = [
        str(artist.get("name")) for artist in album.get("artists") or [] if artist.get("name")
    ]
    release_date = str(album.get("release_date") or "")
    images = [image for image in album.get("images") or [] if image.get("url")]
    cover_url = (
        max(
            images,
            key=lambda image: (image.get("width") or 0) * (image.get("height") or 0),
        ).get("url")
        if images
        else None
    )
    external_url = (raw_track.get("external_urls") or {}).get("spotify") or source_url
    song = Song.from_missing_data(
        name=str(raw_track["name"]),
        artists=artists or ["Unknown artist"],
        artist=artists[0] if artists else "Unknown artist",
        artist_id=artists_meta[0].get("id") if artists_meta else None,
        genres=[],
        disc_number=raw_track.get("disc_number") or 1,
        disc_count=1,
        album_id=album.get("id"),
        album_name=album.get("name") or "",
        album_artist=(
            album_artists[0] if album_artists else (artists[0] if artists else "Unknown artist")
        ),
        album_type=album.get("album_type"),
        duration=int(raw_track["duration_ms"] / 1000),
        year=(
            int(release_date[:4]) if len(release_date) >= 4 and release_date[:4].isdigit() else 0
        ),
        date=release_date,
        track_number=raw_track.get("track_number") or 1,
        tracks_count=album.get("total_tracks") or 0,
        song_id=raw_track.get("id") or external_url.rsplit("/", 1)[-1],
        explicit=bool(raw_track.get("explicit")),
        publisher=album.get("label") or "",
        url=external_url,
        isrc=(raw_track.get("external_ids") or {}).get("isrc") or "",
        cover_url=cover_url,
        copyright_text=None,
        popularity=raw_track.get("popularity"),
    )
    normalize_song_for_download(song)
    return song


def resolve_spotify_track_resilient(
    source_url: str,
    client: Any | None = None,
    attempts: int = 3,
    sleeper: Callable[[float], None] = time.sleep,
) -> Song:
    """Resolve a track with one API request per attempt and bounded retry."""
    spotify = client or SpotifyClient()
    last_error: Exception | None = None
    for attempt in range(max(1, attempts)):
        try:
            raw_track = spotify.track(source_url)
            if not isinstance(raw_track, dict):
                raise ValueError("Spotify returned incomplete track metadata")
            return song_from_spotify_track_response(raw_track, source_url)
        except Exception as exc:  # noqa: BLE001 - unofficial provider variance
            last_error = exc
            if attempt + 1 < max(1, attempts):
                sleeper(_SPOTIFY_RESOLVE_RETRY_SECONDS * (attempt + 1))
    raise RuntimeError(
        "Spotify track metadata is temporarily unavailable after "
        f"{max(1, attempts)} attempts: {last_error}"
    ) from last_error


def reinitialize_song_resilient(
    song: Song,
    client: Any | None = None,
    attempts: int = 3,
    sleeper: Callable[[float], None] | None = None,
) -> Song:
    """Complete structural fields locally; never contact Spotify during download.

    Resolve/import already supplies user-facing metadata. Re-fetching each track
    made long jobs depend on an anonymous Spotify session for a second time and
    caused bursts of ``Could not get session`` failures after hundreds of songs.
    """
    _ = client, attempts, sleeper
    normalize_song_for_download(song)
    return song


# Downloader.search_and_download resolves this module global at runtime. Installing
# the resilient implementation once keeps spotDL's download pipeline intact while
# removing its redundant album/artist calls.
spotdl_downloader_module.reinit_song = reinitialize_song_resilient


_OUTPUT_SUFFIX_ATTRIBUTE = "_playlistdl_output_suffix"


def _create_file_name_with_collision_suffix(*args: Any, **kwargs: Any) -> Path:
    """Preserve selected naming preset while disambiguating colliding tracks."""
    path = spotdl_create_file_name(*args, **kwargs)
    song = kwargs.get("song") or args[0]
    suffix = str(getattr(song, _OUTPUT_SUFFIX_ATTRIBUTE, "") or "")
    return path.with_name(f"{path.stem}{suffix}{path.suffix}") if suffix else path


spotdl_downloader_module.create_file_name = _create_file_name_with_collision_suffix


def _path_key(path: Path | str) -> str:
    return os.path.normcase(os.path.abspath(str(path))).casefold()


def assign_unique_output_suffixes(
    songs: list[Song],
    output_template: str,
    audio_format: str,
    known_songs: dict[str, list[Path]] | None = None,
) -> None:
    """Avoid silent overwrites when different tracks format to the same path."""
    known_owner = {
        _path_key(path): str(url) for url, paths in (known_songs or {}).items() for path in paths
    }
    groups: dict[str, list[tuple[Song, Path]]] = {}
    for song in songs:
        setattr(song, _OUTPUT_SUFFIX_ATTRIBUTE, "")
        base_path = spotdl_create_file_name(
            song=song,
            template=output_template,
            file_extension=audio_format,
            restrict="none",
        )
        groups.setdefault(_path_key(base_path), []).append((song, base_path))

    reserved = set(known_owner)
    for base_key, entries in groups.items():
        if entries[0][1].exists():
            reserved.add(base_key)
        existing_owner = known_owner.get(base_key)
        keeper: Song | None = next(
            (song for song, _ in entries if str(song.url or "") == existing_owner),
            None,
        )
        if keeper is None and base_key not in reserved:
            keeper = entries[0][0]
            reserved.add(base_key)

        for song, base_path in entries:
            if song is keeper:
                continue
            raw_id = re.sub(r"[^\w-]", "", str(song.song_id or song.url or ""))[:8]
            token = raw_id or str(song.list_position or "track")
            ordinal = 1
            while True:
                tail = "" if ordinal == 1 else f"-{ordinal}"
                suffix = f" [{token}{tail}]"
                candidate = base_path.with_name(f"{base_path.stem}{suffix}{base_path.suffix}")
                candidate_key = _path_key(candidate)
                candidate_owner = known_owner.get(candidate_key)
                if candidate_owner == str(song.url or "") or (
                    candidate_key not in reserved and not candidate.exists()
                ):
                    setattr(song, _OUTPUT_SUFFIX_ATTRIBUTE, suffix)
                    reserved.add(candidate_key)
                    break
                ordinal += 1


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
        self._progress_state: dict[str, tuple[int, str, str | None]] = {}

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

    def resolve_search(self, query: str, limit: int = 12, client: Any | None = None) -> PlaylistDto:
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
        song = resolve_spotify_track_resilient(url)
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
        primary_query = f"{artist} {title}".strip()
        if not primary_query:
            raise ValueError("Search needs a title or artist")
        queries = list(dict.fromkeys((primary_query, title.strip())))
        raw: list[dict[str, Any]] = []
        for query in queries:
            for search_filter in ("songs", "videos"):
                try:
                    raw.extend(client.search(query, filter=search_filter, limit=limit))
                except Exception:  # noqa: BLE001 - provider variance
                    logger.exception(
                        "ytmusicapi search failed for query %r filter %s",
                        query,
                        search_filter,
                    )
        candidates = []
        seen: set[str] = set()
        for item in raw:
            candidate = _candidate_from_result(item)
            if candidate is None or candidate["url"] in seen:
                continue
            seen.add(candidate["url"])
            candidates.append(candidate)
        ranked = rank_candidates(candidates, duration_seconds)
        ranked.sort(
            key=lambda candidate: (
                not candidate_is_relevant(
                    candidate,
                    title=title,
                    artists=[artist] if artist else [],
                    duration_seconds=duration_seconds,
                )
            )
        )
        return ranked[:limit]

    @staticmethod
    def ensure_runtime() -> None:
        """Load provider runtime resources omitted easily by freezer recipes."""
        from ytmusicapi import YTMusic

        YTMusic()
        if getattr(sys, "frozen", False) and (bundled := find_bundled_server_modules()):
            raise RuntimeError(
                "Frozen backend unexpectedly contains unused server modules: " + ", ".join(bundled)
            )

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
        stored_songs = self._songs.get(playlist_id)
        if stored_songs is None:
            raise ValueError("Unknown or expired playlist id")
        if track_ids:
            selected = set(track_ids)
            stored_songs = [song for song in stored_songs if (song.song_id or song.url) in selected]
            if not stored_songs:
                raise ValueError("No requested tracks exist in this playlist")
        # Downloaders and fallback recovery mutate Song.download_url and other fields.
        # Keep resolved metadata pristine so clearing a manual source or retrying a job
        # starts from automatic matching instead of a source chosen by an earlier run.
        songs = copy.deepcopy(stored_songs)
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
            normalize_song_for_download(song)

        output, output_template = build_output_paths(
            output_dir,
            self._names.get(playlist_id) or "playlist",
            naming_preset,
            create_source_folder,
        )
        output.mkdir(parents=True, exist_ok=True)
        self._cancel.clear()
        self._progress_state.clear()

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
        assign_unique_output_suffixes(
            songs,
            output_template,
            audio_format,
            getattr(downloader, "known_songs", None),
        )
        downloader.progress_handler.close()
        downloader.progress_handler = ProgressHandler(
            simple_tui=True,
            update_callback=self._on_progress,
        )
        downloader.progress_handler.set_songs(songs)

        try:
            self._download_windows(
                playlist_id=playlist_id,
                output=output,
                songs=songs,
                downloader=downloader,
                threads=threads,
                throttle_seconds=throttle_seconds,
                retries=retries,
                write_m3u=write_m3u,
            )
        finally:
            downloader.progress_handler.close()

    def _download_windows(
        self,
        playlist_id: str,
        output: Path,
        songs: list[Song],
        downloader: Downloader,
        threads: int,
        throttle_seconds: float,
        retries: int,
        write_m3u: bool,
    ) -> None:
        """Run bounded download windows and always leave one final record per track."""

        results_by_id: dict[str, dict[str, Any]] = {}
        started = time.monotonic()
        worker_count = max(1, min(threads, 4))
        window_size = worker_count * 4
        for offset in range(0, len(songs), window_size):
            if self._cancel.is_set():
                self._emit_cancelled(results_by_id, songs)
                return
            if offset and throttle_seconds > 0 and self._wait_cancellable(throttle_seconds):
                self._emit_cancelled(results_by_id, songs)
                return

            window = songs[offset : offset + window_size]
            pending = list(window)
            for attempt in range(max(0, retries) + 1):
                if self._run_attempt(downloader, pending, results_by_id, threads, 0):
                    self._emit_cancelled(results_by_id, songs)
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
                    self._emit_cancelled(results_by_id, songs)
                    return

            fallback_pending = [
                song
                for song in window
                if (record := results_by_id.get(song.song_id or song.url)) is not None
                and not record["success"]
                and record["error_class"] in _FALLBACK_FAILURE_CLASSES
            ]
            for index, song in enumerate(fallback_pending):
                if self._cancel.is_set():
                    self._emit_cancelled(results_by_id, songs)
                    return
                if index and self._wait_cancellable(_FALLBACK_PAUSE_SECONDS):
                    self._emit_cancelled(results_by_id, songs)
                    return
                self._try_source_fallback(downloader, song, results_by_id)

            self._emit_window_results(window, songs, results_by_id, started)

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
        # Give spotDL's bounded worker pool enough queued work to keep every slot
        # busy. Tiny worker-sized batches caused head-of-line stalls whenever one
        # slow track held the next batch back.
        worker_count = max(1, min(threads, 4))
        batch_size = worker_count * 4
        for offset in range(0, len(songs), batch_size):
            if self._cancel.is_set():
                return True
            if offset and throttle_seconds > 0 and self._wait_cancellable(throttle_seconds):
                return True
            batch = songs[offset : offset + batch_size]
            errors_before = len(downloader.errors)
            try:
                downloaded = downloader.download_multiple_songs(batch)
            except Exception as exc:  # noqa: BLE001 - isolate provider batch failures
                logger.exception("Downloader batch failed; continuing with later tracks")
                error = f"{exc.__class__.__name__}: {exc}"
                for song in batch:
                    track_id = song.song_id or song.url
                    results_by_id[track_id] = self._result_record(song, None, error)
                continue
            for resolved_song, path in downloaded:
                track_id = resolved_song.song_id or resolved_song.url
                results_by_id[track_id] = self._result_record(resolved_song, path)
            new_errors = list(downloader.errors[errors_before:])
            self._attribute_errors(batch, new_errors, results_by_id)
            for song in batch:
                track_id = song.song_id or song.url
                if track_id not in results_by_id:
                    results_by_id[track_id] = self._result_record(
                        song,
                        None,
                        "Provider returned no result for this track",
                    )
        return False

    @staticmethod
    def _result_record(
        song: Song, path: Path | str | None, error: str | None = None
    ) -> dict[str, Any]:
        track_id = song.song_id or song.url
        return {
            "track_id": track_id,
            "path": str(path) if path else None,
            "success": path is not None,
            "error": error,
            "error_class": None if path else classify_failure(error),
            "source_url": getattr(song, "download_url", None),
            "fallback_used": False,
        }

    def _emit_window_results(
        self,
        window: list[Song],
        all_songs: list[Song],
        results_by_id: dict[str, dict[str, Any]],
        started: float,
    ) -> None:
        for song in window:
            record = results_by_id.get(song.song_id or song.url)
            if record is not None:
                self._emit({"type": "track_result", **record})
        processed = len(results_by_id)
        succeeded = sum(record["success"] for record in results_by_id.values())
        elapsed = max(0.001, time.monotonic() - started)
        rate = processed / elapsed * 60
        remaining = max(0, len(all_songs) - processed)
        self._emit(
            {
                "type": "job_progress",
                "processed": processed,
                "total": len(all_songs),
                "succeeded": succeeded,
                "failed": processed - succeeded,
                "tracks_per_minute": round(rate, 2),
                "eta_seconds": round(remaining / rate * 60) if rate > 0 else None,
            }
        )

    def _emit_cancelled(
        self,
        results_by_id: dict[str, dict[str, Any]],
        songs: list[Song],
    ) -> None:
        results = [
            results_by_id[track_id]
            for song in songs
            if (track_id := song.song_id or song.url) in results_by_id
        ]
        self._emit({"type": "job_cancelled", "results": results})

    @staticmethod
    def _attribute_errors(
        batch: list[Song],
        new_errors: list[str],
        results_by_id: dict[str, dict[str, Any]],
    ) -> None:
        """Match spotDL error strings to failed songs without cross-track leakage."""
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
            identities = [
                str(record["track_id"]),
                str(getattr(song, "url", "") or ""),
                str(getattr(song, "download_url", "") or ""),
                str(display_name or ""),
            ]
            match = next(
                (
                    text
                    for text in unmatched
                    if any(identity and identity in text for identity in identities)
                ),
                None,
            )
            if match is not None:
                unmatched.remove(match)
                record["error"] = match
        for record in failed:
            if record["error"] is None and unmatched:
                record["error"] = unmatched.pop(0)
            record["error_class"] = classify_failure(record["error"])

    def _try_source_fallback(
        self,
        downloader: Downloader,
        song: Song,
        results_by_id: dict[str, dict[str, Any]],
    ) -> None:
        """Try up to three strong alternate sources, sequentially and conservatively."""
        track_id = song.song_id or song.url
        attempted = {_source_identity(getattr(song, "download_url", None))}
        try:
            candidates = self.search_sources(
                song.name,
                (song.artists or [getattr(song, "artist", "")])[0],
                duration_seconds=song.duration,
                limit=24,
            )
        except Exception:  # noqa: BLE001 - fallback must not abort the whole job
            logger.exception("Alternate source search failed for %s", track_id)
            return

        relevant = [
            candidate
            for candidate in candidates
            if _source_identity(str(candidate.get("url") or "")) not in attempted
            and candidate_is_relevant(
                candidate,
                title=song.name,
                artists=list(song.artists or []),
                duration_seconds=song.duration,
            )
        ][:6]
        for candidate in relevant:
            if self._cancel.is_set():
                return
            source_url = str(candidate["url"])
            attempted.add(_source_identity(source_url))
            song.download_url = source_url
            self._emit(
                {
                    "type": "track_progress",
                    "track_id": track_id,
                    "progress": 0,
                    "status": "Trying alternate source",
                }
            )
            self._run_attempt(
                downloader,
                [song],
                results_by_id,
                threads=1,
                throttle_seconds=0,
            )
            record = results_by_id[track_id]
            record["source_url"] = source_url
            record["fallback_used"] = True
            if record["success"]:
                return

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
        track_id = tracker.song.song_id or tracker.song.url
        state = (int(tracker.progress), message, tracker.path)
        if self._progress_state.get(track_id) == state:
            return
        self._progress_state[track_id] = state
        self._emit(
            {
                "type": "track_progress",
                "track_id": track_id,
                "progress": int(tracker.progress),
                "status": message,
                "path": tracker.path,
            }
        )
