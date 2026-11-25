#!/usr/bin/env bash
# Installs RL Map Loader for the current user (no root needed). Optional: just opening the
# rl-map-loader binary does the same thing the first time.
#   ./install.sh                 install the binary next to this script
#   ./install.sh /path/to/rl-map-loader
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bin="${1:-$here/rl-map-loader}"
if [[ ! -f "$bin" ]]; then
    echo "rl-map-loader binary not found at: $bin" >&2
    echo "Build it first with ./build.sh (needs dotnet-sdk), or pass its path." >&2
    exit 1
fi
chmod +x "$bin"
# copies itself to ~/.local/bin/rl-map-loader and adds the app-menu entry + icon
"$bin" --install
echo
echo "Tip (KDE Plasma / Wayland): the F3 hotkey works while Rocket League has focus."
echo "To open the overlay from anywhere, bind this command in System Settings > Shortcuts:"
echo "    $HOME/.local/bin/rl-map-loader --toggle-overlay"
