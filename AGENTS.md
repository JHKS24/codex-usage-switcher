# AGENTS.md

Guidance for AI coding agents (Claude Code, Codex CLI, etc.) operating in this
repository **and** for end users who want to drive this tool through an agent by
saying things like *"add a Codex account"* or *"register my Claude login"*.

This file is intentionally short and command-oriented so an agent can act on it
directly. It does not override the human-facing docs ([README.ko.md](README.ko.md),
[README.en.md](README.en.md), [SECURITY.md](SECURITY.md)) — read those for full
context.

---

## Golden rules (read first)

1. **Never print, echo, log, or paste token values.** Do not `cat`, read, or
   display `auth.json`, `credentials.json`, or any OAuth code/token. The CLI is
   designed to never reveal tokens — keep it that way.
2. **OAuth login requires a human + browser.** You (the agent) cannot complete a
   login alone. You run the command; a browser opens; the **user** signs in and
   (for Claude) copies a one-time code back to you.
3. **Direct CLI account switching requires Codex Desktop to be fully quit.**
   `use --apply` refuses to switch while Codex Desktop or its `app-server` is
   running. The menu app may call `stop-codex` only after the user explicitly
   clicks a profile to switch; do not run process cleanup as a background task.
4. **`use` is dry-run by default.** A real account switch only happens with
   `--apply`.
5. **One-time codes are single-use.** If a Claude OAuth code fails (stale /
   rate-limited), do **not** retry the same code. Run `claude-login-reset`, then
   start a fresh login.

---

## Resolving the CLI path

Prefer the command on `PATH`:

```bash
codex-desktop-switch <subcommand> ...
```

If it is not on `PATH`, call it from the installed app bundle:

```bash
"$HOME/Applications/CodexDesktopMenu.app/Contents/Resources/codex-desktop-switch" <subcommand> ...
```

(Or from a fresh build: `./build/CodexDesktopMenu.app/Contents/Resources/codex-desktop-switch`.)

On Windows after local install, prefer:

```powershell
& "$HOME\bin\codex-desktop-switch.cmd" <subcommand> ...
```

Or call the bundled script from the tray-app install:

```powershell
python "$env:LOCALAPPDATA\CodexDesktopUsageSwitcher\codex-desktop-switch.py" <subcommand> ...
```

A quick check that the CLI is reachable and working:

```bash
codex-desktop-switch list --json
```

---

## Task: "Add a Codex account" / "Codex 아이디 추가"

When the user asks to add a new Codex profile/account named e.g. `work`:

1. Run:
   ```bash
   codex-desktop-switch login work
   ```
   This creates `~/.codex-switch/profiles/work/` and launches the Codex browser
   login scoped to that profile.
2. Tell the user a browser window opened and ask them to finish signing in there.
3. Confirm it landed:
   ```bash
   codex-desktop-switch list
   ```
4. (Optional) To make `work` the **active** Codex Desktop account, ensure Codex
   Desktop is quit, then:
   ```bash
   codex-desktop-switch use work --apply
   ```

Notes:
- Profile names must match `^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$`.
- `login <name>` on an existing profile re-authenticates it; it does not
  duplicate.
- Requires Codex Desktop and the `codex` CLI. On Windows, set `CODEX_CLI_PATH`
  if the CLI is not on `PATH`.

## Task: "Switch Codex account" / "계정 전환"

```bash
codex-desktop-switch current          # show active profile
codex-desktop-switch use work              # dry-run preview
codex-desktop-switch use work --apply      # actually switch (Codex Desktop must be quit)
codex-desktop-switch stop-codex --json     # explicit cleanup helper; may terminate sessions
```

Only use `stop-codex` when the user asked to switch/clean up Codex processes. It
can terminate open Codex app, app-server, and tool-session processes.

## Task: "Register Claude" / "Claude 아이디 등록" (for usage display)

Claude login is **optional** and only powers the Claude 5H / Week usage display.
On Windows tray UI, `Claude 로그인` should open a visible terminal and run the
interactive `codex-desktop-switch claude-login` flow so the user has a prompt for
the one-time OAuth code.
The Windows tray `설정...` window should expose Codex profile login/save actions
through visible terminal flows; do not require users to type those commands
manually for normal setup.
Current Windows UI is tray-icon-only by default, with a custom popup opened from
left-click or right-click rather than the default WinForms tray context menu. Keep
refresh in-place so the popup does not close. Providers are Codex and Claude
usage only (Cursor and AGY integrations were removed in 2026-06). Claude Code is
a separate login action, not a provider/usage row.
Use the two-step, agent-friendly flow (not the interactive `claude-login`):

1. Start the flow — this opens the Claude OAuth page in the browser:
   ```bash
   codex-desktop-switch claude-login-start
   ```
2. Ask the user to authorize in the browser and paste the OAuth code into the
   visible terminal/stdin prompt, **not into chat** and not as a command-line
   argument.
3. Finish through stdin so the code does not appear in argv/history:
   ```bash
   codex-desktop-switch claude-login-finish --code-stdin
   ```
4. Verify:
   ```bash
   codex-desktop-switch claude-usage
   ```

If something is stuck (stale code, rate limit, state mismatch):

```bash
codex-desktop-switch claude-login-reset   # clears pending/cooldown, keeps credentials
```

To sign out of Claude entirely:

```bash
codex-desktop-switch claude-logout
```

Credentials are stored locally (Keychain / `~/.config/claude-usage-bar/`), never
in this repo.

---

## Command reference

| Command | What it does |
| --- | --- |
| `list [--json]` | List known Codex profiles |
| `current [--json]` | Show the active Codex profile |
| `usage [profile] [--json]` | Show Codex 5H / Week usage |
| `login <profile>` | Create/re-auth a Codex profile (browser) |
| `use <profile> [--apply]` | Preview / apply an account switch |
| `save <profile> [--apply] [--force]` | Save current `~/.codex/auth.json` into a profile |
| `restore <backup-id> [--apply]` | Restore a previous backup |
| `doctor [--json]` | Diagnose setup/permissions |
| `stop-codex [--json] [--grace-seconds N]` | Terminate leftover Codex processes for menu-switch recovery |
| `claude-login-start [--json]` | Begin Claude OAuth (opens browser) |
| `claude-login-finish <code> [--code-stdin] [--json]` | Complete Claude OAuth with the code |
| `claude-login-reset [--json]` | Clear stuck pending/cooldown state |
| `claude-logout` | Remove local Claude credentials |
| `claude-usage [--json]` | Show Claude 5H / Week usage |
| `import-cdx [profile] [--all] [--force] [--json]` | One-time migration from `cdx` profiles |

`import-cdx` is a one-time migration helper only; this tool does not depend on
`cdx` at runtime. See [Credits](README.en.md#credits).
