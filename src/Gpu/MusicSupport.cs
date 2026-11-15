// Everything the music scenes need from the rest of the app, in one place so the shared files
// (IdleVeil.cs, Savers.cs) only get one-line hooks:
//   MusicConfig  which visualizer and clock "music" mode uses (settings), labels of
//                the new scene ids, the GDI fallback, and the random picks that must not choose
//                audio-reactive scenes while nothing is playing
//   SoundProbe   "is anything playing right now?", asked of the Windows audio meter (no capture)
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace IdleVeil
{
    static class MusicConfig
    {
        // Music mode: what plays while music is on and what shows when it is quiet.
        // Viz is a visualizer id or "random" (a new pick from Pool every time the music starts).
        public static string Viz = "spectrum";
        public static string Clock = "clock";
        public static List<string> Pool;   // null: every visualizer
        // Per-screen mode: a visualizer (or "random") per monitor, by EDID key. Missing: same as Viz.
        public static readonly Dictionary<string, string> VizFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static readonly string[] Visualizers = { "spectrum", "scope", "scopering", "radial", "tunnel", "ripples", "waterfall", "skyline", "burst", "lissajous", "ridges" };
        public static readonly string[] Clocks = { "clock", "clock-led", "clock-neon", "clock-analog", "clock-music" };

        public static string Label(string id)
        {
            switch (id)
            {
                case "scope": return "Oscilloscope";
                case "scopering": return "Oscilloscope ring";
                case "radial": return "Radial";
                case "tunnel": return "Tunnel";
                case "ripples": return "Ripples";
                case "waterfall": return "Waterfall";
                case "skyline": return "Skyline";
                case "burst": return "Beat burst";
                case "lissajous": return "Lissajous";
                case "ridges": return "Ridges";
                case "spectrum": return "Spectrum (music)";
                case "clock": return "Digital clock";
                case "clock-led": return "LED clock";
                case "clock-neon": return "Neon clock";
                case "clock-analog": return "Analog clock";
                case "clock-music": return "Music clock";
                case "music": return "Music (visualizer + clock)";
                case "random": return "Random from pool";
                default: return null;
            }
        }

        static bool IsViz(string id) { return Array.IndexOf(Visualizers, id) >= 0; }

        // Called from Options.LoadSettings with the key as written (musicviz.<key> keeps its case).
        // Unknown ids are ignored.
        public static void Set(string key, string value)
        {
            value = (value ?? "").Trim().ToLowerInvariant();
            string k = key.ToLowerInvariant();
            if (k == "musicviz" && (IsViz(value) || value == "random")) Viz = value;
            else if (k == "musicclock" && Array.IndexOf(Clocks, value) >= 0) Clock = value;
            else if (k == "musicpool")
            {
                var list = new List<string>();
                foreach (string part in value.Split(','))
                {
                    string id = part.Trim();
                    if (IsViz(id) && !list.Contains(id)) list.Add(id);
                }
                Pool = list;
            }
            else if (k.StartsWith("musicviz.") && (IsViz(value) || value == "random")) VizFor[key.Substring(9)] = value;
        }

        public static void SaveExtra(List<string> lines)
        {
            if (Pool != null) lines.Add("musicpool=" + string.Join(",", Pool.ToArray()));
            foreach (var kv in VizFor) lines.Add("musicviz." + kv.Key + "=" + kv.Value);
        }

        public static bool InPool(string id) { return Pool == null ? IsViz(id) : Pool.Contains(id); }

        // The visualizers Random picks from (never empty).
        public static string[] PoolIds()
        {
            var list = new List<string>();
            foreach (string id in Visualizers) if (InPool(id)) list.Add(id);
            if (list.Count == 0) list.AddRange(Visualizers);
            return list.ToArray();
        }

        // What a screen's music mode shows: its own choice in per-screen mode, else Viz.
        public static string ChoiceFor(ScreenMode mode, string screenKey)
        {
            string v;
            if (mode == ScreenMode.PerScreen && !string.IsNullOrEmpty(screenKey) && VizFor.TryGetValue(screenKey, out v)) return v;
            return Viz;
        }

        // One random seed per shown veil (SceneHost sets it), so in each mode every screen's music
        // mode makes the same random pick, and they change together when the music starts again.
        public static uint Session = 1;

        public static string RandomPick(string[] ids, int generation, string salt)
        {
            uint h = Session * 2654435761u + (uint)generation * 40503u;
            foreach (char c in salt ?? "") h = (h ^ c) * 16777619u;
            h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
            return ids[(int)(h % (uint)ids.Length)];
        }

        // GDI cannot draw the new scenes. A clock id, or music mode, falls back to the plain clock.
        public static IAnimation GdiFallback(string id, System.Drawing.Size size)
        {
            if (id == "music" || Array.IndexOf(Clocks, id) >= 0) return new DigitalClock(size);
            return null;
        }

        // Random: the usual animations; the clock stands for all clock styles; the audio-reactive
        // scenes only join in while something is actually playing.
        public static string Pick(Random rng, string[] animations)
        {
            var pool = new System.Collections.Generic.List<string>(animations);
            if (SoundProbe.IsPlaying())
                foreach (string id in GpuScenes.AudioIds)
                    if (id != "clock-music") pool.Add(id);
            string pick = pool[rng.Next(pool.Count)];
            if (pick == "clock") pick = Clocks[rng.Next(Clocks.Length)];
            return pick;
        }

        // A random GPU scene for a monitor with no choice (per-screen mode): never "blank", and no
        // audio-reactive scene unless something is playing.
        public static string RandomGpu(Random rng)
        {
            bool playing = SoundProbe.IsPlaying();
            var pool = new System.Collections.Generic.List<string>();
            foreach (string id in GpuScenes.Ids)
            {
                if (id == "blank") continue;
                if (!playing && GpuScenes.UsesAudio(id)) continue;
                pool.Add(id);
            }
            return pool[rng.Next(pool.Count)];
        }
    }

    // Asks the peak meter of the device music mode listens to (audiodevice=, see AudioDevices.cs)
    // whether anything is making sound. This works without opening a capture stream, so it can
    // run at the moment the veil is about to show. auto: any playback device counts.
    static class SoundProbe
    {
        static int checkedAt;
        static bool last;

        public static bool IsPlaying()
        {
            int now = Environment.TickCount;
            if (checkedAt != 0 && now - checkedAt < 1500) return last;
            last = Measure();
            checkedAt = now == 0 ? 1 : now;
            Program.Log("sound probe: " + (last ? "playing" : "quiet"));
            return last;
        }

        // The same check without the cache or the log line (polled by the settings window).
        public static bool IsPlayingQuiet() { return Measure(); }

        static bool Measure()
        {
            Wasapi.IMMDeviceEnumerator e = null;
            var meters = new List<AudioDevices.IAudioMeterInformation>();
            try
            {
                e = AudioDevices.NewEnumerator();
                string spec = AudioConfig.Effective;
                if (spec == "auto")
                {
                    foreach (AudioDevice d in AudioDevices.List())
                        if (!d.Capture) meters.Add(AudioDevices.Meter(e, d.Id));
                }
                else
                {
                    AudioTarget t = AudioDevices.Resolve(e, spec, null, null);
                    if (t != null) meters.Add(AudioDevices.Meter(e, t.Id));
                }
                float peak = 0f;
                for (int i = 0; i < 8; i++)   // music has gaps: look at 8 moments over ~160 ms
                {
                    foreach (var m in meters) peak = Math.Max(peak, AudioDevices.Peak(m));
                    Thread.Sleep(20);
                }
                return peak > 0.003f;
            }
            catch (Exception ex)
            {
                Program.Log("sound probe failed: " + ex.Message);
                return false;
            }
            finally
            {
                foreach (var m in meters) if (m != null) try { Marshal.ReleaseComObject(m); } catch { }
                if (e != null) try { Marshal.ReleaseComObject(e); } catch { }
            }
        }
    }
}
