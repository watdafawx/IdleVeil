// Aurora (reference scene for the full-screen shader pattern): green/violet curtains over a dark,
// starry sky, computed per pixel at native resolution. Nothing is uploaded per frame except the
// shared Frame constants: all motion comes from Time.
//
// Coordinates: x runs across the whole world (curtains continue from one monitor to the next in
// span mode), y is measured from the top of each screen in design units, so every screen has its
// own curtain at the same physical height.
namespace IdleVeil
{
    sealed class AuroraScene : GpuScene
    {
        FullscreenPass pass;

        const string Shader = @"
float3 Stars(float2 w)
{
    // One candidate star per 34-unit cell, drifting slowly to the right (the sky turns).
    float cell = 34.0 * U;
    float2 p = w + float2(Time * 1.6 * U, 0.0);
    float2 id = floor(p / cell);
    float2 h = hash22(id + Seed * 91.0);
    if (h.x > 0.16) return 0;
    float2 c = (id + 0.15 + 0.7 * hash22(id + 7.3)) * cell;
    float d = length(p - c) / PixelWorld;              // pixels
    float size = 0.55 + 0.9 * h.y;
    float tw = 0.65 + 0.35 * sin(Time * (0.7 + 2.0 * h.x * 6.0) + h.y * 40.0);
    float k = exp(-d * d / (size * size)) * tw * (0.10 + 0.40 * h.y);
    return k * lerp(float3(0.75, 0.82, 1.0), float3(1.0, 0.92, 0.82), h.x * 6.0);
}

// One curtain layer. xd: design-unit x (1 = one 1080p screen height), yd: design-unit y from the
// top of the screen.
float3 Curtain(float xd, float yd, float layer)
{
    float t = Time;
    float x = xd * (0.85 + 0.3 * layer) + layer * 4.1 + Seed * 13.0;
    // The curtain's lower edge: long slow swells plus a smaller wandering ripple.
    float base = 0.50 + 0.07 * layer
               + 0.075 * sin(x * 1.15 + t * (0.031 + 0.011 * layer))
               + 0.10 * (fbm(float2(x * 0.7 - t * 0.012, layer * 3.7), 3) - 0.5);
    // Small hem ripples, so the lower edge folds like cloth instead of a smooth line.
    base += 0.016 * sin(x * 7.0 + t * 0.21 + layer * 2.0) + 0.008 * sin(x * 15.0 - t * 0.33 + layer);
    float d = yd - base;                        // > 0 below the edge
    // Rays: thin streaks across x (bent by a slow fold), whose brightness also varies along
    // their length, so they read as shimmering filaments rather than solid columns.
    float fold = fbm(float2(x * 1.4 + t * 0.018, t * 0.025 + layer * 2.0), 3);
    float rx = (x + fold * 0.9) * 55.0;
    float r1 = noise(float2(rx, d * 0.6 + t * 0.14 + layer * 9.0));
    float r2 = noise(float2(rx * 2.4 + 17.0, d * 1.2 - t * 0.21 + layer * 5.0));
    float rays = saturate((r1 * 0.65 + r2 * 0.35) * 1.7 - 0.45);
    rays = 0.22 + 1.9 * rays * rays;
    // Where along the sky the curtain is present at all.
    float presence = saturate(0.35 + 0.9 * (fbm(float2(x * 0.33 + t * 0.006, 40.0 + layer), 3) - 0.35) * 2.0);
    float below = exp(-d * d / 0.0011);         // crisp-ish lower hem
    float above = exp(d / (0.17 + 0.07 * layer)) * (1.0 - exp(d * 40.0)); // long soft fade upwards
    float shape = d > 0 ? below : max(above, exp(-d * d / 0.0011));
    float k = shape * rays * presence;
    // Colour: green at the hem, teal then violet higher up; each layer leans a bit differently.
    float up = saturate(-d * 3.2);
    float3 hem = lerp(float3(0.10, 0.95, 0.45), float3(0.20, 0.85, 0.70), layer * 0.5);
    float3 top = lerp(float3(0.45, 0.25, 0.95), float3(0.85, 0.25, 0.65), layer * 0.5);
    float3 c = lerp(hem, top, up * up);
    return c * k;
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 w = PixelToWorld(pos.xy);
    float unit = 1080.0 * U;
    float xd = (w.x - World.x + Burn.x) / unit;
    float yd = (w.y - View.y + Burn.y) / unit;
    // Night sky: near-black at the zenith, a faint blue haze towards the bottom.
    float3 col = lerp(float3(0.004, 0.006, 0.016), float3(0.012, 0.018, 0.034), saturate(yd));
    col += Stars(w);
    float3 aur = Curtain(xd, yd, 0.0) * 0.58 + Curtain(xd, yd, 1.0) * 0.36 + Curtain(xd, yd, 2.0) * 0.22;
    // Soft shoulder instead of a hard clip: bright spots roll off (peaks stay under ~0.55).
    aur = aur / (1.0 + aur * 1.2);
    col += aur;
    return Out(col, pos.xy);
}
";

        protected override void OnInit() { pass = new FullscreenPass(Gpu, Shader); }

        public override void Render(RenderView view) { pass.Draw(); }
    }
}
