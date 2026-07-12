from __future__ import annotations

from types import SimpleNamespace
from typing import Any

import pytest

from playlistdl_backend import engine as engine_module
from playlistdl_backend.engine import Engine


def _fake_song(name: str, position: int) -> SimpleNamespace:
    return SimpleNamespace(
        song_id=f"id-{position}",
        url=f"https://open.spotify.com/track/id-{position}",
        list_position=position,
        name=name,
        artists=["Artist"],
        artist="Artist",
        album_name="Album",
        duration=200,
        cover_url="https://img.example/cover.jpg",
        isrc=None,
        display_name=f"Artist - {name}",
        download_url=None,
    )


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("ERROR: Sign in to confirm you're not a bot", "youtube_blocked"),
        ("HTTP Error 429: Too Many Requests", "youtube_blocked"),
        ("Max retries exceeded with url: /", "network"),
        ("Failed to complete request. (ConnectTimeoutError)", "network"),
        ("LookupError: No results found for song", "no_match"),
        ("FFmpeg returned non-zero exit status", "convert_error"),
        ("something entirely different", "unknown"),
        (None, "unknown"),
    ],
)
def test_classify_failure_buckets(text: str | None, expected: str) -> None:
    assert engine_module.classify_failure(text) == expected


def test_dominant_failure_class_prefers_most_actionable() -> None:
    assert (
        engine_module.dominant_failure_class(["no_match", "youtube_blocked", "unknown"])
        == "youtube_blocked"
    )
    assert engine_module.dominant_failure_class(["convert_error", "no_match"]) == "no_match"
    assert engine_module.dominant_failure_class([]) is None


class _FakeProgressHandler:
    def __init__(self, simple_tui: bool = True, update_callback: Any = None) -> None:
        self.update_callback = update_callback

    def set_songs(self, songs: Any) -> None:
        self.songs = songs

    def close(self) -> None:
        pass


class _FakeDownloader:
    """Scripted spotDL Downloader stand-in: per-attempt outcomes per song id."""

    last_instance: _FakeDownloader | None = None
    script: dict[str, list[tuple[str | None, str | None]]] = {}

    def __init__(self, settings: dict[str, Any]) -> None:
        self.settings = settings
        self.errors: list[str] = []
        self.progress_handler = _FakeProgressHandler()
        self.attempts: dict[str, int] = {}
        _FakeDownloader.last_instance = self

    def download_multiple_songs(self, batch: list[Any]) -> list[tuple[Any, Any]]:
        results = []
        for song in batch:
            attempt = self.attempts.get(song.song_id, 0)
            self.attempts[song.song_id] = attempt + 1
            outcomes = _FakeDownloader.script[song.song_id]
            path, error = outcomes[min(attempt, len(outcomes) - 1)]
            if error is not None:
                self.errors.append(f"{song.display_name} - {error}")
            results.append((song, path))
        return results


@pytest.fixture
def download_env(monkeypatch: pytest.MonkeyPatch, tmp_path) -> dict[str, Any]:
    events: list[dict[str, Any]] = []
    instance = Engine(emit=events.append)
    monkeypatch.setattr(engine_module, "Downloader", _FakeDownloader)
    monkeypatch.setattr(engine_module, "ProgressHandler", _FakeProgressHandler)
    monkeypatch.setattr(engine_module, "_RETRY_BACKOFF_SECONDS", 0.0)
    _FakeDownloader.script = {}
    _FakeDownloader.last_instance = None
    songs = [_fake_song("One", 1), _fake_song("Two", 2)]
    instance._songs["job"] = songs  # type: ignore[assignment]
    instance._names["job"] = "My Mix"
    return {"engine": instance, "events": events, "songs": songs, "out": str(tmp_path)}


def _last_completion(events: list[dict[str, Any]]) -> dict[str, Any]:
    completions = [event for event in events if event["type"] == "job_completed"]
    assert completions, f"no job_completed among {[event['type'] for event in events]}"
    return completions[-1]


def test_download_retries_only_retryable_failures(download_env: dict[str, Any]) -> None:
    engine: Engine = download_env["engine"]
    _FakeDownloader.script = {
        "id-1": [(None, "Sign in to confirm you're not a bot"), ("/out/one.mp3", None)],
        "id-2": [(None, "No results found for song")],
    }

    engine.download("job", download_env["out"], retries=1)

    completion = _last_completion(download_env["events"])
    by_id = {record["track_id"]: record for record in completion["results"]}
    assert by_id["id-1"]["success"] is True
    assert by_id["id-2"]["success"] is False
    assert by_id["id-2"]["error_class"] == "no_match"
    assert "No results found" in by_id["id-2"]["error"]
    downloader = _FakeDownloader.last_instance
    assert downloader is not None
    assert downloader.attempts["id-1"] == 2
    assert downloader.attempts["id-2"] == 1
    assert completion["failure_class"] == "no_match"
    assert "Source button" in completion["failure_hint"]


def test_download_reports_dominant_failure_class(download_env: dict[str, Any]) -> None:
    engine: Engine = download_env["engine"]
    _FakeDownloader.script = {
        "id-1": [(None, "HTTP Error 429: Too Many Requests")],
        "id-2": [(None, "No results found for song")],
    }

    engine.download("job", download_env["out"], retries=0)

    completion = _last_completion(download_env["events"])
    assert completion["failure_class"] == "youtube_blocked"
    assert "cookie" in completion["failure_hint"].lower()
    by_id = {record["track_id"]: record for record in completion["results"]}
    assert by_id["id-1"]["error_class"] == "youtube_blocked"
    assert "429" in by_id["id-1"]["error"]


def test_download_passes_ytdlp_args_and_reports_clean_run(download_env: dict[str, Any]) -> None:
    engine: Engine = download_env["engine"]
    _FakeDownloader.script = {
        "id-1": [("/out/one.mp3", None)],
        "id-2": [("/out/two.mp3", None)],
    }

    engine.download(
        "job", download_env["out"], ytdlp_args="--extractor-args youtube:player_client=tv"
    )

    downloader = _FakeDownloader.last_instance
    assert downloader is not None
    assert downloader.settings["yt_dlp_args"] == "--extractor-args youtube:player_client=tv"
    completion = _last_completion(download_env["events"])
    assert completion["failure_class"] is None
    assert completion["failure_hint"] is None


def test_download_blank_ytdlp_args_normalize_to_none(download_env: dict[str, Any]) -> None:
    engine: Engine = download_env["engine"]
    _FakeDownloader.script = {
        "id-1": [("/out/one.mp3", None)],
        "id-2": [("/out/two.mp3", None)],
    }

    engine.download("job", download_env["out"], ytdlp_args="   ")

    assert _FakeDownloader.last_instance is not None
    assert _FakeDownloader.last_instance.settings["yt_dlp_args"] is None


def test_diagnose_reports_endpoint_health() -> None:
    engine = Engine(emit=lambda event: None)

    def probe(url: str) -> tuple[bool, str]:
        return ("spotify" in url, f"probe {url}")

    report = engine.diagnose(probe=probe)

    assert report["frozen"] is False
    assert report["backend_path"]
    assert len(report["checks"]) == 3
    spotify = next(check for check in report["checks"] if "spotify" in check["url"])
    youtube = next(check for check in report["checks"] if "www.youtube" in check["url"])
    assert spotify["ok"] is True
    assert youtube["ok"] is False
    assert all("elapsed_ms" in check for check in report["checks"])
