// Turns one monitor off for real by removing it from the desktop layout (SetDisplayConfig).
// The monitor sees "no signal" and enters its own standby, which is where OLED panels run their
// pixel-cleaning cycle, and nothing in Windows (input, apps asking to keep the display on) can wake it.
// The layout is never saved to the Windows display database, so a reboot or crash leaves it intact,
// and on reattach the exact previous layout and window positions are restored.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace IdleVeil
{
    class MonitorInfo
    {
        public string Id;     // EDID manufacturer + product code, e.g. "AUS2740"
        public string Name;   // friendly name, e.g. "PG27AQDP"
    }

    static class DisplayDetach
    {
        // ---- CCD (QueryDisplayConfig / SetDisplayConfig) interop ----
        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Lo; public int Hi; }
        [StructLayout(LayoutKind.Sequential)] struct PathSource { public LUID Adapter; public uint Id, ModeIdx, Status; }
        [StructLayout(LayoutKind.Sequential)] struct PathTarget { public LUID Adapter; public uint Id, ModeIdx, Tech, Rotation, Scaling, RefreshNum, RefreshDen, ScanLine; public int Available; public uint Status; }
        [StructLayout(LayoutKind.Sequential)] struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        struct ModeInfo { [FieldOffset(0)] public uint Type; [FieldOffset(4)] public uint Id; [FieldOffset(8)] public LUID Adapter; [FieldOffset(28)] public int X; [FieldOffset(32)] public int Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TargetName
        {
            public uint Type, Size; public LUID Adapter; public uint Id;
            public uint Flags, Tech; public ushort Mfg, Product; public uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
        }

        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint np, [Out] PathInfo[] paths, ref uint nm, [Out] ModeInfo[] modes, IntPtr topology);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint np, PathInfo[] paths, uint nm, ModeInfo[] modes, uint flags);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TargetName name);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SourceName
        {
            public uint Type, Size; public LUID Adapter; public uint Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName;
        }
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName name);

        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        const uint SDC_APPLY = 0x80, SDC_USE_SUPPLIED = 0x20, SDC_ALLOW_CHANGES = 0x400, SDC_VALIDATE = 0x40, SDC_USE_DATABASE_CURRENT = 0xF;
        const uint MODE_SOURCE = 1;

        // ---- state ----
        static PathInfo[] savedPaths;
        static ModeInfo[] savedModes;
        static List<WindowSpot> savedWindows;
        static IntPtr savedForeground;
        static string detachedName;

        // Marker file: if IdleVeil is killed while a monitor is detached, the next start puts it back.
        static string MarkerPath { get { return Path.Combine(AppPaths.Dir, "detached.flag"); } }

        public static bool IsDetached { get { return savedPaths != null; } }
        public static string DetachedName { get { return detachedName; } }

        static bool Query(out PathInfo[] paths, out ModeInfo[] modes)
        {
            uint np, nm;
            paths = null; modes = null;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) return false;
            paths = new PathInfo[np]; modes = new ModeInfo[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return false;
            Array.Resize(ref paths, (int)np); Array.Resize(ref modes, (int)nm);
            return true;
        }

        static MonitorInfo Describe(PathTarget t)
        {
            var n = new TargetName();
            n.Type = 2;   // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
            n.Size = (uint)Marshal.SizeOf(typeof(TargetName));
            n.Adapter = t.Adapter; n.Id = t.Id;
            if (DisplayConfigGetDeviceInfo(ref n) != 0) return new MonitorInfo { Id = "", Name = "Unknown display" };
            // Device path looks like \\?\DISPLAY#AUS2740#5&1f582d60&0&UID4357#{...}
            string[] parts = (n.DevicePath ?? "").Split('#');
            string id = parts.Length > 1 ? parts[1] : "";
            string name = string.IsNullOrEmpty(n.FriendlyName) ? id : n.FriendlyName;
            return new MonitorInfo { Id = id, Name = name };
        }

        // Active monitors, for the tray menu.
        public static List<MonitorInfo> List()
        {
            var list = new List<MonitorInfo>();
            PathInfo[] paths; ModeInfo[] modes;
            if (!Query(out paths, out modes)) return list;
            foreach (var p in paths) list.Add(Describe(p.Target));
            return list;
        }

        // Active monitors by GDI device name (\\.\DISPLAY1, as in Screen.DeviceName), to match a
        // WinForms Screen to its EDID id.
        public static Dictionary<string, MonitorInfo> ByGdiName()
        {
            var map = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase);
            PathInfo[] paths; ModeInfo[] modes;
            if (!Query(out paths, out modes)) return map;
            foreach (var p in paths)
            {
                var n = new SourceName();
                n.Type = 1;   // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                n.Size = (uint)Marshal.SizeOf(typeof(SourceName));
                n.Adapter = p.Source.Adapter; n.Id = p.Source.Id;
                if (DisplayConfigGetDeviceInfo(ref n) != 0 || string.IsNullOrEmpty(n.GdiName)) continue;
                map[n.GdiName] = Describe(p.Target);
            }
            return map;
        }

        public static bool Detach(IList<string> monitorIds)
        {
            if (IsDetached || monitorIds == null || monitorIds.Count == 0) return false;
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in monitorIds) if (!string.IsNullOrEmpty(id)) wanted.Add(id);
            if (wanted.Count == 0) return false;
            string picked = string.Join(",", new List<string>(monitorIds).ToArray());
            PathInfo[] paths; ModeInfo[] modes;
            if (!Query(out paths, out modes)) { Program.Log("detach: QueryDisplayConfig failed"); return false; }

            var keep = new List<PathInfo>();
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                var info = Describe(p.Target);
                if (wanted.Contains(info.Id))
                {
                    seen.Add(info.Id);
                    if (!names.Contains(info.Name)) names.Add(info.Name);
                }
                else keep.Add(p);
            }
            if (names.Count == 0) { Program.Log("detach: none of " + picked + " is active"); return false; }
            foreach (string id in wanted) if (!seen.Contains(id)) Program.Log("detach: " + id + " is not active");
            if (keep.Count == 0) { Program.Log("detach: refusing to turn off every active monitor"); return false; }

            // Windows needs a monitor at (0,0) to become primary: shift the remaining ones so the top-left one is there.
            var newModes = (ModeInfo[])modes.Clone();
            int minX = int.MaxValue, minY = int.MaxValue;
            foreach (var p in keep)
            {
                if (p.Source.ModeIdx >= newModes.Length || newModes[p.Source.ModeIdx].Type != MODE_SOURCE) continue;
                minX = Math.Min(minX, newModes[p.Source.ModeIdx].X); minY = Math.Min(minY, newModes[p.Source.ModeIdx].Y);
            }
            if (minX != int.MaxValue)
                foreach (var p in keep)
                    if (p.Source.ModeIdx < newModes.Length && newModes[p.Source.ModeIdx].Type == MODE_SOURCE)
                    { newModes[p.Source.ModeIdx].X -= minX; newModes[p.Source.ModeIdx].Y -= minY; }

            var arr = keep.ToArray();
            int r = SetDisplayConfig((uint)arr.Length, arr, (uint)newModes.Length, newModes, SDC_VALIDATE | SDC_USE_SUPPLIED | SDC_ALLOW_CHANGES);
            if (r != 0) { Program.Log("detach: layout rejected by Windows (" + r + ")"); return false; }

            savedWindows = WindowSpot.Capture();
            savedForeground = WindowSpot.Foreground();
            savedPaths = paths; savedModes = modes; detachedName = string.Join(", ", names.ToArray());
            try { File.WriteAllText(MarkerPath, picked); } catch { }

            r = SetDisplayConfig((uint)arr.Length, arr, (uint)newModes.Length, newModes, SDC_APPLY | SDC_USE_SUPPLIED | SDC_ALLOW_CHANGES);
            if (r != 0)
            {
                Program.Log("detach: SetDisplayConfig failed (" + r + ")");
                Forget();
                return false;
            }
            Program.Log("detached " + detachedName + " (" + picked + "), saved " + savedWindows.Count + " windows");
            return true;
        }

        // Puts the monitor back. Window positions are restored separately (RestoreWindows), a moment later,
        // once Windows has finished re-creating the monitor.
        public static bool Reattach()
        {
            if (!IsDetached) return false;
            int r = SetDisplayConfig((uint)savedPaths.Length, savedPaths, (uint)savedModes.Length, savedModes, SDC_APPLY | SDC_USE_SUPPLIED | SDC_ALLOW_CHANGES);
            if (r != 0)
            {
                Program.Log("reattach: saved layout failed (" + r + "), restoring from the Windows display database");
                r = SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_USE_DATABASE_CURRENT);
            }
            Program.Log("reattached " + detachedName + " (" + r + ")");
            savedPaths = null; savedModes = null;
            try { File.Delete(MarkerPath); } catch { }
            return r == 0;
        }

        public static void RestoreWindows()
        {
            if (savedWindows == null) return;
            int n = WindowSpot.RestoreAll(savedWindows, savedForeground);
            Program.Log("restored " + n + "/" + savedWindows.Count + " windows");
            Forget();
        }

        static void Forget()
        {
            savedPaths = null; savedModes = null; savedWindows = null; savedForeground = IntPtr.Zero; detachedName = null;
            try { File.Delete(MarkerPath); } catch { }
        }

        // Called at start-up: if a previous run died while a monitor was detached, bring it back.
        public static void RecoverAfterCrash()
        {
            if (!File.Exists(MarkerPath)) return;
            int r = SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_USE_DATABASE_CURRENT);
            Program.Log("recovered a monitor left detached by a previous run (" + r + ")");
            try { File.Delete(MarkerPath); } catch { }
        }
    }

    // A top-level window's placement, so it can go back to the same spot after its monitor returns.
    class WindowSpot
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT { public int Length, Flags, ShowCmd; public int MinX, MinY, MaxX, MaxY; public RECT Normal; }

        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref ANIMATIONINFO pv, uint flags);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [StructLayout(LayoutKind.Sequential)] struct ANIMATIONINFO { public uint Size; public int MinAnimate; }

        const int SW_SHOWNOACTIVATE = 4, SW_SHOWMAXIMIZED = 3, SW_SHOWMINNOACTIVE = 7, SW_SHOWMINIMIZED = 2, SW_MAXIMIZE = 3;
        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;

        public IntPtr Hwnd;
        public WINDOWPLACEMENT Placement;

        public static IntPtr Foreground() { return GetForegroundWindow(); }

        public static List<WindowSpot> Capture()
        {
            var list = new List<WindowSpot>();
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                if (GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                int cloaked;
                if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out cloaked, 4) == 0 && cloaked != 0) return true;
                var wp = new WINDOWPLACEMENT();
                wp.Length = Marshal.SizeOf(wp);
                if (GetWindowPlacement(h, ref wp)) list.Add(new WindowSpot { Hwnd = h, Placement = wp });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // Restores every saved window without activating it. Minimized windows are briefly shown and
        // minimized again (with animations off), so their taskbar button moves back to the right taskbar.
        public static int RestoreAll(List<WindowSpot> spots, IntPtr foreground)
        {
            var anim = new ANIMATIONINFO { Size = 8 };
            bool gotAnim = SystemParametersInfo(0x0048 /* SPI_GETANIMATION */, 8, ref anim, 0);
            int oldAnim = anim.MinAnimate;
            if (gotAnim && oldAnim != 0) { anim.MinAnimate = 0; SystemParametersInfo(0x0049 /* SPI_SETANIMATION */, 8, ref anim, 0); }
            int ok = 0;
            try
            {
                foreach (var s in spots)
                {
                    if (!IsWindow(s.Hwnd)) continue;
                    var wp = s.Placement;
                    int cmd = wp.ShowCmd;
                    bool minimized = cmd == SW_SHOWMINIMIZED || cmd == SW_SHOWMINNOACTIVE || IsIconic(s.Hwnd);
                    if (minimized)
                    {
                        // Re-home it: show on its own monitor without focus, then minimize again.
                        wp.ShowCmd = SW_SHOWNOACTIVATE;
                        if ((wp.Flags & 2 /* WPF_RESTORETOMAXIMIZED */) != 0) wp.ShowCmd = SW_SHOWMAXIMIZED;
                        if (SetWindowPlacement(s.Hwnd, ref wp)) ok++;
                        ShowWindow(s.Hwnd, SW_SHOWMINNOACTIVE);
                    }
                    else
                    {
                        wp.ShowCmd = cmd == SW_SHOWMAXIMIZED ? SW_SHOWMAXIMIZED : SW_SHOWNOACTIVATE;
                        if (SetWindowPlacement(s.Hwnd, ref wp)) ok++;
                    }
                }
                if (foreground != IntPtr.Zero && IsWindow(foreground)) SetForegroundWindow(foreground);
            }
            finally
            {
                if (gotAnim && oldAnim != 0) { anim.MinAnimate = oldAnim; SystemParametersInfo(0x0049, 8, ref anim, 0); }
            }
            return ok;
        }
    }
}
