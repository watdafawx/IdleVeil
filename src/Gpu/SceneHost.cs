// Screen modes: decides which scene instances exist and what part of which world each screen
// shows, then runs them. Shared by the live overlay (GpuVeil) and --render-test.
//
//   span       one instance; the world is the virtual desktop; each screen shows its own rectangle
//              of it (a scene with SceneLayout.PerScreen falls back to each)
//   each       one instance per screen; the world is that screen (origin 0,0)
//   mirror     one instance; the world is the largest screen; every screen shows all of it, fitted
//              with the aspect kept (black bars)
//   perscreen  like each, but the scene is chosen per screen (scene.<EDID id>= in the ini)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IdleVeil
{
    enum ScreenMode { Span, Each, Mirror, PerScreen, Groups }

    static class ScreenModes
    {
        public static readonly string[] Names = { "span", "each", "mirror", "perscreen", "groups" };

        public static ScreenMode Parse(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "span": return ScreenMode.Span;
                case "mirror": return ScreenMode.Mirror;
                case "perscreen": case "per-screen": return ScreenMode.PerScreen;
                case "groups": return ScreenMode.Groups;
                default: return ScreenMode.Each;
            }
        }

        public static string Name(ScreenMode m) { return Names[(int)m]; }

        public static string Label(ScreenMode m)
        {
            switch (m)
            {
                case ScreenMode.Span: return "Span (one scene across all screens)";
                case ScreenMode.Mirror: return "Mirror (same picture everywhere)";
                case ScreenMode.PerScreen: return "Per screen (choose below)";
                case ScreenMode.Groups: return "Groups (span some screens, leave others out)";
                default: return "Each (a copy per screen)";
            }
        }
    }

    // A monitor in desktop coordinates, with the EDID id used for per-screen settings.
    sealed class GpuScreen
    {
        public Rectangle Bounds;
        public string Device;   // \\.\DISPLAY1
        public string Id;       // EDID id like AUS2740 ("" if unknown)
        public string Name;     // friendly name like PG27AQDP
        public bool Primary;
        public Rectangle Layout;   // where the screen sits in a span group's world: Bounds, unless the user moved it (place.<id>=x,y)

        public string Key { get { return Id != "" ? Id : Device.TrimStart('\\', '.'); } }

        // OLED panels known by name (EDID friendly names), plus anything that says OLED. Used to keep
        // the span clock off burn-in sensitive screens. A guess: the user can pick the screen.
        static readonly string[] OledModels =
        {
            "PG27AQDP", "PG27AQDM", "PG32UCDM", "PG32UCDP", "PG34WCDM", "PG27UCDM", "PG39WCDM", "PG42UQ", "PG48UQ", "XG27AQDMG", "XG27ACDNG", "XG32UCWMG",
            "AW3423", "AW2725DF", "AW3225QF", "AW2725Q", "AW3425DW", "27GR95QE", "27GS95QE", "32GS95UE", "45GR95QE", "39GS95QE", "45GS95QE",
            "MPG 321URX", "MPG 271QRX", "MAG 271QPX", "MPG 491CQP", "MAG 321UP", "G60SD", "G80SD", "G85SB", "G93SC", "G95SC", "Odyssey OLED",
            "FO27Q", "FO32U", "FO48U", "MO27Q", "Q27G4ZD", "AG276QZD", "AG326UD", "PD32M", "PD3225U", "OLED",
        };

        public bool IsOled
        {
            get
            {
                string n = (Name ?? "").ToUpperInvariant();
                foreach (string m in OledModels) if (n.Contains(m.ToUpperInvariant())) return true;
                return false;
            }
        }

        public static GpuScreen[] Current()
        {
            Dictionary<string, MonitorInfo> ids = DisplayDetach.ByGdiName();
            Screen[] all = Screen.AllScreens;
            var list = new GpuScreen[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                MonitorInfo info;
                ids.TryGetValue(all[i].DeviceName, out info);
                list[i] = new GpuScreen
                {
                    Bounds = all[i].Bounds, Layout = all[i].Bounds, Device = all[i].DeviceName, Primary = all[i].Primary,
                    Id = info != null ? info.Id : "", Name = info != null ? info.Name : all[i].DeviceName,
                };
            }
            return list;
        }
    }

    // Span-mode settings (ini: clockscreen=<EDID key>; empty = automatic).
    static class SpanConfig
    {
        public static string ClockScreen = "";

        public static void Set(string key, string value)
        {
            if (key == "clockscreen") ClockScreen = (value ?? "").Trim();
        }

        // The screen a span clock sits on: the chosen one, else the largest screen that is not an
        // OLED (top-most, then left-most, on a tie), else the largest one.
        public static int PickClockScreen(GpuScreen[] screens)
        {
            for (int i = 0; i < screens.Length; i++)
                if (ClockScreen != "" && string.Equals(screens[i].Key, ClockScreen, StringComparison.OrdinalIgnoreCase)) return i;
            return AutoClockScreen(screens);
        }

        public static int AutoClockScreen(GpuScreen[] screens)
        {
            int best = -1;
            for (int pass = 0; pass < 2 && best < 0; pass++)
                for (int i = 0; i < screens.Length; i++)
                {
                    if (pass == 0 && screens[i].IsOled) continue;
                    if (best < 0 || Better(screens[i].Bounds, screens[best].Bounds)) best = i;
                }
            return Math.Max(0, best);
        }

        static bool Better(Rectangle a, Rectangle b)
        {
            long aa = (long)a.Width * a.Height, ab = (long)b.Width * b.Height;
            if (aa != ab) return aa > ab;
            if (a.Y != b.Y) return a.Y < b.Y;
            return a.X < b.X;
        }
    }

    // One group of screens in Groups mode: its own screen mode and the screens (indexes into the
    // host's screen list) that belong to it. The scene comes from sceneIds of its members.
    sealed class GroupPlan
    {
        public ScreenMode Mode;   // Span, Each or Mirror
        public int[] Members;
    }

    sealed class SceneHost : IDisposable
    {
        sealed class Instance
        {
            public string Id;
            public IGpuScene Scene;
            public readonly FrameConstants Frame = new FrameConstants();
            public readonly FrameContext Context = new FrameContext();
            public float Time;
            public float BurnPhase;
        }

        sealed class Target
        {
            public Instance Inst;
            public RenderView View;
        }

        public const float FadeSeconds = 1.2f;

        readonly Gpu gpu;
        readonly List<Instance> instances = new List<Instance>();
        readonly Target[] targets;
        readonly AudioFrame audio = new AudioFrame();
        readonly IAudioSource audioSource;
        readonly FullscreenPass fade;
        readonly Random seeds = new Random();
        public readonly ScreenMode Mode;   // the mode actually used (span may have fallen back to each)
        float clock;

        public float Time { get { return clock; } }

        readonly float minWorldU;

        // screens: real monitors; sceneIds: the scene for each (all equal except in perscreen mode).
        // minWorldU: thumbnails (settings window) draw World-layout scenes (stars, snow, fireflies) at
        // least this big, so a tiny preview shows a close-up instead of single pixels.
        public SceneHost(Gpu gpu, GpuScreen[] screens, ScreenMode mode, string[] sceneIds, IAudioSource audioSource, float minWorldU = 0f, GroupPlan[] groups = null)
        {
            this.gpu = gpu;
            this.minWorldU = minWorldU;
            this.audioSource = audioSource;
            MusicConfig.Session = (uint)seeds.Next(1, int.MaxValue);
            targets = new Target[screens.Length];
            fade = new FullscreenPass(gpu, "float4 PS(float4 pos : SV_Position) : SV_Target { return float4(0, 0, 0, 1.0 - Fade); }");

            if (mode == ScreenMode.Groups && groups != null)
            {
                var done = new bool[screens.Length];
                foreach (GroupPlan g in groups)
                {
                    if (g.Members == null || g.Members.Length == 0) continue;
                    var sub = new GpuScreen[g.Members.Length];
                    var ids = new string[g.Members.Length];
                    for (int k = 0; k < sub.Length; k++) { sub[k] = screens[g.Members[k]]; ids[k] = sceneIds[g.Members[k]]; done[g.Members[k]] = true; }
                    Build(sub, g.Members, g.Mode == ScreenMode.Groups || g.Mode == ScreenMode.PerScreen ? ScreenMode.Each : g.Mode, ids);
                }
                for (int i = 0; i < screens.Length; i++)   // a screen no group claimed: black
                    if (!done[i]) Build(new[] { screens[i] }, new[] { i }, ScreenMode.Each, new[] { "blank" });
                Mode = ScreenMode.Groups;
            }
            else
            {
                var map = new int[screens.Length];
                for (int i = 0; i < map.Length; i++) map[i] = i;
                Mode = Build(screens, map, mode == ScreenMode.Groups ? ScreenMode.Each : mode, sceneIds);
            }
        }

        // Sets up the scene instances and render views for `screens`, a subset of the host's screens
        // (map[k] is the index of screens[k] in the full list). Returns the mode actually used: span
        // falls back to each for scenes that can't span.
        ScreenMode Build(GpuScreen[] screens, int[] map, ScreenMode mode, string[] sceneIds)
        {
            if (mode != ScreenMode.PerScreen)
                for (int i = 1; i < sceneIds.Length; i++) sceneIds[i] = sceneIds[0];
            if (mode == ScreenMode.Span)
            {
                IGpuScene probe = GpuScenes.Create(sceneIds[0]);
                if (probe == null || probe.Layout == SceneLayout.PerScreen) mode = ScreenMode.Each;
                if (probe != null) probe.Dispose();
            }

            if (mode == ScreenMode.Span)
            {
                // The world is the screens' Layout rectangles: their desktop position, or where the user placed them.
                Rectangle u = screens[0].Layout;
                float maxH = 0;
                var rects = new RectF[screens.Length];
                for (int i = 0; i < screens.Length; i++)
                {
                    u = Rectangle.Union(u, screens[i].Layout);
                    maxH = Math.Max(maxH, screens[i].Bounds.Height);
                    rects[i] = ToRect(screens[i].Layout);
                }
                var keys = new string[screens.Length];
                for (int i = 0; i < screens.Length; i++) keys[i] = screens[i].Key;
                Instance inst = Make(sceneIds[0], ToRect(u), rects, maxH / 1080f, mode, keys, SpanConfig.PickClockScreen(screens));
                for (int i = 0; i < screens.Length; i++)
                    targets[map[i]] = new Target { Inst = inst, View = new RenderView { View = rects[i], Viewport = Pixels(screens[i].Bounds.Size), Index = i } };
            }
            else if (mode == ScreenMode.Mirror)
            {
                GpuScreen refScreen = screens[0];
                foreach (GpuScreen s in screens)
                {
                    long a = (long)s.Bounds.Width * s.Bounds.Height, b = (long)refScreen.Bounds.Width * refScreen.Bounds.Height;
                    if (a > b || (a == b && s.Primary)) refScreen = s;
                }
                var world = new RectF(0, 0, refScreen.Bounds.Width, refScreen.Bounds.Height);
                Instance inst = Make(sceneIds[0], world, new[] { world }, world.H / 1080f, mode, new[] { refScreen.Key }, 0);
                for (int i = 0; i < screens.Length; i++)
                {
                    Size sz = screens[i].Bounds.Size;
                    float k = Math.Min(sz.Width / world.W, sz.Height / world.H);
                    float w = (float)Math.Round(world.W * k), h = (float)Math.Round(world.H * k);
                    var vp = new RectF((float)Math.Floor((sz.Width - w) / 2), (float)Math.Floor((sz.Height - h) / 2), w, h);
                    targets[map[i]] = new Target { Inst = inst, View = new RenderView { View = world, Viewport = vp, Index = i } };
                }
            }
            else
            {
                for (int i = 0; i < screens.Length; i++)
                {
                    Size sz = screens[i].Bounds.Size;
                    var world = new RectF(0, 0, sz.Width, sz.Height);
                    Instance inst = Make(sceneIds[i], world, new[] { world }, sz.Height / 1080f, mode, new[] { screens[i].Key }, 0);
                    targets[map[i]] = new Target { Inst = inst, View = new RenderView { View = world, Viewport = Pixels(sz), Index = i } };
                }
            }
            return mode;
        }

        static RectF ToRect(Rectangle r) { return new RectF(r.X, r.Y, r.Width, r.Height); }
        static RectF Pixels(Size s) { return new RectF(0, 0, s.Width, s.Height); }

        Instance Make(string id, RectF world, RectF[] visible, float u, ScreenMode mode, string[] keys, int clockScreen)
        {
            var inst = new Instance { Id = id, Scene = GpuScenes.Create(id) ?? new BlankScene() };
            // Clocks (and music mode, which holds one) are sized for a whole screen: no close-up.
            if (inst.Scene.Layout == SceneLayout.World && !(inst.Scene is ClockScene) && !(inst.Scene is MusicScene)) u = Math.Max(u, minWorldU);
            uint seed = (uint)seeds.Next(1, int.MaxValue);
            var setup = new SceneSetup { World = world, Screens = visible, U = u, Seed = seed, Mode = mode, Keys = keys, ClockScreen = clockScreen };
            inst.Scene.Init(gpu, setup);
            inst.Frame.World = world;
            inst.Frame.Focus = setup.Focus;
            inst.Frame.U = u;
            inst.Frame.Seed = (seed & 0xffff) / 65536f;
            inst.BurnPhase = (float)seeds.NextDouble() * 100f;
            instances.Add(inst);
            return inst;
        }

        public string Describe()
        {
            var parts = new List<string>();
            foreach (Target t in targets) parts.Add(t.Inst.Id + "@" + t.View.View);
            return ScreenModes.Name(Mode) + " [" + string.Join("; ", parts.ToArray()) + "]";
        }

        // Test hook: drive the synthetic audio by simulated time.
        public SyntheticAudio Synthetic { get { return audioSource as SyntheticAudio; } }

        public void Update(float dt)
        {
            if (dt < 0f) dt = 0f; else if (dt > 0.1f) dt = 0.1f;
            clock += dt;
            audio.Update(audioSource, dt);
            gpu.UploadAudio(audio);
            foreach (Instance inst in instances)
            {
                inst.Time += dt;
                // Burn-in drift: a slow Lissajous of +-14 design units, periods of a few minutes.
                float bt = inst.Time + inst.BurnPhase;
                float amp = 14f * inst.Frame.U;
                float bx = amp * (float)Math.Sin(bt * 6.2831853 / 173.0), by = amp * (float)Math.Sin(bt * 6.2831853 / 229.0 + 1.3);
                FrameContext f = inst.Context;
                f.Time = inst.Time; f.Dt = dt; f.Audio = audio; f.BurnX = bx; f.BurnY = by;
                inst.Frame.Time = inst.Time; inst.Frame.Dt = dt; inst.Frame.BurnX = bx; inst.Frame.BurnY = by;
                float k = Math.Min(1f, clock / FadeSeconds);
                inst.Frame.Fade = k * k * (3f - 2f * k);
                inst.Frame.SetAudio(audio);
                inst.Scene.Update(f);
            }
        }

        // Draws screen `i` into `rtv` (cleared to black first).
        public void Render(int i, IntPtr rtv)
        {
            Target t = targets[i];
            gpu.BeginTarget(rtv, 0f, 0f, 0f);
            gpu.BeginView(t.View, t.Inst.Frame, audio);
            t.Inst.Scene.Render(t.View);
            if (t.Inst.Frame.Fade < 1f)
            {
                gpu.BeginView(t.View, t.Inst.Frame, audio);
                fade.Draw(BlendMode.Alpha);
            }
        }

        public void Dispose()
        {
            foreach (Instance inst in instances)
                try { inst.Scene.Dispose(); } catch { }
            instances.Clear();
            if (audioSource != null) try { audioSource.Dispose(); } catch { }
        }
    }
}
