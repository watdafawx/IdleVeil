// Spectrum (reference scene for the audio pattern): 64 rounded bars with falling peak caps, a
// soft glow and a dim reflection, drawn per pixel from the AudioData constant buffer.
//
// Layout is per screen (each screen gets the whole spectrum; span falls back to each). The
// baseline and margins drift slowly and the palette turns over minutes, so no edge or colour
// stays in one place on an OLED.
namespace IdleVeil
{
    sealed class SpectrumScene : GpuScene
    {
        FullscreenPass pass;
        SceneConstants k;

        public override SceneLayout Layout { get { return SceneLayout.PerScreen; } }

        const string Shader = @"
cbuffer Scene : register(b2)
{
    float Idle;      // 1 when nothing is playing: bars breathe gently instead of lying flat
    float Hue;       // palette rotation, turns slowly
    float2 ScenePad;
};

static const int Bars = 64;

float3 Palette(float h)   // h: 0 bottom .. 1 top of the tallest bar
{
    float3 a = hsv(Hue + 0.62, 0.80, 0.55);      // deep blue-violet
    float3 b = hsv(Hue + 0.50, 0.70, 0.75);      // cyan
    float3 c = hsv(Hue + 0.40, 0.45, 0.85);      // mint
    return h < 0.5 ? lerp(a, b, h * 2.0) : lerp(b, c, h * 2.0 - 1.0);
}

float Level(int i)
{
    float v = BandAt(i);
    float breathe = 0.035 + 0.025 * sin(Time * 0.8 + i * 0.37) + 0.015 * sin(Time * 1.9 - i * 0.11);
    return max(v * 0.96, breathe * Idle);
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 w = PixelToWorld(pos.xy);
    float2 p = w - View.xy;                      // this screen, world units
    float vw = View.z, vh = View.w, px = PixelWorld;
    float margin = vw * 0.09 + Burn.x * 1.5;
    float baseY = vh * 0.70 + Burn.y * 2.0;
    float maxH = vh * 0.46;
    float pitch = (vw - margin * 2.0) / Bars;
    float barW = pitch * 0.62;
    float radius = barW * 0.5;

    float3 col = float3(0.0, 0.0, 0.0);
    // Faint floor glow that swells with the bass.
    float gx = (p.x - vw * 0.5) / (vw * 0.45), gy = (p.y - baseY) / (vh * 0.30);
    col += Palette(0.2) * 0.022 * exp(-(gx * gx + gy * gy)) * (0.6 + 0.8 * Audio.x);

    float fi = (p.x - margin) / pitch;
    int i = (int)floor(fi);
    if (i >= -1 && i <= Bars)
    {
        // Check this bar and its neighbours so glows don't cut off at column edges.
        for (int j = i - 1; j <= i + 1; j++)
        {
            if (j < 0 || j >= Bars) continue;
            float lvl = Level(j);
            float h = max(lvl * maxH, barW);           // never thinner than a dot
            float cx = margin + (j + 0.5) * pitch;
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
            float pk = max(PeakAt(j) * 0.96, lvl);
            float py = baseY - max(pk * maxH, barW) - 7.0 * U;
            float dc = sdRoundBox(p - float2(cx, py), float2(barW * 0.5, 2.2 * U), 2.2 * U);
            col += Palette(1.0) * Coverage(dc, px) * 0.55 * saturate(pk * 6.0);
            // Reflection under the baseline: the same bar, mirrored, dim and fading out.
            float2 rp = float2(p.x, 2.0 * baseY - p.y + 6.0 * U);
            float dr = sdRoundBox(rp - c, float2(barW * 0.5, h * 0.5), radius);
            float fadeR = exp(-max(p.y - baseY, 0.0) / (vh * 0.06));
            if (p.y > baseY) col += Palette(saturate((baseY - rp.y) / maxH)) * Coverage(dr, px) * 0.13 * fadeR;
        }
    }
    return Out(col, pos.xy);
}
";

        protected override void OnInit()
        {
            pass = new FullscreenPass(Gpu, Shader);
            k = new SceneConstants(Gpu, 4);
            k[1] = Rng.Next();
        }

        public override void Update(FrameContext f)
        {
            k[0] = f.Audio.Playing ? 0f : 1f;
            k[1] = (k[1] + f.Dt / 600f) % 1f;   // a full turn of the palette every 10 minutes
        }

        public override void Render(RenderView view)
        {
            k.Bind();
            pass.Draw();
        }
    }
}
