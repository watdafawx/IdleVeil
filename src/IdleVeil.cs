// IdleVeil: a screensaver-style overlay that protects OLED screens from burn-in
// without getting in the way of remote control, streaming or automation tools.
//
// Unlike a real Windows screensaver it stays on the normal desktop:
//  - the window is excluded from screen capture, so screenshots and streams show the desktop underneath
//  - it never takes focus and clicks pass through it
//  - it hides on real mouse/keyboard input or when a watched file grows; input injected by AI agents
//    and automation is ignored unless that option is turned off
//  - it stays off while a fullscreen app asks to keep the display on (video playback)
//  - it can turn one monitor off for real (e.g. an OLED, so it can run pixel cleaning) by detaching it
//  - it hides the mouse cursor while showing, and has a tray icon for options
//
// Usage: IdleVeil.exe [--idle seconds] [--mode random|matrix|mystify] [--watch file] [--fps n]
//                     [--preview] [--ignore-display-requests] [--exit]
// Build: ..\build.ps1 (uses the .NET Framework csc.exe that ships with Windows; C# 5 only).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IdleVeil
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("powrprof.dll")] public static extern uint CallNtPowerInformation(int level, IntPtr inBuf, uint inLen, out uint outBuf, uint outLen);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] public static extern bool PatBlt(IntPtr hdc, int x, int y, int w, int h, uint rop);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("gdi32.dll")] public static extern IntPtr CreatePen(int style, int width, uint color);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool Polyline(IntPtr hdc, POINT[] pts, int count);

        [DllImport("user32.dll")] public static extern IntPtr CreateCursor(IntPtr inst, int hotX, int hotY, int w, int h, byte[] andMask, byte[] xorMask);
        [DllImport("user32.dll")] public static extern bool SetSystemCursor(IntPtr cursor, uint id);
        [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint flags);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);

        public const uint SRCCOPY = 0x00CC0020, BLACKNESS = 0x00000042;
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

        const int SystemExecutionState = 16;
        const uint ES_DISPLAY_REQUIRED = 0x2;

        public static uint LastInputTick()
        {
            var info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(info);
            GetLastInputInfo(ref info);
            return info.dwTime;
        }

        public static bool DisplayRequired()
        {
            uint state;
            return CallNtPowerInformation(SystemExecutionState, IntPtr.Zero, 0, out state, 4) == 0
                && (state & ES_DISPLAY_REQUIRED) != 0;
        }

        // True when the foreground window covers a whole monitor (fullscreen video, games), but not the desktop itself.
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        static uint cachedPid;
        static string cachedName;

        // Exe name (lower case, with .exe) of the app that owns the foreground window, or null for the desktop/taskbar.
        public static string ForegroundApp()
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            var cls = new System.Text.StringBuilder(64);
            GetClassName(h, cls, 64);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "NotifyIconOverflowWindow") return null;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == 0) return null;
            if (pid == cachedPid) return cachedName;
            string name = null;
            try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant() + ".exe"; } catch { }
            cachedPid = pid; cachedName = name;
            return name;
        }

        public static bool ForegroundIsFullscreen()
        {
            IntPtr h = GetForegroundWindow();
            // A maximized window also reaches past the screen edges, so it doesn't count.
            if (h == IntPtr.Zero || IsZoomed(h)) return false;
            var cls = new System.Text.StringBuilder(64);
            GetClassName(h, cls, 64);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;
            RECT r;
            if (!GetWindowRect(h, out r)) return false;
            Rectangle m = Screen.FromHandle(h).Bounds;
            return r.L <= m.Left && r.T <= m.Top && r.R >= m.Right && r.B >= m.Bottom;
        }
    }

    // Swaps every system cursor for a blank one. The overlay is click-through, so the cursor
    // belongs to whatever window is underneath and the normal per-window hiding doesn't work.
    static class CursorHider
    {
        static readonly uint[] Ids = { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651 };
        static bool hidden;

        public static void Hide()
        {
            if (hidden) return;
            var andMask = new byte[32 * 4];
            var xorMask = new byte[32 * 4];
            for (int i = 0; i < andMask.Length; i++) andMask[i] = 0xFF;
            foreach (uint id in Ids)
                Native.SetSystemCursor(Native.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask), id);
            hidden = true;
        }

        // Reloads the user's cursor scheme. Safe to call any time (also at startup, in case we crashed while hidden).
        public static void Restore()
        {
            Native.SystemParametersInfo(0x0057 /* SPI_SETCURSORS */, 0, IntPtr.Zero, 0);
            hidden = false;
        }
    }

    // Settings and log live in %APPDATA%\IdleVeil, unless IdleVeil.ini sits next to the exe (portable mode).
    static class AppPaths
    {
        public static readonly string Dir = PickDir();
        public static readonly string Settings = Path.Combine(Dir, "IdleVeil.ini");
        public static readonly string Log = Path.Combine(Dir, "IdleVeil.log");

        static string PickDir()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(exeDir, "IdleVeil.ini"))) return exeDir;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IdleVeil");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    // "Start with Windows" is a per-user entry under HKCU\...\Run (no admin rights needed).
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "IdleVeil";

        static string Command { get { return "\"" + Application.ExecutablePath + "\""; } }

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(Name) != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(Name, Command);
                    else if (k.GetValue(Name) != null) k.DeleteValue(Name);
                }
            }
        }
    }

    class Options
    {
        public int IdleSeconds = 300;
        public string Mode = "random";
        public string WatchFile;
        public int Fps = 30;
        public bool Preview;
        public string KeepOnFor = "fullscreen";   // honour "keep the display on" requests: fullscreen | always | never
        public bool HideCursor = true;
        public bool IgnoreInjected = true;        // only real mouse/keyboard input counts as activity
        public string OffMonitor = "";            // EDID id of the monitor to turn off (detach), e.g. AUS2740
        public List<string> NoVeilApps = new List<string>();   // never veil while one of these exes is focused
        public bool Debug;                        // log idle state every 10 s
        public int OffAfterSeconds = 300;         // how long after the veil appears
        public bool Exit;

        static string SettingsPath { get { return AppPaths.Settings; } }

        // Settings chosen from the tray menu override the command-line defaults.
        public void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                    if (k == "mode") Mode = v.ToLowerInvariant();
                    else if (k == "idle") IdleSeconds = int.Parse(v);
                    else if (k == "hidecursor") HideCursor = v == "1" || v.ToLowerInvariant() == "true";
                    else if (k == "ignoreinjected") IgnoreInjected = v == "1" || v.ToLowerInvariant() == "true";
                    else if (k == "keeponfor") KeepOnFor = v.ToLowerInvariant();
                    else if (k == "offmonitor") OffMonitor = v;
                    else if (k == "offafter") OffAfterSeconds = int.Parse(v);
                    else if (k == "debug") Debug = v == "1";
                    else if (k == "noveilapps")
                    {
                        NoVeilApps.Clear();
                        foreach (string a in v.Split(',')) if (a.Trim() != "") NoVeilApps.Add(a.Trim().ToLowerInvariant());
                    }
                }
            }
            catch (Exception ex) { Program.Log("settings load error: " + ex.Message); }
        }

        public void SaveSettings()
        {
            try
            {
                File.WriteAllLines(SettingsPath, new[] {
                    "mode=" + Mode, "idle=" + IdleSeconds, "hidecursor=" + (HideCursor ? "1" : "0"),
                    "ignoreinjected=" + (IgnoreInjected ? "1" : "0"), "keeponfor=" + KeepOnFor,
                    "offmonitor=" + OffMonitor, "offafter=" + OffAfterSeconds, "noveilapps=" + string.Join(",", NoVeilApps.ToArray()), "debug=" + (Debug ? "1" : "0") });
            }
            catch (Exception ex) { Program.Log("settings save error: " + ex.Message); }
        }

        public static Options Parse(string[] a)
        {
            var o = new Options();
            for (int i = 0; i < a.Length; i++)
            {
                string k = a[i].ToLowerInvariant();
                string v = i + 1 < a.Length ? a[i + 1] : null;
                if (k == "--idle") { o.IdleSeconds = int.Parse(v); i++; }
                else if (k == "--mode") { o.Mode = v.ToLowerInvariant(); i++; }
                else if (k == "--watch") { o.WatchFile = v; i++; }
                else if (k == "--fps") { o.Fps = int.Parse(v); i++; }
                else if (k == "--preview") o.Preview = true;
                else if (k == "--ignore-display-requests") o.KeepOnFor = "never";
                else if (k == "--count-injected-input") o.IgnoreInjected = false;
                else if (k == "--off-monitor") { o.OffMonitor = v; i++; }
                else if (k == "--off-after") { o.OffAfterSeconds = int.Parse(v); i++; }
                else if (k == "--stats") SaverForm.Stats = true;
                else if (k == "--exit") o.Exit = true;
            }
            return o;
        }
    }

    static class Program
    {
        static string LogPath { get { return AppPaths.Log; } }
        public const string ExitEventName = "Local\\IdleVeilExit";

        public static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("s") + "] " + msg + Environment.NewLine); } catch { }
        }

        // Puts a detached monitor back and its windows where they were (used on exit).
        public static void ReattachNow()
        {
            if (!DisplayDetach.IsDetached) return;
            DisplayDetach.Reattach();
            Thread.Sleep(1500);
            DisplayDetach.RestoreWindows();
        }

        [STAThread]
        static void Main(string[] args)
        {
            var opt = Options.Parse(args);
            if (opt.Exit)
            {
                // Ask a running instance to quit cleanly (used by the installer and uninstaller).
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting(ExitEventName, out ev)) { ev.Set(); ev.Dispose(); }
                return;
            }
            bool created;
            var mutex = new Mutex(true, opt.Preview ? "Local\\IdleVeilPreview" : "Local\\IdleVeil", out created);
            if (!created) return;
            // Per-monitor DPI awareness, so screen sizes are always real pixels even if display
            // scaling changes after start-up (e.g. a different screen while the TV is off).
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }   // PER_MONITOR_AWARE_V2, Windows 10 1703+
            catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
            if (!opt.Preview) { opt.LoadSettings(); DisplayDetach.RecoverAfterCrash(); }
            CursorHider.Restore();
            Log("started v" + AppInfo.Version + ": idle=" + opt.IdleSeconds + "s mode=" + opt.Mode + " watch=" + (opt.WatchFile ?? "-")
                + " displayRequiredNow=" + Native.DisplayRequired());
            try { Application.Run(new Controller(opt)); }
            catch (Exception ex) { Log("crashed: " + ex); }
            finally { CursorHider.Restore(); ReattachNow(); }
            GC.KeepAlive(mutex);
        }
    }

    class Controller : ApplicationContext
    {
        readonly Options opt;
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly Random rng = new Random();
        SaverForm form;
        uint inputAtShow;
        int shownAt, graceMs;
        long watchLen;
        int lastActivity = Environment.TickCount;
        NotifyIcon tray;
        bool paused;
        DateTime pausedUntil = DateTime.MaxValue;
        EventWaitHandle exitEvent;
        PhysicalInput input;
        int detachRetryAt;
        bool loggedAgentWait;
        int lastDebugLog;
        const int AgentQuietMs = 60000;   // don't pull a monitor away while something is still clicking on it

        public Controller(Options o)
        {
            opt = o;
            try { input = new PhysicalInput(); }
            catch (Exception ex) { Program.Log("raw input unavailable: " + ex.Message); }
            watchLen = WatchLength();
            tick.Interval = 150;
            tick.Tick += OnTick;
            tick.Start();
            Application.ApplicationExit += delegate { CursorHider.Restore(); Program.ReattachNow(); if (tray != null) tray.Visible = false; };
            if (opt.Preview) ShowSaver(700);
            else { CreateTray(); ListenForExit(); }
        }

        void ListenForExit()
        {
            var ui = SynchronizationContext.Current;
            exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
            ThreadPool.RegisterWaitForSingleObject(exitEvent, delegate { ui.Post(delegate { Quit(); }, null); }, null, -1, true);
        }

        void Quit()
        {
            if (form != null) HideSaver();
            Program.ReattachNow();
            if (tray != null) tray.Visible = false;
            ExitThread();
        }

        // ---- tray menu ----

        void CreateTray()
        {
            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.ContextMenuStrip = new ContextMenuStrip();
            tray.ContextMenuStrip.Opening += delegate { BuildMenu(); };
            tray.DoubleClick += delegate { ShowNow(); };
            BuildMenu();
            UpdateTooltip();
            tray.Visible = true;
        }

        void BuildMenu()
        {
            var items = tray.ContextMenuStrip.Items;
            items.Clear();

            var show = new ToolStripMenuItem("Show now", null, delegate { ShowNow(); });
            show.Font = new Font(show.Font, FontStyle.Bold);
            items.Add(show);
            items.Add(new ToolStripSeparator());

            var anim = new ToolStripMenuItem("Animation");
            AddChoice(anim, "Random", opt.Mode == "random", delegate { opt.Mode = "random"; Changed(); });
            AddChoice(anim, "Matrix rain", opt.Mode == "matrix", delegate { opt.Mode = "matrix"; Changed(); });
            AddChoice(anim, "Mystify", opt.Mode == "mystify", delegate { opt.Mode = "mystify"; Changed(); });
            AddChoice(anim, "Blank (black)", opt.Mode == "blank", delegate { opt.Mode = "blank"; Changed(); });
            items.Add(anim);

            var after = new ToolStripMenuItem("Start after");
            foreach (int min in new[] { 1, 2, 5, 10, 15, 30, 60 })
            {
                int m = min;
                AddChoice(after, m + (m == 1 ? " minute" : " minutes"), opt.IdleSeconds == m * 60,
                          delegate { opt.IdleSeconds = m * 60; Changed(); });
            }
            items.Add(after);

            var cursor = new ToolStripMenuItem("Hide cursor", null, delegate { opt.HideCursor = !opt.HideCursor; Changed(); });
            cursor.Checked = opt.HideCursor;
            items.Add(cursor);

            var injected = new ToolStripMenuItem("Ignore input from apps (AI agents, automation)", null,
                delegate { opt.IgnoreInjected = !opt.IgnoreInjected; Changed(); });
            injected.Checked = opt.IgnoreInjected;
            items.Add(injected);

            var keep = new ToolStripMenuItem("Keep screen on for video");
            AddChoice(keep, "Fullscreen only", opt.KeepOnFor == "fullscreen", delegate { opt.KeepOnFor = "fullscreen"; Changed(); });
            AddChoice(keep, "Always", opt.KeepOnFor == "always", delegate { opt.KeepOnFor = "always"; Changed(); });
            AddChoice(keep, "Never", opt.KeepOnFor == "never", delegate { opt.KeepOnFor = "never"; Changed(); });
            items.Add(keep);
            items.Add(BuildMonitorMenu());
            items.Add(BuildAppsMenu());
            items.Add(new ToolStripSeparator());

            if (paused)
            {
                string until = pausedUntil == DateTime.MaxValue ? "until resumed" : "until " + pausedUntil.ToString("t");
                items.Add(new ToolStripMenuItem("Paused " + until) { Enabled = false });
                items.Add(new ToolStripMenuItem("Resume", null, delegate { paused = false; UpdateTooltip(); }));
            }
            else
            {
                items.Add(new ToolStripMenuItem("Pause for 1 hour", null, delegate { Pause(DateTime.Now.AddHours(1)); }));
                items.Add(new ToolStripMenuItem("Pause until resumed", null, delegate { Pause(DateTime.MaxValue); }));
            }
            items.Add(new ToolStripSeparator());

            bool auto = false;
            try { auto = Autostart.Enabled; } catch { }
            var startup = new ToolStripMenuItem("Start with Windows", null, delegate
            {
                try { Autostart.Enabled = !auto; Program.Log("autostart: " + !auto); }
                catch (Exception ex) { Program.Log("autostart error: " + ex.Message); }
            });
            startup.Checked = auto;
            items.Add(startup);
            items.Add(new ToolStripMenuItem("About IdleVeil", null, delegate { About(); }));
            items.Add(new ToolStripMenuItem("Exit", null, delegate { Quit(); }));
        }

        // The last real app that had focus (opening the tray menu moves focus to the taskbar).
        string lastFocusedApp;

        ToolStripMenuItem BuildAppsMenu()
        {
            var menu = new ToolStripMenuItem("Don't show while using");
            string own = System.Diagnostics.Process.GetCurrentProcess().ProcessName.ToLowerInvariant() + ".exe";
            if (lastFocusedApp != null && lastFocusedApp != own && !opt.NoVeilApps.Contains(lastFocusedApp))
            {
                string app = lastFocusedApp;
                menu.DropDownItems.Add(new ToolStripMenuItem("Add " + app, null, delegate { opt.NoVeilApps.Add(app); Changed(); }));
            }
            if (opt.NoVeilApps.Count > 0)
            {
                if (menu.DropDownItems.Count > 0) menu.DropDownItems.Add(new ToolStripSeparator());
                menu.DropDownItems.Add(new ToolStripMenuItem("Click an app to remove it:") { Enabled = false });
                foreach (string a in opt.NoVeilApps.ToArray())
                {
                    string app = a;
                    var item = new ToolStripMenuItem(app, null, delegate { opt.NoVeilApps.Remove(app); Changed(); });
                    item.Checked = true;
                    menu.DropDownItems.Add(item);
                }
            }
            if (menu.DropDownItems.Count == 0)
                menu.DropDownItems.Add(new ToolStripMenuItem("Focus an app, then open this menu to add it") { Enabled = false });
            return menu;
        }

        ToolStripMenuItem BuildMonitorMenu()
        {
            var menu = new ToolStripMenuItem("Turn off monitor");
            AddChoice(menu, "None", opt.OffMonitor == "", delegate { opt.OffMonitor = ""; Changed(); });
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (DisplayDetach.IsDetached && opt.OffMonitor != "")
            {
                seen.Add(opt.OffMonitor);
                AddChoice(menu, DisplayDetach.DetachedName + " (off now)", true, delegate { });
            }
            foreach (var m in DisplayDetach.List())
            {
                if (m.Id == "" || !seen.Add(m.Id)) continue;
                string id = m.Id;
                AddChoice(menu, m.Name + " (" + id + ")", string.Equals(opt.OffMonitor, id, StringComparison.OrdinalIgnoreCase),
                          delegate { opt.OffMonitor = id; Changed(); });
            }
            menu.DropDownItems.Add(new ToolStripSeparator());
            menu.DropDownItems.Add(new ToolStripMenuItem("After the veil has shown for:") { Enabled = false });
            foreach (int min in new[] { 1, 2, 5, 10, 15, 30, 60 })
            {
                int m = min;
                AddChoice(menu, "    " + m + (m == 1 ? " minute" : " minutes"), opt.OffAfterSeconds == m * 60,
                          delegate { opt.OffAfterSeconds = m * 60; Changed(); });
            }
            return menu;
        }

        static void About()
        {
            MessageBox.Show(
                "IdleVeil " + AppInfo.Version + "\n\n" +
                "Burn-in protection overlay that stays out of the way of remote control and screen capture.\n\n" +
                "Settings and log: " + AppPaths.Dir,
                "About IdleVeil", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static void AddChoice(ToolStripMenuItem parent, string text, bool isChecked, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text, null, onClick);
            item.Checked = isChecked;
            parent.DropDownItems.Add(item);
        }

        void Changed()
        {
            opt.SaveSettings();
            UpdateTooltip();
            Program.Log("settings: idle=" + opt.IdleSeconds + "s mode=" + opt.Mode + " hideCursor=" + opt.HideCursor
                + " noVeilApps=" + string.Join(",", opt.NoVeilApps.ToArray()) + " ignoreInjected=" + opt.IgnoreInjected + " keepOnFor=" + opt.KeepOnFor + " offMonitor=" + opt.OffMonitor + " offAfter=" + opt.OffAfterSeconds + "s");
        }

        void Pause(DateTime until)
        {
            paused = true;
            pausedUntil = until;
            UpdateTooltip();
        }

        void ShowNow()
        {
            if (form == null) ShowSaver(1500);   // a little longer grace so the click that chose it doesn't close it
        }

        void UpdateTooltip()
        {
            if (tray == null) return;
            string mode = opt.Mode == "matrix" ? "Matrix rain" : opt.Mode == "mystify" ? "Mystify" : "Random";
            tray.Text = paused ? "IdleVeil (paused)" : "IdleVeil: " + mode + " after " + (opt.IdleSeconds / 60) + " min";
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var bg = new SolidBrush(Color.FromArgb(10, 20, 12))) g.FillRectangle(bg, 1, 1, 30, 30);
                    int[] xs = { 5, 12, 19, 26 }, tops = { 4, 12, 2, 9 }, lens = { 22, 16, 26, 18 };
                    for (int i = 0; i < 4; i++)
                        for (int y = 0; y < lens[i]; y += 4)
                        {
                            int b = 80 + 175 * y / lens[i];
                            using (var br = new SolidBrush(y + 4 >= lens[i] ? Color.FromArgb(220, 255, 220) : Color.FromArgb(0, b, b / 4)))
                                g.FillRectangle(br, xs[i] - 2, tops[i] + y, 4, 3);
                        }
                }
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }

        // ---- idle logic ----

        long WatchLength()
        {
            try { return opt.WatchFile == null ? -1 : new FileInfo(opt.WatchFile).Length; }
            catch { return -1; }
        }

        // Tick of the last input that counts as someone being here.
        uint InputTick()
        {
            uint t = opt.IgnoreInjected && input != null && input.Ok ? input.LastPhysicalTick : Native.LastInputTick();
            uint pad = Controllers.LastActiveTick;
            // Ticks wrap around every 49 days, so compare by difference rather than value.
            return pad != 0 && (int)(pad - t) > 0 ? pad : t;
        }

        bool KeepDisplayOn()
        {
            if (opt.Preview || opt.KeepOnFor == "never" || !Native.DisplayRequired()) return false;
            return opt.KeepOnFor == "always" || Native.ForegroundIsFullscreen();
        }

        void MaybeDetach(int now)
        {
            if (opt.Preview || opt.OffMonitor == "" || DisplayDetach.IsDetached) return;
            if (now - shownAt < opt.OffAfterSeconds * 1000 || now - detachRetryAt < 0) return;
            if (input != null && input.Ok && (uint)now - input.LastInjectedTick < AgentQuietMs)
            {
                if (!loggedAgentWait) { Program.Log("turn-off waiting: an app is still sending input"); loggedAgentWait = true; }
                return;
            }
            loggedAgentWait = false;
            if (!DisplayDetach.Detach(opt.OffMonitor)) detachRetryAt = now + 60000;
        }

        void OnTick(object sender, EventArgs e)
        {
            try
            {
                int now = Environment.TickCount;
                Controllers.Poll();
                uint lastInput = InputTick();
                string fgApp = Native.ForegroundApp();
                if (fgApp != null && form == null) lastFocusedApp = fgApp;
                bool appKeepsOn = fgApp != null && opt.NoVeilApps.Contains(fgApp);
                long len = WatchLength();
                bool logGrew = len != watchLen;
                watchLen = len;
                bool displayRequired = KeepDisplayOn();

                if (form != null)
                {
                    // The screen layout changed while showing (e.g. the TV was switched off and back on):
                    // rebuild the overlay at the new size so it covers everything again.
                    if (SystemInformation.VirtualScreen != form.Area) RebuildSaver();
                    // Ignore input in the first moment (e.g. the Enter key that launched a preview).
                    if (now - shownAt < graceMs) { inputAtShow = lastInput; return; }
                    if (lastInput != inputAtShow || logGrew || displayRequired)
                    {
                        Program.Log("veil hidden: " + (lastInput != inputAtShow ? "input" : logGrew ? "watched file grew" : "fullscreen app keeps display on"));
                        HideSaver();
                        lastActivity = now;
                        if (opt.Preview) ExitThread();
                        return;
                    }
                    MaybeDetach(now);
                    return;
                }

                if (logGrew || displayRequired || appKeepsOn) lastActivity = now;
                if (paused && DateTime.Now >= pausedUntil) { paused = false; UpdateTooltip(); }
                if (paused) return;
                int sinceInput = (int)((uint)now - lastInput);
                int idle = Math.Min(sinceInput, now - lastActivity);
                if (opt.Debug && now - lastDebugLog > 10000)
                {
                    lastDebugLog = now;
                    Program.Log("debug: sinceInput=" + sinceInput + " sinceActivity=" + (now - lastActivity)
                        + " sinceInjected=" + (input != null && input.Ok ? ((uint)now - input.LastInjectedTick).ToString() : "-")
                        + " displayRequired=" + Native.DisplayRequired() + " fullscreenFg=" + Native.ForegroundIsFullscreen()
                        + " fgApp=" + (fgApp ?? "-") + " lastPhysical=" + (input != null ? input.LastSource : "-")
                        + " lastController=" + (Controllers.LastSource ?? "-"));
                }
                if (idle >= opt.IdleSeconds * 1000) { Program.Log("veil shown after " + idle / 1000 + "s idle"); ShowSaver(700); }
            }
            catch (Exception ex) { Program.Log("tick error: " + ex); }
        }

        void ShowSaver(int grace)
        {
            shownMode = opt.Mode == "random" ? (rng.Next(2) == 0 ? "matrix" : "mystify") : opt.Mode;
            form = new SaverForm(shownMode, opt.Fps);
            form.ShowSaver();
            if (opt.HideCursor) CursorHider.Hide();
            graceMs = grace;
            shownAt = Environment.TickCount;
            inputAtShow = InputTick();
        }

        string shownMode;

        void RebuildSaver()
        {
            Program.Log("screen changed to " + SystemInformation.VirtualScreen + ", resizing overlay");
            form.Close();
            form.Dispose();
            form = new SaverForm(shownMode, opt.Fps);
            form.ShowSaver();
        }

        void HideSaver()
        {
            CursorHider.Restore();
            form.Close();
            form.Dispose();
            form = null;
            detachRetryAt = 0;
            if (DisplayDetach.IsDetached)
            {
                DisplayDetach.Reattach();
                // Give Windows a moment to bring the monitor back before moving windows onto it.
                var later = new System.Windows.Forms.Timer { Interval = 1500 };
                later.Tick += delegate { later.Stop(); later.Dispose(); DisplayDetach.RestoreWindows(); };
                later.Start();
            }
        }
    }

    interface IAnimation : IDisposable
    {
        void Step();
        void Draw(IntPtr hdc);
    }

    class SaverForm : Form
    {
        readonly IAnimation anim;
        readonly System.Windows.Forms.Timer frame = new System.Windows.Forms.Timer();
        // Our own GDI back buffer: WinForms' double buffer converts the whole frame between GDI+ and GDI, which is slow at 1440p.
        readonly IntPtr bufDC, bufBmp, oldBufBmp;
        readonly int w, h;
        public readonly Rectangle Area;   // the screen area this overlay was built for

        public SaverForm(string mode, int fps)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Area = SystemInformation.VirtualScreen;
            Bounds = Area;
            BackColor = Color.Black;
            Text = "IdleVeil";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            w = Bounds.Width; h = Bounds.Height;

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            bufDC = Native.CreateCompatibleDC(screen);
            bufBmp = Native.CreateCompatibleBitmap(screen, w, h);
            Native.ReleaseDC(IntPtr.Zero, screen);
            oldBufBmp = Native.SelectObject(bufDC, bufBmp);
            Native.PatBlt(bufDC, 0, 0, w, h, Native.BLACKNESS);

            anim = mode == "matrix" ? (IAnimation)new MatrixRain(Bounds.Size)
                 : mode == "blank" ? (IAnimation)new Blank(Bounds.Size)
                 : new Mystify(Bounds.Size);
            // Blank never changes: redraw rarely, just to stay on top.
            frame.Interval = mode == "blank" ? 500 : Math.Max(10, 1000 / Math.Max(1, fps));
            frame.Tick += delegate { RenderFrame(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // TOOLWINDOW | NOACTIVATE | TOPMOST | TRANSPARENT (click-through) | LAYERED
                cp.ExStyle |= 0x80 | 0x08000000 | 0x8 | 0x20 | 0x80000;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetLayeredWindowAttributes(Handle, 0, 255, 2);
            // WDA_EXCLUDEFROMCAPTURE: invisible to screenshots, visible on the TV.
            if (!Native.SetWindowDisplayAffinity(Handle, 0x11))
                Program.Log("SetWindowDisplayAffinity failed: " + Marshal.GetLastWin32Error());
        }

        public void ShowSaver()
        {
            Show();
            // Tell the shell we're a fullscreen window so the taskbar steps aside.
            try
            {
                var tb = (ITaskbarList2)new TaskbarList();
                tb.HrInit();
                tb.MarkFullscreenWindow(Handle, true);
                Marshal.ReleaseComObject(tb);
            }
            catch (Exception ex) { Program.Log("MarkFullscreenWindow failed: " + ex.Message); }
            RaiseToTop();
            frame.Start();
        }

        // The taskbar is topmost too and can push itself above us, so this is re-applied while showing.
        void RaiseToTop()
        {
            // HWND_TOPMOST, NOSIZE | NOMOVE | NOACTIVATE | SHOWWINDOW
            Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        class TaskbarList { }

        [ComImport, Guid("602D4995-B13A-429b-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList2
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        public static bool Stats;
        readonly System.Diagnostics.Stopwatch drawSw = new System.Diagnostics.Stopwatch();
        readonly System.Diagnostics.Stopwatch wallSw = System.Diagnostics.Stopwatch.StartNew();
        int frames;

        void RenderFrame()
        {
            drawSw.Start();
            anim.Step();
            anim.Draw(bufDC);
            Present();
            drawSw.Stop();
            frames++;
            if (frames % 10 == 0) RaiseToTop();
            if (Stats && wallSw.ElapsedMilliseconds > 5000)
            {
                Program.Log("stats: fps=" + (frames * 1000 / wallSw.ElapsedMilliseconds) + " frame ms=" + (drawSw.ElapsedMilliseconds / (double)frames).ToString("0.0"));
                frames = 0; drawSw.Reset(); wallSw.Restart();
            }
        }

        void Present()
        {
            IntPtr wdc = Native.GetDC(Handle);
            Native.BitBlt(wdc, 0, 0, w, h, bufDC, 0, 0, Native.SRCCOPY);
            Native.ReleaseDC(Handle, wdc);
        }

        protected override void OnPaint(PaintEventArgs e) { Present(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                frame.Stop(); frame.Dispose(); anim.Dispose();
                Native.SelectObject(bufDC, oldBufBmp);
                Native.DeleteObject(bufBmp);
                Native.DeleteDC(bufDC);
            }
            base.Dispose(disposing);
        }
    }

    // Pure black: on an OLED, black pixels are switched off.
    class Blank : IAnimation
    {
        readonly int w, h;
        public Blank(Size size) { w = size.Width; h = size.Height; }
        public void Step() { }
        public void Draw(IntPtr hdc) { Native.PatBlt(hdc, 0, 0, w, h, Native.BLACKNESS); }
        public void Dispose() { }
    }

    class MatrixRain : IAnimation
    {
        const int Levels = 12;
        readonly int w, h, cell, cols, rows, glyphCount;
        readonly Bitmap atlas;
        readonly IntPtr atlasDC, atlasBmp, oldBmp;
        readonly float[] head, speed;
        readonly int[] trail;
        readonly int[,] glyphs;
        readonly Random rng = new Random();

        public MatrixRain(Size size)
        {
            w = size.Width; h = size.Height;
            cell = Math.Max(14, h / 56);
            cols = w / cell + 1; rows = h / cell + 1;

            string chars = "0123456789:.=*+-<>|Z";
            for (char c = 'ｦ'; c <= 'ﾝ'; c++) chars += c;
            glyphCount = chars.Length;

            // Pre-render every glyph at every brightness, then just copy cells each frame.
            atlas = new Bitmap(glyphCount * cell, Levels * cell);
            using (var g = Graphics.FromImage(atlas))
            using (var font = new Font(PickFont(), cell * 0.75f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;
                fmt.LineAlignment = StringAlignment.Center;
                g.Clear(Color.Black);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                for (int lv = 0; lv < Levels; lv++)
                    using (var b = new SolidBrush(LevelColor(lv)))
                        for (int i = 0; i < glyphCount; i++)
                            g.DrawString(chars[i].ToString(), font, b, new RectangleF(i * cell, lv * cell, cell, cell), fmt);
            }
            atlasBmp = atlas.GetHbitmap(Color.Black);
            atlasDC = Native.CreateCompatibleDC(IntPtr.Zero);
            oldBmp = Native.SelectObject(atlasDC, atlasBmp);

            head = new float[cols]; speed = new float[cols]; trail = new int[cols];
            glyphs = new int[cols, rows];
            for (int c = 0; c < cols; c++)
            {
                Reset(c);
                head[c] = (float)(rng.NextDouble() * rows * 1.5 - rows * 0.5);
                for (int r = 0; r < rows; r++) glyphs[c, r] = rng.Next(glyphCount);
            }
        }

        static string PickFont()
        {
            using (var fonts = new InstalledFontCollection())
                foreach (var f in fonts.Families)
                    if (f.Name == "MS Gothic") return f.Name;
            return "Consolas";
        }

        static Color LevelColor(int lv)
        {
            if (lv == 0) return Color.FromArgb(210, 255, 210);
            float b = 1f - (lv - 1) * 0.85f / (Levels - 2);
            return Color.FromArgb(0, (int)(230 * b), (int)(65 * b));
        }

        void Reset(int c)
        {
            trail[c] = rng.Next(8, Math.Max(12, rows * 2 / 3));
            speed[c] = (float)(0.2 + rng.NextDouble() * 0.5);
            head[c] = -rng.Next(0, Math.Max(1, rows / 2));
        }

        public void Step()
        {
            for (int c = 0; c < cols; c++)
            {
                int before = (int)head[c];
                head[c] += speed[c];
                int now = (int)head[c];
                if (now != before && now >= 0 && now < rows) glyphs[c, now] = rng.Next(glyphCount);
                if (head[c] - trail[c] > rows) Reset(c);
            }
            for (int k = 0; k < cols / 3; k++) glyphs[rng.Next(cols), rng.Next(rows)] = rng.Next(glyphCount);
        }

        public void Draw(IntPtr hdc)
        {
            {
                Native.PatBlt(hdc, 0, 0, w, h, Native.BLACKNESS);
                for (int c = 0; c < cols; c++)
                {
                    int top = (int)head[c];
                    int len = trail[c];
                    for (int k = 0; k < len; k++)
                    {
                        int r = top - k;
                        if (r < 0) break;
                        if (r >= rows) continue;
                        int lv = k == 0 ? 0 : 1 + (k - 1) * (Levels - 2) / Math.Max(1, len - 1);
                        Native.BitBlt(hdc, c * cell, r * cell, cell, cell, atlasDC, glyphs[c, r] * cell, lv * cell, Native.SRCCOPY);
                    }
                }
            }
        }

        public void Dispose()
        {
            Native.SelectObject(atlasDC, oldBmp);
            Native.DeleteDC(atlasDC);
            Native.DeleteObject(atlasBmp);
            atlas.Dispose();
        }
    }

    class Mystify : IAnimation
    {
        const int Trail = 14;

        class Poly
        {
            public PointF[] P = new PointF[4];
            public PointF[] V = new PointF[4];
            public float Hue, HueSpeed;
            public List<PointF[]> History = new List<PointF[]>();
        }

        readonly Poly[] polys = new Poly[2];
        readonly int w, h;
        readonly float lineWidth;
        readonly Random rng = new Random();
        int frameNo;

        public Mystify(Size size)
        {
            w = size.Width; h = size.Height;
            lineWidth = Math.Max(1.5f, h / 600f);
            float sp = h / 260f;
            for (int i = 0; i < polys.Length; i++)
            {
                var q = new Poly();
                for (int j = 0; j < 4; j++)
                {
                    q.P[j] = new PointF((float)rng.NextDouble() * w, (float)rng.NextDouble() * h);
                    q.V[j] = new PointF(RandSpeed(sp), RandSpeed(sp));
                }
                q.Hue = (float)rng.NextDouble() * 360f;
                q.HueSpeed = 0.3f + (float)rng.NextDouble() * 0.4f;
                polys[i] = q;
            }
        }

        float RandSpeed(float sp)
        {
            float v = sp * (0.5f + (float)rng.NextDouble());
            return rng.Next(2) == 0 ? v : -v;
        }

        public void Step()
        {
            frameNo++;
            foreach (var q in polys)
            {
                for (int j = 0; j < 4; j++)
                {
                    float x = q.P[j].X + q.V[j].X, y = q.P[j].Y + q.V[j].Y;
                    if (x < 0 || x >= w) { q.V[j].X = -q.V[j].X; x = Math.Max(0, Math.Min(w - 1, x)); }
                    if (y < 0 || y >= h) { q.V[j].Y = -q.V[j].Y; y = Math.Max(0, Math.Min(h - 1, y)); }
                    q.P[j] = new PointF(x, y);
                }
                q.Hue = (q.Hue + q.HueSpeed) % 360f;
                if (frameNo % 3 == 0)
                {
                    q.History.Add((PointF[])q.P.Clone());
                    if (q.History.Count > Trail) q.History.RemoveAt(0);
                }
            }
        }

        // Plain GDI lines, like the original Mystify: far cheaper than GDI+ anti-aliasing at 1440p.
        public void Draw(IntPtr hdc)
        {
            {
                Native.PatBlt(hdc, 0, 0, w, h, Native.BLACKNESS);
                var pts = new Native.POINT[5];
                foreach (var q in polys)
                {
                    Color c = FromHue(q.Hue);
                    int n = q.History.Count;
                    for (int i = 0; i <= n; i++)
                    {
                        PointF[] poly = i < n ? q.History[i] : q.P;
                        Color pc = i < n ? Scale(c, (i + 1f) / (n + 1)) : c;
                        for (int j = 0; j < 5; j++) { pts[j].X = (int)poly[j % 4].X; pts[j].Y = (int)poly[j % 4].Y; }
                        IntPtr pen = Native.CreatePen(0, (int)Math.Round(lineWidth), (uint)(pc.R | (pc.G << 8) | (pc.B << 16)));
                        IntPtr old = Native.SelectObject(hdc, pen);
                        Native.Polyline(hdc, pts, 5);
                        Native.SelectObject(hdc, old);
                        Native.DeleteObject(pen);
                    }
                }
            }
        }

        static Color Scale(Color c, float f)
        {
            return Color.FromArgb((int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
        }

        static Color FromHue(float hue)
        {
            float x = 1 - Math.Abs((hue / 60f) % 2 - 1);
            float r, g, b;
            if (hue < 60) { r = 1; g = x; b = 0; }
            else if (hue < 120) { r = x; g = 1; b = 0; }
            else if (hue < 180) { r = 0; g = 1; b = x; }
            else if (hue < 240) { r = 0; g = x; b = 1; }
            else if (hue < 300) { r = x; g = 0; b = 1; }
            else { r = 1; g = 0; b = x; }
            return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
        }

        public void Dispose() { }
    }
}
