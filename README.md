# LlamaSwapManager

A cross-platform Avalonia desktop UI for managing `llama-swap` and `llama.cpp` `llama-server` runtimes.

## Features

- Start, stop, restart, and refresh `llama-swap` from a desktop UI.
- Detect existing `llama-swap` and `llama-server` processes.
- Graceful stop flow: unload models, send SIGTERM/taskkill, then force-kill only as fallback.
- Edit `llama-swap` `config.yml` visually.
- Configure multiple model runtimes with local GGUF files or Hugging Face GGUF repositories.
- Guided `llama-server` parameter editor split by category:
  - Runtime
  - GPU / Memory
  - KV / Cache
  - Sampling
  - Server / API
  - Chat / Reasoning
  - Advanced raw flags
- Matrix configuration builder for model combinations.
- Config preview and logs split by source.

## Requirements

- .NET 9 SDK
- `llama-swap`
- `llama.cpp` `llama-server`

Expected default paths:

```text
~/.llama-swap/llama-swap
~/.llama-swap/config.yml
~/.llama/llama-server
```

Windows also supports `llama-swap.exe` via app folder, `~/.llama-swap`, or PATH.

## Run

```bash
dotnet run --project LlamaSwapManager.Desktop
```

## Build

```bash
dotnet build
```

## Notes

This app writes `llama-swap` configuration files only when you explicitly save from the UI. It does not install models or binaries automatically.
