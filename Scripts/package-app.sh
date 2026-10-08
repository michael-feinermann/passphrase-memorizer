#!/bin/bash
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
APP_PATH="$PROJECT_DIR/build/Passphrase Memorizer.app"
ZIP_PATH="$PROJECT_DIR/build/Passphrase-Memorizer-1.0.1.zip"
DEVELOPMENT=0
if [[ "$#" == 1 && "$1" == "--dev" ]]; then DEVELOPMENT=1
elif [[ "$#" != 0 ]]; then echo "Usage: $0 [--dev]" >&2; exit 64
fi
SIGN_IDENTITY="${SIGN_IDENTITY:--}"
NOTARY_PROFILE="${NOTARY_PROFILE:-}"
if [[ "$SIGN_IDENTITY" == "-" && "$DEVELOPMENT" != 1 ]]; then
    echo "Set SIGN_IDENTITY to a Developer ID Application identity, or explicitly use --dev." >&2
    exit 1
fi
if [[ -n "$NOTARY_PROFILE" && ( "$SIGN_IDENTITY" == "-" || "$DEVELOPMENT" == 1 ) ]]; then
    echo "Notarization requires a production Developer ID identity." >&2
    exit 1
fi
if [[ "$(uname -s)" != "Darwin" ]]; then echo "macOS is required." >&2; exit 1; fi
ICON_SOURCE="$PROJECT_DIR/Assets/AppIcon-1024.png"
[[ -f "$ICON_SOURCE" ]] || { echo "The supplied app icon is missing: $ICON_SOURCE" >&2; exit 1; }

cd "$PROJECT_DIR"
swift build -c release -Xswiftc -warnings-as-errors --product MnemonicStoryApp
# No network fetch is implicit. Prepare the pinned source with --fetch first.
"$PROJECT_DIR/Scripts/build-local-ai.sh"
BIN_DIR="$(swift build -c release --show-bin-path)"
RUNNER_PATH="$PROJECT_DIR/.build/local-ai/LocalMnemonicRunner"
[[ -x "$BIN_DIR/MnemonicStoryApp" && -x "$RUNNER_PATH" ]] || { echo "Release binaries are missing." >&2; exit 1; }
[[ "$APP_PATH" == "$PROJECT_DIR/build/Passphrase Memorizer.app" ]] || exit 1
mkdir -p "$PROJECT_DIR/build"
rm -rf "$APP_PATH"
rm -f "$ZIP_PATH" "$ZIP_PATH.sha256"
mkdir -p "$APP_PATH/Contents/MacOS" "$APP_PATH/Contents/Helpers" "$APP_PATH/Contents/Resources/Licenses"
install -m 755 "$BIN_DIR/MnemonicStoryApp" "$APP_PATH/Contents/MacOS/MnemonicStoryApp"
install -m 755 "$RUNNER_PATH" "$APP_PATH/Contents/Helpers/LocalMnemonicRunner"
install -m 644 "$PROJECT_DIR/Config/Info.plist" "$APP_PATH/Contents/Info.plist"
for resource in english.txt eff_large_wordlist.txt; do
    install -m 644 "$PROJECT_DIR/Sources/MnemonicStoryCore/Resources/$resource" "$APP_PATH/Contents/Resources/$resource"
done
for notice in LICENSE THIRD_PARTY_NOTICES.md; do
    install -m 644 "$PROJECT_DIR/$notice" "$APP_PATH/Contents/Resources/$notice"
done
for notice in "$PROJECT_DIR/Licenses/"* "$PROJECT_DIR/.build/local-ai/licenses/"*; do
    [[ -f "$notice" ]] || { echo "Dependency license files are missing." >&2; exit 1; }
    install -m 644 "$notice" "$APP_PATH/Contents/Resources/Licenses/$(basename "$notice")"
done
ICONSET_PATH="$PROJECT_DIR/build/AppIcon.iconset"
rm -rf "$ICONSET_PATH"
mkdir -p "$ICONSET_PATH"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$ICON_SOURCE" --out "$ICONSET_PATH/icon_${size}x${size}.png" >/dev/null
    doubled=$((size * 2))
    sips -z "$doubled" "$doubled" "$ICON_SOURCE" --out "$ICONSET_PATH/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET_PATH" -o "$APP_PATH/Contents/Resources/AppIcon.icns"

for binary in "$APP_PATH/Contents/MacOS/MnemonicStoryApp" "$APP_PATH/Contents/Helpers/LocalMnemonicRunner"; do
    # Remove SwiftPM/CMake build-machine paths before signing. No external
    # libraries or plugins are permitted in this native bundle.
    while IFS= read -r rpath; do
        [[ -z "$rpath" || "$rpath" == "/usr/lib/swift" ]] || install_name_tool -delete_rpath "$rpath" "$binary"
    done < <(otool -l "$binary" | awk '/cmd LC_RPATH/{nextpath=1;next} nextpath && /path /{print $2;nextpath=0}' | sort -u)
    unexpected="$(otool -L "$binary" | awk 'NR>1{print $1}' | awk '$0 !~ "^/usr/lib/" && $0 !~ "^/System/Library/"')"
    [[ -z "$unexpected" ]] || { echo "A binary has an external runtime dependency: $unexpected" >&2; exit 1; }
done

TIMESTAMP_ARGUMENT="--timestamp"
[[ "$SIGN_IDENTITY" != "-" ]] || TIMESTAMP_ARGUMENT="--timestamp=none"
# No App Sandbox entitlement: the new helper applies its own stricter Seatbelt
# policy before receiving secrets. Inherited App Sandbox cannot be nested.
codesign --force --options runtime --identifier local.passphrasereminder.reminder.runner \
    --sign "$SIGN_IDENTITY" "$TIMESTAMP_ARGUMENT" "$APP_PATH/Contents/Helpers/LocalMnemonicRunner"
codesign --force --options runtime --identifier local.passphrasereminder.reminder \
    --sign "$SIGN_IDENTITY" "$TIMESTAMP_ARGUMENT" "$APP_PATH"
codesign --verify --deep --strict --verbose=2 "$APP_PATH"
plutil -lint "$APP_PATH/Contents/Info.plist"
ditto -c -k --sequesterRsrc --keepParent "$APP_PATH" "$ZIP_PATH"
if [[ -n "$NOTARY_PROFILE" ]]; then
    # Credentials remain in the named Keychain profile. No key or password is exported.
    xcrun notarytool submit "$ZIP_PATH" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$APP_PATH"
    xcrun stapler validate "$APP_PATH"
    rm -f "$ZIP_PATH"
    ditto -c -k --sequesterRsrc --keepParent "$APP_PATH" "$ZIP_PATH"
fi
(cd "$PROJECT_DIR/build" && shasum -a 256 "$(basename "$ZIP_PATH")" > "$(basename "$ZIP_PATH").sha256")
if [[ "$DEVELOPMENT" == 1 ]]; then
    ALLOW_UNNOTARIZED_DEVELOPMENT=1 "$PROJECT_DIR/Scripts/verify-release.sh" "$APP_PATH" "$ZIP_PATH"
elif [[ -n "$NOTARY_PROFILE" ]]; then
    "$PROJECT_DIR/Scripts/verify-release.sh" "$APP_PATH" "$ZIP_PATH"
else
    echo "Signed candidate only. Complete notarization and run verify-release.sh before publication."
fi
echo "App: $APP_PATH"
echo "Archive: $ZIP_PATH"
echo "SHA-256 sidecar: $ZIP_PATH.sha256"
