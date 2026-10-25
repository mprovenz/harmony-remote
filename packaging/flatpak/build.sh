#!/usr/bin/env bash
# Builds the Harmony Remote Flatpak and installs it for the current user.
#
#   packaging/flatpak/build.sh             build + install (generates nuget-sources.json first if it's missing)
#   packaging/flatpak/build.sh --sources   regenerate nuget-sources.json (after changing NuGet package references)
#   packaging/flatpak/build.sh --bundle    build + install, and also write harmony-remote.flatpak (single-file bundle)
#
# Needs: flatpak-builder, org.freedesktop.Sdk//26.08 and org.freedesktop.Sdk.Extension.dotnet10//26.08.
# Build state lives in ~/.cache/harmony-remote-flatpak, not in the source tree.
set -euo pipefail

APP_ID=io.github.mprovenz.HarmonyRemote
FREEDESKTOP=26.08
DOTNET=10
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/harmony-remote-flatpak"
MANIFEST="$HERE/$APP_ID.yml"

regen_sources() {
  echo "==> Generating nuget-sources.json (restores inside the dotnet$DOTNET SDK extension)"
  # Restore from a scratch copy: the generator restores in place and would rewrite this tree's obj/ folders with
  # sandbox package paths. The copy lives under $HOME because the SDK sandbox doesn't see the host /tmp.
  local work="$CACHE/sources-src"
  rm -rf "$work" && mkdir -p "$work"
  cp -r "$ROOT/Directory.Build.props" "$ROOT/src" "$work/"
  rm -rf "$work"/src/*/bin "$work"/src/*/obj
  # -p:SelfContained=true makes the restore include the .NET runtime pack the self-contained publish needs.
  (cd "$work" && python3 "$HERE/flatpak-dotnet-generator.py" \
      "$HERE/nuget-sources.json" src/HarmonyRemote.App/HarmonyRemote.App.csproj \
      --dotnet "$DOTNET" --freedesktop "$FREEDESKTOP" --runtime linux-x64 \
      --dotnet-args -p:SelfContained=true)
  rm -rf "$work"
  echo "    $(grep -c '"url"' "$HERE/nuget-sources.json") packages listed"
}

case "${1:-}" in
  --sources) regen_sources; exit 0 ;;
  --bundle|"") ;;
  *) echo "usage: $0 [--sources|--bundle]" >&2; exit 2 ;;
esac

[[ -f "$HERE/nuget-sources.json" ]] || regen_sources

echo "==> Building and installing $APP_ID for $USER"
mkdir -p "$CACHE"
flatpak-builder --user --install --force-clean \
  --state-dir="$CACHE/state" --repo="$CACHE/repo" \
  "$CACHE/build" "$MANIFEST"

if [[ "${1:-}" == "--bundle" ]]; then
  echo "==> Writing harmony-remote.flatpak"
  flatpak build-bundle "$CACHE/repo" "$ROOT/harmony-remote.flatpak" "$APP_ID" \
    --runtime-repo=https://dl.flathub.org/repo/flathub.flatpakrepo
fi

echo "==> Done. Start it from the menu, or: flatpak run $APP_ID"
