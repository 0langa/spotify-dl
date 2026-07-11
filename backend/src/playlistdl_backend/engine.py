from __future__ import annotations

import logging
import os
import threading
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

from playlistdl_backend.models import PlaylistDto, TrackDto
from playlistdl_backend.playlist_file import write_m3u8

logger = logging.getLogger(__name__)

EventSink = Callable[[dict[str, Any]], None]

_SOURCE_TYPES = ("playlist", "album", "track")

SUPPORTED_FORMATS = ("mp3", "m4a", "opus", "flac", "wav", "ogg")


def effective_bitrate(audio_format: str, bitrate: str | None) -> str | None:
    """Map the UI bitrate choice onto spotDL's bitrate option per format."""
    if audio_format == "mp3":
        return bitrate or "0"
    if audio_format in ("m4a", "opus"):
        # Source audio is already AAC/Opus; copy the stream instead of re-encoding.
        return "disable"
    # Lossless targets (flac/wav) and ogg re-encode with converter defaults.
    return None


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

        output = Path(output_dir).expanduser().resolve()
        output.mkdir(parents=True, exist_ok=True)
        self._cancel.clear()

        settings: dict[str, Any] = {
            "audio_providers": ["youtube-music", "youtube"],
            "lyrics_providers": [],
            "format": audio_format,
            "bitrate": effective_bitrate(audio_format, bitrate),
            "threads": max(1, min(threads, 4)),
            "output": str(output / "{list-position} - {artist} - {title}.{output-ext}"),
            "overwrite": "skip",
            "scan_for_songs": True,
            "restrict": "none",
            "simple_tui": True,
            "cookie_file": cookie_file,
            "ffmpeg": os.environ.get("PLAYLISTDL_FFMPEG", "ffmpeg"),
        }
        downloader = Downloader(settings)
        downloader.progress_handler.close()
        downloader.progress_handler = ProgressHandler(
            simple_tui=True,
            update_callback=self._on_progress,
        )
        downloader.progress_handler.set_songs(songs)

        results: list[dict[str, Any]] = []
        batch_size = max(1, min(threads, 4))
        for offset in range(0, len(songs), batch_size):
            if self._cancel.is_set():
                self._emit({"type": "job_cancelled"})
                return
            batch = songs[offset : offset + batch_size]
            for resolved_song, path in downloader.download_multiple_songs(batch):
                results.append(
                    {
                        "track_id": resolved_song.song_id or resolved_song.url,
                        "path": str(path) if path else None,
                        "success": path is not None,
                    }
                )
        downloader.progress_handler.close()
        m3u_path = (
            self._write_playlist_file(playlist_id, output, songs, results) if write_m3u else None
        )
        self._emit(
            {
                "type": "job_completed",
                "results": results,
                "m3u_path": str(m3u_path) if m3u_path else None,
            }
        )

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
