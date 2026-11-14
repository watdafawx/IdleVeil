// Tunnel: thin glowing rings fly outward from a vanishing point that wanders around the screen,
// and 28 rails (each lit by its own part of the spectrum) carry dashes of light along the walls.
// Everything fades into fog near the vanishing point, so the middle stays dark. The kick makes
// the rings flare, the rails and the whole tunnel turn slowly.
namespace IdleVeil
{
    sealed class TunnelScene : VizScene
    {
        protected override string Shader { get { return Body; } }

        const string Body = @"
float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 p = PixelToWorld(pos.xy) - View.xy;
    float vw = View.z, vh = View.w, px = PixelWorld;
    float2 c = float2(vw * 0.5 + sin(Time * 0.071 + Seed * 20.0) * vw * 0.05,
                      vh * 0.5 + sin(Time * 0.053 + Seed * 9.0 + 1.3) * vh * 0.045) + Burn * 2.0;
    float2 v = p - c;
    float r = length(v);
    float k = r / (vh * 0.5);
    float turns = atan2(v.x, -v.y) / TAU + SQ.y * 0.008;

    float z = 0.30 / (k + 0.03);              // depth, infinite at the vanishing point
    float zp = z * 2.2 + SQ.y * 1.3;          // ring phase: rings fly outward
    float fog = smoothstep(0.03, 0.30, k);
    float near = saturate(k);
    float flare = 0.30 + 0.90 * SP.z + 1.20 * SP.w;

    // Rings: constant pixel width at any depth (distance measured through fwidth).
    float fw = max(fwidth(zp), 1e-4);
    float fz = frac(zp);
    float ringPx = min(fz, 1.0 - fz) / fw;
    float ringId = floor(zp + 0.5);
    float hwPx = 0.9 + 1.8 * near;
    float cov = saturate(hwPx + 0.5 - ringPx);
    float glow = exp(-ringPx / (4.0 + 8.0 * near)) * 0.35;
    float3 rc = Pal(frac(ringId * 0.071) * 0.8);
    float3 col = (Hot(rc, 0.55) * cov + rc * glow) * fog * flare;

    // Rails: lit by the spectrum, mirrored left/right, with dashes running outward.
    float nR = 28.0;
    float sa = turns * nR;
    float railPx = abs(frac(sa + 0.5) - 0.5) / nR * TAU * r;
    float railId = floor(sa + 0.5);
    float tt = abs(2.0 * frac(railId / nR) - 1.0);
    float L = BandF(tt);
    float dp = frac(zp * 0.45 + hash11(railId * 3.7));
    float dash = smoothstep(0.0, 0.08, dp) * smoothstep(0.55, 0.47, dp);
    float rcov = saturate(0.9 + 0.5 - railPx);
    float3 tc = Pal(0.1 + tt * 0.6);
    col += (Hot(tc, 0.4) * rcov + tc * exp(-railPx / (3.0 * U)) * 0.25) * dash * fog * (0.06 + 1.1 * L);

    // A faint glow at the vanishing point.
    col += Pal(0.1) * exp(-r / (14.0 * U)) * (0.05 + 0.20 * SP.w);
    return Out(Tone(col), pos.xy);
}
";
    }
}
