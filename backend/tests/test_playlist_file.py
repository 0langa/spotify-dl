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
