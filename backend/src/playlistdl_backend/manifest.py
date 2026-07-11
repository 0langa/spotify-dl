from __future__ import annotations

import csv
import hashlib
import json
from pathlib import Path
from typing import Any

from spotdl.types.song import Song


def load_manifest(path_value: str) -> tuple[str, list[Song]]:
    """Load a CSV or JSON track manifest into complete spotDL Song objects."""
    path = Path(path_value).expanduser().resolve()
    if not path.is_file():
        raise ValueError("Manifest file does not exist")
    suffix = path.suffix.lower()
    if suffix == ".csv":
        with path.open("r", encoding="utf-8-sig", newline="") as stream:
            rows = list(csv.DictReader(stream))
        name = path.stem
    elif suffix == ".json":
        with path.open("r", encoding="utf-8-sig") as stream:
            document = json.load(stream)
        if isinstance(document, list):
            rows = document
            name = path.stem
        elif isinstance(document, dict) and isinstance(document.get("tracks"), list):
            rows = document["tracks"]
            name = str(document.get("name") or path.stem)
        else:
            raise ValueError("JSON manifest must be a track array or an object with a tracks array")
    else:
        raise ValueError("Manifest must be a .csv or .json file")

    songs = [_song_from_row(row, index) for index, row in enumerate(rows, start=1)]
    if not songs:
        raise ValueError("Manifest contains no tracks")
    return name, songs


def _value(row: dict[str, Any], *aliases: str) -> str:
    normalized = {str(key).strip().lower(): value for key, value in row.items()}
    for alias in aliases:
        value = normalized.get(alias.lower())
        if value is not None and str(value).strip():
            return str(value).strip()
    return ""


def _duration_seconds(row: dict[str, Any]) -> int:
    milliseconds = _value(row, "duration (ms)", "duration_ms", "durationms")
    seconds = _value(row, "duration_seconds", "duration (s)", "duration")
    try:
        return (
            max(0, round(float(milliseconds) / 1000))
            if milliseconds
            else max(0, round(float(seconds)))
        )
    except ValueError as exc:
        raise ValueError("Track duration must be a number") from exc


def _song_from_row(row_value: Any, position: int) -> Song:
    if not isinstance(row_value, dict):
        raise ValueError(f"Track {position} must be an object")
    row: dict[str, Any] = row_value
    name = _value(row, "title", "track name", "name")
    artist_text = _value(row, "artist", "artist name(s)", "artists", "artist name")
    if not name or not artist_text:
        raise ValueError(f"Track {position} requires title and artist")
    artists = [part.strip() for part in artist_text.replace(";", ",").split(",") if part.strip()]
    spotify_url = _value(row, "spotify_url", "track url", "track uri", "url")
    if spotify_url.startswith("spotify:track:"):
        spotify_url = f"https://open.spotify.com/track/{spotify_url.rsplit(':', 1)[-1]}"
    identifier = (
        _value(row, "id", "track id")
        or hashlib.sha256(f"{position}\0{name}\0{artist_text}".encode()).hexdigest()[:24]
    )
    year_text = _value(row, "year", "release year")
    track_text = _value(row, "track number", "track_number")
    return Song(
        name=name,
        artists=artists,
        artist=artists[0],
        genres=[],
        disc_number=1,
        disc_count=1,
        album_name=_value(row, "album", "album name"),
        album_artist=_value(row, "album artist") or artists[0],
        duration=_duration_seconds(row),
        year=int(year_text) if year_text.isdigit() else 0,
        date=_value(row, "release date", "date"),
        track_number=int(track_text) if track_text.isdigit() else position,
        tracks_count=0,
        song_id=identifier,
        explicit=False,
        publisher="",
        url=spotify_url,
        isrc=_value(row, "isrc") or None,
        cover_url=_value(row, "cover_url", "cover url", "artwork url") or None,
        copyright_text=None,
        list_name="",
        list_url=str(Path()),
        list_position=position,
        list_length=0,
    )
