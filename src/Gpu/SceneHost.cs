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
    enum ScreenMode { Span, Each, Mirror, PerScreen }

    static class ScreenModes
    {
        public static readonly string[] Names = { "span", "each", "mirror", "perscreen" };

        public static ScreenMode Parse(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "span": return ScreenMode.Span;
                case "mirror": return ScreenMode.Mirror;
                case "perscreen": case "per-screen": return ScreenMode.PerScreen;
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

        public string Key { get { return Id != "" ? Id : Device.TrimStart('\\', '.'); } }

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
                    Bounds = all[i].Bounds, Device = all[i].DeviceName, Primary = all[i].Primary,
                    Id = info != null ? info.Id : "", Name = info != null ? info.Name : all[i].DeviceName,
                };
            }
            return list;
        }
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

        // screens: real monitors; sceneIds: the scene for each (all equal except in perscreen mode).
        public SceneHost(Gpu gpu, GpuScreen[] screens, ScreenMode mode, string[] sceneIds, IAudioSource audioSource)
        {
            this.gpu = gpu;
            this.audioSource = audioSource;
            targets = new Target[screens.Length];
            fade = new FullscreenPass(gpu, "float4 PS(float4 pos : SV_Position) : SV_Target { return float4(0, 0, 0, 1.0 - Fade); }");

            if (mode != ScreenMode.PerScreen)
                for (int i = 1; i < sceneIds.Length; i++) sceneIds[i] = sceneIds[0];
            if (mode == ScreenMode.Span)
            {
                IGpuScene probe = GpuScenes.Create(sceneIds[0]);
                if (probe == null || probe.Layout == SceneLayout.PerScreen) mode = ScreenMode.Each;
                if (probe != null) probe.Dispose();
            }
            Mode = mode;

            if (mode == ScreenMode.Span)
            {
                Rectangle u = screens[0].Bounds;
                float maxH = 0;
                var rects = new RectF[screens.Length];
                for (int i = 0; i < screens.Length; i++)
                {
                    u = Rectangle.Union(u, screens[i].Bounds);
                    maxH = Math.Max(maxH, screens[i].Bounds.Height);
                    rects[i] = ToRect(screens[i].Bounds);
                }
                Instance inst = Make(sceneIds[0], ToRect(u), rects, maxH / 1080f, mode);
                for (int i = 0; i < screens.Length; i++)
                    targets[i] = new Target { Inst = inst, View = new RenderView { View = rects[i], Viewport = Pixels(screens[i].Bounds.Size), Index = i } };
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
                Instance inst = Make(sceneIds[0], world, new[] { world }, world.H / 1080f, mode);
                for (int i = 0; i < screens.Length; i++)
                {
                    Size sz = screens[i].Bounds.Size;
                    float k = Math.Min(sz.Width / world.W, sz.Height / world.H);
                    float w = (float)Math.Round(world.W * k), h = (float)Math.Round(world.H * k);
                    var vp = new RectF((float)Math.Floor((sz.Width - w) / 2), (float)Math.Floor((sz.Height - h) / 2), w, h);
                    targets[i] = new Target { Inst = inst, View = new RenderView { View = world, Viewport = vp, Index = i } };
                }
            }
            else
            {
                for (int i = 0; i < screens.Length; i++)
                {
                    Size sz = screens[i].Bounds.Size;
                    var world = new RectF(0, 0, sz.Width, sz.Height);
                    Instance inst = Make(sceneIds[i], world, new[] { world }, sz.Height / 1080f, mode);
                    targets[i] = new Target { Inst = inst, View = new RenderView { View = world, Viewport = Pixels(sz), Index = i } };
                }
            }
        }

        static RectF ToRect(Rectangle r) { return new RectF(r.X, r.Y, r.Width, r.Height); }
        static RectF Pixels(Size s) { return new RectF(0, 0, s.Width, s.Height); }

        Instance Make(string id, RectF world, RectF[] visible, float u, ScreenMode mode)
        {
            var inst = new Instance { Id = id, Scene = GpuScenes.Create(id) ?? new BlankScene() };
            uint seed = (uint)seeds.Next(1, int.MaxValue);
            inst.Scene.Init(gpu, new SceneSetup { World = world, Screens = visible, U = u, Seed = seed, Mode = mode });
            inst.Frame.World = world;
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
