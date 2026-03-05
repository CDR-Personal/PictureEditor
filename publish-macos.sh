#!/bin/bash
set -e

# Publish PictureEditor as a macOS .app bundle
# Usage: ./publish-macos.sh [arm64|x64]
#   arm64 = Apple Silicon (default)
#   x64   = Intel Mac

ARCH="${1:-arm64}"
RID="osx-${ARCH}"
PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)/PictureEditor"
PUBLISH_DIR="${PROJECT_DIR}/bin/publish/macos-${ARCH}"
APP_NAME="PictureEditor"
APP_BUNDLE="${PUBLISH_DIR}/${APP_NAME}.app"

echo "=== Publishing PictureEditor for macOS (${RID}) ==="

# Clean previous publish
rm -rf "${PUBLISH_DIR}"

# Publish self-contained
dotnet publish "${PROJECT_DIR}/PictureEditor.csproj" \
    -c Release \
    -r "${RID}" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PublishTrimmed=false \
    -o "${PUBLISH_DIR}/output"

echo "=== Creating .app bundle ==="

# Create .app bundle structure
mkdir -p "${APP_BUNDLE}/Contents/MacOS"
mkdir -p "${APP_BUNDLE}/Contents/Resources"

# Copy the published output into the bundle
cp -R "${PUBLISH_DIR}/output/"* "${APP_BUNDLE}/Contents/MacOS/"

# Copy Info.plist and app icon
cp "${PROJECT_DIR}/Assets/Info.plist" "${APP_BUNDLE}/Contents/Info.plist"
cp "${PROJECT_DIR}/Assets/AppIcon.icns" "${APP_BUNDLE}/Contents/Resources/AppIcon.icns"

# Make the executable runnable
chmod +x "${APP_BUNDLE}/Contents/MacOS/${APP_NAME}"

# Clean up the flat output directory
rm -rf "${PUBLISH_DIR}/output"

# Copy to /Applications
echo "=== Installing to /Applications ==="
rm -rf "/Applications/${APP_NAME}.app"
cp -R "${APP_BUNDLE}" "/Applications/${APP_NAME}.app"

echo ""
echo "=== Build complete ==="
echo "App bundle: ${APP_BUNDLE}"
echo "Installed:  /Applications/${APP_NAME}.app"
echo ""
echo "To run:  open /Applications/${APP_NAME}.app"
