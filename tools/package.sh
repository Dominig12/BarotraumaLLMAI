#!/usr/bin/env bash
# Builds the mod (server plugin + optional client settings window) for every platform and assembles a ready-to-copy package in dist/LlmAgents.
#   tools/package.sh                      -> dist/LlmAgents
#   tools/package.sh /path/to/LocalMods   -> also copies it to /path/to/LocalMods/LlmAgents (keeps an existing llm_config.json)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/dist/LlmAgents"
rm -rf "$OUT"; mkdir -p "$OUT"
cp -r "$ROOT"/Content/. "$OUT"/
for P in Windows Linux OSX; do
  dotnet build "$ROOT/src/LlmAgents.Server/LlmAgents.Server.csproj" -c Release -p:GamePlatform=$P -v q -nologo
  mkdir -p "$OUT/bin/Server/$P"
  cp "$ROOT/src/LlmAgents.Server/bin/Release/$P/LlmAgents.dll" "$OUT/bin/Server/$P/"
  # optional client part: the in-game settings window
  dotnet build "$ROOT/src/LlmAgents.Client/LlmAgents.Client.csproj" -c Release -p:GamePlatform=$P -v q -nologo
  mkdir -p "$OUT/bin/Client/$P"
  cp "$ROOT/src/LlmAgents.Client/bin/Release/$P/LlmAgentsClient.dll" "$OUT/bin/Client/$P/"
done
echo "Package: $OUT"
if [ $# -ge 1 ]; then
  DEST="$1/LlmAgents"
  mkdir -p "$DEST"
  [ -f "$DEST/llm_config.json" ] && cp "$DEST/llm_config.json" "$OUT/llm_config.json.keep"
  cp -r "$OUT"/. "$DEST"/
  [ -f "$OUT/llm_config.json.keep" ] && mv "$DEST/llm_config.json.keep" "$DEST/llm_config.json" && rm -f "$OUT/llm_config.json.keep"
  echo "Deployed to $DEST"
fi
