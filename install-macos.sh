#!/usr/bin/env bash
# Build and install PSMS into ~/Applications for local development / first install.
# Usage: ./install-macos.sh [Version]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
VERSION="${1:-1.0.0}"
INSTALL_ROOT="${PSMS_INSTALL_DIR:-$HOME/Applications}"

HOST_ARCH="$(uname -m)"
if [[ "$HOST_ARCH" == "arm64" ]]; then
  RID="osx-arm64"
else
  RID="osx-x64"
fi

"$ROOT/build-macos.sh" Release "$VERSION" "$RID"

mkdir -p "$INSTALL_ROOT"
rm -rf "$INSTALL_ROOT/PSMS.app"
cp -a "$ROOT/artifacts/macos/PSMS.app" "$INSTALL_ROOT/PSMS.app"
xattr -cr "$INSTALL_ROOT/PSMS.app" 2>/dev/null || true

echo ""
echo "Installed to: $INSTALL_ROOT/PSMS.app"
echo "Open with:    open \"$INSTALL_ROOT/PSMS.app\""
