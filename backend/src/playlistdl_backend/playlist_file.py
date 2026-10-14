from __future__ import annotations

import ctypes
import functools
import os
import re
from pathlib import Path

_INVALID_CHARS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
_CONTROL_CHARS = re.compile(r"[\x00-\x1f]+")
_MAX_NAME_LENGTH = 120
_RESERVED_NAMES = frozenset(
    {
        "con",
        "prn",
        "aux",
        "nul",
        "conin$",
        "conout$",
        *(f"com{index}" for index in "123456789\u00b9\u00b2\u00b3"),
        *(f"lpt{index}" for index in "123456789\u00b9\u00b2\u00b3"),
    }
)


def is_reserved_name(name: str) -> bool:
    """True when Windows would open a device instead of this file or folder.

    Neither the extension nor spaces before it matter: "nul.txt" and "nul .txt" both
    name the NUL device.
    """
    return name.split(".", 1)[0].rstrip(" ").lower() in _RESERVED_NAMES


def sanitize_filename(name: str) -> str:
    """Make a collection name safe as a Windows file name."""
    cleaned = _INVALID_CHARS.sub("_", name).strip(" .")
    # Long source names otherwise push every track path past the Windows limit and
    # make the whole job fail on the first write.
    cleaned = cleaned[:_MAX_NAME_LENGTH].strip(" .")
    if is_reserved_name(cleaned):
        cleaned = f"_{cleaned}"
    return cleaned or "playlist"


def windows_safe_path(path: Path, first_part: int) -> Path:
    """Make every part of a formatted track path from ``first_part`` on valid on Windows.

    spotDL drops the characters Windows forbids from tag values but keeps control
    characters, a folder name that ends in a space, and device names such as "Con".
    Windows strips that trailing space when it creates the folder and then cannot find
    the folder the file is written to, so every track of such an album failed. The parts
    before ``first_part`` are the chosen output folder and are never changed.
    """
    parts = list(path.parts)
    last = len(parts) - 1
    for index in range(max(first_part, 0), len(parts)):
        part = _CONTROL_CHARS.sub(" ", parts[index])
        if index < last:
            part = part.rstrip(" .") or "_"
        if is_reserved_name(part):
            part = f"_{part}"
        parts[index] = part
    return Path(*parts)


# Without long path support Windows refuses a path of MAX_PATH (260 UTF-16 units, the
# terminating NUL included) or more.
_MAX_PATH_UNITS = 259
# A shortened name leaves room for the longest sibling the downloader writes next to a
# track ("<file>.partial") and for the longest audio extension, so the same stem fits
# whichever format and step it is used for.
_SIBLING_RESERVE = len(".partial")
_LONGEST_EXTENSION = len(".flac")
_MIN_STEM_UNITS = 16


@functools.cache
def long_paths_enabled() -> bool:
    """True when this process may use paths past MAX_PATH, which is always so off Windows.

    Both the LongPathsEnabled policy and a long-path-aware manifest are needed, and
    RtlAreLongPathsEnabled reports exactly that combination for the running process.
    """
    if os.name != "nt":
        return True
    try:
        check = ctypes.WinDLL("ntdll").RtlAreLongPathsEnabled
    except (OSError, AttributeError):
        return False
    check.argtypes = []
    check.restype = ctypes.c_ubyte
    return bool(check())


def _utf16_units(text: str) -> int:
    return len(text.encode("utf-16-le")) // 2


def _cut_utf16(text: str, units: int) -> str:
    """Return the longest prefix of ``text`` that is at most ``units`` UTF-16 units long."""
    used = 0
    for index, char in enumerate(text):
        used += 2 if ord(char) > 0xFFFF else 1
        if used > units:
            return text[:index]
    return text


def fit_windows_max_path(path: Path, suffix: str = "") -> Path:
    """Return ``path`` with ``suffix`` added to its name, shortened when Windows needs it.

    Without long path support Windows cannot create, convert, or tag a file whose full
    path reaches MAX_PATH, and long classical titles under the album preset got there.
    Only a path that could not be written is changed, and only the stem of its file name:
    the folders, the collision ``suffix``, and the extension are kept. With long paths
    enabled every name stays as it is.
    """
    named = path.with_name(f"{path.stem}{suffix}{path.suffix}")
    if long_paths_enabled() or _utf16_units(os.path.abspath(named)) <= _MAX_PATH_UNITS:
        return named
    budget = (
        _MAX_PATH_UNITS
        - _utf16_units(os.path.abspath(path.parent))
        - len(os.sep)
        - _utf16_units(suffix)
        - _LONGEST_EXTENSION
        - _SIBLING_RESERVE
    )
    # A folder that is itself too long cannot be fixed from the file name; the stem
    # keeps a readable minimum and the write fails as it did before.
    stem = _cut_utf16(path.stem, max(budget, _MIN_STEM_UNITS)).rstrip(" .") or "_"
    return path.with_name(f"{stem}{suffix}{path.suffix}")


def write_m3u8(output_dir: Path, name: str, track_paths: list[str]) -> Path:
    """Write an extended M3U8 file referencing downloaded tracks in order.

    Paths inside output_dir are written relative so the folder stays portable.
    """
    target = output_dir / f"{sanitize_filename(name)}.m3u8"
    lines = ["#EXTM3U"]
    for raw_path in track_paths:
        path = Path(raw_path)
        try:
            lines.append(str(path.relative_to(output_dir)))
        except ValueError:
            lines.append(str(path))
    target.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return target
