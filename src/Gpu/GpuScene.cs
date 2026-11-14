// The scene interface every GPU effect implements, and the registry of effects.
// How to write one: docs/renderer.md.
using System;

namespace IdleVeil
{
    // How a scene wants to be laid out in span mode.
    enum SceneLayout
    {
        World,       // one continuous world across all screens (starfield, aurora, fireflies)
        PerScreen,   // a composition per screen (spectrum, clock): span falls back to each
    }

    // Given once, at Init. Everything is in world units: virtual-desktop pixels in span mode, the
    // screen's own pixels (origin 0,0) in each / per-screen mode, the reference screen in mirror mode.
    sealed class SceneSetup
    {
        public RectF World;       // the whole world
        public RectF[] Screens;   // the parts of the world that are actually visible (spawn and cull against these)
        public float U;           // world units per design unit (1 on a 1080p screen)
        public uint Seed;         // random, never 0
        public ScreenMode Mode;
        public string[] Keys;     // EDID key of each entry of Screens ("" if unknown, e.g. thumbnails)
        public int ClockScreen;   // span: index into Screens of the screen a clock sits on (never an OLED if avoidable)

        // True if (x, y) is on some screen: span mode has holes (the empty corner under a short monitor).
        public bool Visible(float x, float y, float margin)
        {
            foreach (RectF r in Screens)
                if (x >= r.X - margin && y >= r.Y - margin && x < r.Right + margin && y < r.Bottom + margin) return true;
            return false;
        }

        // True when several screens share one world (span mode): scenes compose across them.
        public bool Spanning { get { return Screens.Length > 1; } }

        // Index of the screen that contains (x, y), or -1 (a hole or outside).
        public int ScreenAt(float x, float y)
        {
            for (int i = 0; i < Screens.Length; i++) if (Screens[i].Contains(x, y)) return i;
            return -1;
        }

        Edge[] floors, ceilings;
        RectF? focus;

        // Bottom edges with no screen directly below them: where things land and where fire burns.
        // Neighbouring pieces at the same height are merged, so a row of equal screens is one floor.
        public Edge[] Floors { get { return floors ?? (floors = Edges(true)); } }
        // Top edges with no screen directly above them: where snow and rain come from.
        public Edge[] Ceilings { get { return ceilings ?? (ceilings = Edges(false)); } }

        // The largest rectangle that is visible everywhere (no hole, no gap). Centred compositions
        // (radial, tunnel, ring, scope) sit in it, so a hole never cuts them. One screen: the world.
        public RectF Focus { get { if (focus == null) focus = LargestVisibleRect(); return focus.Value; } }

        Edge[] Edges(bool bottom)
        {
            var raw = new System.Collections.Generic.List<Edge>();
            for (int i = 0; i < Screens.Length; i++)
            {
                RectF s = Screens[i];
                float y = bottom ? s.Bottom : s.Y;
                // Start with the whole edge, cut out the parts another screen continues.
                var parts = new System.Collections.Generic.List<float[]> { new[] { s.X, s.Right } };
                for (int j = 0; j < Screens.Length; j++)
                {
                    if (j == i) continue;
                    RectF o = Screens[j];
                    float oy = bottom ? o.Y : o.Bottom;
                    if (Math.Abs(oy - y) > 1f) continue;
                    var next = new System.Collections.Generic.List<float[]>();
                    foreach (float[] p in parts)
                    {
                        if (o.Right <= p[0] || o.X >= p[1]) { next.Add(p); continue; }
                        if (o.X > p[0]) next.Add(new[] { p[0], o.X });
                        if (o.Right < p[1]) next.Add(new[] { o.Right, p[1] });
                    }
                    parts = next;
                }
                foreach (float[] p in parts)
                    if (p[1] - p[0] >= 8f) raw.Add(new Edge { X0 = p[0], X1 = p[1], Y = y, Screen = i, CutLeft = p[0] > s.X + 0.5f, CutRight = p[1] < s.Right - 0.5f });
            }
            raw.Sort(delegate(Edge a, Edge b) { int c = a.Y.CompareTo(b.Y); return c != 0 ? c : a.X0.CompareTo(b.X0); });
            var merged = new System.Collections.Generic.List<Edge>();
            foreach (Edge e in raw)
            {
                if (merged.Count > 0)
                {
                    Edge last = merged[merged.Count - 1];
                    if (Math.Abs(last.Y - e.Y) <= 1f && Math.Abs(last.X1 - e.X0) <= 1f)
                    {
                        last.X1 = e.X1; last.CutRight = e.CutRight;
                        merged[merged.Count - 1] = last;
                        continue;
                    }
                }
                merged.Add(e);
            }
            merged.Sort(delegate(Edge a, Edge b) { return a.X0.CompareTo(b.X0); });
            return merged.ToArray();
        }

        // Every rectangle with corners on screen edges, checked cell by cell. Few screens: cheap.
        RectF LargestVisibleRect()
        {
            if (Screens.Length == 1) return Screens[0];
            var xs = new System.Collections.Generic.List<float>();
            var ys = new System.Collections.Generic.List<float>();
            foreach (RectF r in Screens)
            {
                if (!xs.Contains(r.X)) xs.Add(r.X);
                if (!xs.Contains(r.Right)) xs.Add(r.Right);
                if (!ys.Contains(r.Y)) ys.Add(r.Y);
                if (!ys.Contains(r.Bottom)) ys.Add(r.Bottom);
            }
            xs.Sort(); ys.Sort();
            int nx = xs.Count - 1, ny = ys.Count - 1;
            var cell = new bool[nx, ny];
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                    cell[i, j] = ScreenAt((xs[i] + xs[i + 1]) * 0.5f, (ys[j] + ys[j + 1]) * 0.5f) >= 0;
            RectF best = Screens[0];
            double bestScore = -1;
            for (int x0 = 0; x0 < nx; x0++)
                for (int x1 = x0; x1 < nx; x1++)
                    for (int y0 = 0; y0 < ny; y0++)
                        for (int y1 = y0; y1 < ny; y1++)
                        {
                            bool ok = true;
                            for (int i = x0; i <= x1 && ok; i++)
                                for (int j = y0; j <= y1 && ok; j++) ok = cell[i, j];
                            if (!ok) break;   // taller rects from this y0 contain the hole too
                            float w = xs[x1 + 1] - xs[x0], h = ys[y1 + 1] - ys[y0];
                            // Compositions are sized by the height, so a wide strip is a fine stage: plain area.
                            double score = (double)w * h;
                            if (score > bestScore) { bestScore = score; best = new RectF(xs[x0], ys[y0], w, h); }
                        }
            return best;
        }
    }

    // A horizontal piece of screen edge (a floor or a ceiling) in world units.
    struct Edge
    {
        public float X0, X1, Y;
        public int Screen;               // the screen it belongs to (the first one, if merged)
        public bool CutLeft, CutRight;   // the end is inside a screen (another screen continues there), not at a bezel
        public float W { get { return X1 - X0; } }
    }

    // Once per frame, before any Render.
    sealed class FrameContext
    {
        public float Time;          // seconds since this scene started
        public float Dt;            // seconds since the last frame (clamped to 0..0.1)
        public AudioFrame Audio;    // smoothed bands, peaks, waveform, bass, beat
        public float BurnX, BurnY;  // the same slow drift as Burn in the shader (world units)
    }

    // One screen's view of the world.
    struct RenderView
    {
        public RectF View;       // world rect shown
        public RectF Viewport;   // where it lands in the render target (pixels)
        public int Index;        // screen index
    }

    interface IGpuScene : IDisposable
    {
        SceneLayout Layout { get; }
        // Make shaders, buffers and initial state. Throwing here fails the whole overlay over to GDI.
        void Init(Gpu gpu, SceneSetup setup);
        // Advance the simulation; fill sprite batches / constants. Called once per frame.
        void Update(FrameContext frame);
        // Draw one screen. The target is cleared to black and the Frame/Audio constants for this
        // view are bound. Called once per screen per frame: no simulation here.
        void Render(RenderView view);
    }

    // Convenience base: keeps the setup around. GPU objects made through Gpu are released with it,
    // so most scenes need no Dispose.
    abstract class GpuScene : IGpuScene
    {
        protected Gpu Gpu;
        protected SceneSetup Setup;
        protected RectF World;
        protected float U;
        protected FastRandom Rng;

        public virtual SceneLayout Layout { get { return SceneLayout.World; } }

        public void Init(Gpu gpu, SceneSetup setup)
        {
            Gpu = gpu; Setup = setup; World = setup.World; U = setup.U;
            Rng = new FastRandom(setup.Seed);
            OnInit();
        }

        protected abstract void OnInit();
        public virtual void Update(FrameContext frame) { }
        public abstract void Render(RenderView view);
        public virtual void Dispose() { }
    }

    // Pure black. Nothing to draw: the host already cleared the target.
    sealed class BlankScene : GpuScene
    {
        protected override void OnInit() { }
        public override void Render(RenderView view) { }
    }

    static class GpuScenes
    {
        // Ported scenes, in tray order. Add new ids here and in Create.
        public static readonly string[] Ids = { "starfield", "aurora", "fireflies", "plasma", "snow", "fire", "life", "mystify", "matrix", "spectrum", "scope", "scopering", "radial", "tunnel", "ripples", "waterfall", "skyline", "burst", "lissajous", "ridges", "clock", "clock-led", "clock-neon", "clock-analog", "clock-music", "music", "blank" };

        public static IGpuScene Create(string id)
        {
            switch (id)
            {
                case "starfield": return new StarfieldScene();
                case "aurora": return new AuroraScene();
                case "fireflies": return new FirefliesScene();
                case "spectrum": return new SpectrumScene();
                case "plasma": return new PlasmaScene();
                case "snow": return new SnowScene();
                case "fire": return new FireScene();
                case "life": return new LifeScene();
                case "mystify": return new MystifyScene();
                case "matrix": return new MatrixScene();
                case "scope": return new ScopeScene();
                case "scopering": return new ScopeRingScene();
                case "radial": return new RadialScene();
                case "tunnel": return new TunnelScene();
                case "ripples": return new RipplesScene();
                case "waterfall": return new WaterfallScene();
                case "skyline": return new SkylineScene();
                case "burst": return new BurstScene();
                case "lissajous": return new LissajousScene();
                case "ridges": return new RidgesScene();
                case "clock": return new ClockSevenSegScene();
                case "clock-led": return new ClockLedScene();
                case "clock-neon": return new ClockNeonScene();
                case "clock-analog": return new ClockAnalogScene();
                case "clock-music": return new ClockMusicScene();
                case "music": return new MusicScene();
                case "blank": return new BlankScene();
                default: return null;
            }
        }

        public static bool Has(string id) { return Array.IndexOf(Ids, id) >= 0; }

        // False for scenes laid out per screen: span shows them as each. Every scene is World now.
        public static bool CanSpan(string id)
        {
            IGpuScene s = Create(id);
            if (s == null) return false;
            try { return s.Layout == SceneLayout.World; } finally { s.Dispose(); }
        }

        // Scenes that read Audio / AudioData. The loopback capture only runs while one is on screen.
        public static readonly string[] AudioIds = { "spectrum", "scope", "scopering", "radial", "tunnel", "ripples", "waterfall", "skyline", "burst", "lissajous", "ridges", "clock-music", "music" };

        public static bool UsesAudio(string id) { return Array.IndexOf(AudioIds, id) >= 0; }
    }
}
