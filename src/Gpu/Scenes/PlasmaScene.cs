// Plasma: slow, domain-warped sine fields with deep dark valleys and a hue that turns over
// minutes. Pure per-pixel shader at native resolution (nothing uploaded per frame), so the
// gradients are smooth at any size. The field is continuous in world coordinates, so in span
// mode one plasma flows across every monitor.
//
// Burn-in: the average level is low (valleys are black, crests peak near 0.4), the pattern never
// stops moving and the palette keeps rotating.
namespace IdleVeil
{
    sealed class PlasmaScene : GpuScene
    {
        FullscreenPass pass;

        const string Shader = @"
float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 w = PixelToWorld(pos.xy);
    // Design units (1 = one pixel of a 1080p screen), measured from the world origin.
    // (x1.5: about three bands across a 1080p screen, so the pattern has some structure.)
    float2 p = ((w - World.xy) / U + Seed * 400.0) * 1.5;
    float2 mid = (World.zw * 0.5 / U + Seed * 400.0) * 1.5;
    float t = Time * 0.55;

    // Domain warp: bend the sampling point with two slow low-frequency waves, so the bands
    // fold and swirl instead of staying a plain grid of sines.
    float2 q = p + 55.0 * float2(sin(p.y * 0.0085 + t * 0.45 + Seed * 9.0),
                                 cos(p.x * 0.0075 - t * 0.38 + Seed * 5.0));

    float v = sin(q.x * 0.0064 + t)
            + sin(q.y * 0.0079 + t * 1.3)
            + sin((q.x + q.y) * 0.0043 + t * 0.7)
            + sin(length(q - mid) * 0.0105 - t * 1.6);
    v *= 0.25;                                   // -1..1

    // A second, independent field drives the colour, so brightness and hue don't move together.
    float u = sin(q.x * 0.0031 - t * 0.31) + sin(q.y * 0.0047 + t * 0.23) + sin((q.x - q.y) * 0.0029 + t * 0.17);
    u *= 0.3333;

    float hue = 0.58 + 0.16 * v + 0.14 * u + Time / 600.0;          // a full turn every 10 minutes
    float sat = 0.72 - 0.12 * u;
    // Mostly dark: a squared ridge of the field. The soft shoulder keeps the crests under ~0.5.
    float ridge = saturate(0.5 + 0.62 * v);
    float b = ridge * ridge * (1.15 - 0.3 * u);
    b = b / (1.0 + b * 0.55) * 0.68;
    // Fine, slow moire on top of the bands gives the otherwise smooth field some texture.
    float fine = 0.5 + 0.5 * sin(q.x * 0.021 + q.y * 0.017 + t * 2.1 + 3.0 * v);
    b *= 0.82 + 0.18 * fine;

    float3 col = hsv(hue, sat, b);
    col += float3(0.0015, 0.002, 0.004);        // keep the black from clipping flat
    return Out(col, pos.xy);
}
";

        protected override void OnInit() { pass = new FullscreenPass(Gpu, Shader); }

        public override void Render(RenderView view) { pass.Draw(); }
    }
}
