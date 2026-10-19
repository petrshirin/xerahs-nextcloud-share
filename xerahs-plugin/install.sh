#!/bin/sh
# Собирает плагин и копирует его в папку плагинов XerahS. После установки XerahS нужно перезапустить.
set -e
cd "$(dirname "$0")"
dotnet build -c Release -nologo -v quiet

out=bin/Release/net10.0
# XerahS сам переносит плагины из Plugins/ в Plugins/macos64/, поэтому ставим сразу туда
dest="$HOME/Documents/XerahS/Plugins/macos64/ncshare"
mkdir -p "$dest"
cp "$out/NcShare.Plugin.dll" "$out/NcShare.Plugin.pdb" "$out/NcShare.Plugin.deps.json" \
   "$out/NcShare.Plugin.runtimeconfig.json" plugin.json "$dest/"
echo "Плагин установлен в $dest — перезапустите XerahS"
