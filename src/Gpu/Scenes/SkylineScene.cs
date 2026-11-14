// Skyline: a night city along the floors, drifting slowly sideways. Each building's windows light
// up with the part of the spectrum that sits above it (bass on the left, highs on the right),
// the sky over the roofs glows with the same bands, and on a kick the tall towers blink.
//
// Span: the city stands on every floor of the layout (SceneSetup.Floors), at the real bottom of
// each column, never in a hole; one band position runs across the whole world, so the spectrum
// reads left to right over all screens. Two layers scroll at different speeds, so nothing stays put.
using System;

namespace IdleVeil
{
    sealed class SkylineScene : VizScene
    {
        const int MaxFloors = 8;
        Edge[] floors;
        float scrollA, scrollB;

        protected override string Shader { get { return Body; } }

        // EX[0]: floors, building pitch, scroll back, scroll front. EX[1 + i]: floor y, x0, x1, screen h.
        const string Body = @"
float3 Layer(float2 p, float baseY, float scrH, float pitch, float scroll, float salt, float tall, float bright,
             float t0, inout float covered)
{
    float sx = p.x + scroll;
    float bi = floor(sx / pitch);
    float fx = sx - bi * pitch;
    float h1 = hash11(bi * 1.71 + salt), h2 = hash11(bi * 3.13 + salt + 7.0);
    float gap = pitch * (0.05 + 0.18 * h2);
    float hgt = scrH * (0.06 + tall * (0.35 * h1 * h1 + 0.08 * h2));
    float roof = baseY - hgt;
    float3 col = float3(0.0, 0.0, 0.0);
    // Where the band for this building is read: its centre on screen.
    float cxw = p.x - fx + pitch * 0.5;
    float t = saturate((cxw - World.x) / World.z);
    float band = BandF(t);
    if (fx < gap || fx > pitch - gap * 0.4) return col;
    if (p.y < roof) return col;
    covered = 1.0;
    col = Pal(0.6) * 0.010 * bright;                         // the dark body
    col += Pal(0.25) * 0.10 * bright * Coverage(abs(p.y - roof) - 0.6 * U, PixelWorld);   // roof line
    // Windows: a grid, each lit or not by hash vs. the band level, flickering slowly.
    float wx = 7.0 * U, wy = 11.0 * U;
    float2 local = float2(fx - gap, p.y - roof - 6.0 * U);
    float2 cell = floor(local / float2(wx, wy));
    float2 inC = local - cell * float2(wx, wy);
    if (local.y > 0.0 && inC.x > wx * 0.30 && inC.x < wx * 0.80 && inC.y > wy * 0.30 && inC.y < wy * 0.75
        && local.x < pitch - gap * 1.4 - wx * 0.5)
    {
        float hw = hash12(cell + float2(bi * 17.0, salt * 31.0));
        float slow = 0.5 + 0.5 * sin(Time * (0.15 + hw * 0.4) + hw * 40.0);
        float on = step(hw, 0.08 + 0.75 * band + 0.10 * slow * (1.0 - SP.y));
        float3 warm = lerp(Pal(0.15 + 0.3 * hw), float3(1.0, 0.78, 0.45), 0.35);
        col += warm * on * (0.18 + 0.55 * band) * bright;
    }
    // Tall towers carry a blinking light that flashes on the beat.
    if (h1 > 0.72)
    {
        float2 tip = float2(p.x - fx + gap + (pitch - gap * 1.4) * 0.5, roof - 3.0 * U);
        float d = length(p - tip);
        float blink = max(SP.w, 0.25 + 0.25 * sin(Time * 2.0 + h2 * 9.0));
        col += float3(1.0, 0.25, 0.20) * exp(-d / (2.5 * U)) * blink * 0.9 * bright;
    }
    return col;
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 p = PixelToWorld(pos.xy);
    float4 G = EX[0];
    int s = -1;
    float best = 1e9;
    [loop]
    for (int q = 0; q < (int)G.x; q++)
    {
        float4 F = EX[1 + q];
        if (p.x < F.y || p.x >= F.z || F.x < p.y - 1.0) continue;
        if (F.x < best) { best = F.x; s = q; }
    }
    if (s < 0) return Out(float3(0.0, 0.0, 0.0), pos.xy);
    float4 F = EX[1 + s];
    float baseY = F.x + Burn.y, scrH = F.w;

    float3 col = float3(0.0, 0.0, 0.0);
    // Sky glow over the city, fed by the bands above it.
    float t = saturate((p.x - World.x) / World.z);
    float band = BandF(t);
    float up = (baseY - scrH * 0.18 - p.y) / (scrH * 0.30);
    col += Pal(0.05 + 0.4 * t) * 0.10 * band * exp(-max(up, 0.0) * 1.6) * step(p.y, baseY);

    float covered = 0.0;
    float3 back = Layer(p, baseY - scrH * 0.02, scrH, G.y * 0.75, G.z, 11.0, 1.25, 0.45, t, covered);
    if (covered > 0.0) col = back;
    float c2 = 0.0;
    float3 front = Layer(p, baseY, scrH, G.y, G.w, 3.0, 0.85, 1.0, t, c2);
    if (c2 > 0.0) col = front;
    // A hazy ground line.
    col += Pal(0.3) * 0.04 * exp(-abs(p.y - baseY) / (10.0 * U)) * (0.5 + band);
    return Out(Tone(col), pos.xy);
}
";

        protected override void OnInitViz()
        {
            Edge[] all = Setup.Floors;
            float longest = 0f;
            foreach (Edge e in all) longest = Math.Max(longest, e.W);
            var keep = new System.Collections.Generic.List<Edge>();
            foreach (Edge e in all) if (e.W >= longest * 0.12f && keep.Count < MaxFloors) keep.Add(e);
            floors = keep.ToArray();
            scrollA = Rng.Next() * 5000f * U;
            scrollB = Rng.Next() * 5000f * U;
        }

        protected override void OnUpdateViz(FrameContext f)
        {
            float pitch = 64f * U;
            float wrap = pitch * 3f * 1024f;   // a multiple of both pitches (64 and 48): no jump on wrap
            scrollA = (scrollA + f.Dt * 3.5f * U) % wrap;
            scrollB = (scrollB + f.Dt * (7f + 10f * Bass) * U) % wrap;
            SetEx(0, floors.Length, pitch, scrollA, scrollB);
            for (int i = 0; i < floors.Length; i++)
            {
                Edge e = floors[i];
                SetEx(1 + i, e.Y, e.X0, e.X1, Setup.Screens[e.Screen].H);
            }
        }
    }
}
