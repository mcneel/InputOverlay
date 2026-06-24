# Rhino Input Overlay

A small always-on-top, click-through overlay that shows, near the cursor, which mouse button
and which modifier keys (Ctrl/Cmd/Alt/Shift) are currently pressed. Intended for
documentation / tutorial / screencast use.

The overlay is only visible while **Rhino is the frontmost application**, and it works over
the whole Rhino UI — viewports, toolbars, panels, and dialogs — because it is a separate
top-level window rather than a viewport display conduit.

Native Windows and macOS implementations are both available. They share the same visual design:
a translucent white modifier badge next to a capsule mouse glyph whose pressed half is highlighted.
Windows shows modifiers as `Ctrl`/`Alt`/`Shift`; macOS uses the native `⌃` `⌥` `⇧` `⌘` symbols
(and includes Cmd). Both expose a Settings window for colours, sizes and placement.

## Windows (`windows/`)

.NET 8 WinForms app. A layered, click-through, top-most window updated via
`UpdateLayeredWindow` (per-pixel alpha → crisp glyph, no transparency-key halo). Global input
is read with `GetAsyncKeyState` / `GetCursorPos`; the active app is checked with
`GetForegroundWindow` (process name `Rhino`). DPI-aware (PerMonitorV2). No special permissions.

```
dotnet run --project windows
# or build:
dotnet build windows -c Release
# → windows/bin/Release/net8.0-windows/RhinoInputOverlay.exe
```

A tray icon appears; right-click → **Exit** to quit.

## macOS (`mac/`)

Native Swift + AppKit app (single file, `mac/main.swift`). A borderless, click-through, top-most
`NSWindow` drawn with Core Graphics. Global input is read from snapshot APIs
(`NSEvent.pressedMouseButtons` / `NSEvent.modifierFlags`) plus a global `.scrollWheel` monitor for
the wheel; the active app is checked via `NSWorkspace.frontmostApplication` (process name `Rhino`).
No Accessibility permission required. Runs as an accessory (no Dock icon) with a menu bar item for
Settings / Start / Stop / Quit. Point-based and Retina-correct (no DPI scaling needed).

```
cd mac
swiftc -O -o RhinoInputOverlay main.swift -framework Cocoa
./RhinoInputOverlay
```

Or build a proper `.app` bundle — so it launches without a Terminal window, carries the icon, and
can be zipped for distribution:

```
cd mac
./make-app.sh && open RhinoInputOverlay.app   # universal (arm64 + x86_64) .app + .icns
./package.sh                                   # → RhinoInputOverlay.zip
```

The `.app`/`.zip` are universal but only ad-hoc signed (not notarized): on first launch from a
download, right-click → **Open** (or System Settings → Privacy & Security → **Open Anyway**).

## Tuning

Both apps have a **Settings** window (the five colours, mouse/text size, and signed X/Y distance
from the cursor) that applies live. Settings persist per-user:
`%AppData%\RhinoInputOverlay\settings.json` on Windows,
`~/Library/Application Support/RhinoInputOverlay/settings.json` on macOS.
