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
        ("AudioProviderError: YT-DLP download error", "source_unavailable"),
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
    batches: list[list[str]] = []

    def __init__(self, settings: dict[str, Any]) -> None:
        self.settings = settings
        self.errors: list[str] = []
        self.progress_handler = _FakeProgressHandler()
        self.attempts: dict[str, int] = {}
        _FakeDownloader.last_instance = self

    def download_multiple_songs(self, batch: list[Any]) -> list[tuple[Any, Any]]:
        _FakeDownloader.batches.append([song.song_id for song in batch])
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
    monkeypatch.setattr(Engine, "search_sources", staticmethod(lambda *args, **kwargs: []))
    _FakeDownloader.script = {}
    _FakeDownloader.last_instance = None
    _FakeDownloader.batches = []
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


def test_download_uses_rolling_windows_larger_than_worker_count(
    download_env: dict[str, Any],
) -> None:
    engine: Engine = download_env["engine"]
    songs = [_fake_song(f"Song {index}", index) for index in range(1, 11)]
    engine._songs["job"] = songs  # type: ignore[assignment]
    _FakeDownloader.script = {song.song_id: [(f"/out/{song.song_id}.mp3", None)] for song in songs}

    engine.download("job", download_env["out"], threads=2, retries=0)

    assert [len(batch) for batch in _FakeDownloader.batches] == [8, 2]
    assert _FakeDownloader.last_instance is not None
    assert _FakeDownloader.last_instance.settings["threads"] == 2


def test_download_tries_ranked_relevant_fallback_after_source_failure(
    download_env: dict[str, Any], monkeypatch: pytest.MonkeyPatch
) -> None:
    engine: Engine = download_env["engine"]
    song = download_env["songs"][0]
    engine._songs["job"] = [song]  # type: ignore[assignment]
    _FakeDownloader.script = {
        song.song_id: [
            (None, "AudioProviderError: YT-DLP download error"),
            ("/out/one.mp3", None),
        ]
    }
    monkeypatch.setattr(
        Engine,
        "search_sources",
        staticmethod(
            lambda *args, **kwargs: [
                {
                    "url": "https://www.youtube.com/watch?v=fallback",
                    "title": "Artist - One (Lyrics)",
                    "artists": ["Artist"],
                    "duration_seconds": 201,
                    "result_type": "video",
                    "duration_delta_seconds": 1,
                }
            ]
        ),
    )

    engine.download("job", download_env["out"], retries=0)

    result = _last_completion(download_env["events"])["results"][0]
    assert result["success"] is True
    assert result["fallback_used"] is True
    assert result["source_url"].endswith("fallback")
    assert song.download_url.endswith("fallback")


def test_download_rejects_irrelevant_fallback_candidate(
    download_env: dict[str, Any], monkeypatch: pytest.MonkeyPatch
) -> None:
    engine: Engine = download_env["engine"]
    song = download_env["songs"][0]
    engine._songs["job"] = [song]  # type: ignore[assignment]
    _FakeDownloader.script = {
        song.song_id: [(None, "No results found for song")],
    }
    monkeypatch.setattr(
        Engine,
        "search_sources",
        staticmethod(
            lambda *args, **kwargs: [
                {
                    "url": "https://www.youtube.com/watch?v=wrong",
                    "title": "Completely Different Track",
                    "artists": ["Someone Else"],
                    "duration_seconds": 200,
                    "result_type": "video",
                    "duration_delta_seconds": 0,
                }
            ]
        ),
    )

    engine.download("job", download_env["out"], retries=0)

    result = _last_completion(download_env["events"])["results"][0]
    assert result["success"] is False
    assert result.get("fallback_used") is not True
    assert len(_FakeDownloader.batches) == 1


def test_attribute_errors_matches_spotify_url_before_batch_leftover() -> None:
    songs = [_fake_song("One", 1), _fake_song("Two", 2)]
    results = {
        song.song_id: {
            "track_id": song.song_id,
            "path": None,
            "success": False,
            "error": None,
            "error_class": None,
        }
        for song in songs
    }

    Engine._attribute_errors(
        songs,
        [
            f"{songs[1].url} - second failed",
            f"{songs[0].url} - first failed",
        ],
        results,
    )

    assert "first failed" in results["id-1"]["error"]
    assert "second failed" in results["id-2"]["error"]


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
