from __future__ import annotations

import json
from pathlib import Path

import pytest

from playlistdl_backend.manifest import load_manifest


def test_loads_exportify_style_csv(tmp_path: Path) -> None:
    path = tmp_path / "Road Trip.csv"
    path.write_text(
        "Track Name,Artist Name(s),Album Name,Duration (ms),Track URI,ISRC\n"
        'Song One,"Artist A, Artist B",Album One,185000,spotify:track:abc,US123\n',
        encoding="utf-8",
    )

    name, songs = load_manifest(str(path))

    assert name == "Road Trip"
    assert songs[0].name == "Song One"
    assert songs[0].artists == ["Artist A", "Artist B"]
    assert songs[0].duration == 185
    assert songs[0].url == "https://open.spotify.com/track/abc"
    assert songs[0].isrc == "US123"


def test_loads_named_json_manifest(tmp_path: Path) -> None:
    path = tmp_path / "tracks.json"
    path.write_text(
        json.dumps(
            {
                "name": "My imports",
                "tracks": [
                    {"title": "Song", "artist": "Artist", "album": "Album", "duration_seconds": 42}
                ],
            }
        ),
        encoding="utf-8",
    )

    name, songs = load_manifest(str(path))

    assert name == "My imports"
    assert songs[0].album_name == "Album"
    assert songs[0].duration == 42


def test_rejects_rows_without_required_metadata(tmp_path: Path) -> None:
    path = tmp_path / "bad.json"
    path.write_text('[{"title": "Missing artist"}]', encoding="utf-8")

    with pytest.raises(ValueError, match="requires title and artist"):
        load_manifest(str(path))
