# macOS Icon — Dock / Cmd+Tab

## Por que `dotnet run` não mostra o ícone no Dock

Quando você roda `dotnet run` no macOS, o SDK do .NET **não gera um `.app` bundle** — ele executa o binário diretamente como um processo Unix. Sem um `.app` bundle, não há `Info.plist` com `CFBundleIconFile`, e o macOS não sabe qual ícone usar no Dock ou no Cmd+Tab. O resultado é o ícone padrão (branco/blank).

## Como testar o ícone corretamente

### Opção 1: Script de package (recomendado)

```bash
cd ~/projects/LlamaSwapManager
bash scripts/package-macos.sh
```

Isso cria `./publish/LlamaSwapManager.app` com:
- `Contents/Info.plist` com `CFBundleIconFile = icon`
- `Contents/Resources/icon.icns` (16x16 até 1024x1024)
- Todos os DLLs e dependências em `Contents/MacOS/`

Depois abra:

```bash
open ./publish/LlamaSwapManager.app
```

### Opção 2: Publish manual

```bash
cd ~/projects/LlamaSwapManager
dotnet publish LlamaSwapManager.Desktop/LlamaSwapManager.Desktop.csproj \
  -r osx-arm64 -c Release --self-contained true -o ./publish/raw

# Montar o .app bundle manualmente (ver scripts/package-macos.sh para referência)
```

## O que funciona sem o .app bundle

| Recurso | `dotnet run` | `LlamaSwapManager.app` |
|---|---|---|
| Janela abre | ✅ | ✅ |
| Tray icon | ✅ | ✅ |
| Ícone da janela (title bar) | ✅ | ✅ |
| **Ícone do Dock** | ❌ branco | ✅ llama |
| **Ícone Cmd+Tab** | ❌ branco | ✅ llama |

## Arquivos de ícone

- **`LlamaSwapManager.Desktop/Assets/llama.png`** — fonte original
- **`LlamaSwapManager.Desktop/icon.icns`** — formato macOS nativo (gerado com `sips` + `iconutil`)

## Notas

- O script `package-macos.sh` é auto-contido e pode ser rodado repetidamente.
- Se quiser distribuir o app, assine com `codesign` e notarie com `xcrun altool`.
- Para re-build limpo, execute `rm -rf publish/` antes de rodar o script.
