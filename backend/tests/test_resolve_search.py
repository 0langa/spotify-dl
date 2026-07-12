from __future__ import annotations

from typing import Any

import pytest

from playlistdl_backend.engine import Engine


class _FakeYtMusic:
    def __init__(self, results: list[dict[str, Any]]) -> None:
        self.results = results
        self.queries: list[str] = []

    def search(self, query: str, filter: str, limit: int) -> list[dict[str, Any]]:
        assert filter == "songs"
        self.queries.append(query)
        return self.results


def _song_result(video_id: str, title: str, duration: str = "3:20") -> dict[str, Any]:
    return {
        "videoId": video_id,
        "title": title,
        "duration": duration,
        "resultType": "song",
        "artists": [{"name": "Artist"}],
        "album": {"name": "Album"},
        "thumbnails": [
            {"url": "https://img.example/small.jpg"},
            {"url": "https://img.example/large.jpg"},
        ],
    }


def test_resolve_search_builds_downloadable_playlist() -> None:
    engine = Engine(emit=lambda event: None)
    client = _FakeYtMusic([_song_result("v1", "Tune"), _song_result("v2", "Tune 2")])

    playlist = engine.resolve_search("artist tune", client=client)

    assert playlist.source_type == "search"
    assert playlist.source_url == "search:artist tune"
    assert playlist.name == "artist tune"
    assert [track.title for track in playlist.tracks] == ["Tune", "Tune 2"]
    assert playlist.tracks[0].duration_seconds == 200
    assert playlist.tracks[0].cover_url == "https://img.example/large.jpg"
    songs = engine._songs[playlist.id]
    assert songs[0].download_url == "https://music.youtube.com/watch?v=v1"
    assert songs[0].list_length == 2
    assert client.queries == ["artist tune"]


def test_resolve_search_dedupes_and_respects_limit() -> None:
    engine = Engine(emit=lambda event: None)
    client = _FakeYtMusic(
        [
            _song_result("dup", "Same"),
            _song_result("dup", "Same"),
            _song_result("v2", "Other"),
            _song_result("v3", "Third"),
        ]
    )

    playlist = engine.resolve_search("query", limit=2, client=client)

    assert len(playlist.tracks) == 2
    assert [track.title for track in playlist.tracks] == ["Same", "Other"]


def test_resolve_search_rejects_blank_and_empty_results() -> None:
    engine = Engine(emit=lambda event: None)

    with pytest.raises(ValueError, match="artist, title"):
        engine.resolve_search("   ", client=_FakeYtMusic([]))
    with pytest.raises(ValueError, match="No songs found"):
        engine.resolve_search("obscure", client=_FakeYtMusic([]))
