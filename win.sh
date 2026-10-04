#!/usr/bin/env bash
# Build/run from WSL using the Windows .NET SDK.
#
# Windows build tools (mt.exe, the XAML compiler) can't handle \\wsl.localhost paths, so this
# mirrors the source to a folder on the Windows drive and runs dotnet.exe there.
#
#   ./win.sh build              Debug build
#   ./win.sh run                Build and launch (stops a running copy first)
#   ./win.sh publish            Release build to %LOCALAPPDATA%\Programs\CalendarFlyout, then launch it
#   ./win.sh stop               Kill a running CalendarFlyout.exe
set -euo pipefail

SRC="$(cd "$(dirname "$0")" && pwd)"
WIN_LOCALAPPDATA="$(cmd.exe /c 'echo %LOCALAPPDATA%' 2>/dev/null | tr -d '\r')"
MIRROR="$(wslpath "$WIN_LOCALAPPDATA")/CalendarFlyout-build"
PUBLISH_DIR="$WIN_LOCALAPPDATA\\Programs\\CalendarFlyout"

sync() {
    mkdir -p "$MIRROR"
    rsync -a --delete \
        --exclude .git/ --exclude bin/ --exclude obj/ --exclude .vs/ \
        --exclude '*:Zone.Identifier' --exclude 'client_secret*.json' \
        --filter 'protect bin/' --filter 'protect obj/' \
        "$SRC/" "$MIRROR/"
}

stop() {
    taskkill.exe /IM CalendarFlyout.exe /F >/dev/null 2>&1 || true
}

# Launch through Explorer. Anything started from here (directly, or via cmd's `start`)
# inherits WSL's interop pipes, and the script then waits until the app exits.
# explorer.exe always exits with status 1, so ignore it.
launch() {
    echo "Launching $1"
    (cd "$MIRROR" && explorer.exe "$1" </dev/null >/dev/null 2>&1) || true
}

dn() {
    (cd "$MIRROR" && dotnet.exe "$@")
}

case "${1:-build}" in
    build)
        sync
        dn build
        ;;
    run)
        stop
        sync
        dn build
        exe="$(find "$MIRROR/bin" -name CalendarFlyout.exe -path '*Debug*' | head -1)"
        launch "$(wslpath -w "$exe")"
        ;;
    publish)
        stop
        sync
        dn publish -c Release -o "$PUBLISH_DIR"
        echo "Published to $PUBLISH_DIR"
        launch "$PUBLISH_DIR\\CalendarFlyout.exe"
        ;;
    stop)
        stop
        ;;
    *)
        sed -n '2,11p' "$0"
        exit 1
        ;;
esac
