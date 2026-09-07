#!/usr/bin/env bash
# Build a macOS .app bundle + DMG installer (+ .app.zip for in-app updates).
# Usage: ./build-macos.sh [Configuration] [Version] [RID]
#   RID defaults to the host arch (osx-arm64 or osx-x64).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
CONFIGURATION="${1:-Release}"
VERSION="${2:-1.0.0}"

HOST_ARCH="$(uname -m)"
if [[ -n "${3:-}" ]]; then
  RID="$3"
elif [[ "$HOST_ARCH" == "arm64" ]]; then
  RID="osx-arm64"
else
  RID="osx-x64"
fi

case "$RID" in
  osx-arm64) ARCH_LABEL="arm64" ;;
  osx-x64)   ARCH_LABEL="x64" ;;
  *)
    echo "Unsupported RID: $RID (expected osx-arm64 or osx-x64)" >&2
    exit 1
    ;;
esac

APP_PROJ="$ROOT/src/PSMS.App/PSMS.App.csproj"
ARTIFACTS="$ROOT/artifacts"
PUBLISH_DIR="$ARTIFACTS/publish/$RID"
APP_DIR="$ARTIFACTS/macos/PSMS.app"
DMG_STAGE="$ARTIFACTS/macos/dmg-stage"
PLIST_TEMPLATE="$ROOT/packaging/macos/Info.plist"
ICON_PNG="$ROOT/src/PSMS.App/wwwroot/appicon.png"

echo "==> Publishing PSMS ($CONFIGURATION, $RID, v$VERSION)…"
rm -rf "$PUBLISH_DIR" "$APP_DIR" "$DMG_STAGE"
mkdir -p "$ARTIFACTS"

dotnet publish "$APP_PROJ" \
  -c "$CONFIGURATION" \
  -r "$RID" \
  --self-contained true \
  -p:Version="$VERSION" \
  -p:PublishSingleFile=false \
  -o "$PUBLISH_DIR"

if [[ ! -x "$PUBLISH_DIR/PSMS.App" && ! -f "$PUBLISH_DIR/PSMS.App" ]]; then
  echo "Published binary PSMS.App not found in $PUBLISH_DIR" >&2
  ls -la "$PUBLISH_DIR" >&2 || true
  exit 1
fi
chmod +x "$PUBLISH_DIR/PSMS.App"

echo "==> Assembling PSMS.app…"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
# Publish output becomes the MacOS folder contents
cp -a "$PUBLISH_DIR"/. "$APP_DIR/Contents/MacOS/"

# Info.plist with version stamped
sed "s/__VERSION__/${VERSION}/g" "$PLIST_TEMPLATE" > "$APP_DIR/Contents/Info.plist"

# App icon (.icns) when possible
if [[ -f "$ICON_PNG" ]] && command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
  ICONSET="$ARTIFACTS/macos/AppIcon.iconset"
  rm -rf "$ICONSET"
  mkdir -p "$ICONSET"
  sips -z 16 16     "$ICON_PNG" --out "$ICONSET/icon_16x16.png" >/dev/null
  sips -z 32 32     "$ICON_PNG" --out "$ICONSET/diana.s@example.org" >/dev/null
  sips -z 32 32     "$ICON_PNG" --out "$ICONSET/icon_32x32.png" >/dev/null
  sips -z 64 64     "$ICON_PNG" --out "$ICONSET/ivan.p@example.net" >/dev/null
  sips -z 128 128   "$ICON_PNG" --out "$ICONSET/icon_128x128.png" >/dev/null
  sips -z 256 256   "$ICON_PNG" --out "$ICONSET/wendy.h@example.net" >/dev/null
  sips -z 256 256   "$ICON_PNG" --out "$ICONSET/icon_256x256.png" >/dev/null
  sips -z 512 512   "$ICON_PNG" --out "$ICONSET/wendy.h@example.net" >/dev/null
  sips -z 512 512   "$ICON_PNG" --out "$ICONSET/icon_512x512.png" >/dev/null
  sips -z 1024 1024 "$ICON_PNG" --out "$ICONSET/walt.e@example.net" >/dev/null
  iconutil -c icns "$ICONSET" -o "$APP_DIR/Contents/Resources/AppIcon.icns"
  rm -rf "$ICONSET"
else
  # Fallback: keep PNG in Resources (Dock may not pick it up without .icns)
  if [[ -f "$ICON_PNG" ]]; then
    cp -f "$ICON_PNG" "$APP_DIR/Contents/Resources/AppIcon.png"
  fi
fi

# Clear quarantine on local builds
xattr -cr "$APP_DIR" 2>/dev/null || true

ZIP_NAME="PSMS-${VERSION}-osx-${ARCH_LABEL}.app.zip"
ZIP_PATH="$ARTIFACTS/$ZIP_NAME"
STABLE_ZIP="$ARTIFACTS/PSMS-osx-${ARCH_LABEL}.app.zip"

echo "==> Creating $ZIP_NAME (for updates)…"
rm -f "$ZIP_PATH" "$STABLE_ZIP"
(
  cd "$ARTIFACTS/macos"
  # -y store symlinks as-is; ditto preserves resource forks better on macOS
  ditto -c -k --sequesterRsrc --keepParent "PSMS.app" "$ZIP_PATH"
)
cp -f "$ZIP_PATH" "$STABLE_ZIP"

DMG_NAME="PSMS-${VERSION}-osx-${ARCH_LABEL}.dmg"
DMG_PATH="$ARTIFACTS/$DMG_NAME"
STABLE_DMG="$ARTIFACTS/PSMS-osx-${ARCH_LABEL}.dmg"

echo "==> Creating $DMG_NAME (installer)…"
rm -rf "$DMG_STAGE"
mkdir -p "$DMG_STAGE"
cp -a "$APP_DIR" "$DMG_STAGE/PSMS.app"
ln -sf /Applications "$DMG_STAGE/Applications"
rm -f "$DMG_PATH" "$STABLE_DMG"
hdiutil create \
  -volname "PSMS" \
  -srcfolder "$DMG_STAGE" \
  -ov \
  -format UDZO \
  "$DMG_PATH" >/dev/null
cp -f "$DMG_PATH" "$STABLE_DMG"
rm -rf "$DMG_STAGE"

echo ""
echo "Done."
echo "  App:  $APP_DIR"
echo "  Zip:  $STABLE_ZIP"
echo "  DMG:  $STABLE_DMG"
echo ""
echo "Install: open the DMG and drag PSMS into Applications."
