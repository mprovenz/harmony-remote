#!/usr/bin/env bash
# Installs Harmony Remote for the current user: the app in ~/.local/share/harmony-remote,
# a menu entry and an icon. Re-run to update. Remove with: scripts/install.sh --uninstall
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
APP_DIR="$DATA/harmony-remote"
DESKTOP="$DATA/applications/harmony-remote.desktop"
ICON="$DATA/icons/hicolor/256x256/apps/harmony-remote.png"

if [[ "${1:-}" == "--uninstall" ]]; then
  rm -rf "$APP_DIR" "$DESKTOP" "$ICON"
  echo "Removed Harmony Remote (settings in ~/.config/harmony-remote are kept)."
  exit 0
fi

# Framework-dependent for this machine only: ~23 MB instead of ~560 MB with every platform's native libraries.
RID="linux-$(uname -m | sed 's/x86_64/x64/; s/aarch64/arm64/')"
dotnet publish "$ROOT/src/HarmonyRemote.App" -c Release -r "$RID" --self-contained false -o "$APP_DIR" --nologo
install -Dm644 "$ROOT/src/HarmonyRemote.App/Assets/app-256.png" "$ICON"
mkdir -p "$(dirname "$DESKTOP")"
cat > "$DESKTOP" <<EOF
[Desktop Entry]
Type=Application
Name=Harmony Remote
Comment=Control your Logitech Harmony hub from the system tray
Exec="$APP_DIR/harmony-remote" --show
Icon=harmony-remote
Terminal=false
Categories=AudioVideo;Utility;
EOF
echo "Installed to $APP_DIR. Start it from the menu, or run: $APP_DIR/harmony-remote"
