// Oscilloscope (line): the waveform as one thin, white-hot beam with a coloured halo and a slower
// echo trace behind it. The trace is triggered (see VizScene), so a steady tone stands still.
// Ends fade out, the centre line wanders with Burn, and with no music the trace becomes a slow
// swell instead of a flat line.
namespace IdleVeil
{
    sealed class ScopeScene : VizScene
    {
        protected override string Shader { get { return Body; } }

        const string Body = @"
float TraceW(int j, bool echo) { return echo ? EchoN(j) : WaveN(j); }

float2 TracePt(int j, bool echo, float xl, float xr, float cy, float amp)
{
    j = clamp(j, 0, 511);
    float t = j / 511.0;
    float env = smoothstep(0.0, 0.16, t) * smoothstep(1.0, 0.84, t);
    return float2(lerp(xl, xr, t), cy - TraceW(j, echo) * amp * env);
}

// Distance from p to the polyline of the trace (7 segments around p.x are enough for the glow).
float TraceDist(float2 p, bool echo, float xl, float xr, float cy, float amp)
{
    int i = (int)floor((p.x - xl) / (xr - xl) * 511.0);
    float d = 1e9;
    float2 a = TracePt(i - 3, echo, xl, xr, cy, amp);
    [unroll]
    for (int k = -3; k <= 3; k++)
    {
        float2 b = TracePt(i + k + 1, echo, xl, xr, cy, amp);
        d = min(d, sdSegment(p, a, b));
        a = b;
    }
    return d;
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 p = PixelToWorld(pos.xy) - View.xy;
    float vw = View.z, vh = View.w, px = PixelWorld;
    float cy = vh * 0.5 + Burn.y * 2.5 + sin(Time * 0.05) * vh * 0.03;
    float xl = vw * 0.03 + Burn.x * 2.0, xr = vw * 0.97 + Burn.x * 2.0;
    float amp = vh * 0.27;
    float tx = saturate((p.x - xl) / (xr - xl));
    float edge = smoothstep(0.0, 0.10, tx) * smoothstep(1.0, 0.90, tx);

    float d1 = TraceDist(p, false, xl, xr, cy, amp);
    float d2 = TraceDist(p, true, xl, xr, cy, amp);
    float lift = 1.0 + 0.9 * SP.w;

    float3 c1 = Pal(0.05 + tx * 0.45);
    float3 c2 = Pal(0.55 + tx * 0.30);
    float3 col = float3(0.0, 0.0, 0.0);
    // Echo: wide, dim.
    col += c2 * (exp(-d2 / (14.0 * U)) * 0.20 + Coverage(d2 - 1.0 * U, px) * 0.34) * edge;
    // Main beam: coloured halo, white-hot core.
    float g = exp(-d1 / (9.0 * U)) * 0.50 + exp(-d1 / (38.0 * U)) * 0.10;
    col += c1 * g * lift * edge;
    col += Hot(c1, 0.80) * Coverage(d1 - 1.15 * U, px) * edge;
    return Out(Tone(col), pos.xy);
}
";
    }
}
