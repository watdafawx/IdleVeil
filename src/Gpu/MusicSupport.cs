// Everything the music scenes need from the rest of the app, in one place so the shared files
// (IdleVeil.cs, Savers.cs) only get one-line hooks:
//   MusicConfig  which visualizer and clock "music" mode uses (settings), labels of
//                the new scene ids, the GDI fallback, and the random picks that must not choose
//                audio-reactive scenes while nothing is playing
//   SoundProbe   "is anything playing right now?", asked of the Windows audio meter (no capture)
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace IdleVeil
{
    static class MusicConfig
    {
        // Music mode: what plays while music is on and what shows when it is quiet.
        public static string Viz = "spectrum";
        public static string Clock = "clock";

        public static readonly string[] Visualizers = { "spectrum", "scope", "scopering", "radial", "tunnel", "ripples" };
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
                case "spectrum": return "Spectrum (music)";
                case "clock": return "Digital clock";
                case "clock-led": return "LED clock";
                case "clock-neon": return "Neon clock";
                case "clock-analog": return "Analog clock";
                case "clock-music": return "Music clock";
                case "music": return "Music (visualizer + clock)";
                default: return null;
            }
        }

        // Called from Options.LoadSettings. Unknown ids are ignored.
        public static void Set(string key, string value)
        {
            value = (value ?? "").Trim().ToLowerInvariant();
            if (key == "musicviz" && Array.IndexOf(Visualizers, value) >= 0) Viz = value;
            else if (key == "musicclock" && Array.IndexOf(Clocks, value) >= 0) Clock = value;
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

    // Asks the default output device's peak meter whether anything is making sound. This works
    // without opening a capture stream, so it can run at the moment the veil is about to show.
    static class SoundProbe
    {
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
        interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
            [PreserveSig] int GetMeteringChannelCount(out uint count);
            [PreserveSig] int GetChannelsPeakValues(uint count, IntPtr peaks);
            [PreserveSig] int QueryHardwareSupport(out uint mask);
        }

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
            object enumObj = null, meterObj = null;
            Wasapi.IMMDevice dev = null;
            try
            {
                enumObj = new Wasapi.MMDeviceEnumeratorComObject();
                var e = (Wasapi.IMMDeviceEnumerator)enumObj;
                if (e.GetDefaultAudioEndpoint(Wasapi.eRender, Wasapi.eMultimedia, out dev) != 0 || dev == null) return false;
                Guid iid = typeof(IAudioMeterInformation).GUID;
                if (dev.Activate(ref iid, Wasapi.CLSCTX_ALL, IntPtr.Zero, out meterObj) != 0 || meterObj == null) return false;
                var meter = (IAudioMeterInformation)meterObj;
                float peak = 0f;
                for (int i = 0; i < 8; i++)   // music has gaps: look at 8 moments over ~160 ms
                {
                    float p;
                    if (meter.GetPeakValue(out p) == 0 && p > peak) peak = p;
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
                if (meterObj != null) try { Marshal.ReleaseComObject(meterObj); } catch { }
                if (dev != null) try { Marshal.ReleaseComObject(dev); } catch { }
                if (enumObj != null) try { Marshal.ReleaseComObject(enumObj); } catch { }
            }
        }
    }
}
