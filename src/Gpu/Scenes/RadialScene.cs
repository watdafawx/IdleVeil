// Radial: 96 rounded spokes around a ring (the spectrum mirrored left/right, bass at the bottom),
// each with a falling peak dot and a soft halo. The ring swells with the bass, turns very slowly
// and wanders with Burn, so no spoke keeps its place. A thin inner ring flashes on the kick.
namespace IdleVeil
{
    sealed class RadialScene : VizScene
    {
        // Centred on the whole world, so in span mode one big composition crosses every screen.
        public override SceneLayout Layout { get { return SceneLayout.World; } }

        protected override string Shader { get { return Body; } }

        const string Body = @"
static const int N = 96;

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 p = PixelToWorld(pos.xy) - World.xy;
    float vw = World.z, vh = World.w, px = PixelWorld;
    float2 c = float2(vw * 0.5, vh * 0.5) + Burn * 2.0;
    float2 v = p - c;
    float r = length(v);
    float r0 = vh * 0.19 * (1.0 + 0.10 * SP.z + 0.05 * SP.w);
    float lenMax = vh * 0.27;
    float pitch = TAU * r0 / N;
    float hw = pitch * 0.30;
    float rot = SQ.y * 0.012;
    float turns = atan2(v.x, -v.y) / TAU - rot;
    int i0 = (int)floor(frac(turns) * N);

    float3 col = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int k = -1; k <= 1; k++)
    {
        int j = i0 + k;
        float phi = ((j + 0.5) / N + rot) * TAU;
        float2 dir = float2(sin(phi), -cos(phi));
        float2 perp = float2(cos(phi), sin(phi));
        float rad = dot(v, dir), tg = dot(v, perp);
        float jj = fmod(j + N, N);
        float tt = abs(2.0 * (jj + 0.5) / N - 1.0);          // 0 at the bottom (bass), 1 at the top
        float L = BandF(tt);
        float len = max(L * lenMax, 3.0 * U);
        float d = sdRoundBox(float2(tg, rad - (r0 + len * 0.5)), float2(hw, len * 0.5), hw);
        float cov = Coverage(d, px);
        float3 tone = Pal(tt);
        float hh = saturate((rad - r0) / lenMax);
        col += tone * cov * (0.40 + 0.60 * hh) * 0.85;
        col += tone * exp(-max(d, 0.0) / (9.0 * U)) * 0.16 * (0.3 + L) * (1.0 - cov);
        // Falling peak dot.
        float pk = max(PeakF(tt), L);
        float dp = length(float2(tg, rad - (r0 + pk * lenMax + 9.0 * U))) - 2.0 * U;
        col += Hot(tone, 0.7) * Coverage(dp, px) * 0.8 * saturate(pk * 5.0);
    }

    // Inner ring and a soft core.
    float rin = r0 - 16.0 * U;
    col += Pal(0.2) * (Coverage(abs(r - rin) - 0.9 * U, px) * (0.20 + 0.50 * SP.w) + exp(-abs(r - rin) / (10.0 * U)) * 0.05);
    col += Pal(0.1) * exp(-r * r / (r0 * r0 * 0.35)) * (0.03 + 0.15 * SP.z);
    return Out(Tone(col), pos.xy);
}
";
    }
}
