// Waterfall: a spectrogram that flows down. The newest row of the spectrum enters at the top of
// the world and sinks, so the last ten seconds or so of the music hang on screen as a glowing
// curtain. The bass sits in the middle (of Focus, the largest hole-free rect, so it is never lost
// in a hole), the highs fan out mirrored to both sides.
//
// Span: time runs down the whole world, so on stacked screens the curtain falls through the
// bezel from the upper screen into the lower one. Nothing stands still: every pixel is fed by
// rows that move down, and the centre drifts with Burn.
using System;

namespace IdleVeil
{
    sealed class WaterfallScene : VizScene
    {
        VizHistory hist;
        float rowH;

        protected override string Shader { get { return VizHistory.Hlsl + Body; } }

        const string Body = @"
float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 p = PixelToWorld(pos.xy);
    float rowH = EX[0].x, rows = EX[15].z;
    float rowsAgo = (p.y - World.y - Burn.y) / rowH;
    float age = saturate(rowsAgo / (rows - 2.0));

    float cx = Focus.x + Focus.z * 0.5 + Burn.x;
    float half = max(cx - World.x, World.x + World.z - cx);
    float d = abs(p.x - cx) / half;
    float t = pow(d, 0.85);

    float v = History(t, rowsAgo);
    float lit = pow(v, 1.7);
    float3 col = Pal(0.05 + 0.55 * v + 0.25 * age) * lit * 0.95;
    // A faint thread at the band edges keeps the texture readable when it is quiet.
    col += Pal(0.4) * 0.025 * v;
    // Hotter where it is loud and fresh.
    col = Hot(col, saturate(lit - 0.55) * 0.6 * (1.0 - age));
    // Thin dark seams between the bins keep it crisp at any width.
    float seam = 1.0 - abs(frac(t * 63.0) - 0.5) * 2.0;   // 1 halfway between two band centres
    col *= 1.0 - 0.35 * pow(seam, 8.0);
    col *= exp(-age * 1.2) * (0.85 + 0.25 * SP.w * (1.0 - age));
    return Out(Tone(col), pos.xy);
}
";

        protected override void OnInitViz()
        {
            // About 5 design units per row; the ring covers the whole world height.
            int rows = (int)Math.Ceiling(World.H / (5f * U)) + 2;
            rows = Math.Max(64, Math.Min(512, rows));
            rowH = World.H / (rows - 2);
            // Ten seconds from the top of the world to the bottom.
            hist = new VizHistory(Gpu, rows, (rows - 2) / 10f);
        }

        protected override void OnUpdateViz(FrameContext f)
        {
            float newest, progress;
            hist.Update(f.Dt, Bands, out newest, out progress);
            SetEx(0, rowH, 0f, 0f, 0f);
            SetEx(VizHistory.Slot, newest, progress, hist.Rows, 0f);
        }

        public override void Render(RenderView view)
        {
            hist.Bind();
            base.Render(view);
        }
    }
}
