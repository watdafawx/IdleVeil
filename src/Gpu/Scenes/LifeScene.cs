// Game of Life: a wrapping grid that runs about nine generations a second. Cells are drawn as
// small rounded squares with a soft halo, born bright cyan and ageing towards violet; a dead cell
// leaves a fading violet ember.
//
// Pattern: the grid is simulated on the CPU, and a small texture (one texel per cell: brightness,
// age, alive) is sampled with SampPoint. The shader draws the rounded cell and its glow per pixel
// at native resolution, so the cells stay crisp at any size. Brightness is eased on the CPU every
// frame, so births and deaths fade smoothly between generations.
//
// Span: one grid over the whole virtual desktop, so gliders travel from one monitor to the next.
// The cells in the holes of an uneven layout are held dead (a wall), so nothing evolves unseen.
//
// Burn-in: cells that stay alive for ~20 s (still lifes) fade and finally die, so nothing sits
// still; patterns are re-seeded when the grid gets sparse; the grid drifts with Burn.
using System;

namespace IdleVeil
{
    sealed class LifeScene : GpuScene
    {
        int gw, gh;
        float cell;
        byte[] alive, next, visible;
        ushort[] age;
        float[] level;
        GpuTexture tex;
        FullscreenPass pass;
        SceneConstants k;
        float step, lowPop, burstClock;
        int visibleCells;

        const float Rate = 9f;          // generations per second
        const int MaxAge = 190;         // generations a cell may stay alive without a break

        const string Shader = @"
cbuffer Scene : register(b2)
{
    float GridW;
    float GridH;
    float Cell;
    float Pad;
};
Texture2D CellTex : register(t1);

float3 CellColor(float4 c)
{
    float age = c.g;
    float live = c.b;
    float fatigue = 1.0 - 0.60 * smoothstep(0.08, 0.95, age);
    float3 a = hsv(0.50 + age * 0.30 + Time / 900.0, 0.62, 0.80 * fatigue);
    float3 d = hsv(0.90 + Time / 900.0, 0.80, 0.42);
    return lerp(d, a, live) * c.r;
}

float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 w = PixelToWorld(pos.xy);
    float2 g = (w - World.xy + Burn) / Cell;
    int2 ic = (int2)floor(g);
    float2 f = g - ic;                                  // 0..1 inside the cell
    float3 col = 0;
    // Halo: the nine cells around this pixel each add a soft gaussian of their light.
    [unroll] for (int j = -1; j <= 1; j++)
    [unroll] for (int i = -1; i <= 1; i++)
    {
        int2 n = ic + int2(i, j);
        if (n.x < 0 || n.y < 0 || n.x >= (int)GridW || n.y >= (int)GridH) continue;
        float4 c = CellTex.Load(int3(n, 0));
        if (c.r < 0.01) continue;
        float2 dv = (f - (float2(i, j) + 0.5)) * Cell;      // world units from that cell's centre
        float k = exp(-dot(dv, dv) / (Cell * Cell * 0.55));
        col += CellColor(c) * k * 0.20;
    }
    // The cell itself: a rounded square that grows in when it is born.
    if (ic.x >= 0 && ic.y >= 0 && ic.x < (int)GridW && ic.y < (int)GridH)
    {
        float4 c = CellTex.Load(int3(ic, 0));
        if (c.r > 0.01)
        {
            float2 p = (f - 0.5) * Cell;
            float hs = Cell * (0.5 - 0.12) * (0.62 + 0.38 * sqrt(c.r));
            float d = sdRoundBox(p, float2(hs, hs), Cell * 0.22);
            col += CellColor(c) * Coverage(d, PixelWorld) * (0.55 + 0.45 * c.r);
        }
    }
    col = col / (1.0 + col * 0.25);
    return Out(col, pos.xy);
}
";

        protected override void OnInit()
        {
            cell = 12f * U;
            gw = Math.Max(8, (int)Math.Ceiling(World.W / cell));
            gh = Math.Max(8, (int)Math.Ceiling(World.H / cell));
            int n = gw * gh;
            alive = new byte[n]; next = new byte[n]; visible = new byte[n];
            age = new ushort[n]; level = new float[n];
            for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                    if (Setup.Visible(World.X + (x + 0.5f) * cell, World.Y + (y + 0.5f) * cell, 0f))
                    {
                        visible[y * gw + x] = 1;
                        visibleCells++;
                    }
            for (int i = 0; i < n; i++)
                if (visible[i] != 0 && Rng.Next() < 0.20f) alive[i] = 1;
            tex = new GpuTexture(Gpu, gw, gh);
            pass = new FullscreenPass(Gpu, Shader);
            k = new SceneConstants(Gpu, 4);
            burstClock = 20f + Rng.Next() * 15f;
            for (int i = 0; i < 40; i++) Generation();   // start from an already developed soup
            for (int i = 0; i < n; i++) level[i] = alive[i];
        }

        void Generation()
        {
            int pop = 0;
            for (int y = 0; y < gh; y++)
            {
                int ym = (y == 0 ? gh - 1 : y - 1) * gw, yp = (y == gh - 1 ? 0 : y + 1) * gw, row = y * gw;
                for (int x = 0; x < gw; x++)
                {
                    int xm = x == 0 ? gw - 1 : x - 1, xp = x == gw - 1 ? 0 : x + 1;
                    int nb = alive[ym + xm] + alive[ym + x] + alive[ym + xp]
                           + alive[row + xm] + alive[row + xp]
                           + alive[yp + xm] + alive[yp + x] + alive[yp + xp];
                    int i = row + x;
                    bool live = visible[i] != 0 && (nb == 3 || (alive[i] != 0 && nb == 2));
                    if (live)
                    {
                        int a = alive[i] != 0 ? age[i] + 1 : 0;
                        if (a > MaxAge) { live = false; a = 0; }   // still life worn out: it burns out
                        age[i] = (ushort)a;
                    }
                    else age[i] = 0;
                    next[i] = (byte)(live ? 1 : 0);
                    if (live) pop++;
                }
            }
            byte[] tmp = alive; alive = next; next = tmp;

            // Keep it going: drop in a glider, an R-pentomino or an acorn when the grid gets sparse,
            // plus a rare handful of random cells.
            lowPop = pop < visibleCells / 24 ? lowPop + 1f / Rate : 0f;
            if (lowPop > 1.2f || Rng.Next() < 0.025f)
            {
                lowPop = 0f;
                float roll = Rng.Next();
                Stamp(roll < 0.45f ? Glider : roll < 0.75f ? RPentomino : Acorn);
            }
        }

        static readonly int[] Glider = { 1, 0, 2, 1, 0, 2, 1, 2, 2, 2 };
        static readonly int[] RPentomino = { 1, 0, 2, 0, 0, 1, 1, 1, 1, 2 };
        static readonly int[] Acorn = { 1, 0, 3, 1, 0, 2, 1, 2, 4, 2, 5, 2, 6, 2 };

        void Stamp(int[] shape)
        {
            for (int tries = 0; tries < 20; tries++)
            {
                int sx = Rng.Int(gw), sy = Rng.Int(gh);
                if (visible[sy * gw + sx] == 0) continue;
                for (int j = 0; j < shape.Length; j += 2)
                {
                    int i = ((sy + shape[j + 1]) % gh) * gw + (sx + shape[j]) % gw;
                    if (visible[i] != 0) alive[i] = 1;
                }
                return;
            }
        }

        // A patch of random cells: a fresh spark of chaos that keeps the picture from settling.
        void Burst()
        {
            for (int tries = 0; tries < 20; tries++)
            {
                int sx = Rng.Int(gw), sy = Rng.Int(gh);
                if (visible[sy * gw + sx] == 0) continue;
                int r = 7;
                for (int y = -r; y <= r; y++)
                    for (int x = -r; x <= r; x++)
                    {
                        if (x * x + y * y > r * r || Rng.Next() > 0.38f) continue;
                        int i = ((sy + y + gh) % gh) * gw + (sx + x + gw) % gw;
                        if (visible[i] != 0) alive[i] = 1;
                    }
                return;
            }
        }

        public override void Update(FrameContext f)
        {
            step = Math.Min(step + f.Dt, 0.5f);
            while (step >= 1f / Rate)
            {
                step -= 1f / Rate;
                Generation();
            }
            burstClock -= f.Dt;
            if (burstClock <= 0f) { burstClock = 25f + Rng.Next() * 25f; Burst(); }

            // Ease brightness: quick fade in on birth, a slower fade out on death.
            float up = 1f - (float)Math.Exp(-f.Dt * 16f), down = (float)Math.Exp(-f.Dt * 4.2f);
            int[] px = tex.Pixels;
            float invAge = 255f / MaxAge;
            for (int i = 0; i < alive.Length; i++)
            {
                float l = level[i];
                if (alive[i] != 0) l += (1f - l) * up; else l *= down;
                if (l < 0.02f) l = 0f;
                level[i] = l;
                if (l == 0f) { px[i] = 0; continue; }
                int a = (int)(age[i] * invAge);
                px[i] = (255 << 24) | ((int)(l * 255f) << 16) | (a << 8) | (alive[i] != 0 ? 255 : 0);
            }
            tex.Upload();
        }

        public override void Render(RenderView view)
        {
            tex.Bind(1);
            k[0] = gw; k[1] = gh; k[2] = cell;
            k.Bind();
            pass.Draw(BlendMode.Opaque);
        }
    }
}
