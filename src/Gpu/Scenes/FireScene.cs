// Fire: flames rising from the bottom edge of every screen, with a few drifting embers.
//
// Pattern: the classic heat-grid simulation runs on the CPU at a fixed 40 Hz (one small grid per
// screen, 5-unit cells), and the two latest steps are blended into a texture every frame, so the
// motion is smooth at 60 fps. A full-screen shader samples that texture with linear filtering and
// turns heat into colour, adding fine turbulence on top, so the flames are smooth and sharp at
// native resolution instead of showing the coarse cells.
//
// Span: every monitor has its own grid and burns along its own bottom edge (uneven layouts and
// holes are fine). Burn-in: the flames only reach about a third of the screen, the source row is
// modulated by slowly travelling waves (so there is no steady bright base line), the grid is
// shifted by the burn drift, and the background is true black.
using System;

namespace IdleVeil
{
    sealed class FireScene : GpuScene
    {
        sealed class Grid
        {
            public RectF Rect;
            public int W, H, HeatMax;
            public byte[] Cur, Prev;
            public GpuTexture Tex;
        }

        Grid[] grids;
        FullscreenPass pass;
        SceneConstants k;
        SpriteBatch embers;
        float step, cell, margin, simTime;

        // Embers: x, y, vx, vy, age, life, size
        float[] ex, ey, evx, evy, eage, elife, esize;
        int[] escreen;

        const float Cool = 0.44f;
        const float TickRate = 40f;

        const string Shader = @"
cbuffer Scene : register(b2)
{
    float GridW;     // cells
    float GridH;
    float Cell;      // world units per cell
    float Margin;    // extra world units kept on the left and right of the grid
};
Texture2D HeatTex : register(t1);

float3 Flame(float h)
{
    // dark red -> orange -> pale yellow; only the very hottest cells get near white.
    float r = min(1.0, h * 2.1);
    float g = saturate(h * 2.1 - 0.85) * 0.80;
    float b = clamp(h * 3.0 - 2.5, 0.0, 0.45);
    return float3(r, g, b);
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 w = PixelToWorld(pos.xy);
    float bottom = View.y + View.w + 16.0 * U;                 // the source row sits a little below the edge
    float gx = (w.x - (View.x - Margin) + Burn.x) / Cell;
    float gy = (w.y - (bottom - GridH * Cell) + Burn.y) / Cell;
    if (gy < 0.0 || gy >= GridH) return float4(0, 0, 0, 1);
    float2 uv = float2(gx / GridW, gy / GridH);

    // Turbulence: nudge the lookup sideways and upwards with moving noise, so the edges of the
    // flames lick and curl at pixel scale.
    float2 np = float2(w.x / (26.0 * U), w.y / (34.0 * U) + Time * 1.7);
    float n1 = noise(np) - 0.5;
    float n2 = noise(np * 2.3 + float2(5.2, Time * 2.9)) - 0.5;
    uv += float2(n1 * 0.9 + n2 * 0.5, n1 * 0.4) * float2(1.0 / GridW, 1.0 / GridH) * 3.2;

    float h = HeatTex.Sample(SampLinear, uv).r;
    // Contrast: crisp flame edges, deep reds in the cool parts.
    h = saturate((h - 0.05) / 0.95);
    h = h * h * (3.0 - 2.0 * h) * 0.55 + h * 0.45;
    h *= 0.92 + 0.16 * (noise(np * 4.0 + float2(0.0, Time * 4.1)));
    float3 col = Flame(saturate(h)) * 0.58;
    col = col / (1.0 + col * 0.35);
    // The strip below the screen edge is cut off by the screen itself; fade the last few pixels
    // above the grid top so nothing ends in a hard line.
    col *= saturate(gy / 6.0);
    return Out(col, pos.xy);
}
";

        protected override void OnInit()
        {
            cell = 5f * U;
            margin = 24f * U;
            RectF[] sc = Setup.Screens;
            grids = new Grid[sc.Length];
            for (int i = 0; i < sc.Length; i++)
            {
                var g = new Grid { Rect = sc[i] };
                g.W = Math.Max(16, (int)Math.Ceiling((sc[i].W + 2f * margin) / cell));
                g.H = Math.Max(16, (int)Math.Ceiling((sc[i].H * 0.50f + 40f * U) / cell));
                int rows = Math.Max(10, (int)Math.Round(0.40f * sc[i].H / cell));    // target flame height in cells
                g.HeatMax = Math.Max(10, (int)Math.Round(rows * Cool));
                g.Cur = new byte[g.W * g.H];
                g.Prev = new byte[g.W * g.H];
                g.Tex = new GpuTexture(Gpu, g.W, g.H);
                grids[i] = g;
            }
            pass = new FullscreenPass(Gpu, Shader);
            k = new SceneConstants(Gpu, 4);

            float area = 0f;
            foreach (RectF r in sc) area += r.W * r.H;
            int n = Math.Max(24, Math.Min(400, (int)(34f * area / (1920f * 1080f))));
            ex = new float[n]; ey = new float[n]; evx = new float[n]; evy = new float[n];
            eage = new float[n]; elife = new float[n]; esize = new float[n]; escreen = new int[n];
            for (int i = 0; i < n; i++)
            {
                escreen[i] = i % sc.Length;
                SpawnEmber(i);
                eage[i] = Rng.Next() * elife[i];
                ey[i] -= evy[i] * eage[i] * 0.6f;
            }
            embers = new SpriteBatch(Gpu, n);
            for (int i = 0; i < 90; i++) Tick();   // warm up so the first frame already has flames
        }

        void SpawnEmber(int i)
        {
            RectF s = Setup.Screens[escreen[i]];
            ex[i] = s.X + Rng.Next() * s.W;
            ey[i] = s.Bottom - Rng.Next() * 0.10f * s.H;
            evx[i] = (Rng.Next() - 0.5f) * 30f * U;
            evy[i] = -(55f + Rng.Next() * 110f) * U;
            eage[i] = 0f;
            elife[i] = 1.6f + Rng.Next() * 2.4f;
            esize[i] = (0.9f + Rng.Next() * 1.2f) * U;
        }

        // One 40 Hz simulation step of every grid.
        void Tick()
        {
            simTime += 1f / TickRate;
            foreach (Grid g in grids)
            {
                int fw = g.W, fh = g.H;
                byte[] heat = g.Cur;
                // Source row: travelling waves make some stretches burn tall and others low.
                int row = (fh - 1) * fw;
                for (int x = 0; x < fw; x++)
                {
                    float p = 0.5f + 0.27f * (float)Math.Sin(x * 0.045f + simTime * 0.55f)
                                   + 0.18f * (float)Math.Sin(x * 0.0173f - simTime * 0.31f + 2f)
                                   + 0.10f * (float)Math.Sin(x * 0.11f + simTime * 1.3f);
                    p = Math.Max(0.12f, Math.Min(1f, p));
                    int v = (int)(g.HeatMax * p) - 2 + Rng.Int(5);
                    heat[row + x] = (byte)Math.Max(0, Math.Min(g.HeatMax, v));
                }
                for (int x = 0; x < fw; x++)
                    for (int y = 1; y < fh; y++)
                    {
                        int r = Rng.Int(3);
                        int cool = Rng.Next() < Cool ? 1 : 0;
                        int v = heat[y * fw + x] - cool;
                        heat[(y - 1) * fw + ((x + fw + 1 - r) % fw)] = (byte)(v < 0 ? 0 : v);
                    }
            }
        }

        public override void Update(FrameContext f)
        {
            step = Math.Min(step + f.Dt, 0.2f);
            float period = 1f / TickRate;
            while (step >= period)
            {
                step -= period;
                foreach (Grid g in grids) Array.Copy(g.Cur, g.Prev, g.Cur.Length);
                Tick();
            }
            float a = step / period;
            foreach (Grid g in grids)
            {
                int[] px = g.Tex.Pixels;
                float scale = 255f / g.HeatMax;
                byte[] c = g.Cur, p = g.Prev;
                for (int i = 0; i < px.Length; i++)
                {
                    int v = (int)((p[i] + (c[i] - p[i]) * a) * scale + 0.5f);
                    px[i] = (255 << 24) | (v << 16);
                }
                g.Tex.Upload();
            }

            // Embers: little sparks that float up out of the flames and cool as they go.
            embers.Clear();
            float dt = f.Dt;
            for (int i = 0; i < ex.Length; i++)
            {
                eage[i] += dt;
                if (eage[i] >= elife[i]) { SpawnEmber(i); continue; }
                float life = eage[i] / elife[i];
                ex[i] += (evx[i] + (float)Math.Sin(f.Time * 2.3f + i * 1.7f) * 14f * U) * dt;
                ey[i] += evy[i] * dt;
                evy[i] *= 1f - 0.25f * dt;
                float b = (1f - life) * (life < 0.08f ? life / 0.08f : 1f);
                float heat = 1f - life;
                if (b < 0.02f) continue;
                embers.AddDot(ex[i], ey[i], esize[i] * (0.6f + 0.4f * heat), esize[i] * 4.2f, 0.28f,
                              1f, 0.30f + 0.45f * heat * heat, 0.05f + 0.12f * heat * heat * heat, b * 0.8f);
            }
        }

        int GridIndex(RenderView v)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < grids.Length; i++)
            {
                RectF r = grids[i].Rect;
                float dx = r.CenterX - v.View.CenterX, dy = r.CenterY - v.View.CenterY;
                float d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        public override void Render(RenderView view)
        {
            Grid g = grids[GridIndex(view)];
            g.Tex.Bind(1);
            k[0] = g.W; k[1] = g.H; k[2] = cell; k[3] = margin;
            k.Bind();
            pass.Draw(BlendMode.Opaque);
            embers.Draw(BlendMode.Add);
        }
    }
}
