#!/bin/zsh

set -euo pipefail

SCRIPT_DIR="${0:A:h}"
PROJECT_ROOT="${SCRIPT_DIR:h}"
RUNTIME="${1:-osx-arm64}"

if [[ "$RUNTIME" != "osx-arm64" && "$RUNTIME" != "osx-x64" ]]; then
    echo "Поддерживаемые архитектуры: osx-arm64 и osx-x64." >&2
    exit 1
fi

PROJECT_FILE="$PROJECT_ROOT/src/WorkTimeRegister/WorkTimeRegister.csproj"
INFO_PLIST="$PROJECT_ROOT/packaging/macos/Info.plist"
APP_ICON="$PROJECT_ROOT/packaging/macos/WorkTimeRegister.icns"
COMPONENT_PLIST="$PROJECT_ROOT/packaging/macos/components.plist"
APP_VERSION="$(/usr/bin/plutil -extract CFBundleShortVersionString raw "$INFO_PLIST")"
ARTIFACTS_DIR="$PROJECT_ROOT/artifacts"
PACKAGE_IDENTIFIER="com.worktimeregister.desktop.pkg"
WORK_DIR="$(mktemp -d /private/tmp/worktimeregister-installer.XXXXXX)"
PUBLISH_DIR="$WORK_DIR/publish"
PKG_ROOT="$WORK_DIR/pkg-root"
APP_BUNDLE="$PKG_ROOT/Applications/WorkTimeRegister.app"
DMG_STAGE="$WORK_DIR/dmg"
PKG_FILE="$ARTIFACTS_DIR/WorkTimeRegister-$APP_VERSION-$RUNTIME.pkg"
DMG_FILE="$ARTIFACTS_DIR/WorkTimeRegister-$APP_VERSION-$RUNTIME.dmg"
LEGACY_BUILD_DIR="$ARTIFACTS_DIR/macos-$RUNTIME"

cleanup() {
    rm -rf -- "$WORK_DIR"
}

trap cleanup EXIT

mkdir -p "$ARTIFACTS_DIR" "$PUBLISH_DIR" "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources" "$DMG_STAGE"
touch "$ARTIFACTS_DIR/.metadata_never_index"
rm -f -- "$PKG_FILE" "$DMG_FILE"

# Remove only legacy build copies. They made Spotlight display the same app several times.
if [[ -d "$LEGACY_BUILD_DIR" ]]; then
    rm -rf -- "$LEGACY_BUILD_DIR"
fi

dotnet publish "$PROJECT_FILE" \
    --configuration Release \
    --runtime "$RUNTIME" \
    --self-contained true \
    --output "$PUBLISH_DIR" \
    -p:Version="$APP_VERSION" \
    -p:DebugType=None \
    -p:DebugSymbols=false

cp "$INFO_PLIST" "$APP_BUNDLE/Contents/Info.plist"
cp "$APP_ICON" "$APP_BUNDLE/Contents/Resources/WorkTimeRegister.icns"
cp -R "$PROJECT_ROOT/packaging/macos/Resources/." "$APP_BUNDLE/Contents/Resources/"
cp -R "$PUBLISH_DIR/." "$APP_BUNDLE/Contents/MacOS/"
chmod +x "$APP_BUNDLE/Contents/MacOS/WorkTimeRegister"

codesign --force --deep --sign - "$APP_BUNDLE"

pkgbuild \
    --root "$PKG_ROOT" \
    --component-plist "$COMPONENT_PLIST" \
    --identifier "$PACKAGE_IDENTIFIER" \
    --version "$APP_VERSION" \
    --install-location / \
    --ownership recommended \
    "$PKG_FILE"

cp "$PKG_FILE" "$DMG_STAGE/Install WorkTimeRegister.pkg"

hdiutil create \
    -volname "WorkTimeRegister" \
    -srcfolder "$DMG_STAGE" \
    -format UDZO \
    -ov \
    "$DMG_FILE"

echo "Готов пакет обновления: $PKG_FILE"
echo "Готов образ с установщиком: $DMG_FILE"
