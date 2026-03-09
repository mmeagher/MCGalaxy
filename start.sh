#!/bin/bash
# MCGalaxy server launcher (dotnet 8)
# Server data (levels, players, etc.) is stored in the binary directory:
#   CLI/bin/Release/net8.0/

BINARY="/home/mmeagher/code/mcgalaxy/MCGalaxy/CLI/bin/Release/net8.0/MCGalaxyCLI"

if [ ! -f "$BINARY" ]; then
    echo "Server binary not found. Build it first:"
    echo "  cd /home/mmeagher/code/mcgalaxy/MCGalaxy"
    echo "  dotnet build CLI/MCGalaxyCLI_dotnet8.csproj -c Release"
    exit 1
fi

export MCG_DOTNET_PATH=/usr/bin/dotnet

exec "$BINARY"
