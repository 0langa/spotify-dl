from __future__ import annotations

import io
import json

from playlistdl_backend.bridge import Bridge


def test_ping_returns_protocol_response() -> None:
    source = io.StringIO('{"id":"abc","type":"ping"}\n')
    target = io.StringIO()

    Bridge(source, target).run()

    messages = [json.loads(line) for line in target.getvalue().splitlines()]
    assert messages[0] == {"type": "ready", "version": "0.1.0", "protocol": 1}
    assert messages[1] == {"type": "pong", "request_id": "abc"}


def test_unknown_command_is_reported_without_crashing_bridge() -> None:
    source = io.StringIO('{"id":"bad","type":"wat"}\n{"id":"ok","type":"ping"}\n')
    target = io.StringIO()

    Bridge(source, target).run()

    messages = [json.loads(line) for line in target.getvalue().splitlines()]
    assert messages[1]["type"] == "error"
    assert messages[1]["request_id"] == "bad"
    assert messages[2] == {"type": "pong", "request_id": "ok"}
