from __future__ import annotations

import re
from pathlib import Path

_INVALID_CHARS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
_MAX_NAME_LENGTH = 120
_RESERVED_NAMES = frozenset(
    {
        "con",
        "prn",
        "aux",
        "nul",
        *(f"com{index}" for index in range(1, 10)),
        *(f"lpt{index}" for index in range(1, 10)),
    }
)


def sanitize_filename(name: str) -> str:
    """Make a collection name safe as a Windows file name."""
    cleaned = _INVALID_CHARS.sub("_", name).strip(" .")
    # Long source names otherwise push every track path past the Windows limit and
    # make the whole job fail on the first write.
    cleaned = cleaned[:_MAX_NAME_LENGTH].strip(" .")
    if cleaned.split(".", 1)[0].lower() in _RESERVED_NAMES:
        cleaned = f"_{cleaned}"
    return cleaned or "playlist"


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
