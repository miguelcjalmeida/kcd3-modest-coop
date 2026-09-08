#!/usr/bin/env bash
#
# Linux/Steam Proton equivalent of Build-And-Install-Mod.ps1.
#
# Packs mod/Data into itemswap.pak (stored, uncompressed - matches CryEngine's
# pak reader expectations) and installs it into the RETAIL KCD2 Mods folder:
#   <RetailInstall>/Mods/itemswap/mod.manifest
#   <RetailInstall>/Mods/itemswap/Data/itemswap.pak
#
# The pak's internal root is the CONTENTS of mod/Data (e.g. Scripts/Startup/
# itemswap.lua), not the Data folder itself, so it overlays correctly.
#
# Usage:
#   ./Build-And-Install-Mod.sh [retail-install-path]
#
# If omitted, retail-install-path defaults to the most common Steam location
# for a Proton-run KCD2 install. Override it if your Steam library lives
# somewhere else (a second drive, a custom library folder, etc).

set -euo pipefail

RETAIL_INSTALL="${1:-$HOME/.steam/steam/steamapps/common/KingdomComeDeliverance2}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
DATA_SRC="$REPO_ROOT/mod/Data"
MANIFEST="$REPO_ROOT/mod/mod.manifest"
INSTALL_DIR="$RETAIL_INSTALL/Mods/itemswap"
DATA_DST_DIR="$INSTALL_DIR/Data"
PAK_PATH="$DATA_DST_DIR/itemswap.pak"

if [ ! -d "$DATA_SRC" ]; then
    echo "Mod source not found: $DATA_SRC" >&2
    exit 1
fi
if [ ! -f "$MANIFEST" ]; then
    echo "mod.manifest not found: $MANIFEST" >&2
    exit 1
fi
if [ ! -d "$RETAIL_INSTALL" ]; then
    echo "Retail install not found: $RETAIL_INSTALL" >&2
    echo "Pass your KCD2 install path as the first argument if it's somewhere else." >&2
    exit 1
fi
if ! command -v zip >/dev/null 2>&1; then
    echo "'zip' is required but not found. Install it (e.g. 'sudo apt install zip')." >&2
    exit 1
fi

mkdir -p "$DATA_DST_DIR"
rm -f "$PAK_PATH"

# -X: no extra file attributes (avoids Unix permission bits confusing the
# pak reader). -0: store, no compression - matches the PowerShell script's
# CompressionLevel::NoCompression, required for CryEngine's pak reader.
( cd "$DATA_SRC" && zip -X -0 -r "$PAK_PATH" . )

cp -f "$MANIFEST" "$INSTALL_DIR/"

echo ""
echo "Installed:"
echo "  $INSTALL_DIR/mod.manifest"
echo "  $PAK_PATH"
echo ""
echo "Launch retail with -devmode via Steam (right-click KCD2 > Properties >"
echo "Launch Options) by setting:"
echo "  -devmode %command%"
echo "or run it directly through Proton, e.g.:"
echo "  STEAM_COMPAT_DATA_PATH=\"\$HOME/.steam/steam/steamapps/compatdata/<appid>\" \\"
echo "  STEAM_COMPAT_CLIENT_INSTALL_PATH=\"\$HOME/.steam/steam\" \\"
echo "  \"<path-to-proton>\" run \"$RETAIL_INSTALL/Bin/Win64MasterMasterSteamPGO/KingdomCome.exe\" -devmode"
