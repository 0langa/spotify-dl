from __future__ import annotations

import os
from pathlib import Path

import pytest

from playlistdl_backend import playlist_file
from playlistdl_backend.playlist_file import fit_windows_max_path, sanitize_filename, write_m3u8


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


@pytest.fixture
def without_long_paths(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(playlist_file, "long_paths_enabled", lambda: False)


def _units(path: Path) -> int:
    return len(os.path.abspath(path).encode("utf-16-le")) // 2


def test_long_path_support_is_detected() -> None:
    assert isinstance(playlist_file.long_paths_enabled(), bool)


@pytest.mark.usefixtures("without_long_paths")
def test_a_path_that_fits_is_left_alone(tmp_path: Path) -> None:
    path = tmp_path / "Band" / "01 - Song.mp3"

    assert fit_windows_max_path(path) == path
    assert fit_windows_max_path(path, " [abc]") == tmp_path / "Band" / "01 - Song [abc].mp3"


@pytest.mark.usefixtures("without_long_paths")
def test_a_too_long_name_is_shortened_until_it_can_be_written(tmp_path: Path) -> None:
    folder = tmp_path / "Band" / "Album"
    path = folder / f"01 - {'Symphony No. 9 in D minor, Op. 125 ' * 10}.flac"
    assert _units(path) > 259

    fitted = fit_windows_max_path(path)

    assert fitted.parent == folder
    assert fitted.suffix == ".flac"
    assert fitted.stem.startswith("01 - Symphony No. 9")
    assert not fitted.stem.endswith((" ", "."))
    # Room is left for the ".partial" copy the downloader stages next to a reused file.
    assert _units(fitted.with_name(fitted.name + ".partial")) <= 259
    folder.mkdir(parents=True)
    fitted.write_bytes(b"audio")
    assert fitted.is_file()


@pytest.mark.usefixtures("without_long_paths")
def test_shortening_keeps_the_collision_suffix_and_ignores_the_extension(tmp_path: Path) -> None:
    stem = "Concerto " * 40

    with_suffix = fit_windows_max_path(tmp_path / f"{stem}.mp3", " [id-12345]")

    assert with_suffix.name.endswith(" [id-12345].mp3")
    assert _units(with_suffix) <= 259
    # The same track gets the same stem in every format, so a file from an earlier run
    # under another format is still recognized.
    assert (
        fit_windows_max_path(tmp_path / f"{stem}.mp3").stem
        == fit_windows_max_path(tmp_path / f"{stem}.opus").stem
    )


@pytest.mark.usefixtures("without_long_paths")
def test_shortening_counts_utf16_units_and_never_splits_a_character(tmp_path: Path) -> None:
    note = "\U0001f3b5"  # outside the BMP: two UTF-16 units each
    path = tmp_path / f"{note * 200}.mp3"

    fitted = fit_windows_max_path(path)

    assert _units(fitted) <= 259
    fitted.stem.encode("utf-8")  # a split surrogate pair would not encode
    assert set(fitted.stem) == {note}


def test_names_are_never_shortened_with_long_paths_enabled(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(playlist_file, "long_paths_enabled", lambda: True)
    path = tmp_path / f"{'x' * 300}.mp3"

    assert fit_windows_max_path(path) == path
