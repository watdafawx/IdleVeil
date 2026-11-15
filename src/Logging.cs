// Logging for development and bug reports. See docs/logging.md.
//
// <data dir>\logs\
//   app-YYYY-MM-DD.log           general events (levels Debug/Info/Warn/Error, category, thread)
//   perf-YYYY-MM-DD.log          a sample every 10 s while a veil runs, a summary when it closes,
//                                and a low-rate line every 5 min while idle
//   crash-YYYY-MM-DD-HHMMSS.log  one per crash: exception, context, the last 200 app log lines
//
// Writing never blocks the caller: lines go to a queue and a background thread appends them.
// Hot paths (the render loop) never format strings per frame: FrameStats keeps fixed buffers and
// formats one line per sample period. Old files are removed at start and once a day.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace IdleVeil
{
    enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

    static class AppLog
    {
        public static LogLevel Min = LogLevel.Info;   // debug=1 in the ini: Debug
        public static bool ToConsole;                  // --log-console
        public static int Days = 7;                    // logdays= in the ini
        public static int PerfSeconds = 10;            // --perf-every
        public const int CrashDays = 30;
        public const long MaxFileBytes = 20L * 1024 * 1024, MaxFolderBytes = 200L * 1024 * 1024;
        public static readonly DateTime Started = DateTime.Now;

        static string dir;
        public static string Dir
        {
            get
            {
                if (dir == null)
                {
                    dir = Path.Combine(AppPaths.Dir, "logs");
                    try { Directory.CreateDirectory(dir); } catch { }
                }
                return dir;
            }
        }

        struct Entry { public bool Perf; public string Text; }

        static readonly object gate = new object();
        static readonly Queue<Entry> queue = new Queue<Entry>();
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static readonly ManualResetEvent drained = new ManualResetEvent(true);
        static Thread writer;

        // The last lines of the app log, for crash reports and "Copy diagnostics".
        const int RingSize = 200;
        static readonly string[] ring = new string[RingSize];
        static int ringNext, ringCount;

        public static void Debug(string cat, string msg) { Write(LogLevel.Debug, cat, msg); }
        public static void Info(string cat, string msg) { Write(LogLevel.Info, cat, msg); }
        public static void Warn(string cat, string msg) { Write(LogLevel.Warn, cat, msg); }
        public static void Error(string cat, string msg) { Write(LogLevel.Error, cat, msg); }
        public static bool DebugOn { get { return Min <= LogLevel.Debug; } }

        static readonly string[] LevelNames = { "DEBUG", "INFO ", "WARN ", "ERROR" };

        public static void Write(LogLevel level, string cat, string msg)
        {
            if (level < Min) return;
            string line = Stamp() + " " + LevelNames[(int)level] + " [" + ThreadName() + "] " + cat + ": " + msg;
            Enqueue(false, line);
        }

        // A perf sample (always written, whatever the level).
        public static void Perf(string msg)
        {
            Enqueue(true, Stamp() + " " + msg);
        }

        static string Stamp() { return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"); }

        static string ThreadName()
        {
            Thread t = Thread.CurrentThread;
            return t.Name ?? ("t" + t.ManagedThreadId);
        }

        static void Enqueue(bool perf, string text)
        {
            lock (gate)
            {
                if (!perf)
                {
                    ring[ringNext] = text;
                    ringNext = (ringNext + 1) % RingSize;
                    if (ringCount < RingSize) ringCount++;
                }
                queue.Enqueue(new Entry { Perf = perf, Text = text });
                drained.Reset();
                if (writer == null)
                {
                    writer = new Thread(WriterLoop) { IsBackground = true, Name = "IdleVeil log" };
                    writer.Start();
                }
            }
            wake.Set();
        }

        // The newest app log lines, oldest first.
        public static string[] Recent(int max)
        {
            lock (gate)
            {
                int n = Math.Min(max, ringCount);
                var r = new string[n];
                for (int i = 0; i < n; i++) r[i] = ring[(ringNext - n + i + RingSize * 2) % RingSize];
                return r;
            }
        }

        // Waits (up to ms) until everything queued so far is on disk. For exit and crashes.
        public static void Flush(int ms)
        {
            if (writer == null) return;
            wake.Set();
            drained.WaitOne(ms);
        }

        // ---- writer thread ----

        sealed class Channel
        {
            public string Prefix;        // "app" or "perf"
            public string Day, Path;
            public StreamWriter W;
            public int Part;
        }

        static readonly Channel app = new Channel { Prefix = "app" }, perf = new Channel { Prefix = "perf" };
        static DateTime nextCleanup = DateTime.MinValue;

        static void WriterLoop()
        {
            var batch = new List<Entry>();
            while (true)
            {
                wake.WaitOne(1000);
                try
                {
                    if (DateTime.Now >= nextCleanup)
                    {
                        nextCleanup = DateTime.Now.AddHours(24);
                        Cleanup();
                    }
                    while (true)
                    {
                        batch.Clear();
                        lock (gate)
                        {
                            while (queue.Count > 0) batch.Add(queue.Dequeue());
                            if (batch.Count == 0) { drained.Set(); break; }
                        }
                        foreach (Entry e in batch)
                        {
                            Channel c = e.Perf ? perf : app;
                            StreamWriter w = Open(c);
                            if (w != null) w.WriteLine(e.Text);
                            if (ToConsole) try { Console.WriteLine(e.Perf ? "perf " + e.Text : e.Text); } catch { }
                        }
                        Flush(app);
                        Flush(perf);
                    }
                }
                catch { try { drained.Set(); } catch { } }
            }
        }

        static void Flush(Channel c)
        {
            if (c.W == null) return;
            try
            {
                c.W.Flush();
                if (c.W.BaseStream.Length > MaxFileBytes) { c.W.Dispose(); c.W = null; c.Part++; }   // next line opens .1, .2, ...
            }
            catch { }
        }

        static StreamWriter Open(Channel c)
        {
            string day = DateTime.Now.ToString("yyyy-MM-dd");
            if (c.W != null && c.Day == day) return c.W;
            if (c.W != null) { try { c.W.Dispose(); } catch { } c.W = null; }
            if (c.Day != day) { c.Day = day; c.Part = 0; }
            for (int tries = 0; tries < 50; tries++)
            {
                string name = c.Prefix + "-" + day + (c.Part > 0 ? "." + c.Part : "") + ".log";
                c.Path = Path.Combine(Dir, name);
                try
                {
                    if (File.Exists(c.Path) && new FileInfo(c.Path).Length > MaxFileBytes) { c.Part++; continue; }
                    var fs = new FileStream(c.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    c.W = new StreamWriter(fs, new UTF8Encoding(false));
                    return c.W;
                }
                catch (IOException) { c.Part++; }   // locked by another copy of the app: use the next part
                catch { return null; }
            }
            return null;
        }

        // ---- retention ----

        // Logs older than Days (crash reports: 30 days), then the oldest files until the folder is
        // under 200 MB. The single IdleVeil.log of older versions is moved in first, so it ages out too.
        public static void Cleanup()
        {
            try
            {
                MigrateLegacy();
                var files = new List<FileInfo>(new DirectoryInfo(Dir).GetFiles("*.log"));
                DateTime now = DateTime.Now;
                long total = 0;
                int removed = 0;
                foreach (FileInfo f in files.ToArray())
                {
                    int keep = f.Name.StartsWith("crash-") ? CrashDays : Math.Max(1, Days);
                    if ((now - f.LastWriteTime).TotalDays > keep && !IsOpen(f.FullName))
                    {
                        try { f.Delete(); files.Remove(f); removed++; } catch { }
                    }
                }
                foreach (FileInfo f in files) total += f.Length;
                files.Sort(delegate(FileInfo a, FileInfo b) { return a.LastWriteTime.CompareTo(b.LastWriteTime); });
                foreach (FileInfo f in files)
                {
                    if (total <= MaxFolderBytes) break;
                    if (IsOpen(f.FullName)) continue;
                    try { long len = f.Length; f.Delete(); total -= len; removed++; } catch { }
                }
                if (removed > 0) Info("log", "cleanup removed " + removed + " old log files (keep " + Days + " days, crash reports " + CrashDays + ")");
            }
            catch { }
        }

        static bool IsOpen(string path)
        {
            return string.Equals(path, app.Path, StringComparison.OrdinalIgnoreCase) || string.Equals(path, perf.Path, StringComparison.OrdinalIgnoreCase);
        }

        static void MigrateLegacy()
        {
            string old = Path.Combine(AppPaths.Dir, "IdleVeil.log");
            if (!File.Exists(old)) return;
            try
            {
                string to = Path.Combine(Dir, "app-legacy-" + File.GetLastWriteTime(old).ToString("yyyy-MM-dd") + ".log");
                if (File.Exists(to)) File.Delete(to);
                File.Move(old, to);
                Info("log", "moved the old IdleVeil.log to " + Path.GetFileName(to));
            }
            catch (Exception ex) { Warn("log", "could not move the old IdleVeil.log: " + ex.Message); }
        }

        // ---- crashes ----

        // What the app is doing, for crash reports (set by the controller).
        public static Func<string> Context;

        static int crashes;
        const int MaxCrashFiles = 5;   // per run: a fault that repeats every frame must not fill the disk

        // Writes crash-YYYY-MM-DD-HHMMSS.log right away (not through the queue) and returns its path.
        public static string Crash(string where, Exception ex)
        {
            string path = null;
            try
            {
                Error("crash", where + ": " + (ex != null ? ex.GetType().Name + ": " + ex.Message : "?"));
                if (Interlocked.Increment(ref crashes) > MaxCrashFiles)
                {
                    Error("crash", "more than " + MaxCrashFiles + " crashes this run, no report written: " + ex);
                    Flush(1500);
                    return null;
                }
                var sb = new StringBuilder();
                sb.AppendLine("IdleVeil crash report");
                sb.AppendLine("where:    " + where);
                sb.AppendLine("time:     " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.AppendLine("thread:   " + ThreadName());
                sb.AppendLine("version:  " + AppInfo.Version + (Updater.IsDevBuild() ? " (dev build)" : ""));
                sb.AppendLine("uptime:   " + Uptime());
                try { if (Context != null) sb.AppendLine("state:    " + Context()); } catch { }
                sb.AppendLine();
                sb.AppendLine("---- exception ----");
                for (Exception e = ex; e != null; e = e.InnerException)
                {
                    if (e != ex) sb.AppendLine("---- inner ----");
                    sb.AppendLine(e.GetType().FullName + ": " + e.Message);
                    sb.AppendLine(e.StackTrace ?? "(no stack)");
                }
                sb.AppendLine();
                sb.AppendLine("---- system ----");
                foreach (string l in SystemInfo.Lines()) sb.AppendLine(l);
                sb.AppendLine();
                sb.AppendLine("---- last app log lines ----");
                foreach (string l in Recent(RingSize)) sb.AppendLine(l);
                path = Path.Combine(Dir, "crash-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".log");
                for (int i = 2; File.Exists(path); i++) path = Path.Combine(Dir, "crash-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + "-" + i + ".log");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                Error("crash", "crash report: " + path);
            }
            catch { }
            Flush(1500);
            return path;
        }

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        // --log-console: mirror the log to the terminal this was started from (or to a redirected stdout).
        public static void OpenConsole()
        {
            try { if (!Console.IsOutputRedirected) AttachConsole(-1); } catch { }
        }

        // Version, system and the last log lines, for "Copy diagnostics" in the settings window.
        public static string Diagnostics()
        {
            var sb = new StringBuilder();
            sb.AppendLine("IdleVeil diagnostics, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ", uptime " + Uptime());
            foreach (string l in SystemInfo.Lines()) sb.AppendLine(l);
            sb.AppendLine("logs: " + Dir + " (level " + Min.ToString().ToLowerInvariant() + ", keep " + Days + " days)");
            try { if (Context != null) sb.AppendLine("state: " + Context()); } catch { }
            sb.AppendLine();
            sb.AppendLine("last log lines:");
            foreach (string l in Recent(RingSize)) sb.AppendLine(l);
            return sb.ToString();
        }

        public static string Uptime()
        {
            TimeSpan t = DateTime.Now - Started;
            return (int)t.TotalHours + "h " + t.Minutes + "m " + t.Seconds + "s";
        }

        // ---- the old single-call API, by category ----

        // Program.Log(msg): picks a category from the message, and Warn for failures.
        public static void Legacy(string msg)
        {
            string m = msg ?? "";
            string cat = Category(m);
            bool bad = m.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                       || m.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("rejected", StringComparison.OrdinalIgnoreCase) >= 0
                       || m.IndexOf("crashed", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("lost", StringComparison.OrdinalIgnoreCase) >= 0;
            LogLevel level = bad ? LogLevel.Warn : m.StartsWith("debug:") ? LogLevel.Debug : LogLevel.Info;
            if (m.StartsWith("debug: ")) m = m.Substring(7);
            if (m.StartsWith(cat + ": ")) m = m.Substring(cat.Length + 2);   // "audio: ..." is already the category
            Write(level, cat, m);
        }

        static string Category(string m)
        {
            string l = m.ToLowerInvariant();
            if (l.StartsWith("audio") || l.StartsWith("sound probe")) return "audio";
            if (l.StartsWith("gpu") || l.Contains("direct3d") || l.Contains("overlay")) return "gpu";
            if (l.StartsWith("veil") || l.StartsWith("turn-off") || l.StartsWith("stats")) return "veil";
            if (l.StartsWith("settings") || l.StartsWith("autostart") || l.StartsWith("open ")) return "settings";
            if (l.StartsWith("detach") || l.StartsWith("reattach") || l.StartsWith("detached") || l.StartsWith("reattached")
                || l.StartsWith("restored") || l.StartsWith("recovered") || l.StartsWith("screen")) return "display";
            if (l.StartsWith("update") || l.StartsWith("installing update")) return "update";
            if (l.StartsWith("controller") || l.StartsWith("raw input")) return "input";
            if (l.StartsWith("thumb") || l.StartsWith("preview")) return "thumbs";
            if (l.StartsWith("render-test") || l.StartsWith("render test")) return "test";
            return "app";
        }
    }

    // CPU, memory and handle counts of this process. Each instance has its own CPU baseline.
    sealed class ProcessStats
    {
        [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);

        readonly Process proc = Process.GetCurrentProcess();
        TimeSpan cpuAt;
        long wallAt;

        public ProcessStats() { Reset(); }

        public void Reset()
        {
            proc.Refresh();
            cpuAt = proc.TotalProcessorTime;
            wallAt = Stopwatch.GetTimestamp();
        }

        // "cpu=0.3% ws=95MB priv=80MB handles=612 gdi=40 user=25 threads=31" (CPU since the last call).
        public string Sample()
        {
            proc.Refresh();
            TimeSpan cpu = proc.TotalProcessorTime;
            long now = Stopwatch.GetTimestamp();
            double secs = Math.Max(1e-3, (now - wallAt) / (double)Stopwatch.Frequency);
            double pct = (cpu - cpuAt).TotalMilliseconds / (secs * 1000.0) * 100.0 / Environment.ProcessorCount;
            cpuAt = cpu; wallAt = now;
            IntPtr h = proc.Handle;
            return "cpu=" + pct.ToString("0.0") + "% ws=" + (proc.WorkingSet64 >> 20) + "MB priv=" + (proc.PrivateMemorySize64 >> 20)
                   + "MB handles=" + proc.HandleCount + " gdi=" + GetGuiResources(h, 0) + " user=" + GetGuiResources(h, 1)
                   + " threads=" + proc.Threads.Count;
        }
    }

    // Frame timing of one veil, kept in fixed buffers (no per-frame allocation or formatting).
    // Frame() on every frame; Due()/Report() every AppLog.PerfSeconds; Summary() when the veil closes.
    sealed class FrameStats
    {
        const int Cap = 16384;
        readonly float[] gaps = new float[Cap], works = new float[Cap], scratch = new float[Cap];
        int n;
        readonly double periodMs;
        readonly long freq = Stopwatch.Frequency;
        long windowStart, secStart, sessionStart;
        int secFrames, late, dropped;
        float minFps = float.MaxValue, maxGap;
        // whole session
        long totalFrames;
        int totalLate, totalDropped;
        float worstP99, worstMinFps = float.MaxValue;
        double workTotal;
        int audioGlitchesAt;
        readonly ProcessStats proc = new ProcessStats();
        readonly ProcessStats sessionProc = new ProcessStats();
        readonly string label;

        // label: what is shown, e.g. gpu="RTX 5070 Ti" screens=3 span fire,fire,fire
        public FrameStats(int fpsCap, string label)
        {
            periodMs = 1000.0 / Math.Max(1, fpsCap);
            this.label = label;
            windowStart = secStart = sessionStart = Stopwatch.GetTimestamp();
            audioGlitchesAt = AudioFeed.Glitches;
        }

        // gapMs: time since the previous frame started; workMs: CPU time spent on this frame.
        public void Frame(double gapMs, double workMs, long now)
        {
            if (n < Cap) { gaps[n] = (float)gapMs; works[n] = (float)workMs; n++; }
            if (gapMs > maxGap) maxGap = (float)gapMs;
            if (gapMs > periodMs * 1.5)
            {
                late++;
                dropped += (int)Math.Round(gapMs / periodMs) - 1;
            }
            workTotal += workMs;
            totalFrames++;
            secFrames++;
            long secTicks = now - secStart;
            if (secTicks >= freq)
            {
                float fps = (float)(secFrames * (double)freq / secTicks);
                if (fps < minFps) minFps = fps;
                secFrames = 0;
                secStart = now;
            }
        }

        public bool Due(long now) { return now - windowStart >= (long)AppLog.PerfSeconds * freq; }

        float Pct(float[] src, double p)
        {
            Array.Copy(src, scratch, n);
            Array.Sort(scratch, 0, n);
            return scratch[Math.Min(n - 1, (int)(p * (n - 1) + 0.5))];
        }

        // One perf line for the last window, then a fresh window.
        public string Report(long now)
        {
            double secs = (now - windowStart) / (double)freq;
            string s;
            if (n == 0) s = "veil fps=0 (no frames in " + secs.ToString("0.0") + " s)";
            else
            {
                float p50 = Pct(gaps, 0.50), p95 = Pct(gaps, 0.95), p99 = Pct(gaps, 0.99);
                float w50 = Pct(works, 0.50), w99 = Pct(works, 0.99);
                float min = minFps == float.MaxValue ? (float)(n / secs) : minFps;
                s = "veil fps=" + (n / secs).ToString("0.0") + " min=" + min.ToString("0.0")
                    + " frame ms p50=" + p50.ToString("0.0") + " p95=" + p95.ToString("0.0") + " p99=" + p99.ToString("0.0") + " max=" + maxGap.ToString("0.0")
                    + " work ms p50=" + w50.ToString("0.00") + " p99=" + w99.ToString("0.00")
                    + " late=" + late + " dropped=" + dropped;
                if (p99 > worstP99) worstP99 = p99;
                if (min < worstMinFps) worstMinFps = min;
            }
            int glitches = AudioFeed.Glitches;
            s += " " + proc.Sample() + " audio glitches=" + (glitches - audioGlitchesAt) + " " + label;
            audioGlitchesAt = glitches;
            totalLate += late; totalDropped += dropped;
            n = 0; late = 0; dropped = 0; maxGap = 0; minFps = float.MaxValue;
            windowStart = now;
            return s;
        }

        public string Summary(long now)
        {
            if (n > 0) Report(now);   // fold the unfinished window into the totals
            double secs = Math.Max(1e-3, (now - sessionStart) / (double)freq);
            return "veil closed after " + secs.ToString("0") + " s: frames=" + totalFrames + " fps=" + (totalFrames / secs).ToString("0.0")
                   + " worst min fps=" + (worstMinFps == float.MaxValue ? "-" : worstMinFps.ToString("0.0"))
                   + " worst p99 ms=" + worstP99.ToString("0.0") + " work ms avg=" + (totalFrames > 0 ? workTotal / totalFrames : 0).ToString("0.00")
                   + " late=" + totalLate + " dropped=" + totalDropped + " " + sessionProc.Sample() + " " + label;
        }
    }

    // System facts for the startup log, crash reports and "Copy diagnostics".
    static class SystemInfo
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint Length, Load;
            public ulong Total, Avail, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

        public static string Os()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string name = (string)k.GetValue("ProductName", "Windows");
                    string build = (string)k.GetValue("CurrentBuild", "");
                    int b;
                    // Windows 11 still says "Windows 10" in ProductName.
                    if (int.TryParse(build, out b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
                    return name + " " + k.GetValue("DisplayVersion", "") + " (build " + build + "." + k.GetValue("UBR", 0) + ")";
                }
            }
            catch { return Environment.OSVersion.ToString(); }
        }

        public static string DotNet()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                    return "CLR " + Environment.Version + ", .NET Framework release " + k.GetValue("Release", "?");
            }
            catch { return "CLR " + Environment.Version; }
        }

        public static string Gpus()
        {
            var names = new List<string>();
            try
            {
                var dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(dd);
                for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
                {
                    if (!string.IsNullOrEmpty(dd.DeviceString) && !names.Contains(dd.DeviceString)) names.Add(dd.DeviceString);
                    dd.cb = Marshal.SizeOf(dd);
                }
            }
            catch { }
            return names.Count == 0 ? "?" : string.Join("; ", names.ToArray());
        }

        public static string Memory()
        {
            var m = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            return GlobalMemoryStatusEx(ref m) ? (m.Total >> 30) + " GB RAM (" + m.Load + "% in use)" : "?";
        }

        public static IEnumerable<string> Monitors()
        {
            var list = new List<string>();
            try
            {
                foreach (GpuScreen s in GpuScreen.Current())
                {
                    Rectangle b = s.Bounds;
                    uint dx = 96, dy = 96;
                    try
                    {
                        IntPtr mon = MonitorFromPoint(new POINT { X = b.X + b.Width / 2, Y = b.Y + b.Height / 2 }, 2);
                        GetDpiForMonitor(mon, 0, out dx, out dy);
                    }
                    catch { }
                    list.Add(s.Name + " " + b.Width + "x" + b.Height + " at (" + b.X + "," + b.Y + ") dpi=" + dx + " (" + (dx * 100 / 96) + "%)"
                             + " edid=" + (s.Id == "" ? "?" : s.Id) + " " + s.Device + (s.Primary ? " primary" : "") + (s.IsOled ? " oled" : ""));
                }
            }
            catch (Exception ex) { list.Add("monitor list failed: " + ex.Message); }
            return list;
        }

        public static string Audio()
        {
            try
            {
                string spec = AudioConfig.Effective;
                string def = null, chosen = null;
                foreach (AudioDevice d in AudioDevices.List())
                {
                    if (d.IsDefault && !d.Capture) def = d.Name;
                    if (string.Equals(d.Id, spec, StringComparison.OrdinalIgnoreCase)) chosen = d.Name;
                }
                return "audiodevice=" + spec + (chosen != null ? " (" + chosen + ")" : "") + ", default output " + (def ?? "none");
            }
            catch (Exception ex) { return "audio: " + ex.Message; }
        }

        public static List<string> Lines()
        {
            var l = new List<string>();
            l.Add("app: IdleVeil " + AppInfo.Version + (Updater.IsDevBuild() ? " (dev build)" : "") + ", " + (Environment.Is64BitProcess ? "64" : "32") + "-bit, "
                  + Application.ExecutablePath);
            l.Add("data: " + AppPaths.Dir);
            l.Add("os: " + Os());
            l.Add(".net: " + DotNet());
            l.Add("cpu: " + Environment.ProcessorCount + " logical cores, " + Memory());
            l.Add("gpu: " + Gpus());
            foreach (string m in Monitors()) l.Add("monitor: " + m);
            l.Add("audio: " + Audio());
            return l;
        }

        static class Application
        {
            public static string ExecutablePath { get { return System.Windows.Forms.Application.ExecutablePath; } }
        }
    }
}
