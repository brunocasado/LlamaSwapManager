# macOS Icon — Dock / Cmd+Tab

## Why `dotnet run` does not show the Dock icon

When you run `dotnet run` on macOS, the .NET SDK **does not generate an `.app` bundle** — it runs the binary directly as a Unix process. Without an `.app` bundle, there is no `Info.plist` with `CFBundleIconFile`, and macOS has no idea which icon to use for the Dock or Cmd+Tab. The result is the default blank icon.

## How to test the icon correctly

### Option 1: Package script (recommended)

```bash
cd ~/projects/LlamaSwapManager
bash scripts/package-macos.sh
```

This creates `./publish/LlamaSwapManager.app` with:
- `Contents/Info.plist` containing `CFBundleIconFile = icon`
- `Contents/Resources/icon.icns` (16x16 through 1024x1024)
- All DLLs and dependencies in `Contents/MacOS/`

Then open:

```bash
open ./publish/LlamaSwapManager.app
```

### Option 2: Manual publish

```bash
cd ~/projects/LlamaSwapManager
dotnet publish LlamaSwapManager.Desktop/LlamaSwapManager.Desktop.csproj \
  -r osx-arm64 -c Release --self-contained true -o ./publish/raw

# Manually assemble the .app bundle (see scripts/package-macos.sh for reference)
```

## What works without the .app bundle

| Feature | `dotnet run` | `LlamaSwapManager.app` |
|---|---|---|
| Window opens | ✅ | ✅ |
| Tray icon | ✅ | ✅ |
| Window icon (title bar) | ✅ | ✅ |
| **Dock icon** | ❌ blank | ✅ llama |
| **Cmd+Tab icon** | ❌ blank | ✅ llama |

## Icon files

- **`LlamaSwapManager.Desktop/Assets/llama.png`** — source image
- **`LlamaSwapManager.Desktop/icon.icns`** — macOS native format (generated with `sips` + `iconutil`)

## Notes

- The `package-macos.sh` script is self-contained and can be re-run at any time.
- To distribute the app, sign with `codesign` and notarize with `xcrun altool`.
- For a clean rebuild, run `rm -rf publish/` before executing the script.
