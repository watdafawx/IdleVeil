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

        // True if (x, y) is on some screen: span mode has holes (the empty corner under a short monitor).
        public bool Visible(float x, float y, float margin)
        {
            foreach (RectF r in Screens)
                if (x >= r.X - margin && y >= r.Y - margin && x < r.Right + margin && y < r.Bottom + margin) return true;
            return false;
        }
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
        public static readonly string[] Ids = { "starfield", "aurora", "spectrum", "blank" };

        public static IGpuScene Create(string id)
        {
            switch (id)
            {
                case "starfield": return new StarfieldScene();
                case "aurora": return new AuroraScene();
                case "spectrum": return new SpectrumScene();
                case "blank": return new BlankScene();
                default: return null;
            }
        }

        public static bool Has(string id) { return Array.IndexOf(Ids, id) >= 0; }

        // Scenes that read Audio / AudioData. The loopback capture only runs while one is on screen.
        public static readonly string[] AudioIds = { "spectrum" };

        public static bool UsesAudio(string id) { return Array.IndexOf(AudioIds, id) >= 0; }
    }
}
