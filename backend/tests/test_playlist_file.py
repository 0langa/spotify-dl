from __future__ import annotations

from pathlib import Path

import pytest

from playlistdl_backend.playlist_file import sanitize_filename, write_m3u8


@pytest.mark.parametrize(
    ("raw", "expected"),
    [
        ("My Mix", "My Mix"),
        ('Road <Trip>: "Best" / Worst?', "Road _Trip__ _Best_ _ Worst_"),
        ("...   ", "playlist"),
        ("", "playlist"),
        ("con\x00trol", "con_trol"),
    ],
)
def test_sanitize_filename(raw: str, expected: str) -> None:
    assert sanitize_filename(raw) == expected


def test_write_m3u8_uses_relative_paths_inside_output(tmp_path: Path) -> None:
    first = tmp_path / "01 - A - One.mp3"
    second = tmp_path / "02 - B - Two.mp3"
    outside = tmp_path.parent / "elsewhere.mp3"

    target = write_m3u8(tmp_path, "My Mix", [str(first), str(second), str(outside)])

    assert target == tmp_path / "My Mix.m3u8"
    lines = target.read_text(encoding="utf-8").splitlines()
    assert lines[0] == "#EXTM3U"
    assert lines[1] == "01 - A - One.mp3"
    assert lines[2] == "02 - B - Two.mp3"
    assert lines[3] == str(outside)


def test_write_m3u8_sanitizes_collection_name(tmp_path: Path) -> None:
    target = write_m3u8(tmp_path, 'Bad/Name:"Mix"', [str(tmp_path / "a.mp3")])

    assert target.name == "Bad_Name__Mix_.m3u8"
    assert target.exists()


def test_sanitize_filename_clamps_long_collection_names() -> None:
    sanitized = sanitize_filename("A" * 400)

    assert len(sanitized) == 120
    assert set(sanitized) == {"A"}


@pytest.mark.parametrize("raw", ["CON", "nul", "LPT1", "com9.mp3"])
def test_sanitize_filename_escapes_reserved_windows_names(raw: str) -> None:
    assert sanitize_filename(raw).startswith("_")


@pytest.mark.parametrize("raw", ["CONIN$", "conout$.txt", "COM\u00b9", "lpt\u00b3.mp3", "nul .mp3"])
def test_sanitize_filename_escapes_every_windows_device_name(raw: str) -> None:
    # Windows 10 opens the device for all of these, including a name padded before its
    # extension, so the collection folder and playlist file would point at a device.
    assert sanitize_filename(raw).startswith("_")


@pytest.mark.parametrize("raw", ["Console", "Auxiliary", "COM10", "nul-mix", "Lpt"])
def test_sanitize_filename_keeps_names_that_only_resemble_devices(raw: str) -> None:
    assert sanitize_filename(raw) == raw
