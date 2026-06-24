#!/bin/bash
# Build RhinoInputOverlay.app — a proper macOS app bundle.
#
# A bare Mach-O executable can't reliably show windows or activate on macOS; AppKit apps need to
# live in a .app bundle with an Info.plist. This script compiles main.swift, generates the .icns
# from the in-code artwork, and assembles the bundle. Run it, then double-click RhinoInputOverlay.app.
#
#   cd mac && ./make-app.sh && open RhinoInputOverlay.app
set -euo pipefail
cd "$(dirname "$0")"

APP="RhinoInputOverlay.app"
BIN="RhinoInputOverlay"

echo "==> Compiling universal (arm64 + x86_64)…"
swiftc -O -target arm64-apple-macos11  -o "$BIN.arm64"  main.swift -framework Cocoa
swiftc -O -target x86_64-apple-macos11 -o "$BIN.x86_64" main.swift -framework Cocoa
lipo -create -output "$BIN" "$BIN.arm64" "$BIN.x86_64"
rm -f "$BIN.arm64" "$BIN.x86_64"

echo "==> Generating icon…"
rm -rf RhinoInputOverlay.iconset RhinoInputOverlay.icns
"./$BIN" --export-iconset RhinoInputOverlay.iconset >/dev/null
iconutil -c icns RhinoInputOverlay.iconset
rm -rf RhinoInputOverlay.iconset

echo "==> Assembling ${APP}…"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/$BIN"
cp RhinoInputOverlay.icns "$APP/Contents/Resources/RhinoInputOverlay.icns"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>            <string>Rhino Input Overlay</string>
  <key>CFBundleDisplayName</key>     <string>Rhino Input Overlay</string>
  <key>CFBundleIdentifier</key>      <string>com.mcneel.rhinoinputoverlay</string>
  <key>CFBundleVersion</key>         <string>1.0</string>
  <key>CFBundleShortVersionString</key> <string>1.0</string>
  <key>CFBundleExecutable</key>      <string>RhinoInputOverlay</string>
  <key>CFBundleIconFile</key>        <string>RhinoInputOverlay.icns</string>
  <key>CFBundlePackageType</key>     <string>APPL</string>
  <key>LSMinimumSystemVersion</key>  <string>11.0</string>
  <key>LSUIElement</key>             <true/>
  <key>NSHighResolutionCapable</key> <true/>
</dict>
</plist>
PLIST

# Ad-hoc code-sign so Gatekeeper/launchd treat it as a real app (no developer account needed).
codesign --force --deep --sign - "$APP" 2>/dev/null || echo "   (codesign skipped)"

echo "==> Done: $APP"
echo "    Launch with:  open $APP"
