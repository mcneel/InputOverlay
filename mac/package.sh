#!/bin/bash
# Build the universal RhinoInputOverlay.app and zip it for distribution.
#
# Uses `ditto` (not Finder's Compress) so the .app bundle structure, symlinks and code signature
# are preserved exactly inside the archive.
#
#   cd mac && ./package.sh   →   RhinoInputOverlay.zip
#
# Note: the app is only ad-hoc signed, not notarized. Recipients who download the zip will hit
# Gatekeeper on first launch and must right-click → Open (or System Settings → Privacy & Security
# → Open Anyway). Frictionless distribution needs a Developer ID signature + notarization.
set -euo pipefail
cd "$(dirname "$0")"

APP="RhinoInputOverlay.app"
ZIP="RhinoInputOverlay.zip"

./make-app.sh

echo "==> Zipping ${APP}…"
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"

echo "==> Done: $ZIP ($(du -h "$ZIP" | cut -f1))"
echo "    Universal: $(lipo -archs "$APP/Contents/MacOS/RhinoInputOverlay")"
