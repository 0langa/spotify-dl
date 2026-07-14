# Security

Report vulnerabilities privately through GitHub Security Advisories when available. Do not include cookies, tokens, downloaded media, or personal logs in public issues.

Playlist DL validates hashes for embedded helper tools before execution. Release builds include SHA-256 checksums for every published asset and run Microsoft Defender when its CLI is available. Releases remain unsigned, so Windows SmartScreen may warn about an unknown publisher.

spotDL 4.5.0 pins an obsolete FastAPI/Starlette web-server stack. Playlist DL does not start that server, excludes FastAPI, Starlette, and Uvicorn from frozen builds, and fails frozen-runtime smoke verification if any become bundled. Dependency audit exceptions cover only advisories in those excluded modules; every other known advisory fails CI.
