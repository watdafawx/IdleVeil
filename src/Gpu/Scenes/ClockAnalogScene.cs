// Analog clock: a thin ring, 60 ticks (hour and quarter ticks stronger), tapered hour and minute
// hands in ice white and a glowing orange second hand that sweeps smoothly (60 fps, not once a
// second) and drags a short comet along the ring. Drawn per pixel with distance fields, so every
// edge is crisp at any size. The dial drifts like the other clocks.
namespace IdleVeil
{
    sealed class ClockAnalogScene : ClockScene
    {
        protected override float AmbientHue { get { return 0.58f; } }
        const float RadiusU = 370f;
        FullscreenPass pass;
        SceneConstants k;

        protected override float ExtentW { get { return RadiusU * 2.1f * U; } }
        protected override float ExtentH { get { return RadiusU * 2.1f * U; } }

        const string Shader = @"
cbuffer Scene : register(b2)
{
    float4 CA;   // x centre x, y centre y, z radius, w hue
    float4 CB;   // x hour angle, y minute angle, z second angle (radians clockwise from 12), w mix
};

float sdTaper(float2 p, float2 a, float2 b, float wa, float wb)
{
    float2 pa = p - a, ba = b - a;
    float h = saturate(dot(pa, ba) / dot(ba, ba));
    return length(pa - ba * h) - lerp(wa, wb, h);
}

float3 HandDir(float a) { return float3(sin(a), -cos(a), 0.0); }

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 w = PixelToWorld(pos.xy);
    float px = PixelWorld;
    float2 v = w - CA.xy;
    float R = CA.z;
    float r = length(v);
    if (r > R * 1.2) return float4(0.0, 0.0, 0.0, 1.0);

    float3 ice = hsv(CA.w, 0.16, 1.0);
    float3 hot = hsv(0.03, 0.85, 1.0);
    float3 col = float3(0.0, 0.0, 0.0);
    float ang = atan2(v.x, -v.y);
    if (ang < 0.0) ang += TAU;

    // Ring.
    col += ice * Coverage(abs(r - R) - 0.8 * U, px) * 0.30;
    col += ice * exp(-abs(r - R) / (6.0 * U)) * 0.05;

    // Ticks: fold the angle onto the nearest of 60.
    float step60 = TAU / 60.0;
    float m = floor(ang / step60 + 0.5);
    float dev = ang - m * step60;
    float tg = r * sin(dev), rd = r * cos(dev);
    float mi = fmod(m, 60.0);
    bool isHour = fmod(mi, 5.0) < 0.5;
    bool isQuarter = fmod(mi, 15.0) < 0.5;
    float len = isQuarter ? 0.17 : (isHour ? 0.11 : 0.045);
    float hw = (isQuarter ? 3.2 : (isHour ? 2.2 : 0.9)) * U;
    float outer = R * 0.965, inner = outer - R * len;
    float dT = sdRoundBox(float2(tg, rd - (inner + outer) * 0.5), float2(hw, (outer - inner) * 0.5), hw);
    float tb = isQuarter ? 0.95 : (isHour ? 0.70 : 0.26);
    col += ice * Coverage(dT, px) * tb;
    col += ice * exp(-max(dT, 0.0) / (5.0 * U)) * tb * 0.10;

    // Hands.
    float2 hd = HandDir(CB.x).xy, md = HandDir(CB.y).xy, sd = HandDir(CB.z).xy;
    float dh = sdTaper(v, -hd * R * 0.10, hd * R * 0.52, 6.0 * U, 3.2 * U);
    float dm = sdTaper(v, -md * R * 0.12, md * R * 0.80, 4.2 * U, 2.0 * U);
    float ds = sdTaper(v, -sd * R * 0.20, sd * R * 0.90, 1.5 * U, 0.9 * U);
    col += lerp(ice, float3(1.0, 1.0, 1.0), 0.5) * (Coverage(dh, px) * 0.90 + exp(-max(dh, 0.0) / (8.0 * U)) * 0.10);
    col += ice * (Coverage(dm, px) * 0.85 + exp(-max(dm, 0.0) / (8.0 * U)) * 0.09);
    col += hot * (Coverage(ds, px) * 0.95 + exp(-max(ds, 0.0) / (10.0 * U)) * 0.22);

    // Comet behind the second hand, along the ring.
    float da = fmod(CB.z - ang + TAU, TAU);
    float tail = pow(saturate(1.0 - da / 0.6), 3.0);
    col += hot * tail * exp(-abs(r - R * 0.965) / (4.0 * U)) * 0.45;

    // Hub.
    col = lerp(col, lerp(ice, float3(1.0, 1.0, 1.0), 0.6) * 0.9, Coverage(r - 7.0 * U, px));
    col = lerp(col, hot * 0.95, Coverage(r - 3.2 * U, px));
    return Out(col * CB.w, pos.xy);
}
";

        protected override void OnInitClock()
        {
            pass = new FullscreenPass(Gpu, Shader);
            k = new SceneConstants(Gpu, 8);
        }

        protected override void OnUpdateClock(FrameContext f)
        {
            const float tau = 6.2831853f;
            float hours = (Hour % 12) + Minute / 60f + Seconds / 3600f;
            k[0] = Cx; k[1] = Cy; k[2] = RadiusU * U;
            k[3] = 0.58f + HueShift;
            k[4] = hours / 12f * tau;
            k[5] = (Minute + Seconds / 60f) / 60f * tau;
            k[6] = Seconds / 60f * tau;
            k[7] = Mix;
        }

        protected override void RenderClock(RenderView view)
        {
            if (Mix <= 0.001f) return;
            k.Bind();
            pass.Draw(BlendMode.Add);
        }
    }
}
