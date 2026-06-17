#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP="$ROOT/build/CodexDesktopMenu.app"
CONTENTS="$APP/Contents"
MACOS="$CONTENTS/MacOS"
RESOURCES="$CONTENTS/Resources"

rm -rf "$APP"
mkdir -p "$MACOS" "$RESOURCES"

swiftc -O "$ROOT/switcher/claude-oauth-post.swift" \
  -o "$RESOURCES/claude-oauth-post"

install -m 755 "$ROOT/switcher/codex-desktop-switch" \
  "$RESOURCES/codex-desktop-switch"

swiftc -O "$ROOT/menu/Sources/main.swift" \
  -framework AppKit \
  -o "$MACOS/CodexDesktopMenu"

install -m 644 "$ROOT/menu/Info.plist.template" \
  "$CONTENTS/Info.plist"

echo "Built: $APP"
