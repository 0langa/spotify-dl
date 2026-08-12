"""Optional official Spotify Web API path, used when credentials are supplied."""

from __future__ import annotations

from typing import Any

import pytest

from playlistdl_backend import engine as engine_module
from playlistdl_backend.bridge import spotify_credentials
from playlistdl_backend.engine import Engine


class _FakeSpotifyClient:
    """Stands in for spotDL's process-wide client singleton."""

    _instance: Any = None
    calls: list[dict[str, Any]] = []

    @classmethod
    def init(cls, **kwargs: Any) -> Any:
        if cls._instance is not None:
            raise RuntimeError("A spotify client has already been initialized")
        cls.calls.append(kwargs)
        cls._instance = object()
        return cls._instance


@pytest.fixture
def spotify(monkeypatch: pytest.MonkeyPatch) -> type[_FakeSpotifyClient]:
    _FakeSpotifyClient._instance = None
    _FakeSpotifyClient.calls = []
    monkeypatch.setattr(engine_module, "SpotifyClient", _FakeSpotifyClient)
    return _FakeSpotifyClient


def test_without_credentials_the_zero_setup_resolver_is_used(
    spotify: type[_FakeSpotifyClient],
) -> None:
    Engine(emit=lambda event: None)._ensure_spotify()

    assert spotify.calls == [
        {"client_id": "", "client_secret": "", "no_cache": True, "use_official_api": False}
    ]


def test_credentials_switch_to_the_official_web_api(
    spotify: type[_FakeSpotifyClient],
) -> None:
    Engine(emit=lambda event: None)._ensure_spotify(("client", "secret"))

    assert spotify.calls == [
        {
            "client_id": "client",
            "client_secret": "secret",
            "no_cache": True,
            "use_official_api": True,
        }
    ]


def test_the_client_is_initialized_once_per_credential_set(
    spotify: type[_FakeSpotifyClient],
) -> None:
    engine = Engine(emit=lambda event: None)

    engine._ensure_spotify(("client", "secret"))
    engine._ensure_spotify(("client", "secret"))

    assert len(spotify.calls) == 1


def test_changing_credentials_replaces_the_running_client(
    spotify: type[_FakeSpotifyClient],
) -> None:
    engine = Engine(emit=lambda event: None)

    engine._ensure_spotify()
    engine._ensure_spotify(("client", "secret"))
    engine._ensure_spotify()

    assert [call["use_official_api"] for call in spotify.calls] == [False, True, False]


def test_resolve_passes_the_credentials_through(
    spotify: type[_FakeSpotifyClient], monkeypatch: pytest.MonkeyPatch
) -> None:
    engine = Engine(emit=lambda event: None)
    monkeypatch.setattr(
        engine_module.Playlist,
        "get_metadata",
        staticmethod(lambda url: ({"name": "Mix"}, [])),
    )

    engine.resolve("https://open.spotify.com/playlist/xyz", ("client", "secret"))

    assert spotify.calls[0]["use_official_api"] is True


@pytest.mark.parametrize(
    "request_payload",
    [
        {},
        {"spotify_client_id": "client"},
        {"spotify_client_secret": "secret"},
        {"spotify_client_id": "", "spotify_client_secret": "secret"},
        {"spotify_client_id": "  ", "spotify_client_secret": "  "},
    ],
)
def test_incomplete_credentials_keep_the_default_resolver(
    request_payload: dict[str, Any],
) -> None:
    assert spotify_credentials(request_payload) is None


def test_credentials_are_read_and_trimmed_from_one_request() -> None:
    payload = {"spotify_client_id": " client ", "spotify_client_secret": " secret "}

    assert spotify_credentials(payload) == ("client", "secret")


def test_rejected_credentials_report_an_actionable_message_without_the_secret(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    class RejectingClient:
        _instance: Any = None

        @classmethod
        def init(cls, **kwargs: Any) -> Any:
            raise RuntimeError(
                f"invalid_client id={kwargs['client_id']} secret={kwargs['client_secret']}"
            )

    monkeypatch.setattr(engine_module, "SpotifyClient", RejectingClient)
    engine = Engine(emit=lambda event: None)

    with pytest.raises(ValueError) as failure:
        engine._ensure_spotify(("theclientid", "the-super-secret"))

    message = str(failure.value)
    assert "the-super-secret" not in message
    assert "theclientid" not in message
    assert "invalid_client" not in message
    assert "Settings" in message
    assert RejectingClient._instance is None
    assert engine._spotify_credentials is None
    assert engine._spotify_initialized is False


def test_a_rejected_switch_keeps_the_resolver_that_was_working(
    spotify: type[_FakeSpotifyClient], monkeypatch: pytest.MonkeyPatch
) -> None:
    engine = Engine(emit=lambda event: None)
    engine._ensure_spotify()
    working = spotify._instance
    assert working is not None

    def reject(cls: Any, **kwargs: Any) -> Any:
        raise RuntimeError("invalid_client")

    monkeypatch.setattr(spotify, "init", classmethod(reject))

    with pytest.raises(ValueError):
        engine._ensure_spotify(("client", "secret"))

    # The public resolver that was already running must survive a failed switch.
    assert spotify._instance is working
    assert engine._spotify_initialized is True
    assert engine._spotify_credentials is None


def test_the_public_resolver_still_works_after_credentials_are_rejected(
    monkeypatch: pytest.MonkeyPatch, spotify: type[_FakeSpotifyClient]
) -> None:
    engine = Engine(emit=lambda event: None)
    calls: list[dict[str, Any]] = []

    def init(**kwargs: Any) -> Any:
        if kwargs["use_official_api"]:
            raise RuntimeError("invalid_client")
        calls.append(kwargs)
        return object()

    monkeypatch.setattr(spotify, "init", classmethod(lambda cls, **kwargs: init(**kwargs)))

    with pytest.raises(ValueError):
        engine._ensure_spotify(("client", "secret"))
    engine._ensure_spotify()

    assert [call["use_official_api"] for call in calls] == [False]


def test_the_bridge_forwards_credentials_from_a_resolve_request() -> None:
    import io
    import json as json_module

    from playlistdl_backend.bridge import Bridge

    seen: list[tuple[str, tuple[str, str] | None]] = []
    requests = [
        {
            "id": "r1",
            "type": "resolve",
            "url": "https://open.spotify.com/playlist/x",
            "spotify_client_id": "client",
            "spotify_client_secret": "secret",
        },
        {"id": "r2", "type": "resolve", "url": "https://open.spotify.com/playlist/y"},
    ]
    source = io.StringIO("".join(json_module.dumps(request) + chr(10) for request in requests))
    target = io.StringIO()
    bridge = Bridge(source, target)

    class _Playlist:
        @staticmethod
        def to_dict() -> dict[str, Any]:
            return {"id": "p", "tracks": []}

    def fake_resolve(url: str, credentials: tuple[str, str] | None = None) -> Any:
        seen.append((url, credentials))
        return _Playlist()

    bridge._engine.resolve = fake_resolve  # type: ignore[method-assign]

    bridge.run()

    assert seen == [
        ("https://open.spotify.com/playlist/x", ("client", "secret")),
        ("https://open.spotify.com/playlist/y", None),
    ]
    # The request payload carries the secret; nothing emitted may repeat it.
    assert "secret" not in target.getvalue()


def test_credentials_are_proven_when_they_are_applied(
    spotify: type[_FakeSpotifyClient], monkeypatch: pytest.MonkeyPatch
) -> None:
    """Building the official client makes no request, so the token is fetched here."""
    fetched: list[bool] = []

    class _Manager:
        @staticmethod
        def get_access_token(check_cache: bool = True) -> str:
            fetched.append(check_cache)
            return "token"

    class _Client:
        auth_manager = _Manager()

    monkeypatch.setattr(
        spotify,
        "init",
        classmethod(lambda cls, **kw: setattr(cls, "_instance", _Client()) or cls._instance),
    )

    Engine(emit=lambda event: None)._ensure_spotify(("client", "secret"))

    assert fetched == [False]


def test_credentials_rejected_at_token_time_are_reported_as_credentials(
    spotify: type[_FakeSpotifyClient], monkeypatch: pytest.MonkeyPatch
) -> None:
    class _Manager:
        @staticmethod
        def get_access_token(check_cache: bool = True) -> str:
            raise RuntimeError("error: invalid_client, error_description: Invalid client secret")

    class _Client:
        auth_manager = _Manager()

    monkeypatch.setattr(
        spotify,
        "init",
        classmethod(lambda cls, **kw: setattr(cls, "_instance", _Client()) or cls._instance),
    )
    engine = Engine(emit=lambda event: None)

    with pytest.raises(ValueError, match="Settings"):
        engine._ensure_spotify(("client", "secret"))

    assert engine._spotify_credentials is None


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("error: invalid_client, error_description: Invalid client secret", True),
        ("SpotifyOauthError: unauthorized", True),
        ("http status: 401, code:-1", True),
        ("Could not get session", False),
        ("No results found for song", False),
    ],
)
def test_auth_failures_are_told_apart_from_source_failures(text: str, expected: bool) -> None:
    assert engine_module.is_spotify_auth_failure(text) is expected


def test_an_auth_failure_during_resolve_is_replaced_with_the_actionable_message(
    spotify: type[_FakeSpotifyClient], monkeypatch: pytest.MonkeyPatch
) -> None:
    engine = Engine(emit=lambda event: None)

    def reject(url: str) -> Any:
        raise RuntimeError("error: invalid_client, error_description: Invalid client secret")

    monkeypatch.setattr(engine_module.Playlist, "get_metadata", staticmethod(reject))

    with pytest.raises(ValueError) as failure:
        engine.resolve("https://open.spotify.com/playlist/xyz", ("client", "secret"))

    assert "invalid_client" not in str(failure.value)
    assert "Settings" in str(failure.value)


def test_a_source_failure_during_resolve_keeps_its_own_message(
    spotify: type[_FakeSpotifyClient], monkeypatch: pytest.MonkeyPatch
) -> None:
    engine = Engine(emit=lambda event: None)

    def missing(url: str) -> Any:
        raise RuntimeError("Playlist not found")

    monkeypatch.setattr(engine_module.Playlist, "get_metadata", staticmethod(missing))

    with pytest.raises(RuntimeError, match="Playlist not found"):
        engine.resolve("https://open.spotify.com/playlist/xyz", ("client", "secret"))
