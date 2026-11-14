// Music mode: shows a visualizer while something is playing and a clock when it is quiet, and
// fades smoothly between the two (about 2 s). Which visualizer and which clock come from
// MusicConfig (tray: Music mode). The audio capture runs for as long as this scene is up (it is in
// GpuScenes.AudioIds), and AudioFrame.Playing already has hysteresis, so a short gap between
// tracks does not flip the picture.
//
// The crossfade needs no render target: the visualizer is drawn first, a black alpha layer then
// dims it to its share, and the clock (which has a Mix level) is added on top. So any scene can be
// the visualizer, ported or not.
//
// Test hook for --render-test: IDLEVEIL_MUSIC_STATE=clock | music | 0..1 pins the mix.
using System;

namespace IdleVeil
{
    sealed class MusicScene : GpuScene
    {
        IGpuScene viz;
        ClockScene clock;
        FullscreenPass dim;
        SceneConstants dimK;
        float vizMix = -1f;     // -1: not decided yet (snap to the state at the first frame)
        float forced = -1f;

        public override SceneLayout Layout { get { return SceneLayout.PerScreen; } }

        const string DimShader = @"
cbuffer Scene : register(b2) { float4 DimA; };
float4 PS(float4 pos : SV_Position) : SV_Target { return float4(0.0, 0.0, 0.0, DimA.x); }
";

        protected override void OnInit()
        {
            string vid = MusicConfig.Viz;
            viz = vid == "music" ? null : GpuScenes.Create(vid);
            if (viz == null) viz = new SpectrumScene();
            clock = GpuScenes.Create(MusicConfig.Clock) as ClockScene;
            if (clock == null) clock = new ClockSevenSegScene();
            viz.Init(Gpu, Setup);
            clock.Init(Gpu, Setup);
            dim = new FullscreenPass(Gpu, DimShader);
            dimK = new SceneConstants(Gpu, 4);

            string s = Environment.GetEnvironmentVariable("IDLEVEIL_MUSIC_STATE");
            if (!string.IsNullOrEmpty(s))
            {
                float v;
                if (s == "clock") forced = 0f;
                else if (s == "music") forced = 1f;
                else if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) forced = Math.Max(0f, Math.Min(1f, v));
            }
        }

        public override void Update(FrameContext f)
        {
            float target = forced >= 0f ? forced : (f.Audio.Playing ? 1f : 0f);
            if (vizMix < 0f) vizMix = target;
            else
            {
                float step = f.Dt / 2f;
                vizMix += Math.Max(-step, Math.Min(step, target - vizMix));
            }
            float e = vizMix * vizMix * (3f - 2f * vizMix);
            clock.Mix = 1f - e;
            dimK[0] = 1f - e;
            viz.Update(f);
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
            if (viz != null) viz.Dispose();
            if (clock != null) clock.Dispose();
        }
    }
}
