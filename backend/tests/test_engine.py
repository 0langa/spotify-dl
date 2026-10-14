from __future__ import annotations

import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import pytest
from spotdl.types.song import Song

from playlistdl_backend import engine as engine_module
from playlistdl_backend import playlist_file
from playlistdl_backend.engine import (
    Engine,
    build_output_paths,
    classify_spotify_url,
    effective_bitrate,
    validate_source_url,
)


def test_engine_import_does_not_require_unused_web_server_modules() -> None:
    script = """
import importlib.abc
import sys

class BlockServerModules(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname.partition('.')[0] in {'fastapi', 'starlette', 'uvicorn', 'anyio'}:
            raise ModuleNotFoundError(fullname)
        return None

sys.meta_path.insert(0, BlockServerModules())
import playlistdl_backend.engine
"""
    result = subprocess.run(
        [sys.executable, "-c", script],
        capture_output=True,
        check=False,
        text=True,
        # Importing spotDL cold takes ~10 s locally and much longer on a loaded CI
        # runner with a cold file cache; a tight bound here fails for timing, not for
        # the module boundary this test is about.
        timeout=180,
    )
    assert result.returncode == 0, result.stderr


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
    monkeypatch.setattr(instance, "_ensure_spotify", lambda *args, **kwargs: None)
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


def test_frozen_runtime_reports_excluded_server_modules(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(
        engine_module.importlib.util,
        "find_spec",
        lambda name: object() if name == "fastapi" else None,
    )

    assert engine_module.find_bundled_server_modules() == ["fastapi"]


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


def _album_song(album: str, album_artist: str = "Band", title: str = "Song") -> Song:
    song = Song.from_missing_data(
        song_id="id-1",
        url="https://open.spotify.com/track/id-1",
        name=title,
        artists=["Band"],
        artist="Band",
        album_name=album,
        album_artist=album_artist,
        track_number=1,
        duration=200,
        list_position=1,
        list_length=1,
    )
    engine_module.normalize_song_for_download(song)
    return song


def _downloader_path(song: Song, template: str) -> Path:
    """The path spotDL's downloader is handed for one song."""
    return engine_module.spotdl_downloader_module.create_file_name(
        song=song, template=template, file_extension="mp3", restrict="none"
    )


@pytest.mark.parametrize(
    ("album", "album_artist", "expected_folders"),
    [
        # French typography puts a space before "?"; spotDL drops the "?" and keeps it.
        ("Qui es-tu ?", "Band", ("Band", "Qui es-tu")),
        ("Hits *", "Band", ("Band", "Hits")),
        ("Live\tTapes", "Band", ("Band", "Live Tapes")),
        ("Aux", "Con", ("_Con", "_Aux")),
    ],
)
def test_album_folders_are_valid_windows_names(
    tmp_path: Path, album: str, album_artist: str, expected_folders: tuple[str, str]
) -> None:
    root, template = build_output_paths(str(tmp_path), "Mix", "album_track_title", True)

    path = _downloader_path(_album_song(album, album_artist), template)

    assert path.parent.parent.parent == root
    assert (path.parent.parent.name, path.parent.name) == expected_folders
    # Windows keeps a trailing space inside a path but strips it when the folder is
    # created, so the converter then could not open the file it was told to write.
    path.parent.mkdir(parents=True)
    path.write_bytes(b"audio")
    assert path.is_file()


def test_control_characters_never_reach_a_file_name(tmp_path: Path) -> None:
    _, template = build_output_paths(str(tmp_path), "Mix", "artist_title", True)

    path = _downloader_path(_album_song("Album", title="Line\x01Break"), template)

    assert path.name == "Band - Line Break.mp3"


def test_reuse_destination_matches_the_downloader_path(tmp_path: Path) -> None:
    _, template = build_output_paths(str(tmp_path), "Mix", "album_track_title", True)
    song = _album_song("Qui es-tu ?")

    assert engine_module._output_path_for(song, template, "mp3") == _downloader_path(song, template)


def test_a_file_name_spotdl_shortens_stays_in_the_output_folder(tmp_path: Path) -> None:
    root, template = build_output_paths(str(tmp_path), "Mix", "album_track_title", True)
    song = _album_song("Album", title="Movement " * 40)

    path = _downloader_path(song, template)

    assert path.parent == root / "Band" / "Album"
    assert len(path.name) < 255
    assert path.name.startswith("01 - Movement")


def test_a_long_title_fits_windows_max_path_without_long_paths(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _, template = build_output_paths(str(tmp_path), "Mix", "album_track_title", True)
    title = "Symphony No. 9 in D minor, Op. 125 'Choral': IV. Presto - Allegro assai"
    song = _album_song("Beethoven: Complete Symphonies", "Berliner Philharmoniker", title)
    # The album folder ends 222 characters in, whatever the temp folder's length, so the
    # full path passes MAX_PATH and a shortened name still fits behind the folders.
    folders = len(str(tmp_path / "Mix" / "Berliner Philharmoniker")) + 1
    song.album_name = "Beethoven- Complete Symphonies ".ljust(222 - folders, "x")
    monkeypatch.setattr(playlist_file, "long_paths_enabled", lambda: True)
    full = _downloader_path(song, template)
    assert len(str(full)) > 259

    monkeypatch.setattr(playlist_file, "long_paths_enabled", lambda: False)
    path = _downloader_path(song, template)

    assert len(str(path)) <= 259
    assert path.parent == full.parent
    assert path.name == "01 - Symphony No. 9 in.mp3"
    assert path.suffix == ".mp3"
    assert engine_module._output_path_for(song, template, "mp3") == path
    path.parent.mkdir(parents=True)
    path.write_bytes(b"audio")
    assert path.is_file()


def test_colliding_shortened_names_keep_their_suffix(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(playlist_file, "long_paths_enabled", lambda: False)
    _, template = build_output_paths(str(tmp_path), "Mix", "artist_title", True)
    stem = "Variation " * 40
    first = _album_song("Album", title=f"{stem}one")
    second = _album_song("Album", title=f"{stem}two")
    second.song_id = "id-2"
    second.url = "https://open.spotify.com/track/id-2"

    engine_module.assign_unique_output_suffixes([first, second], template, "mp3")

    paths = [engine_module._output_path_for(song, template, "mp3") for song in (first, second)]
    assert paths[0] != paths[1]
    assert paths[1].stem.endswith(" [id-2]")
    assert paths[1] == _downloader_path(second, template)
    assert all(len(str(path)) <= 259 for path in paths)


def test_the_selected_output_folder_is_never_renamed(tmp_path: Path) -> None:
    chosen = tmp_path / "Con"
    root, template = build_output_paths(str(chosen), "Ignored", "artist_title", False)

    path = _downloader_path(_album_song("Album"), template)

    assert root == chosen.resolve()
    assert path.parent == root
