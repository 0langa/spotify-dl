from __future__ import annotations

import atexit
from collections.abc import Callable
from typing import Any

import requests

# The session lives on the client itself, so a released client can never hand its
# session to whatever object later reuses its address.
_SESSION_ATTRIBUTE = "_playlistdl_session"
_sessions: list[requests.Session] = []
_fallback_sessions: dict[int, requests.Session] = {}


def _session_for(client: Any, session_factory: Callable[[], requests.Session]):
    session = getattr(client, _SESSION_ATTRIBUTE, None) or _fallback_sessions.get(id(client))
    if session is not None:
        return session

    session = session_factory()
    _sessions.append(session)
    try:
        setattr(client, _SESSION_ATTRIBUTE, session)
    except (AttributeError, TypeError):
        _fallback_sessions[id(client)] = session
    return session


def install_frozen_spotify_transport(
    session_factory: Callable[[], requests.Session] = requests.Session,
) -> None:
    """Replace curl_cffi transport, which cannot connect from our frozen Windows build."""
    from spotapi.http.request import RequestError, TLSClient

    def build_request(client: Any, method: str, url: str | bytes, **kwargs: Any):
        if isinstance(url, (bytes, memoryview)):
            url = bytes(url).decode("utf-8")

        session = _session_for(client, session_factory)
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
    for session in list(_sessions):
        session.close()
    _sessions.clear()
    _fallback_sessions.clear()
