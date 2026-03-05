#!/bin/bash
set -e

# Publish PictureEditor as a Windows 11 executable
# Usage: ./publish-windows.sh [x64|arm64]
#   x64   = 64-bit Intel/AMD (default)
#   arm64 = ARM-based Windows

ARCH="${1:-x64}"
RID="win-${ARCH}"
PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)/PictureEditor"
PUBLISH_DIR="${PROJECT_DIR}/bin/publish/windows-${ARCH}"

echo "=== Publishing PictureEditor for Windows 11 (${RID}) ==="

# Clean previous publish
rm -rf "${PUBLISH_DIR}"

# Publish self-contained single-file executable
dotnet publish "${PROJECT_DIR}/PictureEditor.csproj" \
    -c Release \
    -r "${RID}" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PublishTrimmed=false \
    -o "${PUBLISH_DIR}"

echo ""
echo "=== Build complete ==="
echo "Output directory: ${PUBLISH_DIR}"
echo "Executable: ${PUBLISH_DIR}/PictureEditor.exe"
echo ""
echo "Copy the contents of ${PUBLISH_DIR} to a Windows 11 machine to run."
