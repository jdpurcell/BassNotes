#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."

case "$(uname -m)" in
    arm64) rid=osx-arm64 ;;
    x86_64) rid=osx-x64 ;;
    *) echo "Unsupported macOS architecture." >&2; exit 1 ;;
esac

identity="${APPLE_DEVID_APP_CERT_NAME:-}"
if [[ -n "$identity" ]]; then
    : "${APPLE_ID_USER:?Set APPLE_ID_USER for notarization.}"
    : "${APPLE_ID_PASS:?Set APPLE_ID_PASS to an Apple app-specific password.}"
    if [[ "$identity" =~ \(([A-Z0-9]{10})\)$ ]]; then
        team_id="${BASH_REMATCH[1]}"
    else
        echo "Cannot read the Apple team ID from the signing certificate name." >&2
        exit 1
    fi
fi

version=$(dotnet msbuild BassNotes.csproj -nologo -getProperty:Version)
dotnet publish BassNotes.csproj -c Release -r "$rid" --self-contained true -p:DebugType=None

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
app="$work/BassNotes.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" dist
cp -R "bin/Release/net10.0/$rid/publish/." "$app/Contents/MacOS/"
cp assets/macos/Info.plist "$app/Contents/Info.plist"
cp assets/macos/AppIcon.icns "$app/Contents/Resources/AppIcon.icns"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $version" "$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $version" "$app/Contents/Info.plist"

if [[ -n "$identity" ]]; then
    # Sign native dependencies before sealing the outer bundle.
    while IFS= read -r -d '' binary; do
        if [[ "$(file -b "$binary")" == Mach-O* ]]; then
            codesign --force --sign "$identity" --options runtime --timestamp \
                --entitlements assets/macos/Entitlements.plist "$binary"
        fi
    done < <(find "$app/Contents/MacOS" -type f -print0)
    codesign --force --sign "$identity" --options runtime --timestamp \
        --entitlements assets/macos/Entitlements.plist "$app"
else
    codesign --force --deep --sign - "$app"
fi
codesign --verify --deep --strict "$app"

dmg="dist/BassNotes-$version-macOS-${rid#osx-}.dmg"
rm -f "$dmg"
hdiutil create -volname BassNotes -srcfolder "$app" -format UDZO "$dmg"

if [[ -n "$identity" ]]; then
    codesign --sign "$identity" --timestamp --identifier com.jdp.BassNotes.dmg "$dmg"
    response="$work/notarization.json"
    if ! xcrun notarytool submit "$dmg" --apple-id "$APPLE_ID_USER" \
        --password "$APPLE_ID_PASS" --team-id "$team_id" --wait --output-format json > "$response"; then
        cat "$response"
        exit 1
    fi
    status=$(plutil -extract status raw -o - "$response")
    if [[ "$status" != Accepted ]]; then
        cat "$response"
        submission_id=$(plutil -extract id raw -o - "$response")
        xcrun notarytool log "$submission_id" --apple-id "$APPLE_ID_USER" \
            --password "$APPLE_ID_PASS" --team-id "$team_id" || true
        exit 1
    fi
    # Only the DMG is stapled
    xcrun stapler staple "$dmg"
    xcrun stapler validate "$dmg"
fi

printf 'Created %s\n' "$dmg"
