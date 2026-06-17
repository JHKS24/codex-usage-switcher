# Security

This project manipulates local authentication files. Treat it as a local-only
helper unless you have reviewed the code and understand the risks.

Do not commit or paste:

- `auth.json`
- OAuth credential files such as `credentials.json`
- profile folders, backups, sessions, logs, SQLite state, or token dumps
- screenshots or logs that include account identifiers or OAuth codes

The switcher copies local Codex `auth.json` files between profile folders. It
should never print token values. The menu app should call the switcher and must
not edit auth files directly.

`stop-codex` is local process control only. It does not read credentials, but it
can terminate open Codex app, app-server, and tool-session processes. Treat logs
from this command as potentially private because process command lines can reveal
local paths.

Codex and Claude usage endpoints used by this tool are unofficial and may stop
working. Treat usage failures as display failures, not as account-switching
failures.

For issues or pull requests, describe behavior without attaching credentials or
full command output that contains private paths or account data.

## Reporting Security Issues

If this repository is hosted on GitHub, prefer a private GitHub Security Advisory
for vulnerability reports. Do not open a public issue that contains tokens,
account identifiers, private paths, or OAuth codes.
