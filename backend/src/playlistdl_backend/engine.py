from __future__ import annotations

import logging
import os
import threading
import uuid
from collections.abc import Callable
from pathlib import Path
from typing import Any

from spotdl.download.downloader import Downloader
from spotdl.download.progress_handler import ProgressHandler, SongTracker
from spotdl.types.playlist import Playlist
from spotdl.types.song import Song
from spotdl.utils.spotify import SpotifyClient

from playlistdl_backend.models import PlaylistDto, TrackDto

logger = logging.getLogger(__name__)

EventSink = Callable[[dict[str, Any]], None]


class Engine:
    def __init__(self, emit: EventSink) -> None:
        self._emit = emit
        self._spotify_initialized = False
        self._songs: dict[str, list[Song]] = {}
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
        self._ensure_spotify()
        metadata, songs = Playlist.get_metadata(url)
        playlist_id = uuid.uuid4().hex
        self._songs[playlist_id] = songs
        tracks = [self._track_dto(song, index + 1) for index, song in enumerate(songs)]
        return PlaylistDto(
            id=playlist_id,
            name=str(metadata.get("name") or "Spotify playlist"),
            description=str(metadata.get("description") or ""),
            owner=str(metadata.get("author_name") or ""),
            cover_url=str(metadata.get("cover_url") or ""),
            source_url=url,
            tracks=tracks,
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
    ) -> None:
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
            "format": "mp3",
            "bitrate": bitrate,
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
        self._emit({"type": "job_completed", "results": results})

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
