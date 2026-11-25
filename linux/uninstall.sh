#!/usr/bin/env bash
# Removes RL Map Loader installed by install.sh. Your settings (~/.config/RLMapLoader) and the
# Underpass backup (~/.local/share/RLMapLoader) are kept; delete them by hand if you want.
set -euo pipefail
prefix="${XDG_DATA_HOME:-$HOME/.local/share}"
"$HOME/.local/bin/rl-map-loader" --restore 2>/dev/null || true
rm -f "$HOME/.local/bin/rl-map-loader" \
      "$prefix/applications/rl-map-loader.desktop" \
      "$prefix/icons/hicolor/512x512/apps/rl-map-loader.png" \
      "$prefix/RLMapLoader/installed-version" \
      "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/rl-map-loader.desktop"
echo "RL Map Loader removed."
