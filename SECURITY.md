# Security

Codex Desktop Usage Switcher is a Windows-native system-tray app that
manipulates local authentication files. Treat it as a local-only helper unless
you have reviewed the code and understand the risks.

## What the app guarantees

- **Tokens are never printed, logged, or displayed.** The app reads usage data
  and copies credential files, but it never renders token contents to the UI,
  the dashboard, logs, or any diagnostic output.
- **Credential files are copied atomically, with backups.** Switching a Codex
  account swaps `~/.codex/auth.json` from a saved profile under
  `~/.codex-switch/profiles`. Each switch writes a timestamped backup first and
  supports rollback, so a failed copy never leaves you with a half-written
  `auth.json`.
- **Switching requires Codex Desktop to be fully quit.** The app refuses to
  switch accounts while Codex Desktop or its app-server is running, and it also
  refuses if it cannot inspect the running processes to confirm that — it never
  guesses.
- **OAuth always needs a human and a browser.** The in-app Claude usage login is
  a PKCE OAuth flow: a browser opens and you paste the one-time code into a
  dialog. There is no headless or unattended login path. Codex and Claude Code
  CLI logins run in a terminal you can see.

## Never commit or paste

- `auth.json`
- OAuth credential files (for example `credentials.json`)
- profile folders, backups, sessions, logs, SQLite state, or token dumps
- screenshots or logs that include account identifiers or OAuth codes

Your real `~/.codex`, `~/.codex-switch`, and `~/.config/claude-usage-bar` live
outside the repository and are never committed.

## Usage endpoints

The Codex and Claude usage endpoints this app queries are unofficial and may
stop working. Treat usage failures as display failures, not as
account-switching failures.

When reporting issues or opening pull requests, describe behavior without
attaching credentials, OAuth codes, or full output that contains private paths
or account data.

## Reporting Security Issues

If this repository is hosted on GitHub, prefer a private GitHub Security
Advisory for vulnerability reports. Do not open a public issue that contains
tokens, account identifiers, private paths, or OAuth codes.
