from __future__ import annotations

import json
import logging
import sys
import threading
import traceback
from collections.abc import Callable
from typing import Any, TextIO

from playlistdl_backend import __version__
from playlistdl_backend.engine import Engine

# Stays below the app's graceful stop window so the backend can exit on its own before
# the app force-kills the process tree.
_WORKER_SHUTDOWN_TIMEOUT_SECONDS = 4.0


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
        try:
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
        finally:
            # Interpreter shutdown kills the daemon worker without stopping the
            # converter/downloader child processes it started. Give the cancelled
            # worker a bounded moment to unwind so no ffmpeg/yt-dlp child is orphaned.
            self.stop_worker()

    def stop_worker(self, timeout: float = _WORKER_SHUTDOWN_TIMEOUT_SECONDS) -> None:
        """Cancel the download worker and wait briefly for it to finish."""
        worker = self._worker
        if worker is None or not worker.is_alive():
            return
        self._engine.cancel()
        worker.join(timeout=timeout)

    def _run_off_loop(
        self,
        request_id: str | None,
        handler: Callable[[], dict[str, Any]],
    ) -> None:
        """Answer one network-bound request without blocking the request loop."""

        def worker() -> None:
            try:
                self.emit({**handler(), "request_id": request_id})
            except Exception as exc:  # noqa: BLE001 - request boundary
                logging.exception("Background request failed")
                try:
                    self.emit(
                        {
                            "type": "error",
                            "request_id": request_id,
                            "message": format_exception(exc),
                        }
                    )
                except Exception:  # noqa: BLE001 - stdout closed during shutdown
                    logging.exception("Could not report a background request failure")

        threading.Thread(target=worker, daemon=True).start()

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
            # Endpoint probes take seconds; running them off the read loop keeps
            # cancel and progress traffic flowing while a diagnosis is in flight.
            self._run_off_loop(
                request_id,
                lambda: {"type": "diagnose_result", **self._engine.diagnose()},
            )
            return
        if command == "search_sources":
            title = str(request.get("title", ""))
            artist = str(request.get("artist", ""))
            duration_seconds = int(request.get("duration_seconds", 0))
            limit = int(request.get("limit", 8))
            self._run_off_loop(
                request_id,
                lambda: {
                    "type": "sources_found",
                    "candidates": self._engine.search_sources(
                        title=title,
                        artist=artist,
                        duration_seconds=duration_seconds,
                        limit=limit,
                    ),
                },
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
                # A queue can send the next start right after job_completed while
                # the previous worker thread is still unwinding — give it a moment.
                self._worker.join(timeout=5)
            if self._worker is not None and self._worker.is_alive():
                raise RuntimeError("A download job is already running")
            self._engine.ensure_startable(
                str(request["playlist_id"]), str(request.get("format", "mp3"))
            )
            # Arm the job here, on the request loop, so a cancel that arrives while the
            # worker is still starting up is not cleared by the worker itself.
            self._engine.begin_job()
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
                    "duplicate_policy": str(request.get("duplicate_policy", "download")),
                    "existing_files": request.get("existing_files"),
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
            logging.exception("Download worker stopped unexpectedly")
            self.emit(
                {
                    "type": "error",
                    "request_id": request_id,
                    "message": format_exception(exc),
                }
            )
