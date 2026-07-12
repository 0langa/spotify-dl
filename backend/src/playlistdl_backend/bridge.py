from __future__ import annotations

import json
import logging
import sys
import threading
import traceback
from typing import Any, TextIO

from playlistdl_backend import __version__
from playlistdl_backend.engine import Engine


def format_exception(exc: Exception) -> str:
    """Include safe provider detail hidden by some wrapper exceptions."""
    message = str(exc)
    provider_detail = getattr(exc, "error", None)
    if provider_detail and str(provider_detail) not in message:
        return f"{message} ({provider_detail})"
    return message


class Bridge:
    def __init__(self, input_stream: TextIO | None = None, output_stream: TextIO | None = None):
        self._input = input_stream or sys.stdin
        self._output = output_stream or sys.stdout
        self._write_lock = threading.Lock()
        self._engine = Engine(self.emit)
        self._worker: threading.Thread | None = None

    def emit(self, event: dict[str, Any]) -> None:
        with self._write_lock:
            self._output.write(json.dumps(event, ensure_ascii=False) + "\n")
            self._output.flush()

    def run(self) -> None:
        logging.basicConfig(level=logging.WARNING, stream=sys.stderr)
        self.emit({"type": "ready", "version": __version__, "protocol": 1})
        for line in self._input:
            line = line.strip()
            if not line:
                continue
            request_id: str | None = None
            try:
                request = json.loads(line)
                request_id = request.get("id")
                if self._dispatch(request):
                    return
            except Exception as exc:  # noqa: BLE001 - protocol boundary
                self.emit(
                    {
                        "type": "error",
                        "request_id": request_id,
                        "message": format_exception(exc),
                        "detail": (
                            traceback.format_exc()
                            if logging.getLogger().isEnabledFor(logging.DEBUG)
                            else None
                        ),
                    }
                )

    def _dispatch(self, request: dict[str, Any]) -> bool | None:
        """Handle one request; a truthy return stops the read loop."""
        command = request.get("type")
        request_id = request.get("id")
        if command == "ping":
            self.emit({"type": "pong", "request_id": request_id})
            return None
        if command == "runtime_check":
            self._engine.ensure_runtime()
            self.emit({"type": "runtime_ok", "request_id": request_id})
            return
        if command == "diagnose":
            report = self._engine.diagnose()
            self.emit({"type": "diagnose_result", "request_id": request_id, **report})
            return
        if command == "search_sources":
            candidates = self._engine.search_sources(
                title=str(request.get("title", "")),
                artist=str(request.get("artist", "")),
                duration_seconds=int(request.get("duration_seconds", 0)),
                limit=int(request.get("limit", 8)),
            )
            self.emit(
                {
                    "type": "sources_found",
                    "request_id": request_id,
                    "candidates": candidates,
                }
            )
            return
        if command == "resolve":
            playlist = self._engine.resolve(str(request["url"]))
            self.emit(
                {
                    "type": "playlist_resolved",
                    "request_id": request_id,
                    "playlist": playlist.to_dict(),
                }
            )
            return
        if command == "resolve_search":
            playlist = self._engine.resolve_search(
                str(request["query"]), limit=int(request.get("limit", 12))
            )
            self.emit(
                {
                    "type": "playlist_resolved",
                    "request_id": request_id,
                    "playlist": playlist.to_dict(),
                }
            )
            return
        if command == "import_manifest":
            playlist = self._engine.import_manifest(str(request["path"]))
            self.emit(
                {
                    "type": "playlist_resolved",
                    "request_id": request_id,
                    "playlist": playlist.to_dict(),
                }
            )
            return
        if command == "start":
            if self._worker is not None and self._worker.is_alive():
                raise RuntimeError("A download job is already running")
            self._engine.ensure_startable(
                str(request["playlist_id"]), str(request.get("format", "mp3"))
            )
            self._worker = threading.Thread(
                target=self._download_worker,
                kwargs={
                    "request_id": request_id,
                    "playlist_id": str(request["playlist_id"]),
                    "output_dir": str(request["output_dir"]),
                    "bitrate": str(request.get("bitrate", "0")),
                    "threads": int(request.get("threads", 2)),
                    "cookie_file": request.get("cookie_file"),
                    "track_ids": request.get("track_ids"),
                    "audio_format": str(request.get("format", "mp3")),
                    "write_m3u": bool(request.get("write_m3u", False)),
                    "source_overrides": request.get("source_overrides"),
                    "naming_preset": str(request.get("naming_preset", "position_artist_title")),
                    "create_source_folder": bool(request.get("create_source_folder", True)),
                    "throttle_seconds": float(request.get("throttle_seconds", 0.0)),
                    "retries": int(request.get("retries", 1)),
                    "ytdlp_args": request.get("ytdlp_args"),
                    "embed_lyrics": bool(request.get("embed_lyrics", False)),
                },
                daemon=True,
            )
            self._worker.start()
            self.emit({"type": "job_started", "request_id": request_id})
            return
        if command == "cancel":
            self._engine.cancel()
            self.emit({"type": "cancel_requested", "request_id": request_id})
            return
        if command == "shutdown":
            self._engine.cancel()
            return True
        raise ValueError(f"Unknown command: {command}")

    def _download_worker(self, request_id: str | None, **kwargs: Any) -> None:
        try:
            self._engine.download(**kwargs)
        except Exception as exc:  # noqa: BLE001 - worker boundary
            self.emit(
                {
                    "type": "error",
                    "request_id": request_id,
                    "message": format_exception(exc),
                }
            )
