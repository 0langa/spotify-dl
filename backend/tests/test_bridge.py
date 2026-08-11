from __future__ import annotations

import io
import json
import threading
import time

from playlistdl_backend import __version__
from playlistdl_backend.bridge import Bridge, format_exception


def test_ping_returns_protocol_response() -> None:
    source = io.StringIO('{"id":"abc","type":"ping"}\n')
    target = io.StringIO()

    Bridge(source, target).run()

    messages = [json.loads(line) for line in target.getvalue().splitlines()]
    assert messages[0] == {"type": "ready", "version": __version__, "protocol": 1}
    assert messages[1] == {"type": "pong", "request_id": "abc"}


def test_runtime_check_loads_provider_resources() -> None:
    source = io.StringIO('{"id":"runtime","type":"runtime_check"}\n')
    target = io.StringIO()

    Bridge(source, target).run()

    messages = [json.loads(line) for line in target.getvalue().splitlines()]
    assert messages[1] == {"type": "runtime_ok", "request_id": "runtime"}


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


def test_diagnose_command_emits_diagnose_result(monkeypatch) -> None:
    from playlistdl_backend.engine import Engine

    monkeypatch.setattr(
        Engine,
        "diagnose",
        lambda self: {"backend_path": "x", "frozen": False, "checks": []},
    )
    source = io.StringIO('{"id":"d1","type":"diagnose"}\n')
    target = io.StringIO()

    Bridge(source, target).run()

    messages = [json.loads(line) for line in target.getvalue().splitlines()]
    assert messages[1]["type"] == "diagnose_result"
    assert messages[1]["request_id"] == "d1"
    assert messages[1]["backend_path"] == "x"


def test_start_forwards_reliability_options(monkeypatch) -> None:
    import time

    from playlistdl_backend.engine import Engine

    captured: dict[str, object] = {}
    monkeypatch.setattr(Engine, "ensure_startable", lambda self, playlist_id, fmt: None)

    def fake_download(self, **kwargs) -> None:
        captured.update(kwargs)

    monkeypatch.setattr(Engine, "download", fake_download)
    source = io.StringIO(
        '{"id":"s1","type":"start","playlist_id":"p","output_dir":"out",'
        '"throttle_seconds":2.5,"retries":3,"ytdlp_args":"--foo bar"}\n'
    )
    target = io.StringIO()

    Bridge(source, target).run()

    deadline = time.monotonic() + 5
    while "ytdlp_args" not in captured and time.monotonic() < deadline:
        time.sleep(0.02)
    assert captured["throttle_seconds"] == 2.5
    assert captured["retries"] == 3
    assert captured["ytdlp_args"] == "--foo bar"


def test_shutdown_stops_read_loop_so_process_can_exit() -> None:
    source = io.StringIO('{"id":"bye","type":"shutdown"}\n{"id":"after","type":"ping"}\n')
    target = io.StringIO()

    Bridge(source, target).run()

    messages = [json.loads(line) for line in target.getvalue().splitlines()]
    assert messages[0]["type"] == "ready"
    assert all(message.get("request_id") != "after" for message in messages)


def test_format_exception_includes_hidden_provider_detail() -> None:
    error = RuntimeError("Failed to complete request.")
    error.error = "curl failure"  # type: ignore[attr-defined]

    assert format_exception(error) == "Failed to complete request. (curl failure)"


def test_shutdown_waits_for_the_download_worker_to_stop() -> None:
    """Interpreter exit kills daemon workers; their ffmpeg children would be orphaned."""
    started = threading.Event()
    stopped = threading.Event()

    def slow_download(**kwargs: object) -> None:
        started.set()
        for _ in range(100):
            if bridge._engine._cancel.is_set():  # noqa: SLF001 - worker lifecycle probe
                break
            time.sleep(0.01)
        stopped.set()

    source = io.StringIO(
        '{"id":"start","type":"start","playlist_id":"p","output_dir":"out"}\n'
        '{"id":"bye","type":"shutdown"}\n'
    )
    target = io.StringIO()
    bridge = Bridge(source, target)
    bridge._engine.ensure_startable = lambda *args, **kwargs: None  # type: ignore[method-assign]
    bridge._engine.download = slow_download  # type: ignore[method-assign]

    bridge.run()

    assert started.is_set()
    assert stopped.is_set(), "shutdown returned while the download worker was still running"


def test_diagnose_answers_without_blocking_the_request_loop() -> None:
    source = io.StringIO('{"id":"diag","type":"diagnose"}\n{"id":"ping","type":"ping"}\n')
    target = io.StringIO()
    bridge = Bridge(source, target)
    release = threading.Event()

    def slow_diagnose() -> dict[str, object]:
        release.wait(2)
        return {"backend_path": "backend.exe", "frozen": False, "checks": []}

    bridge._engine.diagnose = slow_diagnose  # type: ignore[method-assign]

    bridge.run()
    emitted = [json.loads(line) for line in target.getvalue().splitlines()]
    types = [message.get("type") for message in emitted]
    # The pong must be out before the diagnosis finishes, which is only possible if the
    # probe ran off the request loop.
    assert "pong" in types, "ping was queued behind the diagnosis"
    assert "diagnose_result" not in types, "diagnose blocked the request loop"
    release.set()

    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        if any(
            json.loads(line).get("type") == "diagnose_result"
            for line in target.getvalue().splitlines()
        ):
            break
        time.sleep(0.02)
    else:  # pragma: no cover - only on a hung worker
        raise AssertionError("diagnose_result never arrived")


def test_start_arms_each_job_so_a_cancelled_job_does_not_block_the_next() -> None:
    observed: list[bool] = []
    seen = threading.Event()

    def record_cancel_state(**kwargs: object) -> None:
        observed.append(bridge._engine._cancel.is_set())  # noqa: SLF001 - flag under test
        seen.set()

    source = io.StringIO(
        '{"id":"j1","type":"start","playlist_id":"p","output_dir":"out"}\n'
        '{"id":"c","type":"cancel"}\n'
        '{"id":"j2","type":"start","playlist_id":"p","output_dir":"out"}\n'
    )
    target = io.StringIO()
    bridge = Bridge(source, target)
    bridge._engine.ensure_startable = lambda *args, **kwargs: None  # type: ignore[method-assign]
    bridge._engine.download = record_cancel_state  # type: ignore[method-assign]

    bridge.run()
    deadline = time.monotonic() + 5
    while len(observed) < 2 and time.monotonic() < deadline:
        time.sleep(0.02)

    assert len(observed) == 2, observed
    assert observed[1] is False, "the second job started with the previous cancel still armed"
