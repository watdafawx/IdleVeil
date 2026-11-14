// LED clock: a 25 x 9 dot-matrix display drawn per pixel (round dots with a hot centre and a soft
// halo, unlit dots just visible), 5 x 7 digits, a colon, and a seconds bar along the bottom row.
// When the minute changes every dot switches at its own moment, which looks like real LEDs.
// Amber, because that is what a clock like this looks like; the hue drifts a little.
namespace IdleVeil
{
    sealed class ClockLedScene : ClockScene
    {
        const int Cols = 25, Rows = 9;
        const float PitchU = 38f;   // dot pitch in design units
        FullscreenPass pass;
        SceneConstants k;

        protected override float ExtentW { get { return Cols * PitchU * U; } }
        protected override float ExtentH { get { return Rows * PitchU * U; } }

        const string Shader = @"
cbuffer Scene : register(b2)
{
    float4 CA;   // x centre x, y centre y, z pitch, w hue
    float4 CB;   // current digits h1 h2 m1 m2
    float4 CC;   // previous digits
    float4 CD;   // x change 0..1, y colon level, z seconds progress 0..1, w mix
};

// 5 x 7 digit font, one row per entry, bit 4 = left column.
static const uint F5[70] = {
    0x0E,0x11,0x11,0x11,0x11,0x11,0x0E,   // 0
    0x04,0x0C,0x04,0x04,0x04,0x04,0x0E,   // 1
    0x0E,0x11,0x01,0x02,0x04,0x08,0x1F,   // 2
    0x1E,0x01,0x01,0x0E,0x01,0x01,0x1E,   // 3
    0x02,0x06,0x0A,0x12,0x1F,0x02,0x02,   // 4
    0x1F,0x10,0x1E,0x01,0x01,0x11,0x0E,   // 5
    0x06,0x08,0x10,0x1E,0x11,0x11,0x0E,   // 6
    0x1F,0x01,0x02,0x04,0x08,0x08,0x08,   // 7
    0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E,   // 8
    0x0E,0x11,0x11,0x0F,0x01,0x02,0x0C    // 9
};

float Bit(float digit, int col, int row)
{
    uint d = (uint)(digit + 0.5);
    return (float)((F5[d * 7 + row] >> (4 - col)) & 1u);
}

// Brightness 0..1 of the dot at (ix, iy); area = 1 if the dot exists on the display at all.
float DotLevel(int ix, int iy, out float area)
{
    area = 0.0;
    if (iy >= 0 && iy < 7)
    {
        int slot = -1, col = 0;
        if (ix >= 0 && ix <= 4) { slot = 0; col = ix; }
        else if (ix >= 6 && ix <= 10) { slot = 1; col = ix - 6; }
        else if (ix >= 14 && ix <= 18) { slot = 2; col = ix - 14; }
        else if (ix >= 20 && ix <= 24) { slot = 3; col = ix - 20; }
        if (slot >= 0)
        {
            area = 1.0;
            float c = Bit(CB[slot], col, iy), p = Bit(CC[slot], col, iy);
            float kd = saturate(CD.x * 1.7 - hash12(float2(ix, iy) + slot * 7.0) * 0.7);
            return lerp(p, c, kd);
        }
        if (ix == 12 && (iy == 2 || iy == 4)) { area = 1.0; return CD.y; }
    }
    if (iy == 8 && ix >= 0 && ix <= 24) { area = 1.0; return (ix + 0.5) / 25.0 <= CD.z ? 0.55 : 0.0; }
    return 0.0;
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 w = PixelToWorld(pos.xy);
    float pitch = CA.z;
    float2 u = (w - CA.xy) / pitch + float2(12.5, 4.5);
    if (u.x < -2.0 || u.x > 27.0 || u.y < -2.0 || u.y > 11.0) return float4(0.0, 0.0, 0.0, 1.0);

    int cx0 = (int)floor(u.x), cy0 = (int)floor(u.y);
    float3 base = hsv(CA.w, 0.92, 1.0);
    float3 warm = hsv(CA.w, 0.40, 1.0);
    float rad = 0.34 * pitch;
    float3 col = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int j = -1; j <= 1; j++)
    {
        [unroll]
        for (int i = -1; i <= 1; i++)
        {
            int ix = cx0 + i, iy = cy0 + j;
            float area;
            float lit = DotLevel(ix, iy, area);
            if (area < 0.5) continue;
            float dist = length((u - float2(ix + 0.5, iy + 0.5)) * pitch);
            float dr = dist - rad;
            float cov = Coverage(dr, PixelWorld);
            col += base * cov * (0.05 + 0.95 * lit) * 0.88;
            col += warm * smoothstep(rad * 0.75, 0.0, dist) * lit * 0.30;
            col += base * exp(-max(dr, 0.0) / (0.22 * pitch)) * lit * 0.30 * (1.0 - cov);
        }
    }
    col *= CD.w * (0.92 + 0.08 * sin(Time * 0.3));
    return Out(col, pos.xy);
}
";

        protected override void OnInitClock()
        {
            pass = new FullscreenPass(Gpu, Shader);
            k = new SceneConstants(Gpu, 16);
        }

        protected override void OnUpdateClock(FrameContext f)
        {
            k[0] = Cx; k[1] = Cy; k[2] = PitchU * U;
            k[3] = 0.075f + HueShift;
            for (int i = 0; i < 4; i++) { k[4 + i] = Cur[i] - '0'; k[8 + i] = Prev[i] - '0'; }
            k[12] = Change;
            float pulse = 0.5f + 0.5f * (float)System.Math.Cos(Seconds * 6.2831853f);
            k[13] = 0.20f + 0.80f * pulse;
            k[14] = Seconds / 60f;
            k[15] = Mix;
        }

        public override void Render(RenderView view)
        {
            if (Mix <= 0.001f) return;
            k.Bind();
            pass.Draw(BlendMode.Add);
        }
    }
}
