// Mystify: bouncing four-corner polygons that leave a trail of fading copies of themselves, like
// the classic Windows screensaver, drawn as smooth glowing lines at native resolution.
//
// Pattern: CPU simulation in world units, every edge of every trail copy is one SpriteBatch segment
// (additive, with a soft halo). In span mode the corners bounce inside the *visible* screens, so
// the polygons roam across every monitor and are reflected at the outer border and at the edges of
// holes in an uneven layout.
//
// Burn-in: everything moves all the time, the trails are thin lines, and each polygon's colour
// turns slowly through the whole hue circle.
using System;

namespace IdleVeil
{
    sealed class MystifyScene : GpuScene
    {
        const int Corners = 4;
        const int Trail = 28;                 // trail copies kept per polygon
        const float TrailEvery = 0.045f;      // seconds between trail copies

        SpriteBatch sprites;
        int polys;
        float[] px, py, vx, vy;               // [poly * Corners + corner]
        float[] hue, hueSpeed;
        float[] hist;                         // [poly][Trail][Corners * 2], ring buffer
        int histHead, histCount;
        float snapClock;

        protected override void OnInit()
        {
            // Two polygons on a single screen, one more for every extra monitor.
            polys = Math.Max(2, Math.Min(7, 1 + Setup.Screens.Length));
            int n = polys * Corners;
            px = new float[n]; py = new float[n]; vx = new float[n]; vy = new float[n];
            hue = new float[polys]; hueSpeed = new float[polys];
            hist = new float[polys * Trail * Corners * 2];
            for (int p = 0; p < polys; p++)
            {
                RectF home = Setup.Screens[p % Setup.Screens.Length];
                for (int c = 0; c < Corners; c++)
                {
                    int i = p * Corners + c;
                    px[i] = home.X + (0.1f + 0.8f * Rng.Next()) * home.W;
                    py[i] = home.Y + (0.1f + 0.8f * Rng.Next()) * home.H;
                    float a = Rng.Next() * 6.2831853f, s = (130f + Rng.Next() * 150f) * U;
                    vx[i] = (float)Math.Cos(a) * s;
                    vy[i] = (float)Math.Sin(a) * s;
                }
                hue[p] = Rng.Next();
                hueSpeed[p] = 0.006f + Rng.Next() * 0.006f;       // a full turn every 1.5 to 3 minutes
            }
            sprites = new SpriteBatch(Gpu, polys * (Trail + 1) * Corners);
            // Pre-roll so the first frames already show a full trail.
            for (int i = 0; i < 200; i++) Simulate(1f / 60f);
        }

        void Simulate(float dt)
        {
            for (int i = 0; i < px.Length; i++)
            {
                float nx = px[i] + vx[i] * dt, ny = py[i] + vy[i] * dt;
                if (!Setup.Visible(nx, py[i], 0f)) vx[i] = -vx[i]; else px[i] = nx;
                if (!Setup.Visible(px[i], ny, 0f)) vy[i] = -vy[i]; else py[i] = ny;
            }
            for (int p = 0; p < polys; p++) hue[p] = (hue[p] + hueSpeed[p] * dt) % 1f;

            snapClock += dt;
            if (snapClock >= TrailEvery)
            {
                snapClock -= TrailEvery;
                if (snapClock > TrailEvery) snapClock = 0f;
                histHead = (histHead + 1) % Trail;
                if (histCount < Trail) histCount++;
                for (int p = 0; p < polys; p++)
                {
                    int o = (p * Trail + histHead) * Corners * 2;
                    for (int c = 0; c < Corners; c++)
                    {
                        hist[o + c * 2] = px[p * Corners + c];
                        hist[o + c * 2 + 1] = py[p * Corners + c];
                    }
                }
            }
        }

        static void Hsv(float h, float s, float v, out float r, out float g, out float b)
        {
            h = (h - (float)Math.Floor(h)) * 6f;
            float x = 1f - Math.Abs(h % 2f - 1f);
            float cr, cg, cb;
            if (h < 1f) { cr = 1; cg = x; cb = 0; }
            else if (h < 2f) { cr = x; cg = 1; cb = 0; }
            else if (h < 3f) { cr = 0; cg = 1; cb = x; }
            else if (h < 4f) { cr = 0; cg = x; cb = 1; }
            else if (h < 5f) { cr = x; cg = 0; cb = 1; }
            else { cr = 1; cg = 0; cb = x; }
            r = v * (1f - s + s * cr); g = v * (1f - s + s * cg); b = v * (1f - s + s * cb);
        }

        public override void Update(FrameContext f)
        {
            Simulate(f.Dt);

            sprites.Clear();
            float line = 0.85f * U, glow = 4.5f * U;
            for (int p = 0; p < polys; p++)
            {
                // Trail copies, oldest first (they fade out), then the live polygon on top.
                for (int h = histCount - 1; h >= 0; h--)
                {
                    int slot = (histHead - h + Trail * 2) % Trail;
                    int o = (p * Trail + slot) * Corners * 2;
                    float age = (h + 1f) / (Trail + 1f);          // 0 new .. 1 old
                    float fade = (float)Math.Pow(1f - age, 1.9);
                    float r, g, b;
                    Hsv(hue[p] + age * 0.07f, 0.72f, 1f, out r, out g, out b);
                    for (int c = 0; c < Corners; c++)
                    {
                        int d = (c + 1) % Corners;
                        sprites.Add(hist[o + c * 2], hist[o + c * 2 + 1], hist[o + d * 2], hist[o + d * 2 + 1],
                                    line * (0.55f + 0.45f * fade), glow * 0.6f, 0.05f * fade, 0f, r, g, b, 0.55f * fade);
                    }
                }
                float lr, lg, lb;
                Hsv(hue[p], 0.60f, 1f, out lr, out lg, out lb);
                for (int c = 0; c < Corners; c++)
                {
                    int i = p * Corners + c, j = p * Corners + (c + 1) % Corners;
                    sprites.Add(px[i], py[i], px[j], py[j], line * 1.25f, glow, 0.20f, 0f, lr, lg, lb, 0.95f);
                }
            }
        }

        public override void Render(RenderView view) { sprites.Draw(BlendMode.Add); }
    }
}
