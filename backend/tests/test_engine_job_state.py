"""Job-level state guarantees: track identity, cancellation, retention, playlist files."""

from __future__ import annotations

from pathlib import Path
from typing import Any

import pytest
from spotdl.types.song import Song
from test_engine_reliability import _fake_song, _FakeDownloader, _FakeProgressHandler

from playlistdl_backend import engine as engine_module
from playlistdl_backend.engine import Engine, assign_track_keys, track_key


@pytest.fixture
def job_env(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> dict[str, Any]:
    events: list[dict[str, Any]] = []
    instance = Engine(emit=events.append)
    monkeypatch.setattr(engine_module, "Downloader", _FakeDownloader)
    monkeypatch.setattr(engine_module, "ProgressHandler", _FakeProgressHandler)
    monkeypatch.setattr(engine_module, "_RETRY_BACKOFF_SECONDS", 0.0)
    monkeypatch.setattr(Engine, "search_sources", staticmethod(lambda *args, **kwargs: []))
    _FakeDownloader.script = {}
    _FakeDownloader.last_instance = None
    _FakeDownloader.batches = []
    # These fixtures script fake output paths, so the saved-file check is exercised
    # by the dedicated integrity tests instead.
    instance._verify_downloads = False
    return {"engine": instance, "events": events, "out": str(tmp_path), "tmp": tmp_path}


def _completion(events: list[dict[str, Any]]) -> dict[str, Any]:
    completions = [event for event in events if event["type"] == "job_completed"]
    assert completions, [event["type"] for event in events]
    return completions[-1]


def test_repeated_track_keeps_its_own_result_and_file(job_env: dict[str, Any]) -> None:
    engine: Engine = job_env["engine"]
    first = _fake_song("Same Song", 1)
    duplicate = _fake_song("Same Song", 1)
    duplicate.list_position = 2
    songs = [first, duplicate]
    engine._remember_source("job", "My Mix", songs)

    assert track_key(first) == "id-1"
    assert track_key(duplicate) == "id-1#2"

    _FakeDownloader.script = {"id-1": [("/out/one.mp3", None)]}
    engine.download("job", job_env["out"])

    results = {record["track_id"] for record in _completion(job_env["events"])["results"]}
    assert results == {"id-1", "id-1#2"}


def test_repeated_track_is_selectable_independently(job_env: dict[str, Any]) -> None:
    engine: Engine = job_env["engine"]
    songs = [_fake_song("Same Song", 1), _fake_song("Same Song", 1)]
    engine._remember_source("job", "My Mix", songs)
    _FakeDownloader.script = {"id-1": [("/out/one.mp3", None)]}

    engine.download("job", job_env["out"], track_ids=["id-1#2"])

    results = _completion(job_env["events"])["results"]
    assert [record["track_id"] for record in results] == ["id-1#2"]


def test_cancel_before_the_worker_starts_is_not_cleared(job_env: dict[str, Any]) -> None:
    engine: Engine = job_env["engine"]
    engine._remember_source("job", "My Mix", [_fake_song("One", 1)])
    _FakeDownloader.script = {"id-1": [("/out/one.mp3", None)]}

    # A cancel that arrives between `start` and the worker's first window used to be
    # erased by the worker itself.
    engine.cancel()
    engine.download("job", job_env["out"])

    types = [event["type"] for event in job_env["events"]]
    assert "job_cancelled" in types
    assert "job_completed" not in types


def test_begin_job_arms_a_new_job_after_a_cancelled_one(job_env: dict[str, Any]) -> None:
    engine: Engine = job_env["engine"]
    engine._remember_source("job", "My Mix", [_fake_song("One", 1)])
    _FakeDownloader.script = {"id-1": [("/out/one.mp3", None)]}

    engine.cancel()
    engine.begin_job()
    engine.download("job", job_env["out"])

    assert _completion(job_env["events"])["results"][0]["success"] is True


def test_resolved_sources_are_bounded_and_active_work_survives(job_env: dict[str, Any]) -> None:
    engine: Engine = job_env["engine"]
    limit = engine_module._MAX_RETAINED_SOURCES
    engine._remember_source("first", "First", [_fake_song("One", 1)])
    for index in range(limit - 1):
        engine._remember_source(f"filler-{index}", f"Filler {index}", [_fake_song("Song", 1)])

    engine._touch_source("first")
    engine._remember_source("newest", "Newest", [_fake_song("New", 1)])

    assert len(engine._songs) == limit
    assert "first" in engine._songs
    assert "newest" in engine._songs
    assert engine._names["first"] == "First"


def test_playlist_file_keeps_tracks_from_earlier_runs(job_env: dict[str, Any]) -> None:
    engine: Engine = job_env["engine"]
    output: Path = job_env["tmp"] / "My Mix"
    songs = [_fake_song(f"Song {index}", index) for index in range(1, 4)]
    engine._remember_source("job", "My Mix", songs)

    # First run saves tracks 1 and 3; track 2 fails.
    _FakeDownloader.script = {
        "id-1": [(str(output / "01 - Artist - Song 1.mp3"), None)],
        "id-2": [(None, "No results found for song")],
        "id-3": [(str(output / "03 - Artist - Song 3.mp3"), None)],
    }
    for song in songs:
        (output).mkdir(parents=True, exist_ok=True)
        Path(str(output / f"{song.list_position:02d} - Artist - {song.name}.mp3")).write_bytes(b"")
    engine.download("job", job_env["out"], write_m3u=True)

    playlist_file = output / "My Mix.m3u8"
    assert playlist_file.exists()

    # A retry of the single failed track must not shrink the playlist file.
    job_env["events"].clear()
    _FakeDownloader.script = {"id-2": [(str(output / "02 - Artist - Song 2.mp3"), None)]}
    engine.begin_job()
    engine.download("job", job_env["out"], track_ids=["id-2"], write_m3u=True)

    entries = [
        line
        for line in playlist_file.read_text(encoding="utf-8").splitlines()
        if line and not line.startswith("#")
    ]
    assert entries == [
        "01 - Artist - Song 1.mp3",
        "02 - Artist - Song 2.mp3",
        "03 - Artist - Song 3.mp3",
    ]


def test_playlist_file_drops_entries_whose_file_disappeared(job_env: dict[str, Any]) -> None:
    engine: Engine = job_env["engine"]
    output: Path = job_env["tmp"] / "My Mix"
    output.mkdir(parents=True, exist_ok=True)
    (output / "My Mix.m3u8").write_text("#EXTM3U\ndeleted.mp3\n", encoding="utf-8")
    saved = output / "01 - Artist - One.mp3"
    saved.write_bytes(b"")
    engine._remember_source("job", "My Mix", [_fake_song("One", 1)])
    _FakeDownloader.script = {"id-1": [(str(saved), None)]}

    engine.download("job", job_env["out"], write_m3u=True)

    entries = [
        line
        for line in (output / "My Mix.m3u8").read_text(encoding="utf-8").splitlines()
        if line and not line.startswith("#")
    ]
    assert entries == ["01 - Artist - One.mp3"]


def test_assign_track_keys_falls_back_when_ids_are_missing() -> None:
    songs = [_fake_song("One", 1), _fake_song("Two", 2)]
    for song in songs:
        song.song_id = ""
        song.url = ""
    assign_track_keys(songs)

    assert [track_key(song) for song in songs] == ["track-1", "track-2"]


def test_track_key_survives_a_deep_copied_song() -> None:
    import copy

    song: Song = _fake_song("One", 1)
    assign_track_keys([song, _fake_song("One", 1)])
    clone = copy.deepcopy(song)

    assert track_key(clone) == track_key(song)


def test_provider_block_stops_retries_and_alternate_source_storms(
    job_env: dict[str, Any], monkeypatch: pytest.MonkeyPatch
) -> None:
    engine: Engine = job_env["engine"]
    searches: list[str] = []
    monkeypatch.setattr(
        Engine,
        "search_sources",
        staticmethod(lambda title, *args, **kwargs: searches.append(title) or []),
    )
    # Four windows of eight at threads=2.
    songs = [_fake_song(f"Song {index}", index) for index in range(1, 33)]
    engine._remember_source("job", "My Mix", songs)
    _FakeDownloader.script = {
        song.song_id: [(None, "Sign in to confirm you're not a bot")] for song in songs
    }

    engine.download("job", job_env["out"], threads=2, retries=1)

    completion = _completion(job_env["events"])
    assert completion["failure_class"] == "youtube_blocked"
    assert len(completion["results"]) == len(songs)
    warnings = [event for event in job_env["events"] if event["type"] == "job_warning"]
    assert len(warnings) == 1
    assert warnings[0]["failure_class"] == "youtube_blocked"
    downloader = _FakeDownloader.last_instance
    assert downloader is not None
    # First two windows still retry and search; the rest are attempted once, with no
    # alternate-source searches at all.
    assert downloader.attempts["id-1"] == 2
    assert downloader.attempts["id-32"] == 1
    assert len(searches) == 16
    assert "Song 32" not in searches


def test_a_success_prevents_the_provider_block_verdict(
    job_env: dict[str, Any], monkeypatch: pytest.MonkeyPatch
) -> None:
    engine: Engine = job_env["engine"]
    searches: list[str] = []
    monkeypatch.setattr(
        Engine,
        "search_sources",
        staticmethod(lambda title, *args, **kwargs: searches.append(title) or []),
    )
    # Two full windows at threads=2, each with one track that still downloads.
    songs = [_fake_song(f"Song {index}", index) for index in range(1, 17)]
    engine._remember_source("job", "My Mix", songs)
    _FakeDownloader.script = {
        song.song_id: (
            [("/out/ok.mp3", None)]
            if song.list_position in (8, 16)
            else [(None, "HTTP Error 429: Too Many Requests")]
        )
        for song in songs
    }

    engine.download("job", job_env["out"], threads=2, retries=0)

    completion = _completion(job_env["events"])
    assert sum(record["success"] for record in completion["results"]) == 2
    # No window was fully blocked, so recovery stays enabled for every failed track.
    assert not [event for event in job_env["events"] if event["type"] == "job_warning"]
    assert len(searches) == 14


def test_alternate_source_recovery_keeps_a_window_unblocked(
    job_env: dict[str, Any], monkeypatch: pytest.MonkeyPatch
) -> None:
    engine: Engine = job_env["engine"]
    songs = [_fake_song(f"Song {index}", index) for index in range(1, 13)]
    engine._remember_source("job", "My Mix", songs)
    monkeypatch.setattr(
        Engine,
        "search_sources",
        staticmethod(
            lambda *args, **kwargs: [
                {
                    "url": "https://music.youtube.com/watch?v=alt",
                    "title": kwargs.get("title") or args[0],
                    "artists": ["Artist"],
                    "album": "Album",
                    "duration_seconds": 200,
                    "result_type": "song",
                }
            ]
        ),
    )
    # Every primary attempt is blocked; the alternate source always works.
    _FakeDownloader.script = {
        song.song_id: [(None, "Sign in to confirm you're not a bot"), ("/out/alt.mp3", None)]
        for song in songs
    }

    engine.download("job", job_env["out"], threads=2, retries=0)

    completion = _completion(job_env["events"])
    assert all(record["success"] for record in completion["results"])
    assert not [event for event in job_env["events"] if event["type"] == "job_warning"]


def test_playlist_file_replaces_a_track_redownloaded_in_another_format(
    job_env: dict[str, Any],
) -> None:
    engine: Engine = job_env["engine"]
    output: Path = job_env["tmp"] / "My Mix"
    output.mkdir(parents=True, exist_ok=True)
    (output / "My Mix.m3u8").write_text(
        "#EXTM3U\n01 - Artist - One.mp3\n02 - Artist - Two.mp3\n", encoding="utf-8"
    )
    for name in ("01 - Artist - One.mp3", "02 - Artist - Two.mp3"):
        (output / name).write_bytes(b"")
    songs = [_fake_song("One", 1), _fake_song("Two", 2)]
    engine._remember_source("job", "My Mix", songs)
    flac = output / "01 - Artist - One.flac"
    flac.write_bytes(b"")
    _FakeDownloader.script = {"id-1": [(str(flac), None)]}

    engine.download("job", job_env["out"], track_ids=["id-1"], audio_format="flac", write_m3u=True)

    entries = [
        line
        for line in (output / "My Mix.m3u8").read_text(encoding="utf-8").splitlines()
        if line and not line.startswith("#")
    ]
    assert entries == ["01 - Artist - One.flac", "02 - Artist - Two.mp3"]


def test_playlist_file_replaces_a_track_redownloaded_under_another_naming_preset(
    job_env: dict[str, Any],
) -> None:
    engine: Engine = job_env["engine"]
    output: Path = job_env["tmp"] / "My Mix"
    output.mkdir(parents=True, exist_ok=True)
    (output / "My Mix.m3u8").write_text("#EXTM3U\n01 - Artist - One.mp3\n", encoding="utf-8")
    (output / "01 - Artist - One.mp3").write_bytes(b"")
    engine._remember_source("job", "My Mix", [_fake_song("One", 1)])
    renamed = output / "Artist - One.mp3"
    renamed.write_bytes(b"")
    _FakeDownloader.script = {"id-1": [(str(renamed), None)]}

    engine.download("job", job_env["out"], naming_preset="artist_title", write_m3u=True)

    entries = [
        line
        for line in (output / "My Mix.m3u8").read_text(encoding="utf-8").splitlines()
        if line and not line.startswith("#")
    ]
    assert entries == ["Artist - One.mp3"]


def test_an_unreadable_file_in_the_output_folder_does_not_abort_the_job(
    job_env: dict[str, Any], monkeypatch: pytest.MonkeyPatch
) -> None:
    """spotDL reads tags of everything already in the folder to find duplicates."""
    engine: Engine = job_env["engine"]
    engine._remember_source("job", "My Mix", [_fake_song("One", 1)])
    _FakeDownloader.script = {"id-1": [("/out/one.mp3", None)]}
    attempts: list[bool] = []

    class ScanningDownloader(_FakeDownloader):
        def __init__(self, settings: dict[str, Any]) -> None:
            scanning = bool(settings.get("scan_for_songs"))
            attempts.append(scanning)
            if scanning:
                raise Exception("can't sync to MPEG frame")
            super().__init__(settings)

    monkeypatch.setattr(engine_module, "Downloader", ScanningDownloader)

    engine.download("job", job_env["out"])

    assert attempts == [True, False]
    assert _completion(job_env["events"])["results"][0]["success"] is True


def test_a_downloader_failure_unrelated_to_the_scan_still_stops_the_job(
    job_env: dict[str, Any], monkeypatch: pytest.MonkeyPatch
) -> None:
    engine: Engine = job_env["engine"]
    engine._remember_source("job", "My Mix", [_fake_song("One", 1)])

    class BrokenDownloader(_FakeDownloader):
        def __init__(self, settings: dict[str, Any]) -> None:
            raise Exception("ffmpeg is not installed")

    monkeypatch.setattr(engine_module, "Downloader", BrokenDownloader)

    with pytest.raises(Exception, match="ffmpeg is not installed"):
        engine.download("job", job_env["out"])


def test_loudness_normalization_is_off_unless_requested(job_env: dict[str, Any]) -> None:
    engine: Engine = job_env["engine"]
    engine._remember_source("job", "My Mix", [_fake_song("One", 1)])
    _FakeDownloader.script = {"id-1": [("/out/one.mp3", None)]}

    engine.download("job", job_env["out"])

    assert _FakeDownloader.last_instance is not None
    assert _FakeDownloader.last_instance.settings["ffmpeg_args"] is None


def test_loudness_normalization_passes_the_r128_filter_to_the_converter(
    job_env: dict[str, Any],
) -> None:
    engine: Engine = job_env["engine"]
    engine._remember_source("job", "My Mix", [_fake_song("One", 1)])
    _FakeDownloader.script = {"id-1": [("/out/one.mp3", None)]}

    engine.download("job", job_env["out"], normalize_loudness=True)

    assert _FakeDownloader.last_instance is not None
    arguments = _FakeDownloader.last_instance.settings["ffmpeg_args"]
    assert "loudnorm" in arguments
    assert "I=-14" in arguments
