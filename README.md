# Rhino Input Overlay

A small always-on-top, click-through overlay that shows, near the cursor, which mouse button
and which modifier keys (Ctrl/Cmd/Alt/Shift) are currently pressed. Intended for
documentation / tutorial / screencast use.

The overlay is only visible while **Rhino is the frontmost application**, and it works over
the whole Rhino UI — viewports, toolbars, panels, and dialogs — because it is a separate
top-level window rather than a viewport display conduit.

The Windows app is the current, maintained implementation. A native macOS port is **planned**
(see below). Both share the same visual design: a light `#eee`/`#333` modifier badge followed by
a mouse glyph with the pressed button highlighted blue.

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

## macOS — planned

A native macOS port (Swift + AppKit) is planned but not yet available in this repository.
The intended approach: a borderless floating `NSWindow` with `ignoresMouseEvents` (click-through),
global input polled from `NSEvent.modifierFlags` / `NSEvent.pressedMouseButtons` (snapshot class
properties — no Accessibility permission needed), and the active app checked via
`NSWorkspace.frontmostApplication`. Contributions welcome.

## Tuning

The Windows app exposes tunables near the top of the source (`OffsetX/Y`, glyph size,
badge padding, colors). Edit and rebuild.
