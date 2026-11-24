#!/usr/bin/env bash
# Installs RL Map Loader for the current user (no root needed).
#   ./install.sh                 install the prebuilt binary next to this script
#   ./install.sh /path/to/rl-map-loader
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bin="${1:-$here/rl-map-loader}"
if [[ ! -f "$bin" ]]; then
    echo "rl-map-loader binary not found at: $bin" >&2
    echo "Build it first with ./build.sh (needs dotnet-sdk), or pass its path." >&2
    exit 1
fi

prefix="${XDG_DATA_HOME:-$HOME/.local/share}"
bindir="$HOME/.local/bin"

install -Dm755 "$bin" "$bindir/rl-map-loader"
install -Dm644 "$here/rl-map-loader.png" "$prefix/icons/hicolor/512x512/apps/rl-map-loader.png"
install -Dm644 "$here/rl-map-loader.desktop" "$prefix/applications/rl-map-loader.desktop"

command -v update-desktop-database >/dev/null && update-desktop-database "$prefix/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q "$prefix/icons/hicolor" || true

echo "Installed: $bindir/rl-map-loader"
case ":$PATH:" in
    *":$bindir:"*) ;;
    *) echo "Note: $bindir is not on your PATH. Add it, or start RL Map Loader from your app menu." ;;
esac
echo
echo "Tip (KDE Plasma / Wayland): the F3 hotkey works while Rocket League has focus."
echo "To open the overlay from anywhere, bind this command in System Settings > Shortcuts:"
echo "    $bindir/rl-map-loader --toggle-overlay"
