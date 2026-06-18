# Codex Desktop Usage Switcher

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D6.svg)](#requirements)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](#build--run-from-source)

A **Windows-only system-tray app** that switches Codex Desktop accounts and
shows Codex and Claude usage. It is a native C# application built on
**.NET 10 WinForms + WebView2** — there is no macOS app, no Python, and no
command-line tool.

## What it does

### Switch Codex Desktop accounts safely

- Switches accounts/profiles by swapping `~/.codex/auth.json` from saved
  profiles stored under `~/.codex-switch/profiles`.
- Every switch makes a **timestamped backup** and supports **rollback**.
- It **refuses to switch while Codex Desktop or its app-server is running**, and
  refuses if it cannot inspect the running processes — so a switch never
  corrupts a live session.

### Usage popup

Left-click the tray icon for a popup that shows Codex and Claude usage side by
side:

- 5-hour and weekly **remaining %** for both providers.
- **Reset countdown** to the next limit window.
- **Per-profile quota**.
- A one-click **switch** for the selected profile.

### Dashboard window

- **14-day, model-stacked token bars**.
- **Weekday × hour heatmap** of activity.
- **Per-turn token statistics**.
- **Live 5h / weekly limit donuts** for both Codex and Claude.
- An **estimated-cost summary** computed from an auto-updating model price
  table. Pricing uses official OpenAI / Anthropic rates from a bundled snapshot
  that refreshes periodically; unknown models are left unpriced.

### Logins (all GUI actions)

- **Claude usage login** — an in-app **OAuth (PKCE)** flow: a browser opens, and
  you paste the one-time code back into a dialog.
- **Codex profile login** and **Claude Code login** — open a terminal that runs
  the respective CLI.
- You can also **save the current Codex login** into a profile.

### Bilingual UI

English / 한국어, with a language switcher in **Settings**. The UI defaults to
the system language and persists your choice.

## Build & run (from source)

Requires the **.NET 10 SDK**. From the repo root:

```powershell
./build-windows.ps1          # build the single-file exe
./build-windows.ps1 -Test    # run the unit tests first, then build
```

This produces a single, **self-contained** executable (~56 MB) that needs
neither the .NET runtime nor Python on the target machine:

```text
build/win-x64/CodexDesktopUsageSwitcher.Windows.exe
```

Double-click the exe to run. It lives in the **system tray**:

- **Left-click** the tray icon → usage popup.
- **Right-click** the tray icon → Open / Quit menu.

Helper scripts live under `scripts/` (`install-local-windows.ps1`,
`package-windows.ps1`, `test-windows.ps1`, and `installer/install.cmd` /
`installer/install.ps1`). The single build entry point is `./build-windows.ps1`
at the repo root.

## Requirements

- **Windows 10 1809+** (10.0.17763) or Windows 11.
- The **Microsoft Edge WebView2 runtime** (preinstalled on current
  Windows 10/11).
- For **Codex profile login**: Codex Desktop and the `codex` CLI. Set the
  `CODEX_CLI_PATH` environment variable if `codex` is not on `PATH`.
- For **Claude Code login**: the Claude Code `claude` CLI.

## Security

- The app **never prints, logs, or displays token contents**.
- `auth.json` / credentials are copied **atomically, with backups**.
- An account switch **requires Codex Desktop to be fully quit**.
- OAuth always requires a **human and a browser**.
- Your real `~/.codex`, `~/.codex-switch`, and
  `~/.config/claude-usage-bar` live **outside the repo and are never
  committed**.

See [SECURITY.md](SECURITY.md) for details.

## Repository & license

- Repo: <https://github.com/JHKS24/codex-desktop-usage-switcher>
- License: **MIT** © 2026 JHK24. See [LICENSE](LICENSE).

## Credits

This project builds on the work of several upstream open-source projects:

- The **usage-insights** logic — the dashboard charts, the Codex/Claude
  transcript parsing, and the usage/cost calculations — is ported from
  [codex-usage-monitor](https://github.com/kimbyungsu/codex-usage-monitor)
  (MIT).
- The **Claude OAuth usage** approach follows **claude-usage-bar**.
- The per-profile model and the `import-cdx` migration path follow the upstream
  [ezpzai/cdx](https://github.com/ezpzai/cdx) (Apache-2.0) lineage. This project
  is an independent implementation, not a fork.

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the full upstream
license texts.
