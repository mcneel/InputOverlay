// Rhino Input Overlay — macOS port (Swift + AppKit).
//
// A borderless, always-on-top, click-through overlay that shows which mouse button and which
// modifier keys (Ctrl/Cmd/Alt/Shift) are currently pressed, near the cursor. Only visible while
// Rhino is the frontmost application. Runs as an accessory (no Dock icon); the input is read from
// snapshot APIs (NSEvent.pressedMouseButtons / .modifierFlags) so no Accessibility permission is
// needed, and the scroll wheel is watched via a global scroll-wheel monitor (also permission-free).
//
// Build/run:
//   cd mac
//   swiftc -O -o RhinoInputOverlay main.swift -framework Cocoa
//   ./RhinoInputOverlay
//
// This mirrors windows/Program.cs. macOS is point-based and Retina-correct, so there is no DPI
// scaling math (the layered-window / PerMonitorV2 plumbing on Windows has no equivalent here), and
// z-order over tooltips is handled by a high window level rather than a re-raise hook.

import Cocoa

// MARK: - Helpers

@inline(__always) func clampInt(_ v: Int, _ lo: Int, _ hi: Int) -> Int { min(max(v, lo), hi) }

enum MouseButton { case none, left, right, middle }

// Colour stored as decimal 0–255 components with alpha, matching the Windows settings model.
struct RGBA: Codable, Equatable {
  var r: Int, g: Int, b: Int, a: Int

  var color: NSColor {
    NSColor(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255,
            alpha: CGFloat(a) / 255)
  }

  // RGB only, fully opaque — what the colour wells display (the picker chooses RGB; alpha is kept).
  var opaqueColor: NSColor {
    NSColor(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: 1)
  }

  static func rgb(of c: NSColor) -> (Int, Int, Int) {
    let s = c.usingColorSpace(.sRGB) ?? c
    return (Int((s.redComponent * 255).rounded()),
            Int((s.greenComponent * 255).rounded()),
            Int((s.blueComponent * 255).rounded()))
  }
}

// MARK: - Live, mutable settings (read every frame by the overlay; edited live by the dialog)

final class OverlaySettings {
  var textBackground  = RGBA(r: 255, g: 255, b: 255, a: 230) // 10% transparent white
  var mouseBackground = RGBA(r: 255, g: 255, b: 255, a: 230) // same as text background
  var buttonColor     = RGBA(r: 45,  g: 45,  b: 45,  a: 235) // pressed-button fill
  var borderColor     = RGBA(r: 80,  g: 80,  b: 80,  a: 180) // outline of badge + mouse
  var textColor       = RGBA(r: 17,  g: 17,  b: 17,  a: 255) // modifier text

  let shadowColor = RGBA(r: 0, g: 0, b: 0, a: 55)            // not user-editable

  var mouseSize = 42      // mouse-glyph height (points), range 16…120
  var textSize  = 13      // modifier-text size (points), range 8…48
  var offsetX   = -20     // horizontal placement vs. cursor: +right / -left
  var offsetY   = 20      // vertical placement vs. cursor:   +down / -up
  var scalePercent = 100  // global size multiplier in 25% steps (100–300); scales the whole
                          // overlay (glyph, text, distances, borders) uniformly.

  // Global scale folded into every drawn dimension (the macOS analogue of the Windows DPI scale).
  var scale: CGFloat { CGFloat(scalePercent) / 100 }

  // Mouse glyph keeps its 26:42 aspect; only the size knob (height) varies.
  var mouseWidth: CGFloat  { CGFloat(mouseSize) * (26.0 / 42.0) }
  var mouseHeight: CGFloat { CGFloat(mouseSize) }

  func applyDefaults() {
    let d = OverlaySettings()
    textBackground = d.textBackground
    mouseBackground = d.mouseBackground
    buttonColor = d.buttonColor
    borderColor = d.borderColor
    textColor = d.textColor
    mouseSize = d.mouseSize
    textSize = d.textSize
    offsetX = d.offsetX
    offsetY = d.offsetY
    scalePercent = d.scalePercent
  }

  // ---- Persistence (~/Library/Application Support/RhinoInputOverlay/settings.json) ----------
  private struct Data: Codable {
    var textBackground: RGBA
    var mouseBackground: RGBA
    var buttonColor: RGBA
    var borderColor: RGBA
    var textColor: RGBA
    var mouseSize: Int
    var textSize: Int
    var offsetX: Int
    var offsetY: Int
    var scalePercent: Int?   // optional: older settings files predate this field
  }

  static var fileURL: URL {
    let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
    return base.appendingPathComponent("RhinoInputOverlay", isDirectory: true)
               .appendingPathComponent("settings.json")
  }

  func save() {
    let d = Data(textBackground: textBackground, mouseBackground: mouseBackground,
                 buttonColor: buttonColor, borderColor: borderColor, textColor: textColor,
                 mouseSize: mouseSize, textSize: textSize, offsetX: offsetX, offsetY: offsetY,
                 scalePercent: scalePercent)
    do {
      let url = OverlaySettings.fileURL
      try FileManager.default.createDirectory(at: url.deletingLastPathComponent(),
                                              withIntermediateDirectories: true)
      let enc = JSONEncoder()
      enc.outputFormatting = .prettyPrinted
      try enc.encode(d).write(to: url)
    } catch { /* settings are best-effort; ignore IO/permission errors */ }
  }

  static func load() -> OverlaySettings {
    let s = OverlaySettings()
    guard let raw = try? Foundation.Data(contentsOf: fileURL),
          let d = try? JSONDecoder().decode(Data.self, from: raw) else { return s }
    s.textBackground = d.textBackground
    s.mouseBackground = d.mouseBackground
    s.buttonColor = d.buttonColor
    s.borderColor = d.borderColor
    s.textColor = d.textColor
    s.mouseSize = clampInt(d.mouseSize, 16, 120)
    s.textSize = clampInt(d.textSize, 8, 48)
    s.offsetX = clampInt(d.offsetX, -50, 50)
    s.offsetY = clampInt(d.offsetY, -50, 50)
    // Snap to the nearest 25% step; clamp to 100–300. Missing field (older files) → 100%.
    let sp = d.scalePercent ?? 0
    s.scalePercent = sp < 100 ? 100 : clampInt((sp + 12) / 25 * 25, 100, 300)
    return s
  }
}

// MARK: - The overlay view (draws the badge + mouse glyph)

final class OverlayView: NSView {
  let settings: OverlaySettings

  // Current input snapshot, refreshed by the controller each frame.
  var button: MouseButton = .none
  var mods: String = ""

  // Scroll-wheel flash: direction (+1 up / -1 down / 0 none) and an expiry the draw loop checks.
  var scrollDir = 0
  var scrollUntil = Date.distantPast

  // Layout constants, multiplied by the global user scale so the whole overlay grows uniformly.
  private var scale: CGFloat { settings.scale }
  private var gap: CGFloat { 8 * scale }
  private var padX: CGFloat { 9 * scale }
  private var padY: CGFloat { 5 * scale }
  private var edgePad: CGFloat { 4 * scale }

  init(settings: OverlaySettings) {
    self.settings = settings
    super.init(frame: .zero)
    wantsLayer = true
  }
  required init?(coder: NSCoder) { fatalError("not used") }

  // Top-down coordinates (matches the Windows drawing logic directly).
  override var isFlipped: Bool { true }

  // Modifier symbols render 20% larger than the configured text size, with letter spacing so the
  // ⌃⌥⇧⌘ glyphs don't crowd each other.
  private var font: NSFont { NSFont.systemFont(ofSize: CGFloat(settings.textSize) * 1.2 * scale) }
  private var kern: CGFloat { font.pointSize * 0.38 }

  private func textAttributes() -> [NSAttributedString.Key: Any] {
    [.font: font, .foregroundColor: settings.textColor.color, .kern: kern]
  }

  // Measured text width, minus the trailing kern that `.kern` adds after the last glyph (so the
  // badge isn't right-heavy).
  private func textMeasure() -> NSSize {
    var s = (mods as NSString).size(withAttributes: textAttributes())
    if !mods.isEmpty { s.width -= kern }
    return s
  }

  // Exact content size so the window is sized to fit (mirrors Render() measuring first).
  func contentSize() -> NSSize {
    let glyphW = settings.mouseWidth * scale
    let glyphH = settings.mouseHeight * scale

    var badgeW: CGFloat = 0, badgeH: CGFloat = 0
    if !mods.isEmpty {
      let ts = textMeasure()
      badgeW = ts.width + 2 * padX
      badgeH = ts.height + 2 * padY
    }

    let h = ceil(max(badgeH, glyphH)) + edgePad
    let w = ceil((mods.isEmpty ? 0 : badgeW + gap) + glyphW) + edgePad
    return NSSize(width: w, height: h)
  }

  override func draw(_ dirtyRect: NSRect) {
    let glyphW = settings.mouseWidth * scale
    let glyphH = settings.mouseHeight * scale
    let centerY = bounds.height / 2

    // Anchor the glyph to the view's right edge and lay the badge out to its left. The window's
    // right edge is pinned near the cursor, so anchoring the glyph from the right keeps it exactly
    // put — independent of the badge width and content-size rounding — while the text grows
    // leftward. (Accumulating from the left instead let ~1px of rounding drift move the glyph.)
    let glyphLeft = bounds.width - 2 * scale - glyphW

    if !mods.isEmpty {
      let ts = textMeasure()
      let badgeW = ts.width + 2 * padX
      let badgeH = ts.height + 2 * padY
      let badgeX = glyphLeft - gap - badgeW
      let rect = NSRect(x: badgeX, y: centerY - badgeH / 2, width: badgeW, height: badgeH)
      let path = NSBezierPath(roundedRect: rect, xRadius: 6 * scale, yRadius: 6 * scale)
      settings.textBackground.color.setFill()
      path.fill()
      settings.borderColor.color.setStroke()
      path.lineWidth = 1 * scale
      path.stroke()
      (mods as NSString).draw(at: NSPoint(x: badgeX + padX, y: centerY - ts.height / 2),
                              withAttributes: textAttributes())
    }

    drawMouseGlyph(left: glyphLeft, centerY: centerY, w: glyphW, h: glyphH)
  }

  private func drawMouseGlyph(left: CGFloat, centerY: CGFloat, w: CGFloat, h: CGFloat) {
    let top = centerY - h / 2
    let cx = left + w / 2
    let radius = w * 0.5 // fully rounded ends → clean capsule outline

    let bodyRect = NSRect(x: left, y: top, width: w, height: h)
    let bodyPath = NSBezierPath(roundedRect: bodyRect, xRadius: radius, yRadius: radius)

    // Soft drop shadow (offset ~1 pt down), then the body fill.
    let shadowPath = NSBezierPath(roundedRect: NSRect(x: left, y: top + 1 * scale, width: w, height: h),
                                  xRadius: radius, yRadius: radius)
    settings.shadowColor.color.setFill()
    shadowPath.fill()
    settings.mouseBackground.color.setFill()
    bodyPath.fill()

    // Pressed button: fill just the top-left / top-right half, clipped to the rounded body so the
    // corners stay round. No divider line — the highlight alone indicates the press.
    let buttonBottom = top + h * 0.40
    if button == .left || button == .right {
      NSGraphicsContext.saveGraphicsState()
      bodyPath.addClip()
      settings.buttonColor.color.setFill()
      let rect = button == .left
        ? NSRect(x: left, y: top, width: cx - left, height: buttonBottom - top)
        : NSRect(x: cx, y: top, width: (left + w) - cx, height: buttonBottom - top)
      rect.fill()
      NSGraphicsContext.restoreGraphicsState()
    }

    // Small vertical pill for the scroll wheel near the top centre (base 4.5 × 9 pt).
    let ww: CGFloat = 4.5 * scale, wh: CGFloat = 9 * scale
    let wheel = NSRect(x: cx - ww / 2, y: top + 6 * scale, width: ww, height: wh)
    let wheelPath = NSBezierPath(roundedRect: wheel, xRadius: ww / 2, yRadius: ww / 2)
    (button == .middle ? settings.buttonColor.color : settings.mouseBackground.color).setFill()
    wheelPath.fill()
    settings.borderColor.color.setStroke()
    wheelPath.lineWidth = 1.4 * scale
    wheelPath.stroke()

    // Body outline on top.
    settings.borderColor.color.setStroke()
    bodyPath.lineWidth = 1.4 * scale
    bodyPath.stroke()

    // Scroll flash: a filled dot whose diameter is the pill width + 1 pt, centred on the rounded
    // cap at the top (scrolling up) or bottom (scrolling down). Relit briefly by each wheel notch.
    if scrollDir != 0 && Date() < scrollUntil {
      let r = ww / 2                 // pill radius (cap-centre reference)
      let dr = r + 0.5 * scale       // dot radius: 1 pt larger diameter than the pill
      let cyDot = scrollDir > 0 ? wheel.minY + r : wheel.maxY - r
      let dot = NSRect(x: cx - dr, y: cyDot - dr, width: dr * 2, height: dr * 2)
      settings.buttonColor.color.setFill()
      NSBezierPath(ovalIn: dot).fill()
    }
  }
}

// MARK: - The overlay window (borderless, clear, click-through, above tooltips)

final class OverlayWindow: NSWindow {
  init(view: NSView) {
    super.init(contentRect: NSRect(x: 0, y: 0, width: 1, height: 1),
               styleMask: .borderless, backing: .buffered, defer: false)
    isOpaque = false
    backgroundColor = .clear
    hasShadow = false
    ignoresMouseEvents = true               // click-through
    // Higher than tooltips/help tags so the overlay isn't covered (the macOS equivalent of the
    // Windows re-raise hook). pop-up-menu level sits above the .floating / tooltip band.
    level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.popUpMenuWindow)))
    collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
    contentView = view
  }

  // Never take focus / become the key or main window.
  override var canBecomeKey: Bool { false }
  override var canBecomeMain: Bool { false }
}

// MARK: - Settings window

// Retains a colour well and applies picks while keeping the configured alpha (picker sets RGB only).
final class WellBinding: NSObject {
  let well: NSColorWell
  let get: () -> RGBA
  let set: (RGBA) -> Void

  init(get: @escaping () -> RGBA, set: @escaping (RGBA) -> Void) {
    self.well = NSColorWell()
    self.get = get
    self.set = set
    super.init()
    well.color = get().opaqueColor
    well.target = self
    well.action = #selector(changed)
  }

  @objc func changed() {
    var rgba = get()
    let (r, g, b) = RGBA.rgb(of: well.color)
    rgba.r = r; rgba.g = g; rgba.b = b   // keep rgba.a
    set(rgba)
    well.color = rgba.opaqueColor        // strip any alpha the panel may have applied
  }

  func refresh() { well.color = get().opaqueColor }
}

// Retains a stepper + its value label.
final class StepperBinding: NSObject {
  let stepper = NSStepper()
  let label = NSTextField(labelWithString: "")
  let get: () -> Int
  let set: (Int) -> Void

  init(min: Int, max: Int, step: Int = 1, get: @escaping () -> Int, set: @escaping (Int) -> Void) {
    self.get = get
    self.set = set
    super.init()
    stepper.minValue = Double(min)
    stepper.maxValue = Double(max)
    stepper.increment = Double(step)
    stepper.valueWraps = false
    stepper.integerValue = get()
    stepper.target = self
    stepper.action = #selector(changed)
    stepper.controlSize = .small
    label.alignment = .right
    label.font = .monospacedDigitSystemFont(ofSize: NSFont.smallSystemFontSize, weight: .regular)
    refresh()
  }

  @objc func changed() {
    let v = stepper.integerValue
    set(v)
    label.stringValue = "\(v)"
  }

  func refresh() {
    stepper.integerValue = get()
    label.stringValue = "\(get())"
  }
}

final class SettingsWindowController: NSObject {
  let settings: OverlaySettings
  let onStart: () -> Void
  let onStop: () -> Void
  let window: NSWindow

  private var wells: [WellBinding] = []
  private var steppers: [StepperBinding] = []

  init(settings: OverlaySettings, onStart: @escaping () -> Void, onStop: @escaping () -> Void) {
    self.settings = settings
    self.onStart = onStart
    self.onStop = onStop
    window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 360, height: 400),
                      styleMask: [.titled, .closable], backing: .buffered, defer: false)
    super.init()

    window.title = "Rhino Input Overlay"
    window.isReleasedWhenClosed = false      // closing just hides it; reopen via the menu bar
    // Hide the (disabled) minimise/zoom traffic-light buttons — the window is fixed-size.
    window.standardWindowButton(.miniaturizeButton)?.isHidden = true
    window.standardWindowButton(.zoomButton)?.isHidden = true
    NSColorPanel.shared.showsAlpha = false   // the picker chooses RGB only

    let grid = NSGridView()
    grid.translatesAutoresizingMaskIntoConstraints = false
    grid.rowSpacing = 8
    grid.columnSpacing = 12
    // Centre each row's contents vertically. The default is baseline alignment, which has no
    // baseline to match in the colour-well rows and so misaligns the label against the pill.
    grid.rowAlignment = .none

    // Restore defaults (top).
    let restore = NSButton(title: "Restore defaults", target: self, action: #selector(restoreDefaults))
    let restoreRow = grid.addRow(with: [restore, NSGridCell.emptyContentView])
    restoreRow.mergeCells(in: NSRange(location: 0, length: 2))
    restore.frame.size = restore.fittingSize

    // Colour wells.
    addColorRow(grid, "Text background",    get: { self.settings.textBackground },  set: { self.settings.textBackground = $0 })
    addColorRow(grid, "Text color",         get: { self.settings.textColor },       set: { self.settings.textColor = $0 })
    addColorRow(grid, "Mouse background",   get: { self.settings.mouseBackground }, set: { self.settings.mouseBackground = $0 })
    addColorRow(grid, "Mouse button color", get: { self.settings.buttonColor },     set: { self.settings.buttonColor = $0 })
    addColorRow(grid, "Border color",       get: { self.settings.borderColor },     set: { self.settings.borderColor = $0 })

    // Steppers.
    addStepperRow(grid, "Mouse icon size", min: 16,  max: 120, get: { self.settings.mouseSize }, set: { self.settings.mouseSize = $0 })
    addStepperRow(grid, "Text size",       min: 8,   max: 48,  get: { self.settings.textSize },  set: { self.settings.textSize = $0 })
    addStepperRow(grid, "Distance X",      min: -50, max: 50,  get: { self.settings.offsetX },   set: { self.settings.offsetX = $0 })
    addStepperRow(grid, "Distance Y",      min: -50, max: 50,  get: { self.settings.offsetY },   set: { self.settings.offsetY = $0 })
    addStepperRow(grid, "Scale %",         min: 100, max: 300, step: 25, get: { self.settings.scalePercent }, set: { self.settings.scalePercent = $0 })

    // Start / Stop.
    let startBtn = NSButton(title: "Start", target: self, action: #selector(startClicked))
    let stopBtn  = NSButton(title: "Stop",  target: self, action: #selector(stopClicked))
    let buttons = NSStackView(views: [startBtn, stopBtn])
    buttons.orientation = .horizontal
    buttons.spacing = 8
    let buttonRow = grid.addRow(with: [buttons, NSGridCell.emptyContentView])
    buttonRow.mergeCells(in: NSRange(location: 0, length: 2))
    buttonRow.topPadding = 8

    if grid.numberOfColumns > 0 { grid.column(at: 0).xPlacement = .trailing }
    for r in 0..<grid.numberOfRows { grid.row(at: r).yPlacement = .center }

    let content = NSView()
    content.addSubview(grid)
    NSLayoutConstraint.activate([
      grid.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 16),
      grid.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -16),
      grid.topAnchor.constraint(equalTo: content.topAnchor, constant: 16),
      grid.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -16),
    ])
    window.contentView = content
    content.layoutSubtreeIfNeeded()
    window.setContentSize(NSSize(width: grid.fittingSize.width + 32,
                                 height: grid.fittingSize.height + 32))
    window.center()
  }

  private func addColorRow(_ grid: NSGridView, _ label: String,
                           get: @escaping () -> RGBA, set: @escaping (RGBA) -> Void) {
    let binding = WellBinding(get: get, set: set)
    wells.append(binding)
    binding.well.translatesAutoresizingMaskIntoConstraints = false
    binding.well.widthAnchor.constraint(equalToConstant: 64).isActive = true
    binding.well.heightAnchor.constraint(equalToConstant: 24).isActive = true
    grid.addRow(with: [NSTextField(labelWithString: label), binding.well])
  }

  private func addStepperRow(_ grid: NSGridView, _ label: String, min: Int, max: Int, step: Int = 1,
                             get: @escaping () -> Int, set: @escaping (Int) -> Void) {
    let binding = StepperBinding(min: min, max: max, step: step, get: get, set: set)
    steppers.append(binding)
    binding.label.translatesAutoresizingMaskIntoConstraints = false
    binding.label.widthAnchor.constraint(equalToConstant: 36).isActive = true
    let stack = NSStackView(views: [binding.label, binding.stepper])
    stack.orientation = .horizontal
    stack.spacing = 6
    grid.addRow(with: [NSTextField(labelWithString: label), stack])
  }

  @objc private func restoreDefaults() {
    settings.applyDefaults()
    wells.forEach { $0.refresh() }
    steppers.forEach { $0.refresh() }
    settings.save()
  }

  @objc private func startClicked() { onStart() }
  @objc private func stopClicked()  { onStop() }

  func show() {
    NSApp.activate(ignoringOtherApps: true)
    window.makeKeyAndOrderFront(nil)
  }
}

// MARK: - App delegate (timer, scroll monitor, foreground gating, start/stop)

final class AppDelegate: NSObject, NSApplicationDelegate {
  let settings = OverlaySettings.load()
  var overlayView: OverlayView!
  var overlayWindow: OverlayWindow!
  var settingsController: SettingsWindowController!
  var timer: Timer?
  var scrollMonitor: Any?
  var statusItem: NSStatusItem!
  var running = false

  func applicationDidFinishLaunching(_ notification: Notification) {
    NSApp.setActivationPolicy(.accessory) // no Dock icon
    NSApp.applicationIconImage = IconArtwork.image(size: 256)

    overlayView = OverlayView(settings: settings)
    overlayWindow = OverlayWindow(view: overlayView)

    settingsController = SettingsWindowController(settings: settings, onStart: start, onStop: stop)
    setupStatusItem()
    settingsController.show()

    // Global scroll-wheel monitor — fires for events delivered to other apps (e.g. Rhino). Global
    // monitors for scroll don't require Accessibility permission, unlike key/mouse-button polling.
    scrollMonitor = NSEvent.addGlobalMonitorForEvents(matching: .scrollWheel) { [weak self] event in
      guard let self = self else { return }
      // Normalise out "natural scrolling": macOS flips scrollingDeltaY's sign when natural
      // scrolling is on (isDirectionInvertedFromDevice), so undo it to get the true wheel
      // direction. +y = wheel up → flash the top cap; -y = down → bottom cap.
      var dy = event.scrollingDeltaY
      if event.isDirectionInvertedFromDevice { dy = -dy }
      if dy != 0 {
        self.overlayView.scrollDir = dy > 0 ? 1 : -1
        self.overlayView.scrollUntil = Date().addingTimeInterval(0.18) // ~180 ms
      }
    }

    let t = Timer(timeInterval: 1.0 / 60.0, repeats: true) { [weak self] _ in self?.tick() }
    RunLoop.main.add(t, forMode: .common)
    timer = t

    start() // auto-start on launch
  }

  func applicationWillTerminate(_ notification: Notification) {
    if let m = scrollMonitor { NSEvent.removeMonitor(m) }
    settings.save()
  }

  // Menu bar item with Settings / Start / Stop / Quit — the macOS equivalent of the Windows tray
  // icon. The app is an accessory (no Dock icon, not in Cmd-Tab), so this is its control surface.
  private func setupStatusItem() {
    statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    statusItem.button?.image = IconArtwork.menuBarImage()
    statusItem.button?.toolTip = "Rhino Input Overlay"

    let menu = NSMenu()
    menu.addItem(NSMenuItem(title: "Settings…", action: #selector(showSettings), keyEquivalent: ","))
    menu.addItem(.separator())
    menu.addItem(NSMenuItem(title: "Start", action: #selector(menuStart), keyEquivalent: ""))
    menu.addItem(NSMenuItem(title: "Stop", action: #selector(menuStop), keyEquivalent: ""))
    menu.addItem(.separator())
    menu.addItem(NSMenuItem(title: "Quit Rhino Input Overlay", action: #selector(quit), keyEquivalent: "q"))
    menu.items.forEach { $0.target = self }
    statusItem.menu = menu
  }

  @objc private func showSettings() { settingsController.show() }
  @objc private func menuStart() { start() }
  @objc private func menuStop() { stop() }
  @objc private func quit() { NSApp.terminate(nil) }

  func start() { running = true }

  func stop() {
    running = false
    overlayWindow.orderOut(nil)
  }

  private func tick() {
    guard running else { return }
    guard rhinoIsFrontmost() else {
      if overlayWindow.isVisible { overlayWindow.orderOut(nil) }
      return
    }

    overlayView.button = currentMouseButton()
    overlayView.mods = modifierText()

    let size = overlayView.contentSize()
    let loc = NSEvent.mouseLocation // screen coords, bottom-left origin, y up

    // Anchor the window's right edge at cursor.x + OffsetX (so the glyph holds position and the
    // text grows leftward); place its top OffsetY below the cursor. OffsetX/Y are signed points:
    // +right/-left, +down/-up. Screen origin is bottom-left, so "below" subtracts from y.
    let origin = NSPoint(x: loc.x + CGFloat(settings.offsetX) * settings.scale - size.width,
                         y: loc.y - CGFloat(settings.offsetY) * settings.scale - size.height)
    overlayWindow.setFrame(NSRect(origin: origin, size: size), display: false)
    overlayView.frame = NSRect(origin: .zero, size: size)

    if !overlayWindow.isVisible { overlayWindow.orderFrontRegardless() }
    overlayView.needsDisplay = true
  }

  private func rhinoIsFrontmost() -> Bool {
    guard let app = NSWorkspace.shared.frontmostApplication else { return false }
    // Never treat our own app as "Rhino" — our name/bundle id contain "rhino", so otherwise the
    // overlay would show whenever the settings window is the active app.
    if app.processIdentifier == getpid() { return false }
    if let bid = app.bundleIdentifier, bid.lowercased().contains("rhino") { return true }
    if let name = app.localizedName, name.lowercased().contains("rhino") { return true }
    return false
  }

  private func currentMouseButton() -> MouseButton {
    let b = NSEvent.pressedMouseButtons
    if b & 1 != 0 { return .left }
    if b & 2 != 0 { return .right }
    if b & 4 != 0 { return .middle }
    return .none
  }

  private func modifierText() -> String {
    let f = NSEvent.modifierFlags
    // Standard macOS symbols in menu order: Control ⌃, Option ⌥, Shift ⇧, Command ⌘.
    var s = ""
    if f.contains(.control) { s += "\u{2303}" }
    if f.contains(.option)  { s += "\u{2325}" }
    if f.contains(.shift)   { s += "\u{21E7}" }
    if f.contains(.command) { s += "\u{2318}" }
    return s
  }
}

// MARK: - App icon artwork (white mouse on a blue rounded tile)

enum IconArtwork {
  // Same design as the Windows .ico: blue rounded tile, white capsule mouse body, wheel pill cut
  // out in the tile blue. Used for NSApp.applicationIconImage and `--export-iconset` for an .icns.
  static func image(size: CGFloat) -> NSImage {
    let img = NSImage(size: NSSize(width: size, height: size))
    img.lockFocus()
    draw(size: size)
    img.unlockFocus()
    return img
  }

  static func draw(size s: CGFloat) {
    let tileBlue = NSColor(srgbRed: 45 / 255, green: 127 / 255, blue: 249 / 255, alpha: 1)

    let tile = NSRect(x: 0, y: 0, width: s, height: s)
    tileBlue.setFill()
    NSBezierPath(roundedRect: tile, xRadius: s * 0.22, yRadius: s * 0.22).fill()

    // White capsule body, centred, 26:42 aspect.
    let bodyH = s * 0.62
    let bodyW = bodyH * (26.0 / 42.0)
    let body = NSRect(x: (s - bodyW) / 2, y: (s - bodyH) / 2, width: bodyW, height: bodyH)
    NSColor.white.setFill()
    NSBezierPath(roundedRect: body, xRadius: bodyW / 2, yRadius: bodyW / 2).fill()

    // Wheel pill cut out in the tile blue, near the top of the body.
    let ww = bodyW * 0.18
    let wh = ww * 2
    let wheel = NSRect(x: body.midX - ww / 2, y: body.maxY - wh - bodyH * 0.12, width: ww, height: wh)
    tileBlue.setFill()
    NSBezierPath(roundedRect: wheel, xRadius: ww / 2, yRadius: ww / 2).fill()
  }

  // Monochrome template glyph for the menu bar (a small mouse outline). Template images adapt to
  // the menu bar appearance (light/dark) automatically.
  static func menuBarImage() -> NSImage {
    let h: CGFloat = 16
    let w = h * (26.0 / 42.0)
    let img = NSImage(size: NSSize(width: w, height: h))
    img.lockFocus()
    let inset: CGFloat = 0.75
    let rect = NSRect(x: inset, y: inset, width: w - 2 * inset, height: h - 2 * inset)
    NSColor.black.setStroke()
    let body = NSBezierPath(roundedRect: rect, xRadius: rect.width / 2, yRadius: rect.width / 2)
    body.lineWidth = 1.2
    body.stroke()
    let ww = rect.width * 0.26
    let wh = ww * 2
    let wheel = NSRect(x: rect.midX - ww / 2, y: rect.maxY - wh - 2.0, width: ww, height: wh)
    let wheelPath = NSBezierPath(roundedRect: wheel, xRadius: ww / 2, yRadius: ww / 2)
    wheelPath.lineWidth = 1.0
    wheelPath.stroke()
    img.unlockFocus()
    img.isTemplate = true
    return img
  }

  // Writes a .iconset directory; run `iconutil -c icns <dir>` to produce RhinoInputOverlay.icns.
  static func exportIconset(to dir: String) {
    let sizes: [(Int, Int)] = [(16, 1), (16, 2), (32, 1), (32, 2), (128, 1), (128, 2),
                               (256, 1), (256, 2), (512, 1), (512, 2)]
    let fm = FileManager.default
    try? fm.createDirectory(atPath: dir, withIntermediateDirectories: true)
    for (pt, scale) in sizes {
      let px = pt * scale
      let img = image(size: CGFloat(px))
      guard let tiff = img.tiffRepresentation,
            let rep = NSBitmapImageRep(data: tiff),
            let png = rep.representation(using: .png, properties: [:]) else { continue }
      let name = scale == 1 ? "icon_\(pt)x\(pt).png" : "icon_\(pt)x\(pt)@2x.png"
      try? png.write(to: URL(fileURLWithPath: dir).appendingPathComponent(name))
    }
    print("Wrote iconset to \(dir). Now run: iconutil -c icns \(dir)")
  }
}

// MARK: - Entry point

if CommandLine.arguments.count >= 3, CommandLine.arguments[1] == "--export-iconset" {
  IconArtwork.exportIconset(to: CommandLine.arguments[2])
} else {
  let app = NSApplication.shared
  let delegate = AppDelegate()
  app.delegate = delegate
  app.run()
}
