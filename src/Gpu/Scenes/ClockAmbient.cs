// The ambient layer behind a clock in span mode: the clock itself sits whole on one screen (see
// ClockScene.Stage), and this layer ties all the screens together. Dim motes drift slowly through
// the whole layout in the clock's hue, and once a minute a soft sweep crosses the world from left
// to right with the seconds. Everything moves, so nothing stays put on an OLED.
using System;

namespace IdleVeil
{
    sealed class ClockAmbient
    {
        readonly SceneSetup setup;
        readonly FastRandom rng;
        readonly SpriteBatch sprites;
        readonly float u;
        readonly float[] x, y, vx, vy, phase, size;

        public ClockAmbient(Gpu gpu, SceneSetup setup, FastRandom rng)
        {
            this.setup = setup;
            this.rng = rng;
            u = setup.U;
            float area = 0f;
            foreach (RectF r in setup.Screens) area += r.W * r.H;
            int n = Math.Max(30, Math.Min(260, (int)(45f * area / (1920f * 1080f * u * u))));
            x = new float[n]; y = new float[n]; vx = new float[n]; vy = new float[n];
            phase = new float[n]; size = new float[n];
            for (int i = 0; i < n; i++)
            {
                Spawn(i);
                phase[i] = rng.Next() * 6.2831853f;
            }
            sprites = new SpriteBatch(gpu, n + 16);
        }

        // Anywhere on a screen, weighted by area.
        void Spawn(int i)
        {
            float total = 0f;
            foreach (RectF r in setup.Screens) total += r.W * r.H;
            float pick = rng.Next() * total;
            RectF s = setup.Screens[0];
            foreach (RectF r in setup.Screens) { s = r; pick -= r.W * r.H; if (pick <= 0f) break; }
            x[i] = s.X + rng.Next() * s.W;
            y[i] = s.Y + rng.Next() * s.H;
            float a = rng.Next() * 6.2831853f, sp = (6f + rng.Next() * 14f) * u;
            vx[i] = (float)Math.Cos(a) * sp;
            vy[i] = (float)Math.Sin(a) * sp - 4f * u;   // a slight upward drift
            size[i] = 0.8f + rng.Next() * 1.6f;
        }

        static void Hsv(float h, float s, float v, out float r, out float g, out float b)
        {
            h = h - (float)Math.Floor(h);
            r = v * (1f - s + s * Clamp(Math.Abs(h * 6f - 3f) - 1f));
            g = v * (1f - s + s * Clamp(2f - Math.Abs(h * 6f - 2f)));
            b = v * (1f - s + s * Clamp(2f - Math.Abs(h * 6f - 4f)));
        }

        static float Clamp(float v) { return v < 0f ? 0f : v > 1f ? 1f : v; }

        // seconds: 0..60; hue: base hue; level: overall brightness (Mix times any audio swell).
        public void Update(FrameContext f, float seconds, float hue, float level)
        {
            sprites.Clear();
            float dt = f.Dt;
            float r, g, b;
            Hsv(hue, 0.55f, 1f, out r, out g, out b);
            for (int i = 0; i < x.Length; i++)
            {
                x[i] += vx[i] * dt;
                y[i] += vy[i] * dt;
                phase[i] += dt * 0.5f;
                if (!setup.Visible(x[i], y[i], 8f * u)) { Spawn(i); continue; }
                if (level <= 0.001f) continue;
                float tw = 0.5f + 0.5f * (float)Math.Sin(phase[i]);
                float a = (0.10f + 0.22f * tw * tw) * level;
                sprites.AddDot(x[i], y[i], size[i] * u, 7f * u, 0.35f, r, g, b, a);
            }
            if (level <= 0.001f) return;

            // The seconds sweep: a faint vertical line with a tail, crossing the world once a minute,
            // split per screen so nothing is drawn in a hole.
            RectF w = setup.World;
            float sx = w.X + w.W * (seconds / 60f);
            float tail = 90f * u;
            float sr, sg, sb;
            Hsv(hue + 0.04f, 0.45f, 1f, out sr, out sg, out sb);
            foreach (RectF s in setup.Screens)
            {
                if (sx < s.X - 2f * u || sx > s.Right + tail) continue;
                float y0 = s.Y + 2f * u, y1 = s.Bottom - 2f * u;
                float cx = Math.Min(sx, s.Right);
                sprites.Add(cx, y0, cx, y1, 0.6f * u, 6f * u, 0.30f, 0f, sr, sg, sb, 0.10f * level);
                // A short glow trail behind the line.
                for (int k = 1; k <= 3; k++)
                {
                    float tx = sx - k * tail / 3f;
                    if (tx < s.X || tx > s.Right) continue;
                    sprites.Add(tx, y0, tx, y1, 0.4f * u, 10f * u, 0.15f, 0f, sr, sg, sb, 0.035f * level / k);
                }
            }
        }

        public void Draw() { sprites.Draw(BlendMode.Add); }
    }
}
