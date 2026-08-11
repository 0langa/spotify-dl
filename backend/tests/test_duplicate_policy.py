"""Cross-job duplicate handling: reuse what an earlier job already downloaded."""

from __future__ import annotations

from pathlib import Path
from typing import Any

import pytest
from test_engine_reliability import _fake_song, _FakeDownloader, _FakeProgressHandler

from playlistdl_backend import engine as engine_module
from playlistdl_backend.engine import Engine


@pytest.fixture
def policy_env(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> dict[str, Any]:
    events: list[dict[str, Any]] = []
    instance = Engine(emit=events.append)
    monkeypatch.setattr(engine_module, "Downloader", _FakeDownloader)
    monkeypatch.setattr(engine_module, "ProgressHandler", _FakeProgressHandler)
    monkeypatch.setattr(Engine, "search_sources", staticmethod(lambda *args, **kwargs: []))
    _FakeDownloader.script = {}
    _FakeDownloader.last_instance = None
    _FakeDownloader.batches = []
    songs = [_fake_song("One", 1), _fake_song("Two", 2)]
    instance._remember_source("job", "My Mix", songs)
    library = tmp_path / "library"
    library.mkdir()
    existing = library / "One.mp3"
    existing.write_bytes(b"audio")
    return {
        "engine": instance,
        "events": events,
        "out": str(tmp_path / "out"),
        "tmp": tmp_path,
        "existing": existing,
    }


def _completion(events: list[dict[str, Any]]) -> dict[str, Any]:
    completions = [event for event in events if event["type"] == "job_completed"]
    assert completions, [event["type"] for event in events]
    return completions[-1]


def _record(events: list[dict[str, Any]], track_id: str) -> dict[str, Any]:
    by_id = {item["track_id"]: item for item in _completion(events)["results"]}
    return by_id[track_id]


def test_download_policy_ignores_existing_files(policy_env: dict[str, Any]) -> None:
    engine: Engine = policy_env["engine"]
    _FakeDownloader.script = {
        "id-1": [("/out/one.mp3", None)],
        "id-2": [("/out/two.mp3", None)],
    }

    engine.download(
        "job",
        policy_env["out"],
        existing_files={"id-1": str(policy_env["existing"])},
    )

    assert _FakeDownloader.last_instance is not None
    assert set(_FakeDownloader.last_instance.attempts) == {"id-1", "id-2"}


def test_skip_policy_reports_the_existing_file_without_downloading(
    policy_env: dict[str, Any],
) -> None:
    engine: Engine = policy_env["engine"]
    _FakeDownloader.script = {"id-2": [("/out/two.mp3", None)]}

    engine.download(
        "job",
        policy_env["out"],
        duplicate_policy="skip",
        existing_files={"id-1": str(policy_env["existing"])},
    )

    reused = _record(policy_env["events"], "id-1")
    assert reused["success"] is True
    assert reused["path"] == str(policy_env["existing"])
    assert reused["reused"] == "already downloaded"
    assert _FakeDownloader.last_instance is not None
    assert "id-1" not in _FakeDownloader.last_instance.attempts
    assert _record(policy_env["events"], "id-2")["success"] is True


def test_copy_policy_places_the_file_in_this_job_output(policy_env: dict[str, Any]) -> None:
    engine: Engine = policy_env["engine"]
    _FakeDownloader.script = {"id-2": [("/out/two.mp3", None)]}

    engine.download(
        "job",
        policy_env["out"],
        duplicate_policy="copy",
        existing_files={"id-1": str(policy_env["existing"])},
    )

    reused = _record(policy_env["events"], "id-1")
    copied = Path(reused["path"])
    assert copied.is_file()
    assert copied.read_bytes() == b"audio"
    assert copied.parent == policy_env["tmp"] / "out" / "My Mix"
    assert reused["reused"] == "copied from library"


def test_hardlink_policy_shares_one_file_on_the_same_volume(
    policy_env: dict[str, Any],
) -> None:
    engine: Engine = policy_env["engine"]
    _FakeDownloader.script = {"id-2": [("/out/two.mp3", None)]}

    engine.download(
        "job",
        policy_env["out"],
        duplicate_policy="hardlink",
        existing_files={"id-1": str(policy_env["existing"])},
    )

    reused = _record(policy_env["events"], "id-1")
    linked = Path(reused["path"])
    assert linked.is_file()
    # tmp_path keeps both files on one volume, so a real hard link must be created.
    assert reused["reused"] == "hard-linked from library"
    assert linked.stat().st_ino == policy_env["existing"].stat().st_ino
    assert linked.stat().st_nlink >= 2


def test_existing_files_are_matched_by_spotify_url_too(policy_env: dict[str, Any]) -> None:
    engine: Engine = policy_env["engine"]
    _FakeDownloader.script = {"id-2": [("/out/two.mp3", None)]}

    engine.download(
        "job",
        policy_env["out"],
        duplicate_policy="skip",
        existing_files={"https://open.spotify.com/track/id-1": str(policy_env["existing"])},
    )

    assert _record(policy_env["events"], "id-1")["success"] is True


def test_missing_or_unreadable_source_falls_back_to_downloading(
    policy_env: dict[str, Any],
) -> None:
    engine: Engine = policy_env["engine"]
    _FakeDownloader.script = {
        "id-1": [("/out/one.mp3", None)],
        "id-2": [("/out/two.mp3", None)],
    }

    engine.download(
        "job",
        policy_env["out"],
        duplicate_policy="copy",
        existing_files={"id-1": str(policy_env["tmp"] / "gone.mp3")},
    )

    assert _FakeDownloader.last_instance is not None
    assert "id-1" in _FakeDownloader.last_instance.attempts
    assert _record(policy_env["events"], "id-1")["success"] is True


def test_reused_tracks_are_listed_in_the_playlist_file(policy_env: dict[str, Any]) -> None:
    engine: Engine = policy_env["engine"]
    output = policy_env["tmp"] / "out" / "My Mix"
    output.mkdir(parents=True)
    second = output / "02 - Artist - Two.mp3"
    second.write_bytes(b"")
    _FakeDownloader.script = {"id-2": [(str(second), None)]}

    engine.download(
        "job",
        policy_env["out"],
        duplicate_policy="copy",
        existing_files={"id-1": str(policy_env["existing"])},
        write_m3u=True,
    )

    entries = [
        line
        for line in (output / "My Mix.m3u8").read_text(encoding="utf-8").splitlines()
        if line and not line.startswith("#")
    ]
    assert entries == ["01 - Artist - One.mp3", "02 - Artist - Two.mp3"]


def test_unknown_duplicate_policy_is_rejected(policy_env: dict[str, Any]) -> None:
    engine: Engine = policy_env["engine"]

    with pytest.raises(ValueError, match="Unsupported duplicate policy"):
        engine.download("job", policy_env["out"], duplicate_policy="move")


def test_reused_tracks_count_towards_job_progress(policy_env: dict[str, Any]) -> None:
    engine: Engine = policy_env["engine"]
    _FakeDownloader.script = {"id-2": [("/out/two.mp3", None)]}

    engine.download(
        "job",
        policy_env["out"],
        duplicate_policy="skip",
        existing_files={"id-1": str(policy_env["existing"])},
    )

    progress = [event for event in policy_env["events"] if event["type"] == "job_progress"]
    assert progress[-1]["processed"] == 2
    assert progress[-1]["total"] == 2
    assert progress[-1]["succeeded"] == 2
    assert progress[-1]["failed"] == 0


def test_reused_tracks_report_completed_progress_to_the_ui(policy_env: dict[str, Any]) -> None:
    engine: Engine = policy_env["engine"]
    _FakeDownloader.script = {"id-2": [("/out/two.mp3", None)]}

    engine.download(
        "job",
        policy_env["out"],
        duplicate_policy="copy",
        existing_files={"id-1": str(policy_env["existing"])},
    )

    reuse_events = [
        event
        for event in policy_env["events"]
        if event["type"] == "track_progress" and event["track_id"] == "id-1"
    ]
    assert reuse_events
    assert reuse_events[-1]["progress"] == 100
    assert reuse_events[-1]["status"] == "Copied from library"


def test_reuse_requires_the_requested_audio_format(policy_env: dict[str, Any]) -> None:
    engine: Engine = policy_env["engine"]
    _FakeDownloader.script = {
        "id-1": [("/out/one.flac", None)],
        "id-2": [("/out/two.flac", None)],
    }

    # The library file is an MP3; this job asks for FLAC.
    engine.download(
        "job",
        policy_env["out"],
        audio_format="flac",
        duplicate_policy="copy",
        existing_files={"id-1": str(policy_env["existing"])},
    )

    assert _FakeDownloader.last_instance is not None
    assert "id-1" in _FakeDownloader.last_instance.attempts
    assert "reused" not in _record(policy_env["events"], "id-1")


def test_reuse_stops_when_the_job_is_cancelled(policy_env: dict[str, Any]) -> None:
    engine: Engine = policy_env["engine"]
    _FakeDownloader.script = {"id-2": [("/out/two.mp3", None)]}
    engine.cancel()

    engine.download(
        "job",
        policy_env["out"],
        duplicate_policy="copy",
        existing_files={"id-1": str(policy_env["existing"])},
    )

    copied = list((policy_env["tmp"] / "out").rglob("*.mp3"))
    assert copied == []
    assert any(event["type"] == "job_cancelled" for event in policy_env["events"])


def test_a_failed_copy_leaves_no_partial_file(
    policy_env: dict[str, Any], monkeypatch: pytest.MonkeyPatch
) -> None:
    engine: Engine = policy_env["engine"]
    _FakeDownloader.script = {
        "id-1": [("/out/one.mp3", None)],
        "id-2": [("/out/two.mp3", None)],
    }

    def failing_copy(source: str, target: str) -> None:
        Path(target).write_bytes(b"half")
        raise OSError("disk full")

    monkeypatch.setattr(engine_module.shutil, "copy2", failing_copy)

    engine.download(
        "job",
        policy_env["out"],
        duplicate_policy="copy",
        existing_files={"id-1": str(policy_env["existing"])},
    )

    assert list((policy_env["tmp"] / "out").rglob("*.partial")) == []
    # The track falls back to a normal download instead of being reported as reused.
    assert _FakeDownloader.last_instance is not None
    assert "id-1" in _FakeDownloader.last_instance.attempts


def test_hardlink_falls_back_to_a_copy_across_volumes(
    policy_env: dict[str, Any], monkeypatch: pytest.MonkeyPatch
) -> None:
    engine: Engine = policy_env["engine"]
    _FakeDownloader.script = {"id-2": [("/out/two.mp3", None)]}

    def cross_volume_link(source: str, target: str) -> None:
        raise OSError("cross-device link")

    monkeypatch.setattr(engine_module.os, "link", cross_volume_link)

    engine.download(
        "job",
        policy_env["out"],
        duplicate_policy="hardlink",
        existing_files={"id-1": str(policy_env["existing"])},
    )

    reused = _record(policy_env["events"], "id-1")
    assert reused["reused"] == "copied from library"
    assert Path(reused["path"]).read_bytes() == b"audio"
