// Ridges: stacked line plots of the spectrum, like the cover of an old pulsar chart. Every few
// tenths of a second a new ridge rises at the bottom, front-most, and the older ones move up and
// back; each ridge hides the ones behind it. The bass lifts the middle of the line, the highs
// ripple out to the sides.
//
// Span: the ridges are centred on Focus (the largest hole-free rect) and climb through the whole
// world, so on stacked screens they pass the bezel from the lower screen into the upper one. Every
// line is always moving up, so no pixel holds still.
using System;

namespace IdleVeil
{
    sealed class RidgesScene : VizScene
    {
        VizHistory hist;
        float spacing, maxAmp;

        protected override string Shader { get { return VizHistory.Hlsl + Body; } }

        const string Body = @"
float Ridge(float x, float r, float cx, float half, float maxAmp)
{
    float d = abs(x - cx) / half;
    float env = exp(-d * d * 4.5);
    float v = History(min(pow(d, 0.9) * 0.9, 1.0), r + EX[15].y);   // exactly row r, no blending
    float grain = noise(float2(x / (9.0 * U), (EX[0].z - r) * 3.7)) - 0.5;
    return (v * (0.25 + 0.75 * env) + grain * 0.06 * (0.25 + env)) * maxAmp * saturate((1.12 - d) * 5.0);   // tapers out, no hard edge
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 p = PixelToWorld(pos.xy);
    float px = PixelWorld;
    float sp = EX[0].x, maxAmp = EX[0].y, rows = EX[15].z, prog = EX[15].y;
    float base = World.y + World.w - sp * 1.5 + Burn.y;
    float cx = Focus.x + Focus.z * 0.5 + Burn.x;
    float half = Focus.z * 0.46;
    float lw = 1.6 * U;

    float3 col = float3(0.0, 0.0, 0.0);
    float rBack = floor((base - (p.y - lw * 3.0)) / sp + prog);
    float rFront = ceil((base - (p.y + maxAmp)) / sp + prog);
    rFront = max(rFront, 0.0);
    rBack = min(rBack, rows - 3.0);
    [loop]
    for (float r = rBack; r >= rFront; r -= 1.0)
    {
        float y0 = base - (r - prog) * sp;
        float h = Ridge(p.x, r, cx, half, maxAmp);
        float yl = y0 - max(h, 0.0);
        float age = r / rows;
        // Below the line: this ridge hides everything behind it.
        if (p.y > yl + lw) col = float3(0.0, 0.0, 0.0);
        float dist = abs(p.y - yl);
        float lvl = saturate(h / maxAmp);
        float3 tone = Pal(0.08 + 0.45 * lvl + 0.2 * age);
        float a = (0.45 + 0.85 * lvl) * exp(-age * 1.3);
        col += tone * (Coverage(dist - lw * 0.5, px) * 0.85 + exp(-dist / (5.0 * U)) * 0.10 * lvl) * a;
    }
    return Out(Tone(col), pos.xy);
}
";

        protected override void OnInitViz()
        {
            spacing = 22f * U;
            maxAmp = 170f * U;
            int rows = (int)Math.Ceiling(World.H / spacing) + 4;
            rows = Math.Max(32, Math.Min(512, rows));
            hist = new VizHistory(Gpu, rows, 5f);
        }

        protected override void OnUpdateViz(FrameContext f)
        {
            float newest, progress;
            hist.Update(f.Dt, Bands, out newest, out progress);
            SetEx(0, spacing, maxAmp, hist.Total % 8192, 0f);
            SetEx(VizHistory.Slot, newest, progress, hist.Rows, 0f);
        }

        public override void Render(RenderView view)
        {
            hist.Bind();
            base.Render(view);
        }
    }
}
