// Fireflies (the worked example in docs/renderer.md): soft yellow-green lights wandering through
// the dark, each pulsing on its own rhythm. Port of the GDI Fireflies in Savers.cs.
//
// Pattern: CPU simulation in world units + SpriteBatch. In span mode the world is the whole
// desktop, so a firefly can drift from one monitor onto the next; it is only steered back when
// it leaves every screen (the gaps of an uneven layout count as "off screen").
using System;

namespace IdleVeil
{
    sealed class FirefliesScene : GpuScene
    {
        SpriteBatch sprites;
        float[] x, y, heading, phase, rate;

        protected override void OnInit()
        {
            // About 50 per 1080p screen of visible area (not of the world: span worlds have holes).
            float area = 0f;
            foreach (RectF r in Setup.Screens) area += r.W * r.H;
            int n = Math.Max(40, Math.Min(400, (int)(50f * area / (1920f * 1080f))));
            x = new float[n]; y = new float[n]; heading = new float[n]; phase = new float[n]; rate = new float[n];
            for (int i = 0; i < n; i++)
            {
                RectF s = Setup.Screens[i % Setup.Screens.Length];
                x[i] = s.X + Rng.Next() * s.W;
                y[i] = s.Y + Rng.Next() * s.H;
                heading[i] = Rng.Next() * 6.2831853f;
                phase[i] = Rng.Next() * 6.2831853f;
                rate[i] = 0.6f + Rng.Next() * 1.2f;
            }
            sprites = new SpriteBatch(Gpu, n);
        }

        public override void Update(FrameContext f)
        {
            float dt = f.Dt, speed = 55f * U, margin = 4f * U;
            sprites.Clear();
            for (int i = 0; i < x.Length; i++)
            {
                // Wander: a random walk on the heading.
                heading[i] += (Rng.Next() - 0.5f) * 3f * dt;
                x[i] += (float)Math.Cos(heading[i]) * speed * dt;
                y[i] += (float)Math.Sin(heading[i]) * speed * dt;
                if (!Setup.Visible(x[i], y[i], -margin))
                {
                    // Off every screen: turn towards the middle of the nearest one and stay on it.
                    RectF home = Nearest(x[i], y[i]);
                    float want = (float)Math.Atan2(home.CenterY - y[i], home.CenterX - x[i]);
                    heading[i] += (float)Math.Sin(want - heading[i]) * 2f * dt;
                    x[i] = Math.Min(Math.Max(x[i], home.X), home.Right);
                    y[i] = Math.Min(Math.Max(y[i], home.Y), home.Bottom);
                }
                phase[i] += dt * rate[i];

                // Pulse: mostly dark, a short bright flash (cubed sine).
                float pulse = 0.5f + 0.5f * (float)Math.Sin(phase[i]);
                pulse = pulse * pulse * pulse;
                if (pulse <= 0.03f) continue;
                float radius = U * (1.4f + 1.6f * pulse);
                float glow = U * (6f + 7f * pulse);
                sprites.AddDot(x[i], y[i], radius, glow, 0.6f, 0.75f, 0.95f, 0.25f, pulse);
            }
        }

        RectF Nearest(float px, float py)
        {
            RectF best = Setup.Screens[0];
            float bestD = float.MaxValue;
            foreach (RectF r in Setup.Screens)
            {
                float dx = Math.Max(0f, Math.Max(r.X - px, px - r.Right));
                float dy = Math.Max(0f, Math.Max(r.Y - py, py - r.Bottom));
                float d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = r; }
            }
            return best;
        }

        public override void Render(RenderView view) { sprites.Draw(BlendMode.Add); }
    }
}
