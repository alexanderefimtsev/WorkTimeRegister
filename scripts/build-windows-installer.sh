#!/bin/zsh

set -euo pipefail

SCRIPT_DIR="${0:A:h}"
PROJECT_ROOT="${SCRIPT_DIR:h}"
RUNTIME="${1:-win-x64}"

if [[ "$RUNTIME" != "win-x64" && "$RUNTIME" != "win-arm64" ]]; then
    echo "Поддерживаемые архитектуры: win-x64 и win-arm64." >&2
    exit 1
fi

PROJECT_FILE="$PROJECT_ROOT/src/WorkTimeRegister/WorkTimeRegister.csproj"
INSTALLER_PROJECT="$PROJECT_ROOT/packaging/windows/WorkTimeRegister.Installer/WorkTimeRegister.Installer.csproj"
INFO_PLIST="$PROJECT_ROOT/packaging/macos/Info.plist"
APP_VERSION="$(/usr/bin/plutil -extract CFBundleShortVersionString raw "$INFO_PLIST")"
ARTIFACTS_DIR="$PROJECT_ROOT/artifacts"
WORK_DIR="$(mktemp -d /private/tmp/worktimeregister-windows-installer.XXXXXX)"
PUBLISH_DIR="$WORK_DIR/publish"
INSTALLER_PUBLISH_DIR="$WORK_DIR/installer"
PAYLOAD_FILE="$WORK_DIR/WorkTimeRegister-payload.zip"
SETUP_FILE="$ARTIFACTS_DIR/WorkTimeRegister-$APP_VERSION-$RUNTIME-Setup.exe"

cleanup() {
    rm -rf -- "$WORK_DIR"
}

trap cleanup EXIT

mkdir -p "$ARTIFACTS_DIR" "$PUBLISH_DIR" "$INSTALLER_PUBLISH_DIR"
rm -f -- "$SETUP_FILE"

dotnet publish "$PROJECT_FILE" \
    --configuration Release \
    --runtime "$RUNTIME" \
    --self-contained true \
    --output "$PUBLISH_DIR" \
    -p:Version="$APP_VERSION" \
    -p:DebugType=None \
    -p:DebugSymbols=false

(
    cd "$PUBLISH_DIR"
    /usr/bin/zip -q -r -9 "$PAYLOAD_FILE" .
)

dotnet publish "$INSTALLER_PROJECT" \
    --configuration Release \
    --runtime "$RUNTIME" \
    --self-contained true \
    --output "$INSTALLER_PUBLISH_DIR" \
    -p:Version="$APP_VERSION" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:InstallerPayload="$PAYLOAD_FILE"

cp "$INSTALLER_PUBLISH_DIR/WorkTimeRegister-Setup.exe" "$SETUP_FILE"

echo "Готов Windows-инсталлятор: $SETUP_FILE"
