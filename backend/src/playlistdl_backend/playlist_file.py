from __future__ import annotations

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
