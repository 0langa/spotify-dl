import io
import sys


def _force_utf8(stream: object) -> None:
    # Piped stdio on Windows defaults to the locale code page (e.g. cp1252),
    # which corrupts non-ASCII JSON for the UTF-8 reader on the app side.
    if isinstance(stream, io.TextIOWrapper):
        stream.reconfigure(encoding="utf-8", errors="replace")


# Must run before importing Bridge: spotdl's imports (colorama/rich) replace
# sys.stdout/sys.stderr with wrappers that hide the underlying TextIOWrapper.
_force_utf8(sys.stdin)
_force_utf8(sys.stdout)
_force_utf8(sys.stderr)

from playlistdl_backend.bridge import Bridge  # noqa: E402


def main() -> None:
    Bridge().run()


if __name__ == "__main__":
    main()
