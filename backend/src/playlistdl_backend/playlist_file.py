from __future__ import annotations

import re
from pathlib import Path

_INVALID_CHARS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')


def sanitize_filename(name: str) -> str:
    """Make a collection name safe as a Windows file name."""
    cleaned = _INVALID_CHARS.sub("_", name).strip(" .")
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
