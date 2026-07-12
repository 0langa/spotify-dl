from __future__ import annotations

import atexit
from collections.abc import Callable
from typing import Any

import requests

_sessions: dict[int, requests.Session] = {}


def install_frozen_spotify_transport(
    session_factory: Callable[[], requests.Session] = requests.Session,
) -> None:
    """Replace curl_cffi transport, which cannot connect from our frozen Windows build."""
    from spotapi.http.request import RequestError, TLSClient

    def build_request(client: Any, method: str, url: str | bytes, **kwargs: Any):
        if isinstance(url, (bytes, memoryview)):
            url = bytes(url).decode("utf-8")

        session = _sessions.get(id(client))
        if session is None:
            session = session_factory()
            _sessions[id(client)] = session
        session.headers.update(dict(client.headers))
        if getattr(client, "proxies", None):
            session.proxies.update(client.proxies)

        error = "Unknown"
        kwargs.setdefault("timeout", (10, 30))
        for _ in range(client.auto_retries):
            try:
                response = session.request(method.upper(), url, **kwargs)
                client.cookies.update(session.cookies.get_dict())
                return response
            except requests.RequestException as exc:
                error = str(exc)

        raise RequestError("Failed to complete request.", error=error)

    TLSClient.build_request = build_request


@atexit.register
def _close_sessions() -> None:
    for session in _sessions.values():
        session.close()
    _sessions.clear()
