#!/usr/bin/env bash
# Install PSMS locally on Linux (~/.local).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
APP_PROJ="$ROOT/src/PSMS.App/PSMS.App.csproj"
VERSION="${1:-1.0.0}"
INSTALL_DIR="${PSMS_INSTALL_DIR:-$HOME/.local/share/psms}"
BIN_DIR="${HOME}/.local/bin"
DESKTOP_DIR="${HOME}/.local/share/applications"
ICON_DIR="${HOME}/.local/share/icons/hicolor/256x256/apps"

echo "Publishing PSMS (linux-x64, Release, v$VERSION)..."
dotnet publish "$APP_PROJ" \
  -c Release \
  -r linux-x64 \
  --self-contained false \
  -p:Version="$VERSION" \
  -o "$INSTALL_DIR"

ICON_SRC="$INSTALL_DIR/appicon.png"
if [[ ! -f "$ICON_SRC" ]]; then
  ICON_SRC="$ROOT/src/PSMS.App/wwwroot/appicon.png"
  cp -f "$ICON_SRC" "$INSTALL_DIR/appicon.png" 2>/dev/null || true
fi
cp -f "$ROOT/src/PSMS.App/wwwroot/favicon.ico" "$INSTALL_DIR/favicon.ico" 2>/dev/null || true

mkdir -p "$BIN_DIR" "$DESKTOP_DIR" "$ICON_DIR"

cat > "$BIN_DIR/psms" <<EOF
#!/usr/bin/env bash
export GDK_BACKEND="\${GDK_BACKEND:-x11}"
exec dotnet "$INSTALL_DIR/PSMS.App.dll" "\$@"
EOF
chmod +x "$BIN_DIR/psms"

cp -f "$ICON_SRC" "$ICON_DIR/psms.png" 2>/dev/null || true

cat > "$DESKTOP_DIR/psms.desktop" <<EOF
[Desktop Entry]
Type=Application
Version=1.0
Name=PSMS
GenericName=SQL Management Studio
Comment=PSMS — SQL Management Studio
Exec=$BIN_DIR/psms
Icon=psms
Terminal=false
Categories=Development;Database;
StartupWMClass=PSMS
StartupNotify=true
EOF

echo ""
echo "PSMS installed to: $INSTALL_DIR"
echo "Launcher:          $BIN_DIR/psms"
echo "Desktop entry:     $DESKTOP_DIR/psms.desktop"
echo ""
echo "Run:  psms"
echo "Or search for 'PSMS' in your application menu."
echo ""
if ! ldconfig -p 2>/dev/null | grep -q libwebkit2gtk-4.1; then
  echo "Note: WebKitGTK 4.1 may be required:"
  echo "  sudo apt install libwebkit2gtk-4.1-0"
fi
