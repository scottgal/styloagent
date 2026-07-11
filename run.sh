#!/usr/bin/env bash
# Build Styloagent and launch it as a proper macOS .app bundle.
#
# On macOS the app MUST run as a bundle (not `dotnet run`): only a regular foreground
# application gets a working native folder picker AND lets Dock realize the terminal /
# document panes. Launching the raw apphost from a terminal leaves it a background
# process, so the picker is unresponsive and the centre panes never render.
#
# Usage:
#   ./run.sh                      # Debug build → Welcome screen (pick a folder)
#   ./run.sh Release              # Release build
#   ./run.sh Debug /path/to/repo  # open that project directly (skips the Welcome screen)
set -euo pipefail
cd "$(dirname "$0")"

CONFIG="${1:-Debug}"
BIN="src/Styloagent.App/bin/${CONFIG}/net10.0"
APP="bin/Styloagent.app"

echo "▸ building (${CONFIG})…"
dotnet build src/Styloagent.App -c "${CONFIG}" --nologo -v quiet

echo "▸ assembling ${APP}…"
rm -rf "${APP}"
mkdir -p "${APP}/Contents/MacOS" "${APP}/Contents/Resources"
cp -R "${BIN}/." "${APP}/Contents/MacOS/"
chmod +x "${APP}/Contents/MacOS/Styloagent.App"
cp src/Styloagent.App/icon.icns "${APP}/Contents/Resources/icon.icns"

cat > "${APP}/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>Styloagent.App</string>
  <key>CFBundleIconFile</key><string>icon</string>
  <key>CFBundleIdentifier</key><string>com.mostlylucid.styloagent</string>
  <key>CFBundleName</key><string>Styloagent</string>
  <key>CFBundleDisplayName</key><string>Styloagent</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
PLIST

xattr -dr com.apple.quarantine "${APP}" 2>/dev/null || true

# Optional: open a specific project directly (convenience — sets the launchd env the app reads).
if [ -n "${2:-}" ]; then
  launchctl setenv STYLOAGENT_REPO "${2}"
  echo "▸ opening project: ${2}"
else
  launchctl unsetenv STYLOAGENT_REPO 2>/dev/null || true
fi

echo "▸ launching…"
open "${APP}"
