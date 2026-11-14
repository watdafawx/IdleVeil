// Lissajous: the waveform plotted against a delayed copy of itself, like an XY oscilloscope. A pure
// tone draws an ellipse, chords draw knots, noise draws a glowing ball of string. The figure turns
// slowly, breathes with the bass, and leaves a dimmer echo trace behind it.
//
// Span: centred on Focus (the largest hole-free rect, so never cut by a hole), sized by its shorter
// side; the drawing is sprite segments in world units, so on a layout where the figure crosses a
// bezel it simply continues on the next screen. The centre drifts with Burn and the figure never
// holds still.
using System;

namespace IdleVeil
{
    sealed class LissajousScene : VizScene
    {
        const int N = 512, Delay = 23;
        SpriteBatch sprites;
        float gain = 1.5f, angle;

        protected override string Shader { get { return Body; } }

        // A faint halo behind the figure.
        const string Body = @"
float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 p = PixelToWorld(pos.xy);
    float4 C = EX[0];   // centre x, y, radius
    float d = length(p - C.xy) / C.z;
    float3 col = Pal(0.2) * exp(-d * d * 2.2) * (0.025 + 0.06 * SP.z + 0.05 * SP.w);
    return Out(col, pos.xy);
}
";

        protected override void OnInitViz()
        {
            sprites = new SpriteBatch(Gpu, 2 * N + 8);
            angle = Rng.Next() * 6.2831853f;
        }

        static void Hsv(float h, float s, float v, out float r, out float g, out float b)
        {
            h -= (float)Math.Floor(h);
            r = v * (1f - s + s * Sat(Math.Abs(h * 6f - 3f) - 1f));
            g = v * (1f - s + s * Sat(2f - Math.Abs(h * 6f - 2f)));
            b = v * (1f - s + s * Sat(2f - Math.Abs(h * 6f - 4f)));
        }

        static float Sat(float v) { return v < 0f ? 0f : v > 1f ? 1f : v; }

        void Trace(float[] w, float cx, float cy, float rad, float ca, float sa, float width, float glowAmp,
                   float hue, float alpha)
        {
            float px = 0f, py = 0f;
            for (int i = 0; i < N - Delay; i++)
            {
                float a = w[i] * gain, b = w[i + Delay] * gain;
                // 45 degrees: a mono signal lies on the diagonal of a classic XY scope; turned.
                float u = (a - b) * 0.7071f, v = (a + b) * 0.7071f;
                float x = cx + (u * ca - v * sa) * rad, y = cy + (u * sa + v * ca) * rad;
                if (i > 0)
                {
                    float r, g, bb;
                    Hsv(hue + 0.30f * (i / (float)(N - Delay)), 0.6f, 1f, out r, out g, out bb);
                    sprites.Add(px, py, x, y, width, 6f * U, glowAmp, 0f, r, g, bb, alpha);
                }
                px = x; py = y;
            }
        }

        protected override void OnUpdateViz(FrameContext f)
        {
            float dt = f.Dt;
            // Auto gain: the louder half of the trace fills about 80 % of the radius.
            float peak = 0f;
            for (int i = 0; i < N; i++) peak = Math.Max(peak, Math.Abs(Wave[i]));
            float want = 0.8f / Math.Max(0.12f, peak);
            gain += (want - gain) * (1f - (float)Math.Exp(-dt * 1.2f));
            angle += dt * (0.05f + 0.25f * Energy);

            RectF fo = Setup.Focus;
            float cx = fo.CenterX + f.BurnX, cy = fo.CenterY + f.BurnY;
            float rad = Math.Min(fo.W, fo.H) * 0.40f * (1f + 0.06f * Bass + 0.05f * Beat);
            SetEx(0, cx, cy, rad, 0f);

            sprites.Clear();
            float ca = (float)Math.Cos(angle), sa = (float)Math.Sin(angle);
            float ce = (float)Math.Cos(angle - 0.12f), se = (float)Math.Sin(angle - 0.12f);
            Trace(Echo, cx, cy, rad * 1.04f, ce, se, 1.2f * U, 0.10f, Hue + 0.25f, 0.22f);
            Trace(Wave, cx, cy, rad, ca, sa, (1.1f + 0.8f * Beat) * U, 0.22f + 0.3f * Beat, Hue, 0.75f);
        }

        public override void Render(RenderView view)
        {
            base.Render(view);
            sprites.Draw(BlendMode.Add);
        }
    }
}
