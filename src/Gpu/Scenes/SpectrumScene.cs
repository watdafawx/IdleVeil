// Spectrum (reference scene for the audio pattern): rounded bars with falling peak caps, a soft
// glow and a dim reflection, drawn per pixel from the AudioData constant buffer.
//
// The bars stand on the floors (bottom edges with no screen below, see SceneSetup.Floors). On one
// screen that is its bottom edge: 64 bars, as before. In span mode it is one wide spectrum laid
// along every floor of the layout, low to high from left to right, with no bar in a hole: on a
// layout with a short and a tall column each part stands on its own floor. The baseline and margins
// drift slowly and the palette turns over minutes, so no edge or colour stays put on an OLED.
using System;

namespace IdleVeil
{
    sealed class SpectrumScene : GpuScene
    {
        const int MaxSegs = 8;
        FullscreenPass pass;
        SceneConstants k;
        Edge[] floors;
        float barH;     // reference height for bar sizes (world units)
        float[] lefts, rights;

        const string Shader = @"
cbuffer Scene : register(b2)
{
    float Idle;      // 1 when nothing is playing: bars breathe gently instead of lying flat
    float Hue;       // palette rotation, turns slowly
    float Pitch;     // world units per bar
    float Count;     // bars in total
    float4 SegA[8];  // per floor: x of the first bar's left edge, baseline y, first bar index, bars
    float4 SegB[8];  // per floor: floor y, x0, x1, max bar height
    float4 Misc;     // x floors, y reference height
};

float3 Palette(float h)   // h: 0 bottom .. 1 top of the tallest bar
{
    float3 a = hsv(Hue + 0.62, 0.80, 0.55);      // deep blue-violet
    float3 b = hsv(Hue + 0.50, 0.70, 0.75);      // cyan
    float3 c = hsv(Hue + 0.40, 0.45, 0.85);      // mint
    return h < 0.5 ? lerp(a, b, h * 2.0) : lerp(b, c, h * 2.0 - 1.0);
}

float BarT(float gi) { return Count > 1.0 ? gi / (Count - 1.0) : 0.0; }

float Level(float gi)
{
    float t = BarT(gi);
    float v = Count <= 64.5 ? BandAt((int)gi) : Band(t);
    float breathe = 0.035 + 0.025 * sin(Time * 0.8 + t * 23.0) + 0.015 * sin(Time * 1.9 - t * 7.0);
    return max(v * 0.96, breathe * Idle);
}

float PeakLevel(float gi) { return Count <= 64.5 ? PeakAt((int)gi) : Peak(BarT(gi)); }

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 p = PixelToWorld(pos.xy);
    float px = PixelWorld;

    // The floor under this pixel: the nearest one at or below it that covers this x.
    int s = -1;
    float best = 1e9;
    [loop]
    for (int q = 0; q < (int)Misc.x; q++)
    {
        float4 Bq = SegB[q];
        if (p.x < Bq.y || p.x >= Bq.z || Bq.x < p.y - 1.0) continue;
        if (Bq.x < best) { best = Bq.x; s = q; }
    }
    if (s < 0) return Out(float3(0.0, 0.0, 0.0), pos.xy);

    float4 A = SegA[s], B = SegB[s];
    float baseY = A.y, maxH = B.w, href = Misc.y;
    float barW = Pitch * 0.62;
    float radius = barW * 0.5;

    float3 col = float3(0.0, 0.0, 0.0);
    // Faint floor glow that swells with the bass.
    float span = max(B.z - B.y, 1.0);
    float gx = (p.x - (B.y + B.z) * 0.5) / (span * 0.45), gy = (p.y - baseY) / (href * 0.30);
    col += Palette(0.2) * 0.022 * exp(-(gx * gx + gy * gy)) * (0.6 + 0.8 * Audio.x);

    float fi = (p.x - A.x) / Pitch;
    int i = (int)floor(fi);
    if (i >= -1 && i <= (int)A.w)
    {
        // Check this bar and its neighbours so glows don't cut off at column edges.
        for (int j = i - 1; j <= i + 1; j++)
        {
            if (j < 0 || j >= (int)A.w) continue;
            float gi = A.z + j;
            float lvl = Level(gi);
            float h = max(lvl * maxH, barW);           // never thinner than a dot
            float cx = A.x + (j + 0.5) * Pitch;
            float2 c = float2(cx, baseY - h * 0.5);
            float d = sdRoundBox(p - c, float2(barW * 0.5, h * 0.5), radius);
            float cov = Coverage(d, px);
            float hy = saturate((baseY - p.y) / maxH);          // height fraction of this pixel
            float3 tone = Palette(hy);
            float body = 0.45 + 0.55 * hy;                      // darker towards the baseline
            col += tone * cov * body * 0.62;
            // Glow around the bar (mostly above it).
            float g = exp(-max(d, 0.0) / (14.0 * U)) * (1.0 - cov);
            col += Palette(lvl) * g * 0.10 * (0.4 + lvl);
            // Peak cap.
            float pk = max(PeakLevel(gi) * 0.96, lvl);
            float py = baseY - max(pk * maxH, barW) - 7.0 * U;
            float dc = sdRoundBox(p - float2(cx, py), float2(barW * 0.5, 2.2 * U), 2.2 * U);
            col += Palette(1.0) * Coverage(dc, px) * 0.55 * saturate(pk * 6.0);
            // Reflection under the baseline: the same bar, mirrored, dim and fading out.
            float2 rp = float2(p.x, 2.0 * baseY - p.y + 6.0 * U);
            float dr = sdRoundBox(rp - c, float2(barW * 0.5, h * 0.5), radius);
            float fadeR = exp(-max(p.y - baseY, 0.0) / (href * 0.06));
            if (p.y > baseY) col += Palette(saturate((baseY - rp.y) / maxH)) * Coverage(dr, px) * 0.13 * fadeR;
        }
    }
    return Out(col, pos.xy);
}
";

        protected override void OnInit()
        {
            pass = new FullscreenPass(Gpu, Shader);
            k = new SceneConstants(Gpu, 4 + MaxSegs * 8 + 4);
            k[1] = Rng.Next();
            // The longest floors, at most 8, kept in left-to-right order; slivers carry no bars.
            Edge[] all = Setup.Floors;
            float longest = 0f;
            foreach (Edge e in all) longest = Math.Max(longest, e.W);
            var keep = new System.Collections.Generic.List<Edge>();
            foreach (Edge e in all) if (e.W >= longest * 0.12f && keep.Count < MaxSegs) keep.Add(e);
            floors = keep.ToArray();
            lefts = new float[floors.Length];
            rights = new float[floors.Length];
            // Bar heights follow the shortest screen that has a floor, so bars match from part to part.
            barH = float.MaxValue;
            foreach (Edge e in floors) barH = Math.Min(barH, Setup.Screens[e.Screen].H);
        }

        bool Adjacent(float x, int self)
        {
            for (int i = 0; i < floors.Length; i++)
                if (i != self && (Math.Abs(floors[i].X0 - x) <= 1f || Math.Abs(floors[i].X1 - x) <= 1f)) return true;
            return false;
        }

        public override void Update(FrameContext f)
        {
            k[0] = f.Audio.Playing ? 0f : 1f;
            k[1] = (k[1] + f.Dt / 600f) % 1f;   // a full turn of the palette every 10 minutes

            // Margins: the outer ends of the spectrum keep 9 % of their screen (drifting with the burn
            // offset); ends that touch another floor keep none, so the bars run on across the bezel.
            int n = floors.Length;
            float usable = 0f, minW = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                Edge e = floors[i];
                RectF sc = Setup.Screens[e.Screen];
                minW = Math.Min(minW, sc.W);
                float outer = sc.W * 0.09f;
                lefts[i] = e.X0 + (Adjacent(e.X0, i) ? 0f : outer + f.BurnX * 1.5f);
                rights[i] = e.X1 - (Adjacent(e.X1, i) ? 0f : outer - f.BurnX * 1.5f);
                usable += Math.Max(0f, rights[i] - lefts[i]);
            }
            // One screen: exactly 64 bars. Several: the 1080p-screen pitch, but at most 128 bars.
            float pitch = n == 1 ? usable / 64f : Math.Max(minW * 0.82f / 64f, usable / 128f);
            int index = 0;
            for (int i = 0; i < n; i++)
            {
                Edge e = floors[i];
                float w = Math.Max(0f, rights[i] - lefts[i]);
                int bars = n == 1 ? 64 : (int)Math.Floor(w / pitch + 0.001f);
                float x = lefts[i] + (w - bars * pitch) * 0.5f;
                int a = 4 + i * 4, b = 4 + MaxSegs * 4 + i * 4;
                k[a] = x; k[a + 1] = e.Y - barH * 0.30f + f.BurnY * 2f; k[a + 2] = index; k[a + 3] = bars;
                k[b] = e.Y; k[b + 1] = e.X0; k[b + 2] = e.X1; k[b + 3] = barH * 0.46f;
                index += bars;
            }
            k[2] = pitch;
            k[3] = index;
            int m = 4 + MaxSegs * 8;
            k[m] = n; k[m + 1] = barH;
        }

        public override void Render(RenderView view)
        {
            k.Bind();
            pass.Draw();
        }
    }
}
