#!/bin/sh
# Собирает архив плагина для релиза: dist/nc-share-xerahs.zip, файлы в корне архива.
# Имя без версии, чтобы установщик брал его по ссылке releases/latest/download/nc-share-xerahs.zip.
# Библиотеки XerahS — из /Applications/XerahS.app; другой путь: XERAHS_DIR=<…>/XerahS.app/Contents/MacOS ./pack.sh
set -e
cd "$(dirname "$0")"
dir="${XERAHS_DIR:-/Applications/XerahS.app/Contents/MacOS}"
dotnet build -c Release -nologo -v quiet -p:XerahSDir="$dir"

out=bin/Release/net10.0
dist=../dist
rm -rf "$dist" && mkdir -p "$dist"
zip -j -q "$dist/nc-share-xerahs.zip" "$out/NcShare.Plugin.dll" "$out/NcShare.Plugin.pdb" "$out/NcShare.Plugin.deps.json" \
    "$out/NcShare.Plugin.runtimeconfig.json" plugin.json
version=$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' plugin.json)
echo "Собрано: dist/nc-share-xerahs.zip (nc-share $version)"
