// Oscilloscope ring: the triggered waveform wrapped around a circle that swells with the bass,
// with a slower echo ring, a breathing inner ring and a shock ring that leaves on every kick.
// The waveform is made periodic by subtracting the straight line between its two ends, and the
// seam turns slowly, so no spot on the ring is special.
namespace IdleVeil
{
    sealed class ScopeRingScene : VizScene
    {
        // Centred on Focus (the largest hole-free part of the span), so one big composition
        // crosses the screens and no hole cuts it. On one screen Focus is the screen.
        protected override string Shader { get { return Body; } }

        const string Body = @"
// Waveform around the circle, a in turns (wraps). Zero at the seam on both sides.
float RingW(float a, bool echo)
{
    a = frac(a);
    float w0 = echo ? EchoF(0.0) : WaveF(0.0);
    float w1 = echo ? EchoF(1.0) : WaveF(1.0);
    float v = echo ? EchoF(a) : WaveF(a);
    return v - lerp(w0, w1, a);
}

float RingR(float a, bool echo, float R0, float amp) { return R0 + RingW(a, echo) * amp; }

// Distance from v (relative to the centre) to the wavy ring, corrected for its slope.
float RingDist(float2 v, float rot, bool echo, float R0, float amp)
{
    float r = length(v);
    float a = atan2(v.x, -v.y) / TAU + rot;
    float e = 1.0 / 256.0;
    float R = RingR(a, echo, R0, amp);
    float dR = (RingR(a + e, echo, R0, amp) - RingR(a - e, echo, R0, amp)) / (2.0 * e * TAU);
    float slope = dR / max(r, 1.0);
    return abs(r - R) / sqrt(1.0 + slope * slope);
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 p = PixelToWorld(pos.xy) - Focus.xy;
    float vw = Focus.z, vh = Focus.w, px = PixelWorld;
    float2 c = float2(vw * 0.5, vh * 0.5) + Burn * 2.0;
    float2 v = p - c;
    float r = length(v);
    float rot = SQ.y * 0.01;
    float R0 = vh * 0.25 * (1.0 + 0.10 * SP.z + 0.07 * SP.w);
    float amp = vh * 0.11;
    float a = frac(atan2(v.x, -v.y) / TAU + rot);
    float tt = abs(2.0 * a - 1.0);

    float d1 = RingDist(v, rot, false, R0, amp);
    float d2 = RingDist(v, rot, true, R0 * 1.0, amp * 0.7);
    float3 c1 = Pal(0.05 + tt * 0.45);
    float3 c2 = Pal(0.55 + tt * 0.30);
    float3 col = float3(0.0, 0.0, 0.0);

    col += c2 * (exp(-d2 / (12.0 * U)) * 0.18 + Coverage(d2 - 1.0 * U, px) * 0.30);
    float g = exp(-d1 / (9.0 * U)) * 0.50 + exp(-d1 / (36.0 * U)) * 0.09;
    col += c1 * g * (1.0 + 0.9 * SP.w);
    col += Hot(c1, 0.80) * Coverage(d1 - 1.15 * U, px);

    // Soft core and a breathing inner ring.
    col += Pal(0.1) * exp(-r * r / (R0 * R0 * 0.5)) * (0.03 + 0.16 * SP.z);
    float rin = R0 * (0.55 + 0.03 * sin(Time * 0.6)) * (1.0 + 0.06 * SP.w);
    col += Pal(0.3) * (Coverage(abs(r - rin) - 0.9 * U, px) * (0.12 + 0.45 * SP.w) + exp(-abs(r - rin) / (8.0 * U)) * 0.04);

    // Shock ring: leaves the main ring on a kick and fades.
    float rad = R0 * (1.05 + 0.95 * (1.0 - SP.w));
    col += Pal(0.2) * exp(-abs(r - rad) / (9.0 * U)) * SP.w * SP.w * 0.45;
    return Out(Tone(col), pos.xy);
}
";
    }
}
