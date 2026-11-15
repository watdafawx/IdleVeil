// Starfield (reference scene for the SpriteBatch pattern): 3D stars streaming out of a slowly
// wandering vanishing point, with a rare warp jump. Simulated on the CPU in world units, drawn
// as instanced soft capsules (a dot plus a short motion streak). In span mode the world is the
// whole desktop, so stars leave one monitor and fly on across the next.
using System;

namespace IdleVeil
{
    sealed class StarfieldScene : GpuScene
    {
        SpriteBatch sprites;
        int count;
        float[] sx, sy, sz;        // 3D position: x, y at depth 1 in "reach" units, z in (0, 1]
        float[] tint, twinkle;     // per-star colour temperature (0 blue .. 1 warm) and phase
        float reach;               // world units per x/z unit
        float cx, cy;              // vanishing point this frame
        float minX, maxX, minY, maxY;   // spawn window (x/y at z = 1 that project inside the world)
        float warpClock;

        const float Speed = 0.085f;     // depth per second at cruise
        const float WarpEvery = 55f, WarpLength = 2.4f;

        protected override void OnInit()
        {
            // Density per area of the whole world: about 1300 stars per 1080p screen.
            count = Math.Max(400, Math.Min(16000, (int)(1300f * World.W * World.H / (1920f * 1080f))));
            sx = new float[count]; sy = new float[count]; sz = new float[count];
            tint = new float[count]; twinkle = new float[count];
            reach = 0.36f * (float)Math.Sqrt(World.W * World.H);
            cx = World.CenterX; cy = World.CenterY;
            minX = (World.X - cx) / reach; maxX = (World.Right - cx) / reach;
            minY = (World.Y - cy) / reach; maxY = (World.Bottom - cy) / reach;
            for (int i = 0; i < count; i++) Spawn(i, 0.04f + Rng.Next() * 0.96f);
            sprites = new SpriteBatch(Gpu, count);
            warpClock = WarpEvery * (0.3f + 0.4f * Rng.Next());   // first warp 15-40 s in
        }

        void Spawn(int i, float z)
        {
            // Uniform over the part of the depth-1 plane that lands inside the world, so the
            // density is even across every monitor (not clustered around one centre).
            sx[i] = minX + Rng.Next() * (maxX - minX);
            sy[i] = minY + Rng.Next() * (maxY - minY);
            sz[i] = z;
            float t = Rng.Next();
            tint[i] = t < 0.12f ? 1f : t < 0.30f ? 0f : 0.5f;
            twinkle[i] = Rng.Next() * 6.283f;
        }

        public override void Update(FrameContext f)
        {
            float dt = f.Dt, t = f.Time;
            // Warp: a smooth bump in speed for a couple of seconds every ~minute.
            float wt = (t - warpClock) / WarpLength;
            float warp = wt > 0f && wt < 1f ? (float)Math.Pow(Math.Sin(wt * Math.PI), 2) : 0f;
            if (wt >= 1f) warpClock += WarpEvery * (0.8f + 0.4f * Rng.Next());
            float speed = Speed * (1f + 11f * warp);

            // The vanishing point wanders slowly around the world centre (burn-in: nothing radiates
            // from a fixed spot for long).
            cx = World.CenterX + World.W * 0.05f * (float)Math.Sin(t * 0.021f + Setup.Seed % 97) + f.BurnX;
            cy = World.CenterY + World.H * 0.05f * (float)Math.Sin(t * 0.017f + 1.7f) + f.BurnY;

            float streak = 0.05f + 0.5f * warp;   // seconds of motion shown as a streak
            float margin = 40f * U;
            sprites.Clear();
            for (int i = 0; i < count; i++)
            {
                float z0 = sz[i], z = z0 - dt * speed;
                if (z <= 0.015f) { Spawn(i, 1f); continue; }
                sz[i] = z;
                float x = cx + sx[i] / z * reach, y = cy + sy[i] / z * reach;
                if (x < World.X - margin || x > World.Right + margin || y < World.Y - margin || y > World.Bottom + margin)
                {
                    Spawn(i, 1f);
                    continue;
                }
                if (!Setup.Visible(x, y, margin)) continue;   // in the hole of a span layout

                float near = 1f - z;                          // 0 far .. 1 close
                float birth = Math.Min(1f, near * 6f);        // fade in from the distance
                float b = birth * (0.16f + 0.90f * near * near) * (0.85f + 0.15f * (float)Math.Sin(t * 2.3f + twinkle[i]));
                b *= 1f + 0.6f * warp;
                if (b < 0.01f) continue;
                float radius = U * (0.45f + 1.7f * near * near * near);
                float zt = Math.Min(1f, z + speed * streak);
                float tx = cx + sx[i] / zt * reach, ty = cy + sy[i] / zt * reach;

                float r, g, bl;
                if (tint[i] > 0.75f) { r = 1f; g = 0.86f; bl = 0.70f; }
                else if (tint[i] < 0.25f) { r = 0.62f; g = 0.76f; bl = 1f; }
                else { r = 0.84f; g = 0.89f; bl = 1f; }
                sprites.Add(tx, ty, x, y, radius, radius * 2.6f + 1.2f * U, 0.22f, 1f, r, g, bl, Math.Min(0.9f, b));
            }
        }

        public override void Render(RenderView view) { sprites.Draw(BlendMode.Add); }
    }
}
