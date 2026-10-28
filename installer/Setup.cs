// IdleVeil installer. A single exe with IdleVeil.exe embedded as a resource.
//
// Per-user install (no admin rights): %LOCALAPPDATA%\Programs\IdleVeil
//  - Start menu shortcut, optional desktop shortcut
//  - optional "Start with Windows" (HKCU Run key, same entry the tray menu toggles)
//  - registered under Settings > Apps > Installed apps, with an uninstaller
//
// Usage: IdleVeil-Setup.exe [--silent] [--no-autostart] [--uninstall]
// Build: ..\build.ps1
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IdleVeilSetup
{
    static class Installer
    {
        public const string AppName = "IdleVeil";
        public const string Publisher = "watdafawx";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\IdleVeil";
        const string ExitEventName = "Local\\IdleVeilExit";

        public static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "IdleVeil");
        public static readonly string AppExe = Path.Combine(InstallDir, "IdleVeil.exe");
        static readonly string UninstallExe = Path.Combine(InstallDir, "Uninstall.exe");
        static readonly string StartMenuLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
        static readonly string DesktopLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

        public static bool IsInstalled { get { return File.Exists(AppExe); } }

        public static void Install(bool autostart, bool desktopShortcut, bool launch)
        {
            StopRunning();
            Directory.CreateDirectory(InstallDir);

            using (Stream src = Assembly.GetExecutingAssembly().GetManifestResourceStream("IdleVeil.exe"))
            using (FileStream dst = File.Create(AppExe))
                src.CopyTo(dst);

            string self = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(UninstallExe), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, UninstallExe, true);

            CreateShortcut(StartMenuLink, AppExe, "Burn-in protection overlay for OLED screens");
            if (desktopShortcut) CreateShortcut(DesktopLink, AppExe, "Burn-in protection overlay for OLED screens");
            else TryDelete(DesktopLink);

            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (autostart) k.SetValue("IdleVeil", "\"" + AppExe + "\"");
                else if (k.GetValue("IdleVeil") != null) k.DeleteValue("IdleVeil");
            }

            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", AppInfo.Version);
                k.SetValue("Publisher", Publisher);
                k.SetValue("DisplayIcon", AppExe);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + UninstallExe + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + UninstallExe + "\" --uninstall --silent");
                k.SetValue("EstimatedSize", (int)((new FileInfo(AppExe).Length + new FileInfo(UninstallExe).Length) / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }

            if (launch) Process.Start(new ProcessStartInfo(AppExe) { WorkingDirectory = InstallDir });
        }

        public static void Uninstall()
        {
            StopRunning();
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (k != null && k.GetValue("IdleVeil") != null) k.DeleteValue("IdleVeil");
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            TryDelete(StartMenuLink);
            TryDelete(DesktopLink);
            TryDelete(AppExe);

            // We may be running from the install folder, so remove it after we exit.
            string cmd = "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }

        // Asks a running IdleVeil to quit cleanly (it restores the cursor), then force-closes any copy
        // from the install folder that didn't.
        static void StopRunning()
        {
            EventWaitHandle ev;
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out ev)) { ev.Set(); ev.Dispose(); }

            DateTime deadline = DateTime.Now.AddSeconds(5);
            foreach (Process p in Process.GetProcessesByName("IdleVeil"))
            {
                try
                {
                    if (!string.Equals(Path.GetFullPath(p.MainModule.FileName), Path.GetFullPath(AppExe), StringComparison.OrdinalIgnoreCase))
                        continue;
                    int left = (int)Math.Max(0, (deadline - DateTime.Now).TotalMilliseconds);
                    if (!p.WaitForExit(left)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
            }
        }

        static void CreateShortcut(string path, string target, string description)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type lt = link.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { description });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                Marshal.ReleaseComObject(link);
            }
            finally { Marshal.ReleaseComObject(shell); }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    class SetupForm : Form
    {
        readonly CheckBox autostart, desktop, launch;

        public SetupForm()
        {
            Text = Installer.AppName + " " + AppInfo.Version + " Setup";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var layout = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(18), Dock = DockStyle.Fill
            };

            var title = new Label { Text = Installer.AppName, AutoSize = true, Font = new Font(Font.FontFamily, Font.Size * 1.6f, FontStyle.Bold) };
            var blurb = new Label
            {
                AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(3, 6, 3, 12),
                Text = "Burn-in protection for OLED screens. Shows a screensaver-style animation when your PC is idle, " +
                       "without locking the desktop or showing up in screenshots and streams."
            };
            var where = new Label
            {
                AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 10),
                Text = (Installer.IsInstalled ? "Already installed. This will update it in:\n" : "Installs for your user only, in:\n") + Installer.InstallDir
            };
            autostart = new CheckBox { Text = "Start with Windows", Checked = true, AutoSize = true };
            desktop = new CheckBox { Text = "Create a desktop shortcut", Checked = false, AutoSize = true };
            launch = new CheckBox { Text = "Start IdleVeil when setup finishes", Checked = true, AutoSize = true };

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 14, 0, 0) };
            var install = new Button { Text = Installer.IsInstalled ? "Update" : "Install", AutoSize = true, MinimumSize = new Size(90, 0) };
            var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
            install.Click += delegate { DoInstall(); };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(install);

            layout.Controls.AddRange(new Control[] { title, blurb, where, autostart, desktop, launch, buttons });
            Controls.Add(layout);
            AcceptButton = install;
            CancelButton = cancel;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }

        void DoInstall()
        {
            UseWaitCursor = true;
            Enabled = false;
            try
            {
                Installer.Install(autostart.Checked, desktop.Checked, launch.Checked);
                MessageBox.Show(this,
                    Installer.AppName + " is installed.\n\nIt runs from the system tray: right-click its icon to pick the animation, " +
                    "the idle time, or turn \"Start with Windows\" on or off.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                Enabled = true;
                UseWaitCursor = false;
                MessageBox.Show(this, "Setup failed:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            bool silent = false, uninstall = false, noAutostart = false;
            foreach (string a in args)
            {
                string k = a.ToLowerInvariant().TrimStart('-', '/');
                if (k == "silent" || k == "s" || k == "quiet") silent = true;
                else if (k == "uninstall") uninstall = true;
                else if (k == "no-autostart") noAutostart = true;
            }

            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string caption = Installer.AppName + " Setup";

            try
            {
                if (uninstall)
                {
                    if (!silent && MessageBox.Show("Remove " + Installer.AppName + " from this PC?\n\nYour settings in %APPDATA%\\IdleVeil are kept.",
                            caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                        return 1;
                    Installer.Uninstall();
                    if (!silent) MessageBox.Show(Installer.AppName + " was removed.", caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                if (silent)
                {
                    Installer.Install(!noAutostart, false, true);
                    return 0;
                }

                using (var f = new SetupForm())
                    return f.ShowDialog() == DialogResult.OK ? 0 : 1;
            }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show("Setup failed:\n\n" + ex.Message, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
        }
    }
}
