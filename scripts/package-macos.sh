#!/usr/bin/env bash
# package-macos.sh
# Builds a proper macOS .app bundle for LlamaSwapManager with the correct Dock / Cmd+Tab icon.
# Usage: bash scripts/package-macos.sh

set -euo pipefail

# ── Config ────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT_DIR="$ROOT_DIR/LlamaSwapManager.Desktop"
PUBLISH_DIR="$ROOT_DIR/publish"
RAW_DIR="$PUBLISH_DIR/raw"
APP_BUNDLE="$PUBLISH_DIR/LlamaSwapManager.app"
ICON_ICNS="$PROJECT_DIR/icon.icns"
EXECUTABLE="LlamaSwapManager.Desktop"
APP_NAME="Llama Swap Manager"
BUNDLE_ID="me.brunocasado.llamaswapmanager"
VERSION="1.0.0"

# ── Find dotnet ───────────────────────────────────────────────────
DOTNET_CMD=""
for candidate in \
  "$(command -v dotnet 2>/dev/null)" \
  "$HOME/.dotnet/dotnet" \
  "/usr/local/share/dotnet/dotnet" \
  "/usr/local/bin/dotnet" \
  "/opt/homebrew/bin/dotnet" \
  "/usr/share/dotnet/dotnet"; do
  if [ -n "$candidate" ] && [ -x "$candidate" ]; then
    DOTNET_CMD="$candidate"
    break
  fi
done

if [ -z "$DOTNET_CMD" ]; then
  echo "❌ dotnet not found. Please install the .NET 9 SDK."
  echo "   Common locations: ~/.dotnet/dotnet, /usr/local/share/dotnet/dotnet"
  exit 1
fi

if [ ! -f "$ICON_ICNS" ]; then
  echo "❌ Icon not found: $ICON_ICNS"
  echo "   Generate it with: sips + iconutil from llama.png"
  exit 1
fi

# ── Clean previous artifacts ──────────────────────────────────────
echo "🧹 Cleaning previous artifacts..."
rm -rf "$PUBLISH_DIR"
mkdir -p "$RAW_DIR"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# ── 1. dotnet publish (self-contained, osx-arm64, Release) ───────
echo "📦 Publishing for osx-arm64 (Release)..."
$DOTNET_CMD publish "$PROJECT_DIR/$EXECUTABLE.csproj" \
  -r osx-arm64 \
  -c Release \
  --self-contained true \
  -o "$RAW_DIR" \
  -v quiet

if [ ! -f "$RAW_DIR/$EXECUTABLE" ]; then
  echo "❌ Binary not generated: $RAW_DIR/$EXECUTABLE"
  exit 1
fi

echo "   ✓ Published: $(ls "$RAW_DIR" | wc -l | tr -d ' ') files"

# ── 2. Assemble the .app bundle ──────────────────────────────────
echo "📦 Assembling $APP_BUNDLE..."

# Copy all published files into Contents/MacOS/
cp -R "$RAW_DIR"/* "$APP_BUNDLE/Contents/MacOS/"

# ── 3. Copy Avalonia.Native native library (not included by dotnet publish --self-contained) ──
AVALONIA_NATIVE_DYLIB=""
for pkg_dir in $(find "$HOME/.nuget/packages" -maxdepth 1 -name "avalonia.native" -type d 2>/dev/null); do
    candidate="$pkg_dir/*/runtimes/osx/native/libAvaloniaNative.dylib"
    if ls $candidate &>/dev/null; then
        AVALONIA_NATIVE_DYLIB=$(ls -d $candidate 2>/dev/null | head -1)
        break
    fi
done

if [ -n "$AVALONIA_NATIVE_DYLIB" ]; then
    cp "$AVALONIA_NATIVE_DYLIB" "$APP_BUNDLE/Contents/MacOS/"
    echo "   ✓ libAvaloniaNative.dylib copied"
else
    echo "⚠ Warning: libAvaloniaNative.dylib not found in NuGet cache — app may crash on startup"
fi

# ── 4. Copy icon.icns ────────────────────────────────────────────
cp "$ICON_ICNS" "$APP_BUNDLE/Contents/Resources/icon.icns"
echo "   ✓ icon.icns copied"

# ── 5. Generate Info.plist ───────────────────────────────────────
cat > "$APP_BUNDLE/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>$EXECUTABLE</string>
    <key>CFBundleIconFile</key>
    <string>icon</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF
echo "   ✓ Info.plist generated"

# ── 6. Make the executable executable ────────────────────────────
chmod +x "$APP_BUNDLE/Contents/MacOS/$EXECUTABLE"
echo "   ✓ +x permission applied"

# ── 7. Remove quarantine attribute (if present) ──────────────────
if command -v xattr &>/dev/null; then
  xattr -dr com.apple.quarantine "$APP_BUNDLE" 2>/dev/null || true
  echo "   ✓ Quarantine removed"
fi

# ── 8. Validate structure ────────────────────────────────────────
echo ""
echo "📋 Final structure:"
echo "   publish/LlamaSwapManager.app/"
echo "   └── Contents/"
echo "        ├── Info.plist"
echo "        ├── MacOS/"
echo "        │    └── $EXECUTABLE (and all dependencies)"
echo "        └── Resources/"
echo "             └── icon.icns"
echo ""

# ── 9. Open the app ──────────────────────────────────────────────
echo "🚀 Opening the app..."
open "$APP_BUNDLE"

echo ""
echo "✅ Done! Check the Dock and Cmd+Tab for the llama icon."
