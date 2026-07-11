#!/usr/bin/env bash
set -euo pipefail

RID="${1:?usage: package-release-macos.sh <osx-arm64|osx-x64> <version> <output-dir>}"
VERSION="${2:?version is required}"
OUTPUT_DIR="${3:?output directory is required}"

case "$RID" in
  osx-arm64|osx-x64) ;;
  *) echo "Unsupported macOS RID: $RID" >&2; exit 2 ;;
esac

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT_DIR/LlamaSwapManager.Desktop/LlamaSwapManager.Desktop.csproj"
WORK_DIR="$ROOT_DIR/artifacts/macos-$RID"
PUBLISH_DIR="$WORK_DIR/publish"
APP_DIR="$WORK_DIR/LlamaDeck.app"
EXECUTABLE="LlamaSwapManager.Desktop"
ICON="$ROOT_DIR/LlamaSwapManager.Desktop/icon.icns"

rm -rf "$WORK_DIR"
mkdir -p "$PUBLISH_DIR" "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources" "$OUTPUT_DIR"

dotnet publish "$PROJECT" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  -p:Version="$VERSION" \
  --output "$PUBLISH_DIR"

cp -R "$PUBLISH_DIR"/. "$APP_DIR/Contents/MacOS/"
cp "$ICON" "$APP_DIR/Contents/Resources/icon.icns"
chmod +x "$APP_DIR/Contents/MacOS/$EXECUTABLE"

cat > "$APP_DIR/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>LlamaDeck</string>
  <key>CFBundleDisplayName</key><string>LlamaDeck</string>
  <key>CFBundleIdentifier</key><string>me.brunocasado.llamadeck</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>$EXECUTABLE</string>
  <key>CFBundleIconFile</key><string>icon</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

codesign --force --deep --sign - "$APP_DIR"

ditto -c -k --sequesterRsrc --keepParent \
  "$APP_DIR" \
  "$OUTPUT_DIR/LlamaDeck-$VERSION-${RID/osx-/macos-}.zip"
