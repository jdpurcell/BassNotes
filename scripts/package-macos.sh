#!/usr/bin/env bash
set -euo pipefail

cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
configuration="${1:-Debug}"
dotnet build BassNotes.csproj -c "$configuration"
payload="bin/$configuration/net10.0"
bundle="bin/$configuration/BassNotes.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
cp -R "$payload/." "$bundle/Contents/MacOS/"
cp assets/macos/Info.plist "$bundle/Contents/Info.plist"
cp assets/macos/AppIcon.icns "$bundle/Contents/Resources/AppIcon.icns"
codesign --force --deep --sign - "$bundle"
printf 'Created %s/%s\n' "$PWD" "$bundle"
