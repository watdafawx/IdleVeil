// Snow: soft flakes of three depths drifting down on a gusting wind, with a thin dim drift of snow
// that builds along the bottom of each screen and melts away every few minutes.
//
// Pattern: CPU flakes in world units drawn as SpriteBatch dots (sub-pixel flakes dim instead of
// shimmering) plus one small full-screen pass for the drift. In span mode every monitor is its own
// column: flakes spawn above that monitor's top edge, stay inside its width and land on its own
// bottom, so uneven layouts (different sizes, holes) still snow everywhere and only there.
//
// Burn-in: the drift is capped low (about 8 % of the screen height), dim, fades to black towards
// the bottom edge and melts again, so no bright band ever stays put.
using System;

namespace IdleVeil
{
    sealed class SnowScene : GpuScene
    {
        SpriteBatch sprites;
        FullscreenPass pilePass;
        SceneConstants k;
        GpuTexture[] pileTex;
        float[][] pile;
        float[] binW;
        bool[] melting;

        float[] x, y, vy, size, sway, depth;
        int[] screen;
        float wind;

        const string PileShader = @"
cbuffer Scene : register(b2)
{
    float Bins;      // bins across this screen
    float BinWidth;  // world units per bin
    float2 ScenePad;
};
Texture2D PileTex : register(t1);

float PileHeight(int i)
{
    i = clamp(i, 0, (int)Bins - 1);
    float4 c = PileTex.Load(int3(i, 0, 0));
    return (round(c.r * 255.0) * 256.0 + round(c.g * 255.0)) / 16.0;   // world units
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 w = PixelToWorld(pos.xy);
    float px = PixelWorld;
    float bottom = View.y + View.w;
    float f = (w.x - View.x) / BinWidth - 0.5;
    int i0 = (int)floor(f);
    float h = lerp(PileHeight(i0), PileHeight(i0 + 1), f - i0);
    // Distance below the surface (negative above it), in design units.
    float d = (w.y - (bottom - h)) / U;
    if (d < -14.0 || h < 0.3) return float4(0, 0, 0, 1);

    float edge = saturate(0.5 + d * U / px);                      // anti-aliased surface
    float depthFade = exp(-max(d, 0.0) / 11.0);
    // Bright rim just under the surface, a dim body below it, black towards the screen edge.
    float rim = 0.50 * depthFade + 0.07 * exp(-max(d, 0.0) / 40.0);
    float toBottom = saturate((bottom - w.y) / (h + 1.0));         // 1 at the surface, 0 at the screen edge
    float body = rim * (0.35 + 0.65 * toBottom) * edge;
    // A faint halo above the snow, and slow sparkles on the rim.
    float halo = d < 0.0 ? 0.10 * exp(-d * d / 60.0) : 0.0;
    float sp = hash12(floor(float2(w.x / (3.0 * U), Time * 1.5)) + Seed * 31.0);
    float spark = (sp > 0.992 && d > 0.0 && d < 5.0) ? 0.25 : 0.0;
    float edgeFade = saturate((w.x - View.x) / (30.0 * U)) * saturate((View.x + View.z - w.x) / (30.0 * U));
    float3 col = float3(0.80, 0.87, 0.98) * ((body + halo * saturate(h / (4.0 * U))) * edgeFade + spark * edgeFade * toBottom);
    return Out(col, pos.xy);
}
";

        protected override void OnInit()
        {
            RectF[] sc = Setup.Screens;
            pile = new float[sc.Length][];
            pileTex = new GpuTexture[sc.Length];
            binW = new float[sc.Length];
            melting = new bool[sc.Length];
            for (int s = 0; s < sc.Length; s++)
            {
                binW[s] = 4f * U;
                int nb = Math.Max(4, (int)Math.Ceiling(sc[s].W / binW[s]));
                pile[s] = new float[nb];
                for (int i = 0; i < nb; i++) pile[s][i] = (2f + 2f * (float)Math.Sin(i * 0.09f + s) + 1f * (float)Math.Sin(i * 0.31f)) * U;
                pileTex[s] = new GpuTexture(Gpu, nb, 1);
                UploadPile(s);
            }
            pilePass = new FullscreenPass(Gpu, PileShader);
            k = new SceneConstants(Gpu, 4);

            // About 420 flakes per 1080p screen, split between the screens by area.
            int total = 0;
            int[] per = new int[sc.Length];
            for (int s = 0; s < sc.Length; s++)
            {
                per[s] = Math.Max(60, Math.Min(900, (int)(420f * sc[s].W * sc[s].H / (1920f * 1080f))));
                total += per[s];
            }
            x = new float[total]; y = new float[total]; vy = new float[total];
            size = new float[total]; sway = new float[total]; depth = new float[total]; screen = new int[total];
            int n = 0;
            for (int s = 0; s < sc.Length; s++)
                for (int i = 0; i < per[s]; i++, n++)
                {
                    screen[n] = s;
                    Respawn(n, true);
                }
            sprites = new SpriteBatch(Gpu, total);
        }

        void Respawn(int i, bool anywhere)
        {
            RectF s = Setup.Screens[screen[i]];
            x[i] = s.X + Rng.Next() * s.W;
            y[i] = anywhere ? s.Y + Rng.Next() * s.H : s.Y - (4f + Rng.Next() * 60f) * U;
            float d = Rng.Next();
            depth[i] = d;
            vy[i] = (38f + 92f * d + 14f * Rng.Next()) * U;
            size[i] = (0.75f + 2.9f * d * d) * U;
            sway[i] = Rng.Next() * 6.2831853f;
        }

        float PileAt(int s, float px)
        {
            float[] p = pile[s];
            int b = (int)((px - Setup.Screens[s].X) / binW[s]);
            return p[b < 0 ? 0 : b >= p.Length ? p.Length - 1 : b];
        }

        void UploadPile(int s)
        {
            float[] p = pile[s];
            int[] px = pileTex[s].Pixels;
            for (int i = 0; i < p.Length; i++)
            {
                int v = (int)(Math.Max(0f, p[i]) * 16f);
                if (v > 65535) v = 65535;
                px[i] = (255 << 24) | ((v >> 8) << 16) | ((v & 255) << 8);
            }
            pileTex[s].Upload();
        }

        public override void Update(FrameContext f)
        {
            float dt = f.Dt, t = f.Time;
            // Gusts: two slow waves of different periods.
            wind = (16f * (float)Math.Sin(t * 0.043f + 1f) + 9f * (float)Math.Sin(t * 0.121f + 2.4f)) * U;

            // The pile: grow, slump into neighbours, melt when it gets too deep.
            for (int s = 0; s < pile.Length; s++)
            {
                float[] p = pile[s];
                float screenH = Setup.Screens[s].H;
                float cap = screenH * 0.085f, melt = screenH * 0.055f;
                float sum = 0f;
                for (int i = 0; i < p.Length; i++) sum += p[i];
                float mean = sum / p.Length;
                if (mean > melt) melting[s] = true;
                if (melting[s])
                {
                    for (int i = 0; i < p.Length; i++)
                        p[i] = Math.Max(0f, p[i] - dt * (4f + 5f * (0.5f + 0.5f * (float)Math.Sin(i * 0.17f + t * 0.3f))) * U);
                    if (mean < 1.5f * U) melting[s] = false;
                }
                for (int i = 1; i < p.Length - 1; i++)
                {
                    float avg = (p[i - 1] + p[i + 1]) * 0.5f;
                    if (p[i] > avg + 2f * U)
                    {
                        float d = (p[i] - avg) * 0.2f;
                        p[i] -= d; p[i - 1] += d * 0.5f; p[i + 1] += d * 0.5f;
                    }
                }
                ClampPile(p, cap);
            }

            sprites.Clear();
            for (int i = 0; i < x.Length; i++)
            {
                RectF s = Setup.Screens[screen[i]];
                float d = depth[i];
                y[i] += vy[i] * dt;
                x[i] += (((float)Math.Sin(t * (0.45f + 0.5f * d) + sway[i]) * (12f + 14f * d)) * U + wind * (0.35f + 0.65f * d)) * dt;
                // Stay inside this monitor's column.
                float m = 14f * U;
                if (x[i] < s.X - m) x[i] += s.W + 2f * m; else if (x[i] > s.Right + m) x[i] -= s.W + 2f * m;

                if (y[i] > s.Bottom - PileAt(screen[i], x[i]) - size[i] * 0.3f)
                {
                    if (!melting[screen[i]]) Deposit(screen[i], x[i], size[i]);
                    Respawn(i, false);
                    continue;
                }
                if (y[i] < s.Y - 20f * U) continue;

                // Fade at the sides of the column, so the wrap around is never seen.
                float edge = Math.Min(Math.Min(x[i] - s.X, s.Right - x[i]) / (24f * U) + 0.2f, 1f);
                if (edge <= 0f) continue;
                float a = (0.38f + 0.55f * d) * edge;
                float glow = size[i] * (2.4f + 1.6f * d);
                // A short streak up the flake's path, so the fall reads even in a still frame.
                float streak = vy[i] * 0.05f;
                sprites.Add(x[i], y[i] - streak, x[i], y[i], size[i] * 0.75f, glow, 0.16f + 0.16f * d, 0.85f, 0.82f, 0.90f, 1.0f, a);
            }
            for (int s = 0; s < pile.Length; s++) UploadPile(s);
        }

        // Snow that lands is spread over a few bins so the surface stays smooth.
        void Deposit(int s, float px, float flake)
        {
            float[] p = pile[s];
            int b = (int)((px - Setup.Screens[s].X) / binW[s]);
            float add = flake * 1.7f;
            for (int o = -2; o <= 2; o++)
            {
                int j = b + o;
                if (j < 0 || j >= p.Length) continue;
                p[j] += add * (o == 0 ? 0.4f : Math.Abs(o) == 1 ? 0.25f : 0.05f);
            }
        }

        static void ClampPile(float[] p, float cap)
        {
            for (int i = 0; i < p.Length; i++) if (p[i] > cap) p[i] = cap;
        }

        int ScreenIndex(RenderView v)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < Setup.Screens.Length; i++)
            {
                RectF r = Setup.Screens[i];
                float dx = r.CenterX - v.View.CenterX, dy = r.CenterY - v.View.CenterY;
                float d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        public override void Render(RenderView view)
        {
            int s = ScreenIndex(view);
            pileTex[s].Bind(1);
            k[0] = pile[s].Length;
            k[1] = binW[s];
            k.Bind();
            pilePass.Draw(BlendMode.Add);
            sprites.Draw(BlendMode.Add);
        }
    }
}
