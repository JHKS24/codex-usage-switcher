# AGENTS.md

Guidance for AI coding agents (Claude Code, Codex CLI, etc.) working in this
repository. This is **Codex Desktop Usage Switcher** — a Windows-only system-tray
app (.NET 10 WinForms + WebView2). There is **no CLI, no macOS app, and no
Python** anymore; everything is native C# in-app. Logins and account switches are
**user-driven GUI actions**, not things an agent runs in the background.

For full context see [README.en.md](README.en.md), [README.ko.md](README.ko.md),
and [SECURITY.md](SECURITY.md).

---

## Golden security rules (read first)

1. **Never print, echo, log, or display token values.** Do not read or surface
   the contents of `auth.json`, `credentials.json`, or any OAuth code/token. The
   app is built to never reveal tokens — keep it that way. `auth.json` /
   credentials are copied atomically with timestamped backups, never logged.
2. **OAuth login requires a human + a browser.** The Claude usage login is an
   in-app OAuth (PKCE) flow: a browser opens and the **user** signs in and pastes
   a one-time code into a dialog. An agent cannot complete a login on its own.
3. **An account switch requires Codex Desktop to be fully quit.** Switching swaps
   `~/.codex/auth.json` from a saved profile under `~/.codex-switch/profiles`. The
   app refuses to switch while Codex Desktop or its app-server is running, and
   refuses if it cannot inspect running processes. Do not run process cleanup as a
   background task.
4. **Logins and switches are user-driven GUI actions.** They happen when the user
   clicks in the tray popup / Settings window — never as an automated agent step.
5. **Secrets live outside the repo.** Real `~/.codex`, `~/.codex-switch`, and
   `~/.config/claude-usage-bar` are never committed.

---

## Working in this repo

- **Stack:** .NET 10 WinForms + WebView2. The single build entry point is
  `./build-windows.ps1` at the repo root, which produces a self-contained exe at
  `build/win-x64/CodexDesktopUsageSwitcher.Windows.exe`. Helper scripts live under
  `scripts/` (install/package/test); `./build-windows.ps1` is the one you invoke.
- **Build:** run `./build-windows.ps1` (needs the .NET 10 SDK). Requires Windows
  10 1809+ (10.0.17763) and the Edge WebView2 runtime to run.
- **Test:** run `dotnet test`, or `./build-windows.ps1 -Test` to run the unit
  tests before building.
- **Localization:** the UI is bilingual (English / 한국어). All user-facing
  strings go through the `Localizer` (EN/KO) — never hardcode display text. The
  language defaults to the system language and the user's choice persists; there
  is a language switcher in Settings.

---

## License & credits

MIT, (c) 2026 JHK24. Repo:
<https://github.com/JHKS24/codex-desktop-usage-switcher>.

Keep crediting the upstream open-source projects (see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)): usage-insights logic ported
from **codex-usage-monitor** (MIT), the Claude OAuth usage approach follows
**claude-usage-bar**, plus the upstream **cdx** lineage.
