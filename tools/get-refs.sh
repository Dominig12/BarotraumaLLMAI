#!/usr/bin/env bash
# Downloads the publicized Barotrauma/LuaCs reference DLLs into Refs/ (needed to build the mod).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
URL="${LUACS_REFS_URL:-https://github.com/evilfactory/LuaCsForBarotrauma/releases/download/latest/luacsforbarotrauma_refs.zip}"
TMP="$(mktemp -d)"
echo "Downloading $URL"
curl -fsSL -o "$TMP/refs.zip" "$URL"
mkdir -p "$ROOT/Refs"
unzip -q -o "$TMP/refs.zip" -d "$ROOT/Refs"
rm -rf "$TMP"
test -f "$ROOT/Refs/Linux/DedicatedServer.dll" && echo "Refs ready in $ROOT/Refs"
