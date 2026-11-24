#!/usr/bin/env bash
# Builds a self-contained rl-map-loader binary (no .NET runtime needed to run it).
# Needs the .NET 9 SDK to build:   sudo pacman -S dotnet-sdk-9.0   (CachyOS / Arch)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname "$here")"
arch="$(uname -m)"
case "$arch" in
    x86_64) rid=linux-x64 ;;
    aarch64) rid=linux-arm64 ;;
    *) echo "Unsupported architecture: $arch" >&2; exit 1 ;;
esac

dotnet publish "$root/RLMapLoader.Linux/RLMapLoader.Linux.csproj" -c Release -r "$rid" \
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -o "$here/publish"
cp "$here/publish/rl-map-loader" "$here/rl-map-loader"
echo "Built $here/rl-map-loader  -  run ./install.sh to install it."
