from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any


@dataclass(frozen=True)
class TrackDto:
    id: str
    position: int
    title: str
    artists: list[str]
    album: str
    duration_seconds: int
    cover_url: str | None
    spotify_url: str
    isrc: str | None

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass(frozen=True)
class PlaylistDto:
    id: str
    name: str
    description: str
    owner: str
    cover_url: str
    source_url: str
    source_type: str
    tracks: list[TrackDto]

    def to_dict(self) -> dict[str, Any]:
        value = asdict(self)
        value["track_count"] = len(self.tracks)
        return value
