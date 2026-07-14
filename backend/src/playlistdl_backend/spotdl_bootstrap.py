"""Adapt spotDL imports to Playlist DL's intentionally headless runtime."""

from __future__ import annotations

import sys
import types


def install_console_stub() -> bool:
    """Prevent spotDL from eagerly importing its unused web-console stack."""
    if "spotdl" in sys.modules or "spotdl.console" in sys.modules:
        return False

    console_stub = types.ModuleType("spotdl.console")

    def unsupported_console_entry_point() -> None:
        raise RuntimeError("The spotDL console is not available in Playlist DL.")

    console_stub.console_entry_point = unsupported_console_entry_point  # type: ignore[attr-defined]
    sys.modules["spotdl.console"] = console_stub
    return True


install_console_stub()
