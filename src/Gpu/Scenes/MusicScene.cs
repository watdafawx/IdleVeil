// Music mode: shows a visualizer while something is playing and a clock when it is quiet, and
// fades smoothly between the two (about 2 s). Which visualizer and which clock come from
// MusicConfig (settings window, Music page). The audio capture runs for as long as this scene is
// up (it is in GpuScenes.AudioIds), and AudioFrame.Playing already has hysteresis, so a short gap
// between tracks does not flip the picture.
//
// The visualizer can be "random": every visualizer of the pool is created up front, and each time
// the music starts again (after the clock was fully back) the next random pick takes over. The
// pick comes from MusicConfig.RandomPick with the host's session seed, so in each mode all screens
// agree; in per-screen mode every monitor can have its own choice (MusicConfig.VizFor).
//
// Span: the visualizers and the clocks are all World scenes, so this is one too. The visualizer
// spans every screen, the clock sits on the clock screen with its ambient layer across the others.
//
// The crossfade needs no render target: the visualizer is drawn first, a black alpha layer then
// dims it to its share, and the clock (which has a Mix level) is added on top. So any scene can be
// the visualizer.
//
// Test hook for --render-test: IDLEVEIL_MUSIC_STATE=clock | music | 0..1 pins the mix.
using System;
using System.Collections.Generic;

namespace IdleVeil
{
    sealed class MusicScene : GpuScene
    {
        readonly List<IGpuScene> vizzes = new List<IGpuScene>();
        string[] vizIds;
        IGpuScene viz;
        ClockScene clock;
        FullscreenPass dim;
        SceneConstants dimK;
        float vizMix = -1f;     // -1: not decided yet (snap to the state at the first frame)
        float forced = -1f;
        bool random;
        int generation;
        string salt;

        const string DimShader = @"
cbuffer Scene : register(b2) { float4 DimA; };
float4 PS(float4 pos : SV_Position) : SV_Target { return float4(0.0, 0.0, 0.0, DimA.x); }
";

        protected override void OnInit()
        {
            string key = Setup.Keys != null && Setup.Keys.Length == 1 ? Setup.Keys[0] : "";
            string choice = MusicConfig.ChoiceFor(Setup.Mode, key);
            random = choice == "random";
            // Per-screen: every monitor picks on its own. Other modes: the same pick everywhere.
            salt = Setup.Mode == ScreenMode.PerScreen ? key : "";
            var made = new List<string>();
            foreach (string id in random ? MusicConfig.PoolIds() : new[] { choice })
            {
                IGpuScene s = id == "music" ? null : GpuScenes.Create(id);
                if (s == null) continue;
                s.Init(Gpu, Setup);
                vizzes.Add(s);
                made.Add(id);
            }
            vizIds = made.ToArray();
            if (vizzes.Count == 0)
            {
                var s = new SpectrumScene();
                s.Init(Gpu, Setup);
                vizzes.Add(s);
                vizIds = new[] { "spectrum" };
            }
            viz = Choose();
            clock = GpuScenes.Create(MusicConfig.Clock) as ClockScene;
            if (clock == null) clock = new ClockSevenSegScene();
            clock.Init(Gpu, Setup);
            AppLog.Info("music", "music scene: viz " + VizId + (random ? " (random from " + string.Join(",", vizIds) + ")" : "") + ", clock " + MusicConfig.Clock);
            dim = new FullscreenPass(Gpu, DimShader);
            dimK = new SceneConstants(Gpu, 4);

            string st = Environment.GetEnvironmentVariable("IDLEVEIL_MUSIC_STATE");
            if (!string.IsNullOrEmpty(st))
            {
                float v;
                if (st == "clock") forced = 0f;
                else if (st == "music") forced = 1f;
                else if (float.TryParse(st, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) forced = Math.Max(0f, Math.Min(1f, v));
            }
        }

        string VizId { get { int i = vizzes.IndexOf(viz); return i >= 0 && i < vizIds.Length ? vizIds[i] : "?"; } }

        IGpuScene Choose()
        {
            if (!random || vizzes.Count == 1) return vizzes[0];
            string id = MusicConfig.RandomPick(vizIds, generation, salt);
            int i = Array.IndexOf(vizIds, id);
            return vizzes[i >= 0 && i < vizzes.Count ? i : 0];
        }

        float lastTarget = -1f;

        public override void Update(FrameContext f)
        {
            float target = forced >= 0f ? forced : (f.Audio.Playing ? 1f : 0f);
            if (target != lastTarget)
            {
                if (lastTarget >= 0f) AppLog.Info("music", target > 0f ? "music playing" + (random && vizMix <= 0f ? "" : ": showing " + VizId) : "music stopped: back to the clock");
                lastTarget = target;
            }
            if (vizMix < 0f) vizMix = target;
            else
            {
                // The clock is fully back and the music starts again: a new random pick.
                if (random && vizMix <= 0f && target > 0f)
                {
                    generation++;
                    viz = Choose();
                    AppLog.Info("music", "music visualizer switched to " + VizId);
                }
                float step = f.Dt / 2f;
                vizMix += Math.Max(-step, Math.Min(step, target - vizMix));
            }
            float e = vizMix * vizMix * (3f - 2f * vizMix);
            clock.Mix = 1f - e;
            dimK[0] = 1f - e;
            // Only the visualizer on screen runs; the others wait (their state just eases back in).
            if (e > 0.003f || !random) viz.Update(f);
            clock.Update(f);
        }

        public override void Render(RenderView view)
        {
            float e = vizMix * vizMix * (3f - 2f * vizMix);
            if (e > 0.003f)
            {
                viz.Render(view);
                if (e < 0.997f)
                {
                    dimK.Bind();
                    dim.Draw(BlendMode.Alpha);
                }
            }
            if (e < 0.997f) clock.Render(view);
        }

        public override void Dispose()
        {
            foreach (IGpuScene s in vizzes) s.Dispose();
            if (clock != null) clock.Dispose();
        }
    }
}
