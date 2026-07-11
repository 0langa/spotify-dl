from __future__ import annotations

import io
import json

from playlistdl_backend import __version__
from playlistdl_backend.bridge import Bridge


def test_ping_returns_protocol_response() -> None:
    source = io.StringIO('{"id":"abc","type":"ping"}\n')
    target = io.StringIO()

    Bridge(source, target).run()

    messages = [json.loads(line) for line in target.getvalue().splitlines()]
    assert messages[0] == {"type": "ready", "version": __version__, "protocol": 1}
    assert messages[1] == {"type": "pong", "request_id": "abc"}


def test_start_validation_errors_are_synchronous_and_no_job_started() -> None:
    source = io.StringIO(
        '{"id":"s1","type":"start","playlist_id":"nope","output_dir":"out"}\n'
        '{"id":"s2","type":"start","playlist_id":"nope","output_dir":"out","format":"wma"}\n'
    )
    target = io.StringIO()

    Bridge(source, target).run()

    messages = [json.loads(line) for line in target.getvalue().splitlines()]
    assert messages[1]["type"] == "error"
    assert "Unknown or expired" in messages[1]["message"]
    assert messages[2]["type"] == "error"
    assert "Unsupported audio format" in messages[2]["message"]
    assert all(message["type"] != "job_started" for message in messages)


def test_unknown_command_is_reported_without_crashing_bridge() -> None:
    source = io.StringIO('{"id":"bad","type":"wat"}\n{"id":"ok","type":"ping"}\n')
    target = io.StringIO()

    Bridge(source, target).run()

    messages = [json.loads(line) for line in target.getvalue().splitlines()]
    assert messages[1]["type"] == "error"
    assert messages[1]["request_id"] == "bad"
    assert messages[2] == {"type": "pong", "request_id": "ok"}
