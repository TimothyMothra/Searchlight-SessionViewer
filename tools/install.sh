#!/usr/bin/env bash
#
# Installs (or uninstalls) the cross-platform Avalonia host on macOS/Linux —
# the counterpart of tools/install.ps1 (which installs the WinUI host on
# Windows). Publishes a SELF-CONTAINED build (no .NET runtime needed on the
# target) to a stable location and creates launcher + run-at-login entries:
#
#   macOS:  ~/Applications/Searchlight.app        (Spotlight / Launchpad)
#           ~/Library/LaunchAgents/com.searchlight.viewer.plist  [--no-startup skips]
#   Linux:  ~/.local/share/searchlight/app
#           ~/.local/share/applications/searchlight.desktop      (app menus)
#           ~/.config/autostart/searchlight.desktop              [--no-startup skips]
#
# Usage:
#   tools/install.sh                       # full install
#   tools/install.sh --no-startup         # install, but don't run at login
#   tools/install.sh --skip-publish       # reuse the last published output
#   tools/install.sh --configuration Debug
#   tools/install.sh --uninstall          # remove launchers + installed app
#
# (Do NOT publish the Demo configuration here — it forces the synthetic mock
# datastore.)

set -euo pipefail

ACTION=install
CONFIGURATION=Release
NO_STARTUP=0
SKIP_PUBLISH=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --uninstall)      ACTION=uninstall ;;
        --no-startup)     NO_STARTUP=1 ;;
        --skip-publish)   SKIP_PUBLISH=1 ;;
        --configuration)
            [[ $# -ge 2 ]] || { echo "--configuration needs a value" >&2; exit 1; }
            CONFIGURATION="$2"; shift ;;
        -h|--help)        sed -n '2,22p' "$0"; exit 0 ;;
        *) echo "Unknown option: $1 (see --help)" >&2; exit 1 ;;
    esac
    shift
done

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Searchlight.Avalonia/Searchlight.Avalonia.csproj"
OS="$(uname -s)"
ARCH="$(uname -m)"

case "$OS-$ARCH" in
    Darwin-arm64)  RID=osx-arm64 ;;
    Darwin-x86_64) RID=osx-x64 ;;
    Linux-aarch64) RID=linux-arm64 ;;
    Linux-x86_64)  RID=linux-x64 ;;
    *) echo "Unsupported platform: $OS/$ARCH" >&2; exit 1 ;;
esac

if [[ "$OS" == Darwin ]]; then
    APP_BUNDLE="$HOME/Applications/Searchlight.app"
    APP_DIR="$APP_BUNDLE/Contents/MacOS"
    LAUNCH_AGENT="$HOME/Library/LaunchAgents/com.searchlight.viewer.plist"
else
    APP_DIR="$HOME/.local/share/searchlight/app"
    DESKTOP_FILE="$HOME/.local/share/applications/searchlight.desktop"
    AUTOSTART_FILE="$HOME/.config/autostart/searchlight.desktop"
    ICON_FILE="$HOME/.local/share/icons/hicolor/256x256/apps/searchlight.png"
fi

uninstall() {
    if [[ "$OS" == Darwin ]]; then
        if [[ -f "$LAUNCH_AGENT" ]]; then
            launchctl unload "$LAUNCH_AGENT" 2>/dev/null || true
            rm -f "$LAUNCH_AGENT"
            echo "Removed $LAUNCH_AGENT"
        fi
        [[ -d "$APP_BUNDLE" ]] && rm -rf "$APP_BUNDLE" && echo "Removed $APP_BUNDLE"
    else
        rm -f "$DESKTOP_FILE" "$AUTOSTART_FILE" "$ICON_FILE"
        [[ -d "$APP_DIR" ]] && rm -rf "$(dirname "$APP_DIR")"
        echo "Removed launchers and $APP_DIR"
    fi
    echo "Uninstall complete."
}

publish() {
    echo "Publishing $CONFIGURATION/$RID (self-contained) -> $APP_DIR"
    # Clean the publish target so a reinstall can't leave stale assemblies from
    # a previous publish next to the new output.
    rm -rf "$APP_DIR"
    dotnet publish "$PROJECT" -c "$CONFIGURATION" -r "$RID" --self-contained true \
        -o "$APP_DIR"
}

install_macos() {
    mkdir -p "$APP_BUNDLE/Contents/Resources"

    # Minimal bundle metadata so Finder/Spotlight treat it as an app. The
    # executable is the published apphost, run in place.
    cat > "$APP_BUNDLE/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Searchlight</string>
    <key>CFBundleDisplayName</key><string>Searchlight</string>
    <key>CFBundleIdentifier</key><string>com.searchlight.viewer</string>
    <key>CFBundleVersion</key><string>1.0</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleExecutable</key><string>Searchlight.Avalonia</string>
    <key>CFBundleIconFile</key><string>Searchlight.icns</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
</dict>
</plist>
PLIST

    # Best-effort icon: build an .icns from the shared 256px PNG (sips and
    # iconutil ship with macOS; skip silently if either is missing).
    local png="$REPO_ROOT/src/Searchlight.Avalonia/Assets/app_256.png"
    if command -v sips >/dev/null && command -v iconutil >/dev/null && [[ -f "$png" ]]; then
        local iconset
        iconset="$(mktemp -d)/Searchlight.iconset"
        mkdir -p "$iconset"
        for size in 16 32 64 128 256; do
            sips -z $size $size "$png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
        done
        iconutil -c icns "$iconset" -o "$APP_BUNDLE/Contents/Resources/Searchlight.icns" \
            && echo "Created app icon"
        rm -rf "$(dirname "$iconset")"
    fi

    if [[ $NO_STARTUP -eq 0 ]]; then
        mkdir -p "$(dirname "$LAUNCH_AGENT")"
        cat > "$LAUNCH_AGENT" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>com.searchlight.viewer</string>
    <key>ProgramArguments</key>
    <array><string>$APP_DIR/Searchlight.Avalonia</string></array>
    <key>RunAtLoad</key><true/>
</dict>
</plist>
PLIST
        launchctl unload "$LAUNCH_AGENT" 2>/dev/null || true
        launchctl load "$LAUNCH_AGENT"
        echo "Run-at-login: $LAUNCH_AGENT"
    fi

    echo "Installed: $APP_BUNDLE (launch from Spotlight: 'Searchlight')"
}

install_linux() {
    local png="$REPO_ROOT/src/Searchlight.Avalonia/Assets/app_256.png"
    if [[ -f "$png" ]]; then
        mkdir -p "$(dirname "$ICON_FILE")"
        cp "$png" "$ICON_FILE"
    fi

    mkdir -p "$(dirname "$DESKTOP_FILE")"
    cat > "$DESKTOP_FILE" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Searchlight
Comment=Historical session viewer for Copilot CLI and Claude Code
Exec="$APP_DIR/Searchlight.Avalonia"
Icon=searchlight
Terminal=false
Categories=Development;Utility;
DESKTOP

    if [[ $NO_STARTUP -eq 0 ]]; then
        mkdir -p "$(dirname "$AUTOSTART_FILE")"
        cp "$DESKTOP_FILE" "$AUTOSTART_FILE"
        echo "Run-at-login: $AUTOSTART_FILE"
    fi

    echo "Installed: $APP_DIR (launch from your app menu: 'Searchlight')"
}

if [[ "$ACTION" == uninstall ]]; then
    uninstall
    exit 0
fi

[[ $SKIP_PUBLISH -eq 0 ]] && publish

if [[ "$OS" == Darwin ]]; then
    install_macos
else
    install_linux
fi
