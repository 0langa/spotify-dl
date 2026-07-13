from __future__ import annotations

from typing import Any

import pytest

from playlistdl_backend.engine import (
    Engine,
    _candidate_from_result,
    candidate_is_relevant,
    rank_candidates,
)


class _FakeYtMusic:
    def __init__(self, by_filter: dict[str, list[dict[str, Any]]]) -> None:
        self.by_filter = by_filter
        self.queries: list[tuple[str, str]] = []

    def search(self, query: str, filter: str, limit: int) -> list[dict[str, Any]]:
        self.queries.append((query, filter))
        return self.by_filter.get(filter, [])


def _song(video_id: str, title: str, duration: str, result_type: str = "song") -> dict[str, Any]:
    return {
        "videoId": video_id,
        "title": title,
        "duration": duration,
        "resultType": result_type,
        "artists": [{"name": "Artist"}],
        "album": {"name": "Album"} if result_type == "song" else None,
    }


def test_candidate_normalization_builds_urls_by_type() -> None:
    song = _candidate_from_result(_song("abc", "Tune", "3:20"))
    video = _candidate_from_result(_song("xyz", "Tune (Live)", "3:45", result_type="video"))

    assert song is not None and video is not None
    assert song["url"] == "https://music.youtube.com/watch?v=abc"
    assert video["url"] == "https://www.youtube.com/watch?v=xyz"
    assert song["duration_seconds"] == 200
    assert song["artists"] == ["Artist"]
    assert song["album"] == "Album"


def test_candidate_normalization_rejects_incomplete_results() -> None:
    assert _candidate_from_result({"title": "No id"}) is None
    assert _candidate_from_result({"videoId": "x"}) is None
    assert _candidate_from_result("not-a-dict") is None  # type: ignore[arg-type]


def test_rank_candidates_orders_by_duration_distance_then_type() -> None:
    candidates = [
        {"url": "a", "duration_seconds": 260, "result_type": "video"},
        {"url": "b", "duration_seconds": 205, "result_type": "video"},
        {"url": "c", "duration_seconds": 205, "result_type": "song"},
        {"url": "d", "duration_seconds": 0, "result_type": "song"},
    ]

    ranked = rank_candidates(candidates, target_duration_seconds=200)

    assert [candidate["url"] for candidate in ranked] == ["c", "b", "a", "d"]
    assert ranked[0]["duration_delta_seconds"] == 5
    assert ranked[3]["duration_delta_seconds"] is None


def test_search_sources_merges_filters_and_dedupes() -> None:
    client = _FakeYtMusic(
        {
            "songs": [_song("dup", "Tune", "3:20")],
            "videos": [
                _song("dup", "Tune", "3:20", result_type="video"),
                _song("v1", "Tune (Video)", "3:21", result_type="video"),
            ],
        }
    )

    results = Engine.search_sources("Tune", "Artist", duration_seconds=200, client=client)

    urls = [candidate["url"] for candidate in results]
    assert "https://music.youtube.com/watch?v=dup" in urls
    assert "https://www.youtube.com/watch?v=v1" in urls
    assert len(urls) == len(set(urls))
    assert client.queries == [("Artist Tune", "songs"), ("Artist Tune", "videos")]


def test_search_sources_requires_query_text() -> None:
    with pytest.raises(ValueError, match="title or artist"):
        Engine.search_sources("", "", client=_FakeYtMusic({}))


@pytest.mark.parametrize(
    ("candidate", "expected"),
    [
        (
            {
                "title": "Alligatoah - Willst Du (Lyrics)",
                "artists": ["Alligatoah"],
                "duration_seconds": 218,
            },
            True,
        ),
        (
            {
                "title": "Nightcore Stamp On The Ground - Italobrothers",
                "artists": [],
                "duration_seconds": 157,
            },
            True,
        ),
        (
            {
                "title": "Completely Different Track",
                "artists": ["Someone Else"],
                "duration_seconds": 218,
            },
            False,
        ),
        (
            {
                "title": "Alligatoah - Willst Du",
                "artists": ["Alligatoah"],
                "duration_seconds": 360,
            },
            False,
        ),
    ],
)
def test_candidate_relevance_requires_identity_and_duration(
    candidate: dict[str, Any], expected: bool
) -> None:
    assert (
        candidate_is_relevant(
            candidate,
            title="Willst du"
            if "Willst" in candidate["title"]
            else "Stamp on the Ground - Nightcore & KYANU Edit",
            artists=["Alligatoah"]
            if "Willst" in candidate["title"]
            else ["ItaloBrothers", "KYANU"],
            duration_seconds=218 if "Willst" in candidate["title"] else 157,
        )
        is expected
    )
