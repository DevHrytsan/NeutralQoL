#!/bin/sh
# Builds the mod and puts NeutralQoL.dll (and its .pdb) in Build/ inside the
# repo. Same options as build.bat:
#   ./build.sh            Release build
#   ./build.sh debug      Debug build (includes the Dev sample feature)
#   ./build.sh deploy     also copy it into the game's BepInEx/plugins
#   ./build.sh game "$HOME/.steam/steam/steamapps/common/Nuclear Option"
set -e

root=$(cd "$(dirname "$0")" && pwd)
config=Release
extra=

while [ $# -gt 0 ]; do
    case "$1" in
        debug|Debug) config=Debug ;;
        release|Release) config=Release ;;
        deploy) extra="-p:DeployToGame=true" ;;
        game) export NO_GAME_PATH="$2"; shift ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
    shift
done

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet not found. Install the .NET SDK 9.0.200 or newer from https://dotnet.microsoft.com/download" >&2
    exit 1
fi

echo "Building NeutralQoL ($config)"
dotnet build "$root/src/NeutralQoL/NeutralQoL.csproj" -c "$config" $extra -nologo -v:minimal

out="$root/src/NeutralQoL/bin/$config"
rm -rf "$root/Build"
mkdir -p "$root/Build"
cp "$out/NeutralQoL.dll" "$root/Build/"
cp "$out/NeutralQoL.pdb" "$root/Build/" 2>/dev/null || true

echo
echo "Done: $root/Build/NeutralQoL.dll"
