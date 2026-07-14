from __future__ import annotations

from types import SimpleNamespace
from typing import Any

import pytest

from playlistdl_backend import engine as engine_module
from playlistdl_backend.engine import (
    Engine,
    build_output_paths,
    classify_spotify_url,
    effective_bitrate,
    validate_source_url,
)


@pytest.mark.parametrize(
    ("url", "expected"),
    [
        ("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", "playlist"),
        ("https://open.spotify.com/album/4aawyAB9vmqN3uQ7FjRGTy", "album"),
        ("https://open.spotify.com/track/11dFghVXANMlKmJXsNCbNl", "track"),
        ("https://open.spotify.com/intl-de/track/11dFghVXANMlKmJXsNCbNl?si=abc", "track"),
        ("http://spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", "playlist"),
    ],
)
def test_classify_supported_urls(url: str, expected: str) -> None:
    assert classify_spotify_url(url) == expected


@pytest.mark.parametrize(
    "url",
    [
        "not a url",
        "ftp://open.spotify.com/playlist/x",
        "https://example.com/playlist/x",
        "https://evilspotify.com/playlist/x",
        "https://open.spotify.com/artist/0OdUWJ0sBjDrqHygGUXeCF",
        "https://open.spotify.com/playlist/",
        "https://open.spotify.com/",
    ],
)
def test_classify_rejects_unsupported_urls(url: str) -> None:
    with pytest.raises(ValueError):
        classify_spotify_url(url)


def _fake_song(name: str, position: int) -> SimpleNamespace:
    return SimpleNamespace(
        song_id=f"id-{position}",
        url=f"https://open.spotify.com/track/id-{position}",
        list_position=position,
        name=name,
        artists=["Artist"],
        artist="Artist",
        album_name="Album",
        duration=200,
        cover_url="https://img.example/cover.jpg",
        isrc=None,
    )


@pytest.fixture
def engine(monkeypatch: pytest.MonkeyPatch) -> Engine:
    instance = Engine(emit=lambda event: None)
    monkeypatch.setattr(instance, "_ensure_spotify", lambda: None)
    return instance


def test_resolve_playlist_routes_to_playlist_metadata(
    engine: Engine, monkeypatch: pytest.MonkeyPatch
) -> None:
    metadata: dict[str, Any] = {
        "name": "My Mix",
        "description": "desc",
        "author_name": "Julius",
        "cover_url": "https://img.example/p.jpg",
    }
    songs = [_fake_song("One", 1), _fake_song("Two", 2)]
    monkeypatch.setattr(
        engine_module.Playlist, "get_metadata", staticmethod(lambda url: (metadata, songs))
    )

    result = engine.resolve("https://open.spotify.com/playlist/xyz")

    assert result.source_type == "playlist"
    assert result.name == "My Mix"
    assert result.owner == "Julius"
    assert [track.title for track in result.tracks] == ["One", "Two"]


def test_resolve_handles_more_than_one_thousand_tracks(
    engine: Engine, monkeypatch: pytest.MonkeyPatch
) -> None:
    metadata: dict[str, Any] = {"name": "Huge Mix", "author_name": "Owner"}
    songs = [_fake_song(f"Track {index}", index) for index in range(1, 1201)]
    monkeypatch.setattr(
        engine_module.Playlist, "get_metadata", staticmethod(lambda url: (metadata, songs))
    )

    result = engine.resolve("https://open.spotify.com/playlist/huge")

    assert len(result.tracks) == 1200
    assert result.tracks[-1].position == 1200


def test_resolve_album_routes_to_album_metadata(
    engine: Engine, monkeypatch: pytest.MonkeyPatch
) -> None:
    metadata = {"name": "Great Album", "artist": {"name": "Band"}, "url": "u"}
    songs = [_fake_song("Opener", 1)]
    monkeypatch.setattr(
        engine_module.Album, "get_metadata", staticmethod(lambda url: (metadata, songs))
    )

    result = engine.resolve("https://open.spotify.com/album/xyz")

    assert result.source_type == "album"
    assert result.name == "Great Album"
    assert result.owner == "Band"
    assert result.cover_url == "https://img.example/cover.jpg"
    assert len(result.tracks) == 1


def test_resolve_track_uses_single_song(engine: Engine, monkeypatch: pytest.MonkeyPatch) -> None:
    song = _fake_song("Single", 1)
    monkeypatch.setattr(engine_module, "resolve_spotify_track_resilient", lambda url: song)

    result = engine.resolve("https://open.spotify.com/track/xyz")

    assert result.source_type == "track"
    assert result.name == "Single"
    assert result.owner == "Artist"
    assert len(result.tracks) == 1
    assert result.tracks[0].title == "Single"


def test_resolve_rejects_unsupported_url(engine: Engine) -> None:
    with pytest.raises(ValueError):
        engine.resolve("https://open.spotify.com/artist/xyz")


@pytest.mark.parametrize(
    ("audio_format", "bitrate", "expected"),
    [
        ("mp3", "0", "0"),
        ("mp3", "320k", "320k"),
        ("mp3", None, "0"),
        ("m4a", "320k", "disable"),
        ("opus", "0", "disable"),
        ("flac", "320k", None),
        ("wav", "0", None),
        ("ogg", "0", None),
    ],
)
def test_effective_bitrate_policy(
    audio_format: str, bitrate: str | None, expected: str | None
) -> None:
    assert effective_bitrate(audio_format, bitrate) == expected


@pytest.mark.parametrize(
    "url",
    [
        "https://youtu.be/video-id",
        "https://www.youtube.com/watch?v=video-id",
        "https://music.youtube.com/watch?v=video-id",
    ],
)
def test_validate_source_url_accepts_youtube_hosts(url: str) -> None:
    assert validate_source_url(url) == url


@pytest.mark.parametrize(
    "url",
    ["http://youtube.com/watch?v=x", "https://example.com/video", "not-a-url"],
)
def test_validate_source_url_rejects_unsafe_or_unrelated_urls(url: str) -> None:
    with pytest.raises(ValueError, match="HTTPS YouTube"):
        validate_source_url(url)


def test_download_rejects_unsupported_format(engine: Engine) -> None:
    engine._songs["known"] = [_fake_song("One", 1)]  # type: ignore[assignment]

    with pytest.raises(ValueError, match="Unsupported audio format"):
        engine.download("known", "out", audio_format="wma")


def test_build_output_paths_creates_sanitized_collection_folder(tmp_path) -> None:
    root, template = build_output_paths(
        str(tmp_path), 'Road/Trip: "Mix"', "album_track_title", True
    )

    assert root == tmp_path / "Road_Trip_ _Mix_"
    assert template == str(root / "{album-artist}/{album}/{track-number} - {title}.{output-ext}")


def test_build_output_paths_can_write_directly_to_selected_folder(tmp_path) -> None:
    root, template = build_output_paths(str(tmp_path), "Ignored", "artist_title", False)

    assert root == tmp_path
    assert template == str(root / "{artist} - {title}.{output-ext}")


def test_build_output_paths_rejects_unknown_preset(tmp_path) -> None:
    with pytest.raises(ValueError, match="Unknown file naming preset"):
        build_output_paths(str(tmp_path), "Mix", "custom", True)
