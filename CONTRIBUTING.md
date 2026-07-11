# Contributing to LlamaDeck

Thanks for helping improve LlamaDeck.

## Before you start

- Search existing issues before opening a new one.
- Use Discussions for setup questions and general ideas.
- Keep pull requests focused on one problem.

## Development setup

Requirements:

- .NET 9 SDK
- Git

Run the app:

```bash
dotnet run --project LlamaSwapManager.Desktop
```

Build and test:

```bash
dotnet build LlamaSwapManager.slnx --configuration Release
dotnet test Tests/LlamaSwapManager.Tests/LlamaSwapManager.Tests.csproj --configuration Release
```

## Pull requests

1. Create a branch from `main`.
2. Add or update tests when behavior changes.
3. Run the full test suite.
4. Keep unrelated formatting and refactors out of the change.
5. Explain the user-visible impact in the PR description.

The public product name is LlamaDeck. Internal project and namespace names still use `LlamaSwapManager` for compatibility.
