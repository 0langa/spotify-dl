from __future__ import annotations

from types import SimpleNamespace

import requests

from playlistdl_backend import frozen_transport


class FakeSession:
    def __init__(self) -> None:
        self.headers: dict[str, str] = {}
        self.proxies: dict[str, str] = {}
        self.cookies = requests.cookies.RequestsCookieJar()
        self.calls: list[tuple[str, str]] = []

    def request(self, method: str, url: str, **kwargs):
        self.calls.append((method, url))
        self.cookies.set("sp_t", "session-cookie")
        return SimpleNamespace(status_code=200, text="ok")

    def close(self) -> None:
        pass


def test_frozen_transport_uses_requests_and_preserves_session(monkeypatch) -> None:
    from spotapi.http.request import TLSClient

    original = TLSClient.build_request
    sessions: list[FakeSession] = []

    def factory() -> FakeSession:
        session = FakeSession()
        sessions.append(session)
        return session

    try:
        frozen_transport.install_frozen_spotify_transport(factory)  # type: ignore[arg-type]
        client = SimpleNamespace(
            headers={"User-Agent": "PlaylistDL"},
            proxies={},
            cookies={},
            auto_retries=1,
        )

        first = TLSClient.build_request(client, "get", "https://open.spotify.com")
        second = TLSClient.build_request(client, "get", "https://open.spotify.com/api/token")

        assert first.status_code == 200
        assert second.status_code == 200
        assert len(sessions) == 1
        assert sessions[0].calls == [
            ("GET", "https://open.spotify.com"),
            ("GET", "https://open.spotify.com/api/token"),
        ]
        assert client.cookies["sp_t"] == "session-cookie"
    finally:
        monkeypatch.setattr(TLSClient, "build_request", original)
        frozen_transport._close_sessions()
