// The gentler alternative to detaching a monitor (DisplayDetach): send it a normal standby command
// over DDC/CI (VCP 0xD6, the same one the monitor's power button and Windows' DPMS use), so the
// desktop layout never changes and no windows move. Falls back to Windows' own "turn the display
// off" broadcast when the screen doesn't answer DDC and every screen was picked.
//
// Also answers "what is keeping the display on?" the way `powercfg /requests` does. That query
// needs admin rights; without them it says so instead of guessing.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace IdleVeil
{
    static class MonitorPower
    {
        // ---- dxva2 (DDC/CI) ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        }

        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetVCPFeature(IntPtr hPhysicalMonitor, byte code, uint value);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hPhysicalMonitor, byte code, out uint type, out uint current, out uint maximum);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] monitors);

        const byte VCP_POWER = 0xD6;   // 1 on, 2 standby, 3 suspend, 4 off (soft), 5 hard off
        const uint POWER_ON = 1, POWER_OFF = 4;

        // ---- monitors ----
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public uint Size; public RECT Monitor, Work; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }

        delegate bool EnumMonitorProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, EnumMonitorProc proc, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

        // ---- display power broadcast (fallback and wake) ----
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        const uint WM_SYSCOMMAND = 0x112;
        static readonly IntPtr SC_MONITORPOWER = new IntPtr(0xF170);
        const uint ES_CONTINUOUS = 0x80000000, ES_DISPLAY_REQUIRED = 0x2;

        sealed class Screen
        {
            public IntPtr Handle;
            public string Id, Name;   // EDID id and friendly name (as DisplayDetach reports them)
        }

        // Every active screen, with the EDID id used in the settings.
        static List<Screen> ActiveScreens()
        {
            var list = new List<Screen>();
            Dictionary<string, MonitorInfo> byName = DisplayDetach.ByGdiName();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr hdc, IntPtr rect, IntPtr data)
            {
                var info = new MONITORINFOEX();
                info.Size = (uint)Marshal.SizeOf(typeof(MONITORINFOEX));
                if (!GetMonitorInfo(h, ref info)) return true;
                MonitorInfo known;
                if (info.Device == null || !byName.TryGetValue(info.Device, out known)) return true;
                list.Add(new Screen { Handle = h, Id = known.Id, Name = known.Name });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // ---- state ----
        // One array per screen's physical monitors, so every handle is released exactly once.
        static readonly List<PHYSICAL_MONITOR[]> offGroups = new List<PHYSICAL_MONITOR[]>();
        static bool offByBroadcast;
        static string offNames;

        static string MarkerPath { get { return Path.Combine(AppPaths.Dir, "standby.flag"); } }

        public static bool IsOff { get { return offGroups.Count > 0 || offByBroadcast; } }
        public static string OffName { get { return offNames; } }

        // Turns the given screens off with a normal standby command. Returns false when nothing could
        // be turned off (the caller then retries later).
        public static bool Standby(IList<string> monitorIds)
        {
            if (IsOff || monitorIds == null || monitorIds.Count == 0) return false;
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in monitorIds) if (!string.IsNullOrEmpty(id)) wanted.Add(id);
            if (wanted.Count == 0) return false;

            List<Screen> screens = ActiveScreens();
            int notPicked = 0;
            foreach (Screen s in screens) if (!wanted.Contains(s.Id)) notPicked++;

            var newGroups = new List<PHYSICAL_MONITOR[]>();
            var names = new List<string>();
            var unsupported = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Screen s in screens)
            {
                if (!wanted.Contains(s.Id)) continue;
                seen.Add(s.Id);
                uint count;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(s.Handle, out count) || count == 0)
                {
                    unsupported.Add(s.Name);
                    continue;
                }
                var phys = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(s.Handle, count, phys))
                {
                    unsupported.Add(s.Name);
                    continue;
                }
                bool any = false;
                foreach (PHYSICAL_MONITOR p in phys)
                {
                    uint type, current, max;
                    // A monitor that doesn't answer 0xD6 can't do DDC standby: leave it alone.
                    if (!GetVCPFeatureAndVCPFeatureReply(p.hPhysicalMonitor, VCP_POWER, out type, out current, out max)) continue;
                    if (!SetVCPFeature(p.hPhysicalMonitor, VCP_POWER, POWER_OFF)) continue;
                    any = true;
                }
                if (any) { newGroups.Add(phys); names.Add(s.Name); }
                else { unsupported.Add(s.Name); DestroyPhysicalMonitors(count, phys); }
            }

            foreach (string id in wanted)
                if (!seen.Contains(id)) Program.Log("standby: " + id + " is not active");

            bool broadcast = false;
            if (unsupported.Count > 0)
            {
                // The broadcast turns off every screen at once, so only use it when every screen was
                // picked; otherwise it would pull the others down too.
                if (notPicked == 0)
                {
                    SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, new IntPtr(2));
                    broadcast = true;
                    names.Add("every screen (standby broadcast: " + string.Join(", ", unsupported.ToArray()) + " did not answer DDC)");
                    Program.Log("standby: " + string.Join(", ", unsupported.ToArray()) + " did not answer DDC, used the display standby broadcast");
                }
                else
                {
                    Program.Log("standby: " + string.Join(", ", unsupported.ToArray()) + " did not answer DDC (not turning off every screen instead)");
                }
            }

            if (newGroups.Count == 0 && !broadcast)
            {
                Program.Log("standby: no screen answered DDC power control" + (unsupported.Count > 0 ? " (" + string.Join(", ", unsupported.ToArray()) + ")" : ""));
                offNames = null;
                return false;
            }

            offGroups.AddRange(newGroups);
            offByBroadcast = broadcast;
            offNames = string.Join(", ", names.ToArray());
            try { File.WriteAllText(MarkerPath, offNames); } catch { }
            Program.Log("standby: turned off " + offNames + " with a normal standby command");
            return true;
        }

        // Brings back whatever Standby turned off.
        public static void Wake()
        {
            bool missed = false;
            foreach (PHYSICAL_MONITOR[] group in offGroups)
                foreach (PHYSICAL_MONITOR p in group)
                    if (!SetVCPFeature(p.hPhysicalMonitor, VCP_POWER, POWER_ON)) missed = true;

            if (offGroups.Count > 0 || offByBroadcast || missed)
            {
                // The OS's own display-on, for screens whose DDC port went quiet while they slept.
                SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, new IntPtr(-1));
                SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED);
                SetThreadExecutionState(ES_CONTINUOUS);
            }
            if (offGroups.Count > 0 || offByBroadcast)
                Program.Log("standby: woke " + (offNames ?? "the screens"));
            Forget();
        }

        static void Forget()
        {
            foreach (PHYSICAL_MONITOR[] group in offGroups)
                DestroyPhysicalMonitors((uint)group.Length, group);
            offGroups.Clear();
            offByBroadcast = false;
            offNames = null;
            try { File.Delete(MarkerPath); } catch { }
        }

        // A DDC standby outlives the process that asked for it, so a run that was killed while the
        // screens were off would leave them dark. Wake everything once at start-up if that happened.
        public static void RecoverAfterCrash()
        {
            if (!File.Exists(MarkerPath)) return;
            WakeEveryScreen();
            try { File.Delete(MarkerPath); } catch { }
            Program.Log("recovered screens left in standby by a previous run");
        }

        // Sends "on" to every screen's DDC power control (harmless when they're already on).
        public static void WakeEveryScreen()
        {
            foreach (Screen s in ActiveScreens())
            {
                uint count;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(s.Handle, out count) || count == 0) continue;
                var phys = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(s.Handle, count, phys)) continue;
                foreach (PHYSICAL_MONITOR p in phys) SetVCPFeature(p.hPhysicalMonitor, VCP_POWER, POWER_ON);
                DestroyPhysicalMonitors(count, phys);
            }
        }

        // ---- "what is keeping the display on?" (powercfg /requests) ----

        public sealed class PowerRequests
        {
            public bool Known;                     // the query ran and has a DISPLAY section
            public string Note;                    // why not, when Known is false
            public readonly List<string> Display = new List<string>();   // apps holding a display request
            public int Total;                      // requests listed in the display section
        }

        static PowerRequests cached;
        static int cachedAt;

        // Cached for a few seconds: it starts a process.
        public static PowerRequests Requests()
        {
            int now = Environment.TickCount;
            if (cached != null && now - cachedAt >= 0 && now - cachedAt < 20000) return cached;
            cached = Query();
            cachedAt = now;
            return cached;
        }

        public static PowerRequests Refresh() { cached = Query(); cachedAt = Environment.TickCount; return cached; }

        // Forces the next Requests() to ask again (e.g. after switching the gsudo option).
        public static void ClearCache() { cached = null; }

        public static bool Elevated()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static PowerRequests Query()
        {
            var r = new PowerRequests();
            if (!Elevated() && UseGsudo)
            {
                string gsudo = GsudoPath();
                if (gsudo == null) { r.Note = "gsudo.exe is not on PATH"; return r; }
                string elevated = Run(gsudo, "powercfg /requests", 20000);
                if (elevated != null) Parse(elevated, r);
                if (r.Known) return r;
                r.Note = elevated == null ? "gsudo did not answer (a UAC prompt?)"
                                          : "gsudo did not get admin rights (UAC declined?)";
                return r;
            }
            string text = Run("powercfg.exe", "/requests", 5000);
            if (text == null) { r.Note = "powercfg did not answer"; return r; }
            Parse(text, r);
            if (!r.Known)
                r.Note = Elevated() ? "powercfg listed no display requests section" : "needs admin to name them (powercfg /requests)";
            return r;
        }

        // "Ask gsudo": name the requests without running IdleVeil as admin. Off unless asked: the
        // first check may raise a UAC prompt.
        public static bool UseGsudo;

        static string GsudoPath()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(';'))
            {
                string d = dir.Trim();
                if (d == "") continue;
                try
                {
                    string exe = Path.Combine(d, "gsudo.exe");
                    if (File.Exists(exe)) return exe;
                }
                catch { }
            }
            // The default install locations, in case PATH doesn't have gsudo (the installers add it).
            string[] usual = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"gsudo\Current\gsudo.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\gsudo\bin\gsudo.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".gsudo\gsudo.exe")
            };
            foreach (string exe in usual) { try { if (File.Exists(exe)) return exe; } catch { } }
            return null;
        }

        // Sections look like "DISPLAY:" / "SYSTEM:" and are not localized to a fixed order we can
        // rely on, so the first section is taken to be display (that's the order powercfg prints).
        static void Parse(string text, PowerRequests r)
        {
            string[] lines = text.Replace("\r", "").Split('\n');
            int section = -1;
            string pending = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();
                bool header = trimmed.Length > 1 && trimmed.EndsWith(":") && !line.StartsWith(" ") && !line.StartsWith("\t")
                              && trimmed.IndexOf('\\') < 0 && trimmed.IndexOf("[") < 0;
                if (header) { section++; if (section == 0) r.Known = true; pending = null; continue; }
                if (section != 0 || trimmed.Length == 0) continue;

                string entry = null;
                int open = trimmed.IndexOf('['), close = trimmed.IndexOf(']');
                if (open >= 0 && close > open)
                {
                    string tag = trimmed.Substring(open + 1, close - open - 1).ToUpperInvariant();
                    string rest = trimmed.Substring(close + 1).Trim();
                    if (tag == "PROCESS" || tag == "SERVICE" || tag == "DRIVER")
                    {
                        if (rest.Length > 0) entry = rest;
                        else { pending = tag; continue; }   // the path is on the next line
                    }
                }
                else if (pending != null) { entry = trimmed; pending = null; }
                if (entry == null) continue;

                r.Total++;
                string name = entry;
                int slash = name.LastIndexOf('\\');
                if (slash >= 0 && slash + 1 < name.Length) name = name.Substring(slash + 1);
                if (name.Length > 0 && !r.Display.Contains(name)) r.Display.Add(name);
            }
        }

        // ---- diagnostics: --power-test ----

        // Lists each screen, whether it answers DDC power control, and what powercfg says about
        // display requests now. Reads only; it never turns anything off.
        public static void Test()
        {
            List<Screen> screens = ActiveScreens();
            Out(screens.Count + " active screen(s)");
            foreach (Screen s in screens)
            {
                string support = "no DDC power control";
                uint count;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(s.Handle, out count))
                    support = "no physical monitor handle (error " + Marshal.GetLastWin32Error() + ")";
                else if (count == 0)
                    support = "no physical monitors";
                else
                {
                    var phys = new PHYSICAL_MONITOR[count];
                    if (!GetPhysicalMonitorsFromHMONITOR(s.Handle, count, phys))
                        support = "could not open the monitor (error " + Marshal.GetLastWin32Error() + ")";
                    else
                    {
                        var parts = new List<string>();
                        foreach (PHYSICAL_MONITOR p in phys)
                        {
                            uint type, current, max;
                            parts.Add(GetVCPFeatureAndVCPFeatureReply(p.hPhysicalMonitor, VCP_POWER, out type, out current, out max)
                                ? "power mode " + current + " (max " + max + ")"
                                : "no answer (error " + Marshal.GetLastWin32Error() + ")");
                        }
                        support = string.Join(", ", parts.ToArray());
                        DestroyPhysicalMonitors(count, phys);
                    }
                }
                Out("  " + s.Name + " (" + s.Id + "): " + support);
            }
            PowerRequests r = Refresh();
            Out("display requests: " + (r.Known ? (r.Display.Count == 0 ? "none" : string.Join(", ", r.Display.ToArray())) : r.Note));
            Out("admin: " + (Elevated() ? "yes" : "no"));
        }

        static void Out(string line)
        {
            try { Console.WriteLine("power-test: " + line); } catch { }
            Program.Log("power-test: " + line);
        }

        static string Run(string exe, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.Default, StandardErrorEncoding = Encoding.Default
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    var sb = new StringBuilder();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    p.WaitForExit();
                    lock (sb) return sb.ToString();
                }
            }
            catch (Exception ex) { Program.Log("standby: powercfg check failed: " + ex.Message); return null; }
        }
    }
}
