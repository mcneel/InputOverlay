using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RhinoInputOverlaySetup
{
  // Self-contained installer for Rhino Input Overlay built with only the .NET SDK (no third-party
  // installer tooling). Both architecture builds are embedded as resources; the one matching the
  // OS is extracted to a per-user location. Run with no args to install, "--uninstall" to remove.
  static class Program
  {
    [STAThread]
    static int Main(string[] args)
    {
      bool uninstall = Array.Exists(args, a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase));

      // DPI-aware before any window exists, so the dialogs render crisply (no bitmap-stretch blur).
      Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);

      try
      {
        if (uninstall)
          return RunUninstall();
        Application.Run(new InstallerForm());
        return 0;
      }
      catch (Exception ex)
      {
        MessageBox.Show("Operation failed:\n\n" + ex.Message, Core.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        return 1;
      }
    }

    static int RunUninstall()
    {
      if (MessageBox.Show("Remove " + Core.AppName + "?", Core.AppName + " Uninstall",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        return 0;

      Core.DoUninstall();
      MessageBox.Show(Core.AppName + " was removed.", Core.AppName + " Uninstall",
        MessageBoxButtons.OK, MessageBoxIcon.Information);

      // We're running from InstallDir\uninstall.exe, so we can't delete the folder while it's in
      // use. Hand off to a detached shell that waits for us to exit, then removes what's left.
      Process.Start(new ProcessStartInfo("cmd.exe",
        "/c timeout /t 2 >nul & rmdir /s /q \"" + Core.InstallDir + "\"")
      { CreateNoWindow = true, UseShellExecute = false });
      return 0;
    }
  }

  // Install/uninstall mechanics, shared by the form and the --uninstall path. No UI here.
  static class Core
  {
    public const string AppName = "Rhino Input Overlay";
    public const string AppVersion = "1.0.0";
    public const string Publisher = "McNeel";
    const string ExeName = "RhinoInputOverlay.exe";
    const string UninstallerName = "uninstall.exe";
    const string RegUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RhinoInputOverlay";
    const string RegRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "RhinoInputOverlay";
    const string QuitEventName = "RhinoInputOverlay.Quit.v1"; // matches the app's single-instance event

    public static string InstallDir => Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "Programs", "RhinoInputOverlay");
    public static string ExePath => Path.Combine(InstallDir, ExeName);
    static string ShortcutPath => Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    static string DesktopShortcutPath => Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    public static void DoInstall(bool desktopShortcut, bool startup)
    {
      SignalRunningAppToQuit();           // a running overlay would lock its exe
      Thread.Sleep(300);

      Directory.CreateDirectory(InstallDir);
      ExtractPayload(ExePath);
      File.Copy(Environment.ProcessPath, Path.Combine(InstallDir, UninstallerName), overwrite: true);

      CreateShortcut(ShortcutPath, ExePath, InstallDir);
      if (desktopShortcut)
        CreateShortcut(DesktopShortcutPath, ExePath, InstallDir);

      WriteUninstallInfo();
      using (var run = Registry.CurrentUser.CreateSubKey(RegRunKey))
      {
        if (startup)
          run.SetValue(RunValueName, "\"" + ExePath + "\"");
        else
          run.DeleteValue(RunValueName, throwOnMissingValue: false);
      }
    }

    public static void DoUninstall()
    {
      SignalRunningAppToQuit();
      Thread.Sleep(300);

      TryDelete(ShortcutPath);
      TryDelete(DesktopShortcutPath);
      TryDelete(ExePath);
      using (var run = Registry.CurrentUser.OpenSubKey(RegRunKey, writable: true))
        run?.DeleteValue(RunValueName, throwOnMissingValue: false);
      Registry.CurrentUser.DeleteSubKeyTree(RegUninstallKey, throwOnMissingSubKey: false);
    }

    static void ExtractPayload(string destExe)
    {
      string resource = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "payload-arm64" : "payload-x64";
      using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
      {
        if (s == null)
          throw new InvalidOperationException("Embedded payload '" + resource + "' is missing from the installer.");
        using (var fs = File.Create(destExe))
          s.CopyTo(fs);
      }
    }

    // Late-bound WScript.Shell COM so no extra references/tooling are needed to write the .lnk.
    static void CreateShortcut(string lnkPath, string target, string workingDir)
    {
      Type shellType = Type.GetTypeFromProgID("WScript.Shell");
      object shell = Activator.CreateInstance(shellType);
      object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
      Type t = sc.GetType();
      t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
      t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workingDir });
      t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { AppName });
      t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
    }

    static void WriteUninstallInfo()
    {
      using (var k = Registry.CurrentUser.CreateSubKey(RegUninstallKey))
      {
        k.SetValue("DisplayName", AppName);
        k.SetValue("DisplayVersion", AppVersion);
        k.SetValue("Publisher", Publisher);
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("DisplayIcon", ExePath);
        k.SetValue("UninstallString", "\"" + Path.Combine(InstallDir, UninstallerName) + "\" --uninstall");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
      }
    }

    static void SignalRunningAppToQuit()
    {
      try
      {
        if (EventWaitHandle.TryOpenExisting(QuitEventName, out EventWaitHandle h))
          using (h) h.Set();
      }
      catch { /* nothing running, or no access — fine */ }
    }

    static void TryDelete(string path)
    {
      try { if (File.Exists(path)) File.Delete(path); }
      catch { /* best effort */ }
    }
  }

  // The install window: DPI-aware WinForms so it renders crisply instead of a stretched MessageBox.
  sealed class InstallerForm : Form
  {
    readonly CheckBox m_desktop;
    readonly CheckBox m_startup;
    readonly CheckBox m_launch;
    readonly Button m_install;
    readonly Button m_cancel;
    readonly Label m_status;

    public InstallerForm()
    {
      Text = Core.AppName + " Setup";
      FormBorderStyle = FormBorderStyle.FixedDialog;
      MaximizeBox = false;
      MinimizeBox = false;
      StartPosition = FormStartPosition.CenterScreen;
      AutoScaleMode = AutoScaleMode.Dpi;
      ClientSize = new Size(460, 358);
      try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }

      var heading = new Label
      {
        Text = Core.AppName,
        AutoSize = true,
        Font = new Font("Segoe UI", 15f, FontStyle.Bold),
        Location = new Point(16, 16)
      };
      var sub = new Label
      {
        Text = "Version " + Core.AppVersion + " · per-user install",
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Location = new Point(18, 50)
      };
      var about = new Label
      {
        Text = "A mouse and modifier key overlay for Rhino that shows mouse and modifier key presses.",
        AutoSize = false,
        Location = new Point(18, 80),
        Size = new Size(424, 60)
      };

      m_desktop = new CheckBox { Text = "Create a desktop shortcut", AutoSize = true, Location = new Point(18, 168) };
      m_startup = new CheckBox { Text = "Start automatically when I sign in", AutoSize = true, Location = new Point(18, 194) };
      m_launch  = new CheckBox { Text = "Launch after install", Checked = true, AutoSize = true, Location = new Point(18, 220) };

      m_status = new Label { AutoSize = false, ForeColor = SystemColors.GrayText, Location = new Point(18, 262), Size = new Size(424, 20) };

      m_install = new Button { Text = "Install", Size = new Size(90, 30), Location = new Point(264, 314) };
      m_cancel  = new Button { Text = "Cancel",  Size = new Size(90, 30), Location = new Point(360, 314) };
      m_install.Click += OnInstall;
      m_cancel.Click += (s, e) => Close();
      AcceptButton = m_install;
      CancelButton = m_cancel;

      Controls.AddRange(new Control[] { heading, sub, about, m_desktop, m_startup, m_launch, m_status, m_install, m_cancel });
    }

    void OnInstall(object sender, EventArgs e)
    {
      try
      {
        m_install.Enabled = false;
        m_cancel.Enabled = false;
        m_status.Text = "Installing…";
        Update();

        Core.DoInstall(m_desktop.Checked, m_startup.Checked);

        if (m_launch.Checked)
          Process.Start(new ProcessStartInfo(Core.ExePath) { UseShellExecute = true });
        Close();
      }
      catch (Exception ex)
      {
        m_install.Enabled = true;
        m_cancel.Enabled = true;
        m_status.Text = "";
        MessageBox.Show(this, "Install failed:\n\n" + ex.Message, Core.AppName + " Setup",
          MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }
  }
}
