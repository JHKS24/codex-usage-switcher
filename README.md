# Codex Desktop Usage Switcher

Local helper for switching Codex Desktop profiles and showing AI tool usage.
macOS uses a menu bar app; Windows uses a tray-icon app with a custom usage
popup.

## Quick Start

### Windows — one-click install on another PC

Build `dist\CodexDesktopUsageSwitcher-win-x64.zip` once with
`.\scripts\package-windows.ps1 -SelfContained`, copy it to the target PC,
unzip, and double-click `install.cmd`. No .NET runtime needed; Python 3 is
checked (winget install offered) by the installer.

### From source

Windows:

```powershell
git clone https://github.com/cogusrlchg-wq/codex-desktop-usage-switcher.git
cd codex-desktop-usage-switcher
.\scripts\install-local-windows.ps1
```

Run:

```powershell
& "$env:LOCALAPPDATA\CodexDesktopUsageSwitcher\CodexDesktopUsageSwitcher.Windows.exe"
```

macOS:

```bash
git clone https://github.com/cogusrlchg-wq/codex-desktop-usage-switcher.git && cd codex-desktop-usage-switcher && ./scripts/install-local.sh
```

Then open it:

```bash
open "$HOME/Applications/CodexDesktopMenu.app"
```

The Windows installer builds the tray app, copies it under
`%LOCALAPPDATA%\CodexDesktopUsageSwitcher`, adds a Start Menu shortcut, and
creates `%USERPROFILE%\bin\codex-desktop-switch.cmd`.

The macOS installer builds the menu app and creates `~/bin/codex-desktop-switch`.
Make sure the relevant `bin` folder is in your shell `PATH`.

- [English README](README.en.md)
- [한국어 README](README.ko.md)
- [AGENTS.md](AGENTS.md) — let an AI agent (Claude / Codex) add accounts for you

![Anonymized menu screenshot](assets/menu-screenshot-redacted.svg)

## Windows Package

Create a local distributable ZIP without PDB files, local install paths, or user
state:

```powershell
.\scripts\package-windows.ps1 -SelfContained
```

Output:

```text
dist\CodexDesktopUsageSwitcher-win-x64.zip
```

## Menu Behavior

- On Windows, the app stays in the system tray by default. Left-click or
  right-click the tray icon to open the custom usage popup.
- The tray notification area can show multiple live number icons. In `설정`,
  toggle up to six taskbar indicators: Codex 5H / Week, CodexSub 5H / Week,
  and Claude 5H / Week.
- The popup shows the active Codex profile plus current 5H / Week remaining
  quota immediately.
- Click `새로고침` in the popup to refresh values in place. The popup stays open.
- Click a profile name to select it, then click `Switch profile` (or
  double-click the profile row) to switch accounts. The switcher asks for
  confirmation, quits active Codex sessions first, applies
  `use <profile> --apply`, and opens Codex again.
- Open `설정` from the Windows popup to add a Codex profile, save the current
  Codex login as a profile, open Claude usage login, open Claude Code login, run
  Doctor, or open the profiles folder.
- On Windows, `Claude 로그인` opens a visible terminal window. Finish the browser
  authorization there, then paste only the OAuth code into that terminal.
- Codex and Claude usage are shown as **remaining quota**, so warning icons
  appear at `20%` or less. Claude remaining quota is calculated as
  `100 - current Claude utilization`.
- Profile names in the screenshot are anonymized examples.

## Security First

- Do not commit `auth.json`, `credentials.json`, profile folders, backups,
  sessions, logs, or SQLite state.
- This tool depends on local Codex Desktop and the `codex` CLI. On Windows, set
  `CODEX_DESKTOP_APP_PATH` if Codex.exe is installed outside the common
  locations, and set `CODEX_CLI_PATH` if `codex` is not on `PATH`.
- Review [SECURITY.md](SECURITY.md) before publishing or sharing a fork.
- Codex and Claude usage endpoints are unofficial and may change without notice.

## Credits

The per-profile model and the `import-cdx` migration path are inspired by
[ezpzai/cdx](https://github.com/ezpzai/cdx) (Apache-2.0). This project is an
independent local menu/tray implementation, not a fork.

The usage-insights layer — the dashboard charts, the Codex/Claude transcript
parsing, and the usage/cost calculations — is a C# port of
[codex-usage-monitor](https://github.com/kimbyungsu/codex-usage-monitor) (MIT).
Claude usage via OAuth follows the local credential format and endpoints of
**claude-usage-bar**. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the
full upstream license texts.
