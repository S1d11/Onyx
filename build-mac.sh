#!/bin/bash
# Build Onyx for macOS and package as a .dmg installer
# Run this on a Mac with Xcode and .NET MAUI workloads installed
#
# Prerequisites:
#   dotnet workload install maui-maccatalyst
#   brew install create-dmg  (optional, for prettier DMG)

set -e

APP_VERSION="${1:-2.17.1}"
CONFIG="${2:-Release}"

echo "==> Building Onyx v${APP_VERSION} for macOS"

# Clean
dotnet clean src/Onyx.Mac/Onyx.Mac.csproj -f net10.0-maccatalyst -c "$CONFIG"

# Publish
dotnet publish src/Onyx.Mac/Onyx.Mac.csproj \
  -f net10.0-maccatalyst \
  -c "$CONFIG" \
  -p:Version="$APP_VERSION" \
  -p:ApplicationDisplayVersion="$APP_VERSION" \
  -o "artifacts/mac"

APP_BUNDLE="artifacts/mac/Onyx.app"
if [ ! -d "$APP_BUNDLE" ]; then
    echo "ERROR: App bundle not found at $APP_BUNDLE"
    exit 1
fi

echo "==> App bundle created: $APP_BUNDLE"

# Sign with ad-hoc signature (required for Gatekeeper on macOS 10.15+)
if command -v codesign &> /dev/null; then
    codesign --force --deep --sign - "$APP_BUNDLE"
    echo "==> Signed app bundle"
fi

# Create DMG
DMG_NAME="Onyx-${APP_VERSION}-macOS.dmg"
DMG_PATH="artifacts/${DMG_NAME}"

rm -f "$DMG_PATH"

if command -v create-dmg &> /dev/null; then
    create-dmg \
        --volname "Onyx Installer" \
        --window-size 800 400 \
        --icon-size 100 \
        --app-drop-link 600 185 \
        --icon "Onyx.app" 200 185 \
        "$DMG_PATH" \
        "$APP_BUNDLE"
else
    # Fallback: simple hdiutil
    TEMP_DMG="artifacts/temp.dmg"
    hdiutil create -srcfolder "$APP_BUNDLE" -volname "Onyx" -fs HFS+ \
        -format UDRW -size 100m "$TEMP_DMG"
    hdiutil convert "$TEMP_DMG" -format UDZO -o "$DMG_PATH"
    rm -f "$TEMP_DMG"
fi

echo "==> DMG created: $DMG_PATH"
echo "==> Done"
