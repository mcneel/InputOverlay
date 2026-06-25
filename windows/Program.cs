using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace RhinoInputOverlay
{
  // Standalone, always-on-top, click-through overlay that shows which mouse button and
  // which modifier keys (Ctrl/Alt/Shift) are currently pressed, near the cursor.
  // Only visible while Rhino is the foreground application.
  //
  // Implemented as a per-pixel-alpha layered window (UpdateLayeredWindow) so the glyph is
  // crisply anti-aliased with true transparency and does not intercept any mouse input.
  static class Program
  {
    const string QuitEventName = "RhinoInputOverlay.Quit.v1";

    [STAThread]
    static void Main()
    {
      // Single-instance toggle: if an instance is already running, signal it to quit and
      // exit immediately (so re-launching the app turns the overlay off).
      var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName, out bool createdNew);
      if (!createdNew)
      {
        quitEvent.Set();
        return;
      }

      // Must be set before any window is created so GetDpiForWindow reports the real monitor
      // DPI and Windows does NOT bitmap-stretch our layered window (which would blur it).
      Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      Application.Run(new OverlayContext(quitEvent));
    }
  }

  // Live, mutable appearance/placement settings shared by the overlay and the settings dialog.
  // The overlay reads these every frame, so edits in the dialog take effect immediately.
  sealed class OverlaySettings
  {
    public Color TextBackground  = Color.FromArgb(230, 255, 255, 255); // 10% transparent white
    public Color MouseBackground = Color.FromArgb(230, 255, 255, 255); // same as text background
    public Color ButtonColor     = Color.FromArgb(235, 45, 45, 45);    // pressed-button fill
    public Color BorderColor     = Color.FromArgb(180, 80, 80, 80);    // outline of badge + mouse
    public Color TextColor       = Color.FromArgb(17, 17, 17);         // modifier text

    public readonly Color ShadowColor = Color.FromArgb(55, 0, 0, 0);

    public int MouseSize = 42;   // mouse-glyph height, base (100%) px
    public int TextSize  = 13;   // modifier-text height, base (100%) px
    public int OffsetX   = -20;  // horizontal placement vs. cursor: +right / -left
    public int OffsetY   = 20;   // vertical placement vs. cursor:   +down / -up
    public int ScalePercent = 100; // global size multiplier in 25% steps (100-300); scales the
                                   // whole overlay (glyph, text, distances, borders) uniformly.

    // Mouse glyph keeps its original 26:42 aspect; only the size knob varies.
    public float MouseWidth  => MouseSize * (26f / 42f);
    public float MouseHeight => MouseSize;

    // ---- Persistence (%AppData%\RhinoInputOverlay\settings.json) ----------------------
    static string FilePath => Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
      "RhinoInputOverlay", "settings.json");

    // Plain DTO so colors persist as ARGB ints (Color isn't cleanly System.Text.Json-friendly).
    sealed class Data
    {
      public int TextBackground { get; set; }
      public int MouseBackground { get; set; }
      public int ButtonColor { get; set; }
      public int BorderColor { get; set; }
      public int TextColor { get; set; }
      public int MouseSize { get; set; }
      public int TextSize { get; set; }
      public int OffsetX { get; set; }
      public int OffsetY { get; set; }
      public int ScalePercent { get; set; }
    }

    public void Save()
    {
      try
      {
        var d = new Data
        {
          TextBackground = TextBackground.ToArgb(),
          MouseBackground = MouseBackground.ToArgb(),
          ButtonColor = ButtonColor.ToArgb(),
          BorderColor = BorderColor.ToArgb(),
          TextColor = TextColor.ToArgb(),
          MouseSize = MouseSize,
          TextSize = TextSize,
          OffsetX = OffsetX,
          OffsetY = OffsetY,
          ScalePercent = ScalePercent,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        File.WriteAllText(FilePath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
      }
      catch { /* settings are best-effort; ignore IO/permission errors */ }
    }

    public static OverlaySettings Load()
    {
      var s = new OverlaySettings();
      try
      {
        if (File.Exists(FilePath))
        {
          var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
          if (d != null)
          {
            s.TextBackground = Color.FromArgb(d.TextBackground);
            s.MouseBackground = Color.FromArgb(d.MouseBackground);
            s.ButtonColor = Color.FromArgb(d.ButtonColor);
            s.BorderColor = Color.FromArgb(d.BorderColor);
            s.TextColor = Color.FromArgb(d.TextColor);
            s.MouseSize = Math.Clamp(d.MouseSize, 16, 120);
            s.TextSize = Math.Clamp(d.TextSize, 8, 48);
            s.OffsetX = Math.Clamp(d.OffsetX, -50, 50);
            s.OffsetY = Math.Clamp(d.OffsetY, -50, 50);
            // Snap to the nearest 25% step; clamp to 100-300. Older files (no field) read as 0 → 100%.
            s.ScalePercent = d.ScalePercent < 100 ? 100 : Math.Clamp((d.ScalePercent + 12) / 25 * 25, 100, 300);
          }
        }
      }
      catch { /* fall back to defaults on any parse/IO error */ }
      return s;
    }
  }

  // Owns the overlay window, the poll timer, the settings dialog, and a tray icon.
  // The overlay auto-starts on launch and stops when the dialog (the app) is closed.
  sealed class OverlayContext : ApplicationContext
  {
    readonly OverlaySettings m_settings = OverlaySettings.Load();
    readonly OverlayWindow m_overlay;
    readonly System.Windows.Forms.Timer m_timer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60 fps
    readonly NotifyIcon m_tray;
    readonly SettingsForm m_form;
    readonly EventWaitHandle m_quitEvent;
    readonly RegisteredWaitHandle m_quitWait;

    public OverlayContext(EventWaitHandle quitEvent)
    {
      m_quitEvent = quitEvent;
      m_overlay = new OverlayWindow(m_settings);

      Icon appIcon = null;
      try { appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }

      // Tray icon + menu — the Windows equivalent of the macOS menu-bar item: Settings / Start /
      // Stop / Exit. The settings window can be closed (it just hides); the tray menu is the
      // persistent control surface, so the app keeps running until Exit.
      var menu = new ContextMenuStrip { ShowImageMargin = false }; // no icons → drop the left gutter
      menu.Items.Add("Settings…", null, (s, e) => ShowSettings());
      menu.Items.Add(new ToolStripSeparator());
      menu.Items.Add("Start", null, (s, e) => Start());
      menu.Items.Add("Stop", null, (s, e) => Stop());
      menu.Items.Add(new ToolStripSeparator());
      menu.Items.Add("Exit", null, (s, e) => ExitThread());
      m_tray = new NotifyIcon
      {
        Icon = appIcon ?? SystemIcons.Application,
        Text = "Rhino Input Overlay",
        Visible = true,
        ContextMenuStrip = menu
      };
      m_tray.DoubleClick += (s, e) => ShowSettings(); // double-click opens settings, as is conventional

      // Force the window handle so we can marshal the quit signal onto the UI thread.
      _ = m_overlay.Handle;
      m_quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent,
        (state, timedOut) =>
        {
          if (m_overlay.IsHandleCreated)
            m_overlay.BeginInvoke((Action)ExitThread);
        },
        null, Timeout.Infinite, executeOnlyOnce: true);

      m_timer.Tick += (s, e) => m_overlay.UpdateOverlay();

      m_form = new SettingsForm(m_settings, Start, Stop);
      // Closing the window (X / Alt+F4) just hides it — reopen from the tray. The app quits only
      // via the tray's Exit item. (Windows shutdown and other non-user closes are allowed through.)
      m_form.FormClosing += (s, e) =>
      {
        if (e.CloseReason == CloseReason.UserClosing)
        {
          e.Cancel = true;
          m_form.Hide();
        }
      };
      m_form.Show();

      Start(); // auto-start on launch
    }

    // Show (or restore + focus) the settings window.
    void ShowSettings()
    {
      m_form.Show();
      if (m_form.WindowState == FormWindowState.Minimized)
        m_form.WindowState = FormWindowState.Normal;
      m_form.Activate();
    }

    void Start()
    {
      if (!m_timer.Enabled)
        m_timer.Start();
    }

    void Stop()
    {
      m_timer.Stop();
      if (m_overlay.IsHandleCreated)
        m_overlay.Hide();
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        m_settings.Save();
        m_quitWait?.Unregister(null);
        m_timer.Dispose();
        m_tray.Visible = false;
        m_tray.Dispose();
        m_overlay.Dispose();
        m_form.Dispose();
        m_quitEvent?.Dispose();
      }
      base.Dispose(disposing);
    }
  }

  // Modeless dialog to edit the overlay's colors, sizes and placement, with Start/Stop.
  sealed class SettingsForm : Form
  {
    readonly OverlaySettings m_s;
    readonly Action m_start, m_stop;
    readonly System.Collections.Generic.List<Action> m_refresh = new System.Collections.Generic.List<Action>();
    int m_row;

    public SettingsForm(OverlaySettings settings, Action start, Action stop)
    {
      m_s = settings; m_start = start; m_stop = stop;

      Text = "Rhino Input Overlay";
      FormBorderStyle = FormBorderStyle.FixedDialog;
      MaximizeBox = false;
      MinimizeBox = false;
      ShowInTaskbar = true;
      StartPosition = FormStartPosition.CenterScreen;
      try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
      AutoScaleMode = AutoScaleMode.Dpi;
      AutoSize = true;
      AutoSizeMode = AutoSizeMode.GrowAndShrink;

      var t = new TableLayoutPanel
      {
        ColumnCount = 2,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        Padding = new Padding(12),
        Dock = DockStyle.Fill
      };
      t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
      t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

      var restoreBtn = new Button { Text = "Restore defaults", AutoSize = true, Margin = new Padding(3, 3, 3, 10) };
      restoreBtn.Click += (s, e) => RestoreDefaults();
      t.Controls.Add(restoreBtn, 0, m_row);
      t.SetColumnSpan(restoreBtn, 2);
      m_row++;

      AddColorRow(t, "Text background",    () => m_s.TextBackground,  c => m_s.TextBackground = c);
      AddColorRow(t, "Text color",         () => m_s.TextColor,       c => m_s.TextColor = c);
      AddColorRow(t, "Mouse background",   () => m_s.MouseBackground, c => m_s.MouseBackground = c);
      AddColorRow(t, "Mouse button color", () => m_s.ButtonColor,     c => m_s.ButtonColor = c);
      AddColorRow(t, "Border color",       () => m_s.BorderColor,     c => m_s.BorderColor = c);
      AddIntRow(t, "Mouse icon size", 16, 120, () => m_s.MouseSize, v => m_s.MouseSize = v);
      AddIntRow(t, "Text size",        8,  48, () => m_s.TextSize,  v => m_s.TextSize = v);
      AddIntRow(t, "Distance X",     -50,  50, () => m_s.OffsetX,   v => m_s.OffsetX = v);
      AddIntRow(t, "Distance Y",     -50,  50, () => m_s.OffsetY,   v => m_s.OffsetY = v);
      AddIntRow(t, "Scale %",        100, 300, () => m_s.ScalePercent, v => m_s.ScalePercent = v, step: 25);

      var startBtn = new Button { Text = "Start", AutoSize = true, Margin = new Padding(3, 3, 6, 3) };
      var stopBtn  = new Button { Text = "Stop",  AutoSize = true };
      startBtn.Click += (s, e) => m_start();
      stopBtn.Click  += (s, e) => m_stop();
      var buttons = new FlowLayoutPanel
      {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.LeftToRight,
        Margin = new Padding(3, 12, 3, 3)
      };
      buttons.Controls.Add(startBtn);
      buttons.Controls.Add(stopBtn);
      t.Controls.Add(buttons, 0, m_row);
      t.SetColumnSpan(buttons, 2);

      Controls.Add(t);
    }

    void AddRow(TableLayoutPanel t, string label, Control control)
    {
      var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 7) };
      control.Anchor = AnchorStyles.Left;
      control.Margin = new Padding(3, 4, 3, 4);
      t.Controls.Add(lbl, 0, m_row);
      t.Controls.Add(control, 1, m_row);
      m_row++;
    }

    void AddColorRow(TableLayoutPanel t, string label, Func<Color> get, Action<Color> set)
    {
      var btn = new Button { Width = 90, Height = 26, FlatStyle = FlatStyle.Flat, BackColor = Opaque(get()) };
      btn.Click += (s, e) =>
      {
        using (var dlg = new ColorDialog { FullOpen = true, Color = Opaque(get()) })
        {
          if (dlg.ShowDialog(this) == DialogResult.OK)
          {
            // Keep the configured transparency; the picker only chooses RGB.
            Color prev = get();
            set(Color.FromArgb(prev.A, dlg.Color.R, dlg.Color.G, dlg.Color.B));
            btn.BackColor = dlg.Color;
          }
        }
      };
      m_refresh.Add(() => btn.BackColor = Opaque(get()));
      AddRow(t, label, btn);
    }

    void AddIntRow(TableLayoutPanel t, string label, int min, int max, Func<int> get, Action<int> set, int step = 1)
    {
      var nud = new NumericUpDown { Minimum = min, Maximum = max, Increment = step, Value = get(), Width = 90 };
      nud.ValueChanged += (s, e) => set((int)nud.Value);
      m_refresh.Add(() => nud.Value = get());
      AddRow(t, label, nud);
    }

    // Reset every setting to its default, sync the controls, and persist.
    void RestoreDefaults()
    {
      var d = new OverlaySettings();
      m_s.TextBackground = d.TextBackground;
      m_s.TextColor = d.TextColor;
      m_s.MouseBackground = d.MouseBackground;
      m_s.ButtonColor = d.ButtonColor;
      m_s.BorderColor = d.BorderColor;
      m_s.MouseSize = d.MouseSize;
      m_s.TextSize = d.TextSize;
      m_s.OffsetX = d.OffsetX;
      m_s.OffsetY = d.OffsetY;
      m_s.ScalePercent = d.ScalePercent;
      foreach (var r in m_refresh)
        r();
      m_s.Save();
    }

    static Color Opaque(Color c) => Color.FromArgb(c.R, c.G, c.B);
  }

  sealed class OverlayWindow : Form
  {
    // All appearance/placement tunables live in OverlaySettings (edited live via the dialog).
    // Base (96-DPI / 100%) sizes here are multiplied by the monitor DPI scale at draw time so
    // the overlay stays crisp and correctly sized on high-DPI displays.
    readonly OverlaySettings m_settings;

    Font m_font;
    float m_fontScale = -1f;
    int m_fontSize = -1;

    // Font sized in physical pixels for the current DPI scale + text size, cached until either
    // changes (the dialog can change the text size at runtime).
    Font ScaledFont(float scale, int sizePx)
    {
      if (m_font == null || Math.Abs(scale - m_fontScale) > 0.001f || sizePx != m_fontSize)
      {
        m_font?.Dispose();
        m_font = new Font("Segoe UI", sizePx * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        m_fontScale = scale;
        m_fontSize = sizePx;
      }
      return m_font;
    }

    public OverlayWindow(OverlaySettings settings)
    {
      m_settings = settings;
      FormBorderStyle = FormBorderStyle.None;
      ShowInTaskbar = false;
      StartPosition = FormStartPosition.Manual;
      TopMost = true;
      // Create the handle now so UpdateLayeredWindow can target it; keep the window itself
      // off-screen/empty until the first Update().
      Bounds = new Rectangle(-10000, -10000, 1, 1);
      HookForegroundReorders();
      HookMouseWheel();
    }

    protected override CreateParams CreateParams
    {
      get
      {
        var cp = base.CreateParams;
        cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
        return cp;
      }
    }

    // Never take focus.
    protected override bool ShowWithoutActivation => true;

    public void UpdateOverlay()
    {
      if (!RhinoIsForeground())
      {
        Hide();
        return;
      }

      var btn = CurrentMouseButton();
      string mods = ModifierText();
      float scale = GetDpiForWindow(Handle) / 96f;
      if (scale <= 0f) scale = 1f;
      // Fold the global user scale into the master scale so the whole overlay — glyph, text,
      // distances and border thicknesses — grows/shrinks uniformly.
      scale *= m_settings.ScalePercent / 100f;

      // Render to a 32bpp ARGB bitmap, then push it to the layered window.
      using (var bmp = Render(mods, btn, scale, out Size contentSize))
      {
        if (!Visible)
          Show();
        PushBitmap(bmp, contentSize, scale);
      }
    }

    Bitmap Render(string mods, MouseButton btn, float scale, out Size contentSize)
    {
      Font font = ScaledFont(scale, m_settings.TextSize);
      float glyphW = m_settings.MouseWidth * scale;
      float glyphH = m_settings.MouseHeight * scale;
      float gap = 8f * scale;
      float padX = BadgePadX * scale;
      float padY = BadgePadY * scale;

      // Measure first so the bitmap is exactly content-sized (keeps the layered surface small).
      float badgeW = 0f, badgeH = 0f;
      SizeF textSize = SizeF.Empty;
      using (var probe = new Bitmap(1, 1))
      using (var pg = Graphics.FromImage(probe))
      {
        if (mods.Length > 0)
        {
          textSize = pg.MeasureString(mods, font);
          badgeW = textSize.Width + 2f * padX;
          badgeH = textSize.Height + 2f * padY;
        }
      }

      int pad = (int)Math.Ceiling(4f * scale);
      int contentH = (int)Math.Ceiling(Math.Max(badgeH, glyphH)) + pad;
      int contentW = (int)Math.Ceiling((mods.Length > 0 ? badgeW + gap : 0) + glyphW) + pad;
      contentSize = new Size(contentW, contentH);

      var bmp = new Bitmap(contentW, contentH, PixelFormat.Format32bppArgb);
      using (var g = Graphics.FromImage(bmp))
      {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);

        float x = 2f * scale;
        float centerY = contentH / 2f;

        // Modifier text first (left), then the mouse glyph to its right. The bitmap's right edge
        // is anchored near the cursor (see PushBitmap), so the glyph stays put while the text
        // grows leftward instead of shoving the glyph around as the modifier string changes.
        if (mods.Length > 0)
        {
          var rect = new RectangleF(x, centerY - badgeH / 2f, badgeW, badgeH);
          using (var path = RoundedRect(rect, 6f * scale))
          using (var fill = new SolidBrush(m_settings.TextBackground))
          using (var pen = new Pen(m_settings.BorderColor, 1f * scale))
          using (var textBrush = new SolidBrush(m_settings.TextColor))
          {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
            g.DrawString(mods, font, textBrush, x + padX, centerY - textSize.Height / 2f);
          }
          x += badgeW + gap;
        }

        DrawMouseGlyph(g, x, centerY, glyphW, glyphH, scale, btn);
      }
      return bmp;
    }

    const float BadgePadX = 9f;
    const float BadgePadY = 5f;

    void DrawMouseGlyph(Graphics g, float left, float centerY, float w, float h, float scale, MouseButton active)
    {
      float top = centerY - h / 2f;
      float cx = left + w / 2f;
      float radius = w * 0.5f; // fully rounded ends → clean capsule outline
      var bodyRect = new RectangleF(left, top, w, h);

      using (var bodyPath = RoundedRect(bodyRect, radius))
      using (var shadowPath = RoundedRect(new RectangleF(left, top + 1f * scale, w, h), radius))
      using (var shadowBrush = new SolidBrush(m_settings.ShadowColor))
      using (var fillBrush = new SolidBrush(m_settings.MouseBackground))
      using (var linePen = new Pen(m_settings.BorderColor, 1.4f * scale))
      using (var pressedBrush = new SolidBrush(m_settings.ButtonColor))
      {
        // Soft drop shadow, then the white body.
        g.FillPath(shadowBrush, shadowPath);
        g.FillPath(fillBrush, bodyPath);

        // Pressed button: fill just the top-left / top-right half, clipped to the rounded
        // body so the corners stay round. No divider or button line is drawn — the highlight
        // alone indicates the press, keeping the resting glyph a clean outline.
        float buttonBottom = top + h * 0.40f;
        var savedClip = g.Clip;
        g.SetClip(bodyPath);
        if (active == MouseButton.Left)
          g.FillRectangle(pressedBrush, left, top, cx - left, buttonBottom - top);
        else if (active == MouseButton.Right)
          g.FillRectangle(pressedBrush, cx, top, (left + w) - cx, buttonBottom - top);
        g.Clip = savedClip;

        // Small vertical pill for the scroll wheel near the top centre (1px larger than before).
        float ww = 4.5f * scale, wh = 9f * scale;
        var wheel = new RectangleF(cx - ww / 2f, top + 6f * scale, ww, wh);
        using (var wheelPath = RoundedRect(wheel, ww / 2f))
        {
          g.FillPath(active == MouseButton.Middle ? pressedBrush : fillBrush, wheelPath);
          g.DrawPath(linePen, wheelPath);
        }

        // Body outline on top.
        g.DrawPath(linePen, bodyPath);

        // Scroll flash: a filled dot that fits the wheel pill — diameter equals the pill width,
        // centered on the rounded cap at the top (scrolling up) or bottom (scrolling down).
        // Lit briefly by each wheel notch via the low-level mouse hook.
        if (m_scrollDir != 0 && Environment.TickCount < m_scrollUntilTick)
        {
          float r = ww / 2f;                 // pill radius (cap-centre reference)
          float dr = r + 0.5f * scale;       // dot radius: 1px larger diameter than the pill
          float cyDot = m_scrollDir > 0 ? wheel.Top + r : wheel.Bottom - r;
          g.FillEllipse(pressedBrush, cx - dr, cyDot - dr, dr * 2f, dr * 2f);
        }
      }
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
      float d = radius * 2f;
      var path = new GraphicsPath();
      path.AddArc(r.X, r.Y, d, d, 180, 90);
      path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
      path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
      path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
      path.CloseFigure();
      return path;
    }

    // Position the layered window near the cursor and blit the ARGB bitmap with per-pixel alpha.
    void PushBitmap(Bitmap bmp, Size size, float scale)
    {
      GetCursorPos(out POINT cursor);
      // Anchor the bitmap's right edge at cursor.x + OffsetX (so the glyph holds position and the
      // text grows leftward); OffsetX/Y are signed: +right/-left, +down/-up.
      var screenPos = new POINT { x = cursor.x + (int)(m_settings.OffsetX * scale) - size.Width, y = cursor.y + (int)(m_settings.OffsetY * scale) };
      m_lastRect = new RECT { left = screenPos.x, top = screenPos.y, right = screenPos.x + size.Width, bottom = screenPos.y + size.Height };
      var bmpSize = new SIZE { cx = size.Width, cy = size.Height };
      var src = new POINT { x = 0, y = 0 };

      IntPtr screenDc = GetDC(IntPtr.Zero);
      IntPtr memDc = CreateCompatibleDC(screenDc);
      IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
      IntPtr oldBitmap = SelectObject(memDc, hBitmap);

      var blend = new BLENDFUNCTION
      {
        BlendOp = AC_SRC_OVER,
        BlendFlags = 0,
        SourceConstantAlpha = 255,
        AlphaFormat = AC_SRC_ALPHA
      };

      UpdateLayeredWindow(Handle, screenDc, ref screenPos, ref bmpSize, memDc, ref src, 0, ref blend, ULW_ALPHA);
      RaiseIfCovered();

      SelectObject(memDc, oldBitmap);
      DeleteObject(hBitmap);
      DeleteDC(memDc);
      ReleaseDC(IntPtr.Zero, screenDc);
    }

    RECT m_lastRect;
    IntPtr m_winEventHook;
    WinEventDelegate m_winEventProc; // kept alive so the unmanaged callback isn't GC'd

    // Re-assert top of the topmost z-order band, but only when a visible window is actually
    // sitting on top of our current rectangle. Tooltips/flyouts are also WS_EX_TOPMOST and pop
    // up after us; this lifts us back above them. Gating on "actually covered" means we don't
    // poke the z-order (and provoke a flicker fight) when nothing is on top of us.
    void RaiseIfCovered()
    {
      if (!IsCoveredByHigherWindow(m_lastRect))
        return;
      SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    bool IsCoveredByHigherWindow(RECT mine)
    {
      // Walk z-order from our window toward the front (GW_HWNDPREV = the window in front of one).
      for (IntPtr hw = GetWindow(Handle, GW_HWNDPREV); hw != IntPtr.Zero; hw = GetWindow(hw, GW_HWNDPREV))
      {
        if (IsWindowVisible(hw) && GetWindowRect(hw, out RECT r) &&
            r.left < mine.right && r.right > mine.left && r.top < mine.bottom && r.bottom > mine.top)
          return true;
      }
      return false;
    }

    // Fires the instant any window in another process is shown or reorders its z-order — i.e.
    // exactly when a tooltip pops up on top of us. Reacting here (rather than waiting up to one
    // 16 ms timer tick) closes the gap that caused the visible flicker. WINEVENT_SKIPOWNPROCESS
    // means our own SetWindowPos doesn't feed the hook and start a loop.
    void HookForegroundReorders()
    {
      m_winEventProc = OnWinEvent;
      m_winEventHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_REORDER,
        IntPtr.Zero, m_winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    void OnWinEvent(IntPtr hHook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
      if (Visible)
        RaiseIfCovered();
    }

    // ---- Scroll-wheel flash ----------------------------------------------------------
    const int FlashMs = 180;        // how long a wheel notch keeps the dot lit
    int m_scrollDir;                // +1 = up, -1 = down, 0 = none
    int m_scrollUntilTick;          // Environment.TickCount at which the flash expires
    IntPtr m_mouseHook;
    LowLevelMouseProc m_mouseProc;  // kept alive so the unmanaged callback isn't GC'd

    // The wheel can't be polled like buttons/modifiers, so a global low-level mouse hook
    // watches for WM_MOUSEWHEEL and records the direction + a short expiry to flash the dot.
    void HookMouseWheel()
    {
      m_mouseProc = OnMouseHook;
      m_mouseHook = SetWindowsHookEx(WH_MOUSE_LL, m_mouseProc, GetModuleHandle(null), 0);
    }

    IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
      if (nCode >= 0 && (int)wParam == WM_MOUSEWHEEL)
      {
        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        int delta = (short)((data.mouseData >> 16) & 0xffff); // high word, signed
        m_scrollDir = delta > 0 ? 1 : -1;
        m_scrollUntilTick = Environment.TickCount + FlashMs;
      }
      return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    static bool RhinoIsForeground()
    {
      IntPtr hwnd = GetForegroundWindow();
      if (hwnd == IntPtr.Zero)
        return false;
      GetWindowThreadProcessId(hwnd, out uint pid);
      try
      {
        using (var p = Process.GetProcessById((int)pid))
          return string.Equals(p.ProcessName, "Rhino", StringComparison.OrdinalIgnoreCase);
      }
      catch
      {
        return false;
      }
    }

    static MouseButton CurrentMouseButton()
    {
      if (IsDown(VK_LBUTTON)) return MouseButton.Left;
      if (IsDown(VK_RBUTTON)) return MouseButton.Right;
      if (IsDown(VK_MBUTTON)) return MouseButton.Middle;
      return MouseButton.None;
    }

    static string ModifierText()
    {
      var parts = new System.Collections.Generic.List<string>();
      if (IsDown(VK_CONTROL)) parts.Add("Ctrl");
      if (IsDown(VK_MENU)) parts.Add("Alt");
      if (IsDown(VK_SHIFT)) parts.Add("Shift");
      return string.Join(" + ", parts);
    }

    static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // ---- Win32 -----------------------------------------------------------------------
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOPMOST = 0x00000008;

    const int VK_LBUTTON = 0x01;
    const int VK_RBUTTON = 0x02;
    const int VK_MBUTTON = 0x04;
    const int VK_SHIFT = 0x10;
    const int VK_CONTROL = 0x11;
    const int VK_MENU = 0x12; // Alt

    const byte AC_SRC_OVER = 0x00;
    const byte AC_SRC_ALPHA = 0x01;
    const int ULW_ALPHA = 0x02;

    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_NOOWNERZORDER = 0x0200;
    const uint GW_HWNDPREV = 3;

    const uint EVENT_OBJECT_SHOW = 0x8002;
    const uint EVENT_OBJECT_REORDER = 0x8004;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    const int WH_MOUSE_LL = 14;
    const int WM_MOUSEWHEEL = 0x020A;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx; public int cy; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left; public int top; public int right; public int bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT
    {
      public POINT pt;
      public uint mouseData;
      public uint flags;
      public uint time;
      public IntPtr dwExtraInfo;
    }
    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION
    {
      public byte BlendOp;
      public byte BlendFlags;
      public byte SourceConstantAlpha;
      public byte AlphaFormat;
    }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
      int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
      int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
      IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
      ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hDC);

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        if (m_winEventHook != IntPtr.Zero)
          UnhookWinEvent(m_winEventHook);
        if (m_mouseHook != IntPtr.Zero)
          UnhookWindowsHookEx(m_mouseHook);
        m_font?.Dispose();
      }
      base.Dispose(disposing);
    }
  }

  enum MouseButton { None, Left, Right, Middle }
}
