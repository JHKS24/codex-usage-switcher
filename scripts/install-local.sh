#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="$ROOT/build/CodexDesktopMenu.app"
TARGET="$HOME/Applications/CodexDesktopMenu.app"
BIN_DIR="$HOME/bin"
CLI_LINK="$BIN_DIR/codex-desktop-switch"

if [[ ! -d "$APP" ]]; then
  "$ROOT/scripts/build.sh"
fi

mkdir -p "$HOME/Applications"
rm -rf "$TARGET"
cp -R "$APP" "$TARGET"
mkdir -p "$BIN_DIR"
ln -sfn "$TARGET/Contents/Resources/codex-desktop-switch" "$CLI_LINK"
echo "Installed: $TARGET"
echo "Open with: open \"$TARGET\""
echo "CLI shortcut: $CLI_LINK"
echo "Make sure \"$BIN_DIR\" is in your PATH."
