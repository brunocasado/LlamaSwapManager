#!/bin/bash
DIR="$(dirname "$0")"

# Find dotnet executable
for d in "/Users/brunocasado/.dotnet/dotnet" "/usr/local/share/dotnet/dotnet" "$HOME/.dotnet/dotnet" "$(which dotnet 2>/dev/null)"; do
    if [ -x "$d" ]; then
        DOTNET="$d"
        break
    fi
done

if [ -z "$DOTNET" ]; then
    echo "Error: .NET runtime not found. Please install .NET 9." >&2
    exit 1
fi

exec "$DOTNET" "$DIR/LlamaSwapManager.Desktop.dll" "$@"
