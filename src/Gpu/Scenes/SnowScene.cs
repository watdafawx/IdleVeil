// Snow: soft flakes of three depths drifting down on a gusting wind, with a thin dim drift of snow
// that builds along the floor and melts away every few minutes.
//
// Pattern: CPU flakes in world units drawn as SpriteBatch dots (sub-pixel flakes dim instead of
// shimmering) plus one small full-screen pass per floor for the drift.
//
// Layout: flakes come in above the ceilings (top edges with no screen above, SceneSetup.Ceilings)
// and fall until they land on a floor (bottom edges with no screen below, SceneSetup.Floors). On
// one screen those are its top and bottom. In span mode snow falls straight through stacked
// screens (from the top one into the one below) and only piles up where there really is ground;
// a flake that drifts into a hole or off the layout is replaced by a new one at a ceiling.
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
        Edge[] floors, ceilings;
        GpuTexture[] pileTex;
        float[][] pile;
        float[] binW;
        bool[] melting;
        float ceilingTotal;

        float[] x, y, vy, size, sway, depth;
        float wind;

        const string PileShader = @"
cbuffer Scene : register(b2)
{
    float Bins;      // bins across this floor
    float BinWidth;  // world units per bin
    float FloorX0;   // world x range and y of the floor
    float FloorX1;
    float FloorY;
    float3 ScenePad;
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
    float bottom = FloorY;
    if (w.x < FloorX0 || w.x >= FloorX1 || w.y >= bottom) return float4(0, 0, 0, 0);
    float f = (w.x - FloorX0) / BinWidth - 0.5;
    int i0 = (int)floor(f);
    float h = lerp(PileHeight(i0), PileHeight(i0 + 1), f - i0);
    // Distance below the surface (negative above it), in design units.
    float d = (w.y - (bottom - h)) / U;
    if (d < -14.0 || h < 0.3) return float4(0, 0, 0, 0);

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
    float edgeFade = saturate((w.x - FloorX0) / (30.0 * U)) * saturate((FloorX1 - w.x) / (30.0 * U));
    float3 col = float3(0.80, 0.87, 0.98) * ((body + halo * saturate(h / (4.0 * U))) * edgeFade + spark * edgeFade * toBottom);
    return float4(Dither(max(col, 0.0), pos.xy), 0.0);
}
";

        protected override void OnInit()
        {
            floors = Setup.Floors;
            ceilings = Setup.Ceilings;
            foreach (Edge c in ceilings) ceilingTotal += c.W;
            pile = new float[floors.Length][];
            pileTex = new GpuTexture[floors.Length];
            binW = new float[floors.Length];
            melting = new bool[floors.Length];
            for (int s = 0; s < floors.Length; s++)
            {
                binW[s] = 4f * U;
                int nb = Math.Max(4, (int)Math.Ceiling(floors[s].W / binW[s]));
                pile[s] = new float[nb];
                for (int i = 0; i < nb; i++) pile[s][i] = (2f + 2f * (float)Math.Sin(i * 0.09f + s) + 1f * (float)Math.Sin(i * 0.31f)) * U;
                pileTex[s] = new GpuTexture(Gpu, nb, 1);
                UploadPile(s);
            }
            pilePass = new FullscreenPass(Gpu, PileShader);
            k = new SceneConstants(Gpu, 8);

            // About 420 flakes per 1080p screen of visible area.
            float area = 0f;
            foreach (RectF r in Setup.Screens) area += r.W * r.H;
            int total = Math.Max(60, Math.Min(2400, (int)(420f * area / (1920f * 1080f))));
            x = new float[total]; y = new float[total]; vy = new float[total];
            size = new float[total]; sway = new float[total]; depth = new float[total];
            for (int i = 0; i < total; i++) Respawn(i, true);
            sprites = new SpriteBatch(Gpu, total);
        }

        void Respawn(int i, bool anywhere)
        {
            if (anywhere)
            {
                // Anywhere on the screens, weighted by area.
                float a = 0f;
                foreach (RectF r in Setup.Screens) a += r.W * r.H;
                float pick = Rng.Next() * a;
                RectF s = Setup.Screens[0];
                foreach (RectF r in Setup.Screens) { s = r; pick -= r.W * r.H; if (pick <= 0f) break; }
                x[i] = s.X + Rng.Next() * s.W;
                y[i] = s.Y + Rng.Next() * s.H;
            }
            else
            {
                // Just above a ceiling, weighted by its width: every column gets the same snowfall.
                float pick = Rng.Next() * ceilingTotal;
                Edge c = ceilings[0];
                foreach (Edge e in ceilings) { c = e; pick -= e.W; if (pick <= 0f) break; }
                x[i] = c.X0 + Rng.Next() * c.W;
                y[i] = c.Y - (4f + Rng.Next() * 60f) * U;
            }
            float d = Rng.Next();
            depth[i] = d;
            vy[i] = (38f + 92f * d + 14f * Rng.Next()) * U;
            size[i] = (0.75f + 2.9f * d * d) * U;
            sway[i] = Rng.Next() * 6.2831853f;
        }

        // The floor directly under (px, py) within reach of the flake, or -1.
        int FloorUnder(float px, float py)
        {
            for (int s = 0; s < floors.Length; s++)
            {
                Edge e = floors[s];
                if (px >= e.X0 && px < e.X1 && py <= e.Y + 1f && py > e.Y - Setup.Screens[e.Screen].H * 0.2f) return s;
            }
            return -1;
        }

        float PileAt(int s, float px)
        {
            float[] p = pile[s];
            int b = (int)((px - floors[s].X0) / binW[s]);
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
                float screenH = Setup.Screens[floors[s].Screen].H;
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
            float m = 14f * U;
            for (int i = 0; i < x.Length; i++)
            {
                float d = depth[i];
                y[i] += vy[i] * dt;
                x[i] += (((float)Math.Sin(t * (0.45f + 0.5f * d) + sway[i]) * (12f + 14f * d)) * U + wind * (0.35f + 0.65f * d)) * dt;
                // Off the sides of the layout: come back in on the other side.
                if (x[i] < World.X - m) x[i] += World.W + 2f * m; else if (x[i] > World.Right + m) x[i] -= World.W + 2f * m;

                int fl = FloorUnder(x[i], y[i]);
                if (fl >= 0 && y[i] > floors[fl].Y - PileAt(fl, x[i]) - size[i] * 0.3f)
                {
                    if (!melting[fl]) Deposit(fl, x[i], size[i]);
                    Respawn(i, false);
                    continue;
                }
                bool vis = Setup.Visible(x[i], y[i], 20f * U);
                if (!vis)
                {
                    // Still coming in from above a ceiling: wait. Anywhere else (a hole, below the
                    // layout): gone, a new flake starts at the top.
                    if (y[i] > World.Bottom || Setup.ScreenAt(x[i], y[i] + 80f * U) < 0 && !AboveCeiling(x[i], y[i])) Respawn(i, false);
                    continue;
                }

                float a = 0.38f + 0.55f * d;
                float glow = size[i] * (2.4f + 1.6f * d);
                // A short streak up the flake's path, so the fall reads even in a still frame.
                float streak = vy[i] * 0.05f;
                sprites.Add(x[i], y[i] - streak, x[i], y[i], size[i] * 0.75f, glow, 0.16f + 0.16f * d, 0.85f, 0.82f, 0.90f, 1.0f, a);
            }
            for (int s = 0; s < pile.Length; s++) UploadPile(s);
        }

        // True if (px, py) is in the strip above a ceiling where new flakes come in.
        bool AboveCeiling(float px, float py)
        {
            foreach (Edge c in ceilings)
                if (px >= c.X0 - 30f * U && px < c.X1 + 30f * U && py < c.Y && py > c.Y - 120f * U) return true;
            return false;
        }

        // Snow that lands is spread over a few bins so the surface stays smooth.
        void Deposit(int s, float px, float flake)
        {
            float[] p = pile[s];
            int b = (int)((px - floors[s].X0) / binW[s]);
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

        public override void Render(RenderView view)
        {
            RectF v = view.View;
            for (int s = 0; s < floors.Length; s++)
            {
                Edge e = floors[s];
                if (e.X1 <= v.X || e.X0 >= v.Right || e.Y <= v.Y || e.Y > v.Bottom + 1f) continue;
                pileTex[s].Bind(1);
                k[0] = pile[s].Length; k[1] = binW[s]; k[2] = e.X0; k[3] = e.X1; k[4] = e.Y;
                k.Bind();
                pilePass.Draw(BlendMode.Add);
            }
            sprites.Draw(BlendMode.Add);
        }
    }
}
