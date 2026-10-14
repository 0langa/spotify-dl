"""A saved path is not proof of a usable download; every result is checked."""

from __future__ import annotations

import shutil
import subprocess
from pathlib import Path
from typing import Any

import pytest
from test_engine_reliability import _fake_song, _FakeDownloader, _FakeProgressHandler

from playlistdl_backend import engine as engine_module
from playlistdl_backend.engine import Engine, check_download_integrity


def _probe(readable: bool = True, duration: float | None = 200.0, detail: str = "ok"):
    return lambda path: (readable, duration, detail)


@pytest.fixture
def integrity_env(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> dict[str, Any]:
    events: list[dict[str, Any]] = []
    instance = Engine(emit=events.append)
    monkeypatch.setattr(engine_module, "Downloader", _FakeDownloader)
    monkeypatch.setattr(engine_module, "ProgressHandler", _FakeProgressHandler)
    monkeypatch.setattr(Engine, "search_sources", staticmethod(lambda *args, **kwargs: []))
    _FakeDownloader.script = {}
    _FakeDownloader.last_instance = None
    _FakeDownloader.batches = []
    instance._media_probe = _probe()
    saved = tmp_path / "01 - Artist - One.mp3"
    saved.write_bytes(b"audio-bytes")
    instance._remember_source("job", "My Mix", [_fake_song("One", 1)])
    return {"engine": instance, "events": events, "out": str(tmp_path), "saved": saved}


def _record(events: list[dict[str, Any]]) -> dict[str, Any]:
    completions = [event for event in events if event["type"] == "job_completed"]
    assert completions, [event["type"] for event in events]
    return completions[-1]["results"][0]


def test_a_good_download_is_reported_verified(integrity_env: dict[str, Any]) -> None:
    engine: Engine = integrity_env["engine"]
    _FakeDownloader.script = {"id-1": [(str(integrity_env["saved"]), None)]}

    engine.download("job", integrity_env["out"])

    record = _record(integrity_env["events"])
    assert record["success"] is True
    assert record["verified"] is True


def test_a_missing_file_is_not_reported_as_done(integrity_env: dict[str, Any]) -> None:
    engine: Engine = integrity_env["engine"]
    _FakeDownloader.script = {"id-1": [("/out/never-written.mp3", None)]}

    engine.download("job", integrity_env["out"])

    record = _record(integrity_env["events"])
    assert record["success"] is False
    assert record["error_class"] == "integrity_failed"
    assert "not saved" in record["error"]


def test_an_empty_file_is_rejected(integrity_env: dict[str, Any], tmp_path: Path) -> None:
    engine: Engine = integrity_env["engine"]
    empty = tmp_path / "empty.mp3"
    empty.write_bytes(b"")
    _FakeDownloader.script = {"id-1": [(str(empty), None)]}

    engine.download("job", integrity_env["out"])

    record = _record(integrity_env["events"])
    assert record["success"] is False
    assert "empty" in record["error"]


def test_a_file_that_does_not_decode_is_rejected(integrity_env: dict[str, Any]) -> None:
    engine: Engine = integrity_env["engine"]
    engine._media_probe = _probe(readable=False, duration=None, detail="moov atom not found")
    _FakeDownloader.script = {"id-1": [(str(integrity_env["saved"]), None)]}

    engine.download("job", integrity_env["out"])

    record = _record(integrity_env["events"])
    assert record["success"] is False
    assert "moov atom not found" in record["error"]


def test_a_clearly_truncated_download_is_rejected(integrity_env: dict[str, Any]) -> None:
    engine: Engine = integrity_env["engine"]
    # The source track is 200 s; the saved file holds 12 s.
    engine._media_probe = _probe(duration=12.0)
    _FakeDownloader.script = {"id-1": [(str(integrity_env["saved"]), None)]}

    engine.download("job", integrity_env["out"])

    record = _record(integrity_env["events"])
    assert record["success"] is False
    assert record["error_class"] == "integrity_failed"
    assert "0:12" in record["error"] and "3:20" in record["error"]


def test_small_length_differences_are_accepted(integrity_env: dict[str, Any]) -> None:
    engine: Engine = integrity_env["engine"]
    # A different master or an added outro is normal; 200 s vs 208 s must pass.
    engine._media_probe = _probe(duration=208.0)
    _FakeDownloader.script = {"id-1": [(str(integrity_env["saved"]), None)]}

    engine.download("job", integrity_env["out"])

    assert _record(integrity_env["events"])["success"] is True


def test_verification_can_be_turned_off(integrity_env: dict[str, Any]) -> None:
    engine: Engine = integrity_env["engine"]
    _FakeDownloader.script = {"id-1": [("/out/never-written.mp3", None)]}

    engine.download("job", integrity_env["out"], verify_downloads=False)

    record = _record(integrity_env["events"])
    assert record["success"] is True
    assert record["verified"] is None


def test_a_rejected_download_can_still_recover_from_another_source(
    integrity_env: dict[str, Any], monkeypatch: pytest.MonkeyPatch
) -> None:
    engine: Engine = integrity_env["engine"]
    monkeypatch.setattr(
        Engine,
        "search_sources",
        staticmethod(
            lambda *args, **kwargs: [
                {
                    "url": "https://music.youtube.com/watch?v=alt",
                    "title": "One",
                    "artists": ["Artist"],
                    "album": "Album",
                    "duration_seconds": 200,
                    "result_type": "song",
                }
            ]
        ),
    )
    # The first upload saves a stub; the alternate saves the real file.
    truncated = Path(integrity_env["out"]) / "truncated.mp3"
    truncated.write_bytes(b"x")
    durations = iter([5.0, 200.0])
    engine._media_probe = lambda path: (True, next(durations, 200.0), "ok")
    _FakeDownloader.script = {"id-1": [(str(truncated), None), (str(integrity_env["saved"]), None)]}

    engine.download("job", integrity_env["out"], retries=0)

    record = _record(integrity_env["events"])
    assert record["success"] is True
    assert record["fallback_used"] is True


@pytest.mark.parametrize(
    ("expected", "actual", "accepted"),
    [
        (0, 999.0, True),
        (200, 200.0, True),
        (200, 190.0, True),
        (200, 170.0, False),
        (30, 41.0, False),
        (30, 39.0, True),
    ],
)
def test_duration_tolerance(tmp_path: Path, expected: int, actual: float, accepted: bool) -> None:
    saved = tmp_path / "track.mp3"
    saved.write_bytes(b"audio")

    ok, _ = check_download_integrity(str(saved), expected, _probe(duration=actual))

    assert ok is accepted


def test_an_unavailable_probe_does_not_fail_the_download(tmp_path: Path) -> None:
    saved = tmp_path / "track.mp3"
    saved.write_bytes(b"audio")

    ok, _ = check_download_integrity(
        str(saved), 200, lambda path: (True, None, "probe unavailable")
    )

    assert ok is True


def test_a_rejected_file_is_removed_so_a_retry_can_download_again(
    integrity_env: dict[str, Any], tmp_path: Path
) -> None:
    """spotDL skips a download whose output file already exists."""
    engine: Engine = integrity_env["engine"]
    bad = tmp_path / "truncated.mp3"
    bad.write_bytes(b"x")
    engine._media_probe = _probe(duration=5.0)
    _FakeDownloader.script = {"id-1": [(str(bad), None)]}

    engine.download("job", integrity_env["out"], retries=0)

    assert not bad.exists()


def test_a_hand_picked_source_is_not_judged_by_the_spotify_length(
    integrity_env: dict[str, Any],
) -> None:
    engine: Engine = integrity_env["engine"]
    # A deliberate live version runs far longer than the studio track.
    engine._media_probe = _probe(duration=900.0)
    _FakeDownloader.script = {"id-1": [(str(integrity_env["saved"]), None)]}

    engine.download(
        "job",
        integrity_env["out"],
        source_overrides={"id-1": "https://www.youtube.com/watch?v=live"},
    )

    assert _record(integrity_env["events"])["success"] is True


def test_a_probe_that_says_nothing_does_not_reject_the_download(
    integrity_env: dict[str, Any],
) -> None:
    engine: Engine = integrity_env["engine"]
    engine._media_probe = lambda path: (True, None, "probe returned nothing")
    _FakeDownloader.script = {"id-1": [(str(integrity_env["saved"]), None)]}

    engine.download("job", integrity_env["out"])

    assert _record(integrity_env["events"])["success"] is True


def test_an_integrity_verdict_survives_provider_error_attribution(
    integrity_env: dict[str, Any],
) -> None:
    engine: Engine = integrity_env["engine"]
    engine._media_probe = _probe(duration=3.0)
    # The downloader also reports an unrelated provider error in the same batch.
    _FakeDownloader.script = {"id-1": [(str(integrity_env["saved"]), "Sign in to confirm")]}

    engine.download("job", integrity_env["out"], retries=0)

    record = _record(integrity_env["events"])
    assert record["error_class"] == "integrity_failed"
    assert "integrity check failed" in record["error"]


class _DuplicateAwareDownloader(_FakeDownloader):
    """spotDL's skip for a track it already found under another name in the output folder.

    spotDL then reports the path it would have written, which does not exist, instead of
    the file it found; only once that file is no longer known does it download again.
    """

    def __init__(self, settings: dict[str, Any]) -> None:
        super().__init__(settings)
        self.known_songs: dict[str, list[Path]] = {}
        self.output = Path(settings["output"]).parent

    def download_multiple_songs(self, batch: list[Any]) -> list[tuple[Any, Any]]:
        results = []
        for song in batch:
            target = self.output / f"{song.list_position:02d} - Artist - {song.name}.mp3"
            if any(path.exists() for path in self.known_songs.get(song.url, [])):
                results.append((song, target))
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(b"fresh-audio")
            results.append((song, target))
        return results


def _install_duplicate(
    monkeypatch: pytest.MonkeyPatch, url: str, existing: Path
) -> list[_DuplicateAwareDownloader]:
    created: list[_DuplicateAwareDownloader] = []

    def build(settings: dict[str, Any]) -> _DuplicateAwareDownloader:
        downloader = _DuplicateAwareDownloader(settings)
        downloader.known_songs = {url: [existing]}
        created.append(downloader)
        return downloader

    monkeypatch.setattr(engine_module, "Downloader", build)
    return created


def test_a_track_already_saved_under_another_name_reports_that_file(
    integrity_env: dict[str, Any], monkeypatch: pytest.MonkeyPatch
) -> None:
    """A renamed file is found by its tags; it must not read as a failed download."""
    engine: Engine = integrity_env["engine"]
    renamed = Path(integrity_env["out"]) / "My Mix" / "renamed by the user.mp3"
    renamed.parent.mkdir(parents=True)
    renamed.write_bytes(b"audio-bytes")
    _install_duplicate(monkeypatch, "https://open.spotify.com/track/id-1", renamed)
    searches: list[Any] = []
    monkeypatch.setattr(
        Engine, "search_sources", staticmethod(lambda *args, **kwargs: searches.append(args) or [])
    )

    engine.download("job", integrity_env["out"])

    record = _record(integrity_env["events"])
    assert record["success"] is True
    assert record["verified"] is True
    assert record["path"] == str(renamed)
    assert searches == []


def test_an_unusable_file_found_under_another_name_is_kept_and_downloaded_again(
    integrity_env: dict[str, Any], monkeypatch: pytest.MonkeyPatch
) -> None:
    engine: Engine = integrity_env["engine"]
    stub = Path(integrity_env["out"]) / "My Mix" / "old stub.mp3"
    stub.parent.mkdir(parents=True)
    stub.write_bytes(b"x")
    created = _install_duplicate(monkeypatch, "https://open.spotify.com/track/id-1", stub)
    engine._media_probe = lambda path: (True, 5.0 if Path(path) == stub else 200.0, "ok")
    monkeypatch.setattr(
        Engine,
        "search_sources",
        staticmethod(
            lambda *args, **kwargs: [
                {
                    "url": "https://music.youtube.com/watch?v=alt",
                    "title": "One",
                    "artists": ["Artist"],
                    "album": "Album",
                    "duration_seconds": 200,
                    "result_type": "song",
                }
            ]
        ),
    )

    engine.download("job", integrity_env["out"], retries=0)

    record = _record(integrity_env["events"])
    assert record["success"] is True
    assert record["path"] != str(stub)
    # The user's own copy is never deleted, only no longer offered to the downloader.
    assert stub.read_bytes() == b"x"
    assert created[0].known_songs == {}


def _ffprobe_answer(
    monkeypatch: pytest.MonkeyPatch, returncode: int = 0, stdout: str = "", stderr: str = ""
) -> list[list[str]]:
    calls: list[list[str]] = []

    def run(command: list[str], **kwargs: Any) -> subprocess.CompletedProcess[str]:
        calls.append(command)
        return subprocess.CompletedProcess(command, returncode, stdout, stderr)

    monkeypatch.setattr(subprocess, "run", run)
    return calls


@pytest.mark.parametrize(
    ("returncode", "stdout", "stderr", "expected"),
    [
        (
            0,
            '{"streams": [{"codec_type": "audio"}], "format": {"duration": "201.5"}}',
            "",
            (True, 201.5, "ok"),
        ),
        # Cover art is a video stream next to the audio, not instead of it.
        (
            0,
            '{"streams": [{"codec_type": "video"}, {"codec_type": "audio"}], "format": {}}',
            "",
            (True, None, "ok"),
        ),
        (
            0,
            '{"streams": [{"codec_type": "video"}]}',
            "",
            (False, None, "the file holds no audio stream"),
        ),
        (
            1,
            "",
            "Invalid data found when processing input\n",
            (False, None, "Invalid data found when processing input"),
        ),
        (1, "", "", (False, None, "the file could not be read")),
        # A probe that says nothing, or nothing readable, is no verdict on the file.
        (0, "  ", "", (True, None, "probe returned nothing")),
        (0, "not json", "", (True, None, "probe output could not be read")),
    ],
)
def test_probe_media_verdicts(
    monkeypatch: pytest.MonkeyPatch,
    returncode: int,
    stdout: str,
    stderr: str,
    expected: tuple[bool, float | None, str],
) -> None:
    _ffprobe_answer(monkeypatch, returncode, stdout, stderr)

    assert engine_module.probe_media("track.mp3") == expected


def test_a_missing_probe_skips_verification(monkeypatch: pytest.MonkeyPatch) -> None:
    def run(command: list[str], **kwargs: Any) -> None:
        raise FileNotFoundError(2, "The system cannot find the file specified")

    monkeypatch.setattr(subprocess, "run", run)

    readable, duration, detail = engine_module.probe_media("track.mp3")

    assert (readable, duration) == (True, None)
    assert detail.startswith("probe unavailable")


def test_the_probe_next_to_the_bundled_ffmpeg_is_preferred(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    ffmpeg = tmp_path / "ffmpeg.exe"
    ffprobe = tmp_path / "ffprobe.exe"
    ffmpeg.write_bytes(b"")
    ffprobe.write_bytes(b"")
    monkeypatch.setenv("PLAYLISTDL_FFMPEG", str(ffmpeg))
    calls = _ffprobe_answer(monkeypatch, stdout='{"streams": [{"codec_type": "audio"}]}')

    engine_module.probe_media("track.mp3")

    assert calls[0][0] == str(ffprobe)
    assert calls[0][-1] == "track.mp3"


@pytest.mark.skipif(
    shutil.which("ffmpeg") is None or shutil.which("ffprobe") is None,
    reason="needs FFmpeg on PATH",
)
def test_the_real_probe_reads_a_file_with_a_non_latin_name(tmp_path: Path) -> None:
    track = tmp_path / "Björk – 日本語 ✓.mp3"
    subprocess.run(
        [
            "ffmpeg",
            "-v",
            "error",
            "-f",
            "lavfi",
            "-i",
            "anullsrc=r=44100:cl=mono",
            "-t",
            "2",
            "-q:a",
            "9",
            str(track),
        ],
        check=True,
        timeout=60,
    )
    not_audio = tmp_path / "Björk – notes.mp3"
    not_audio.write_text("not audio", encoding="utf-8")

    readable, duration, detail = engine_module.probe_media(str(track))

    assert (readable, detail) == (True, "ok")
    assert duration is not None and abs(duration - 2.0) < 0.5
    assert engine_module.probe_media(str(not_audio))[0] is False
