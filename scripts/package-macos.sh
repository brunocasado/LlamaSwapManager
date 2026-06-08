#!/usr/bin/env bash
# package-macos.sh
# Cria o .app bundle do LlamaSwapManager com ícone correto no Dock / Cmd+Tab.
# Uso: bash scripts/package-macos.sh

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
APP_NAME="LlamaSwapManager"
BUNDLE_ID="me.brunocasado.llamaswapmanager"
VERSION="1.0.0"

# ── Validar pré-requisitos ────────────────────────────────────────
if ! command -v dotnet &>/dev/null; then
  echo "❌ dotnet não encontrado. Instale o .NET 9 SDK."
  exit 1
fi

if [ ! -f "$ICON_ICNS" ]; then
  echo "❌ Icone não encontrado: $ICON_ICNS"
  echo "   Gere com: sips + iconutil a partir do llama.png"
  exit 1
fi

# ── Limpar artefatos anteriores ───────────────────────────────────
echo "🧹 Limpando pastas anteriores..."
rm -rf "$PUBLISH_DIR"
mkdir -p "$RAW_DIR"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# ── 1. dotnet publish (self-contained, osx-arm64, Release) ───────
echo "📦 Publicando para osx-arm64 (Release)..."
dotnet publish "$PROJECT_DIR/$EXECUTABLE.csproj" \
  -r osx-arm64 \
  -c Release \
  --self-contained true \
  -o "$RAW_DIR" \
  -v quiet

if [ ! -f "$RAW_DIR/$EXECUTABLE" ]; then
  echo "❌ Binário não gerado: $RAW_DIR/$EXECUTABLE"
  exit 1
fi

echo "   ✓ Binário publicado: $(ls "$RAW_DIR" | wc -l | tr -d ' ') arquivos"

# ── 2. Montar o .app bundle ──────────────────────────────────────
echo "📦 Montando $APP_BUNDLE..."

# Copiar tudo para Contents/MacOS/
cp -R "$RAW_DIR"/* "$APP_BUNDLE/Contents/MacOS/"

# ── 3. Copiar icns ───────────────────────────────────────────────
cp "$ICON_ICNS" "$APP_BUNDLE/Contents/Resources/icon.icns"
echo "   ✓ icon.icns copiado"

# ── 4. Gerar Info.plist ──────────────────────────────────────────
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
echo "   ✓ Info.plist gerado"

# ── 5. Tornar executável executável ──────────────────────────────
chmod +x "$APP_BUNDLE/Contents/MacOS/$EXECUTABLE"
echo "   ✓ Permissão +x aplicada"

# ── 6. Remover quarantine (se presente) ──────────────────────────
if command -v xattr &>/dev/null; then
  xattr -dr com.apple.quarantine "$APP_BUNDLE" 2>/dev/null || true
  echo "   ✓ Quarantine removido"
fi

# ── 7. Validar estrutura ─────────────────────────────────────────
echo ""
echo "📋 Estrutura final:"
echo "   publish/LlamaSwapManager.app/"
echo "   └── Contents/"
echo "        ├── Info.plist"
echo "        ├── MacOS/"
echo "        │    └── $EXECUTABLE (e todas as dependências)"
echo "        └── Resources/"
echo "             └── icon.icns"
echo ""

# ── 8. Abrir o app ───────────────────────────────────────────────
echo "🚀 Abrindo o app..."
open "$APP_BUNDLE"

echo ""
echo "✅ Pronto! Verifique o ícone no Dock e no Cmd+Tab."
