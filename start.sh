#!/bin/bash
# MCGalaxy server launcher (dotnet 8)
# Server data (levels, players, etc.) is stored in the binary directory:
#   CLI/bin/Release/net8.0/

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BINARY="$REPO_DIR/CLI/bin/Release/net8.0/MCGalaxyCLI"

if [ ! -f "$BINARY" ]; then
    echo "Server binary not found. Build it first:"
    echo "  cd $REPO_DIR"
    echo "  dotnet build CLI/MCGalaxyCLI_dotnet8.csproj -c Release"
    exit 1
fi

export MCG_DOTNET_PATH=/usr/bin/dotnet

# Copy repo plugins into the server's runtime plugins directory
SERVER_PLUGINS="$REPO_DIR/CLI/bin/Release/net8.0/plugins"
mkdir -p "$SERVER_PLUGINS"
for f in "$REPO_DIR"/plugins/*.cs "$REPO_DIR"/plugins/*-system-prompt.md; do
    [ -f "$f" ] && cp "$f" "$SERVER_PLUGINS/"
done

# Compile plugins (in-game /compile has assembly resolution issues on .NET 8)
RUNTIME_DIR="/usr/lib/dotnet/shared/Microsoft.NETCore.App/8.0.25"
SDK_CSC="/usr/lib/dotnet/sdk/8.0.125/Roslyn/bincore/csc.dll"
MCG_DLL="$REPO_DIR/CLI/bin/Release/net8.0/MCGalaxy_.dll"

for src in "$REPO_DIR"/plugins/*.cs; do
    [ -f "$src" ] || continue
    name="$(basename "$src" .cs)"
    dll="$SERVER_PLUGINS/$name.dll"

    # Skip if .dll is newer than .cs
    if [ -f "$dll" ] && [ "$dll" -nt "$src" ]; then
        continue
    fi

    echo "Compiling plugin: $name"

    # Parse //dotnetref directives from the source file
    EXTRA_REFS=""
    while IFS= read -r line; do
        ref="$(echo "$line" | sed -n 's|^//dotnetref ||p')"
        [ -n "$ref" ] && EXTRA_REFS="$EXTRA_REFS /R:$RUNTIME_DIR/$ref"
    done < <(grep '^//dotnetref ' "$src")

    dotnet exec "$SDK_CSC" \
        /t:library /nostdlib+ /unsafe \
        /R:"$RUNTIME_DIR/System.Private.CoreLib.dll" \
        /R:"$RUNTIME_DIR/System.Runtime.dll" \
        /R:"$RUNTIME_DIR/netstandard.dll" \
        /R:"$RUNTIME_DIR/System.Collections.dll" \
        /R:"$RUNTIME_DIR/System.Net.Primitives.dll" \
        /R:"$RUNTIME_DIR/System.Threading.dll" \
        $EXTRA_REFS \
        /R:"$MCG_DLL" \
        /out:"$dll" \
        "$src" || echo "  FAILED to compile $name"
done

exec "$BINARY"
