# Codex Desktop Usage Switcher

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D6.svg)](#)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](#)

A **Windows-only system-tray app** (.NET 10 WinForms + WebView2) that switches
Codex Desktop accounts and shows Codex and Claude usage at a glance.

## Features

- **Safe account switching** — swaps `~/.codex/auth.json` from saved profiles
  with timestamped backups and rollback; refuses to switch while Codex Desktop
  is running.
- **Usage popup** — Codex and Claude 5-hour and weekly remaining %, reset
  countdown, and per-profile quota, with one-click profile switching.
- **Dashboard** — 14-day token bars, weekday × hour heatmap, per-turn stats,
  live 5h/weekly limit donuts, and estimated cost from an auto-updating model
  price table.
- **GUI logins** — in-app Claude OAuth (PKCE), plus terminal-based Codex and
  Claude Code logins.
- **Bilingual UI** — English / 한국어, switchable in Settings.

## Quick start (from source)

Requires the **.NET 10 SDK**. From the repo root:

```powershell
./build-windows.ps1          # build the single-file exe
./build-windows.ps1 -Test    # run the unit tests first, then build
```

This produces a single, self-contained executable (~56 MB, no .NET runtime
needed on the target machine) at:

```text
build/win-x64/CodexDesktopUsageSwitcher.Windows.exe
```

Double-click it to run. The app lives in the system tray — left-click for the
usage popup, right-click for the Open/Quit menu.

## More

- [English README](README.en.md)
- [한국어 README](README.ko.md)
- [SECURITY.md](SECURITY.md)

## License

MIT © 2026 JHK24. See [LICENSE](LICENSE).
