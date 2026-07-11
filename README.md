# LlamaDeck

[![CI](https://github.com/brunocasado/LlamaDeck/actions/workflows/ci.yml/badge.svg)](https://github.com/brunocasado/LlamaDeck/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/brunocasado/LlamaDeck?display_name=tag&sort=semver)](https://github.com/brunocasado/LlamaDeck/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A desktop control center for `llama-swap`, `llama.cpp`, and local GGUF models.

LlamaDeck gives you one place to configure model profiles, manage the local runtime, build loading combinations, inspect generated configuration, and monitor what is running.

![LlamaDeck models dashboard](docs/images/llamadeck-main-v2.png)

## Download

Download the latest build from [GitHub Releases](https://github.com/brunocasado/LlamaDeck/releases/latest).

Release archives are self-contained and do not require the .NET SDK. Packaged builds can check and install newer stable releases directly from GitHub tags. LlamaDeck itself does not bundle `llama.cpp`, `llama-swap`, or model files.

> macOS builds are currently ad-hoc signed, not notarized. On first launch, macOS may require you to approve the app in Privacy & Security.

## What it does

- Start, stop, restart, and inspect the local `llama-swap` runtime.
- Install and update compatible `llama.cpp` builds.
- Create model profiles using local GGUF files or Hugging Face repositories.
- Configure common `llama-server` runtime, GPU, cache, sampling, server, and reasoning options.
- Build model-loading combinations and eviction priorities visually.
- Preview and save the generated `config.yml`.
- View runtime metrics, loaded models, and application logs.
- Check and install LlamaDeck updates from stable GitHub release tags.
- Check for and install compatible `llama-swap` and `llama.cpp` updates.

## Model combinations

Define which models can remain loaded together and control their eviction order without editing YAML manually.

![LlamaDeck matrix editor](docs/images/llamadeck-matrix-v2.png)

## Third-party software

LlamaDeck does **not** bundle, redistribute, or host `llama.cpp`, `llama-swap`, GGUF models, or model weights.

When LlamaDeck downloads or updates a compatible runtime, it retrieves the files from the upstream project's release source. Those projects and files remain governed by their own licenses, terms, and distribution policies. LlamaDeck is an independent project and is not affiliated with or endorsed by the `llama.cpp` or `llama-swap` maintainers.

## Requirements

- .NET 9 SDK when building from source.
- A supported `llama-swap` binary.
- A compatible `llama.cpp` `llama-server` build.

Default locations:

```text
~/.llama-swap/llama-swap
~/.llama-swap/config.yml
~/.llama/llama-server
```

On Windows, LlamaDeck also detects `llama-swap.exe` in the application directory, `~/.llama-swap`, or `PATH`.

## Run from source

```bash
dotnet run --project LlamaSwapManager.Desktop
```

## Build and test

```bash
dotnet build
dotnet test Tests/LlamaSwapManager.Tests/LlamaSwapManager.Tests.csproj
```

## Platform support

LlamaDeck is built with Avalonia and targets Windows, macOS, and Linux. Runtime installation and process behavior can vary by operating system, so platform-specific testing is welcome.

## Project status

LlamaDeck is under active development. The public product name has changed from **LlamaSwapManager** to **LlamaDeck**; internal project and namespace names remain unchanged for compatibility and will be migrated separately if needed.

## License

LlamaDeck is licensed under the [MIT License](LICENSE). This license applies only to the LlamaDeck source code and does not grant rights to third-party runtimes, models, trademarks, or other externally maintained assets.
