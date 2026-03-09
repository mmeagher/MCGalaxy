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

exec "$BINARY"
