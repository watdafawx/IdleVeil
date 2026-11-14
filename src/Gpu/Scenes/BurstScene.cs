// Beat burst: every kick throws a burst of sparks from a random spot, like a small firework; the
// sparks streak outward, slow down, sag a little and fade. Snares and hi-hats add small quick
// sprays in a second colour. With no music a soft burst still blooms every few seconds.
//
// Span: a World scene, so sparks fly across bezels from one screen into the next; bursts start on
// a random spot of a random screen (weighted by area), never in a hole. All motion, nothing static.
using System;

namespace IdleVeil
{
    sealed class BurstScene : VizScene
    {
        const int Max = 2400;
        readonly float[] x = new float[Max], y = new float[Max], vx = new float[Max], vy = new float[Max];
        readonly float[] life = new float[Max], age = new float[Max], hue = new float[Max], size = new float[Max];
        int count;
        SpriteBatch sprites;
        float lastBeat, hiAvg, sinceKick = 9f, sinceHat = 9f, idleTimer = 1f;
        float flashX, flashY, flash, flashHue;

        protected override string Shader { get { return Body; } }

        // A faint bloom where the last kick landed; the sparks are sprites on top.
        const string Body = @"
float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 p = PixelToWorld(pos.xy);
    float4 F = EX[0];   // x, y, strength, hue
    float d = length(p - F.xy) / (260.0 * U);
    float3 col = Pal(F.w) * exp(-d * d) * 0.14 * F.z;
    return Out(col, pos.xy);
}
";

        protected override void OnInitViz()
        {
            sprites = new SpriteBatch(Gpu, 1024);
        }

        void Spot(out float px, out float py)
        {
            float total = 0f;
            foreach (RectF r in Setup.Screens) total += r.W * r.H;
            float pick = Rng.Next() * total;
            RectF s = Setup.Screens[0];
            foreach (RectF r in Setup.Screens) { s = r; pick -= r.W * r.H; if (pick <= 0f) break; }
            px = s.X + s.W * (0.15f + 0.70f * Rng.Next());
            py = s.Y + s.H * (0.18f + 0.60f * Rng.Next());
        }

        void Burst(int n, float speed, float seconds, float h, float sz)
        {
            float bx, by;
            Spot(out bx, out by);
            for (int i = 0; i < n && count < Max; i++)
            {
                int k = count++;
                float a = Rng.Next() * 6.2831853f;
                float s = speed * (0.35f + 0.65f * (float)Math.Sqrt(Rng.Next()));
                x[k] = bx; y[k] = by;
                vx[k] = (float)Math.Cos(a) * s;
                vy[k] = (float)Math.Sin(a) * s;
                age[k] = 0f;
                life[k] = seconds * (0.6f + 0.6f * Rng.Next());
                hue[k] = h + 0.08f * (Rng.Next() - 0.5f);
                size[k] = sz * (0.7f + 0.6f * Rng.Next());
            }
            flashX = bx; flashY = by; flashHue = h; flash = Math.Max(flash, Math.Min(1f, n / 120f));
        }

        protected override void OnUpdateViz(FrameContext f)
        {
            float dt = f.Dt;
            AudioFrame a = f.Audio;
            sinceKick += dt; sinceHat += dt;
            if (a.Playing)
            {
                if (a.Beat > lastBeat + 0.3f && sinceKick > 0.18f)
                {
                    Burst((int)(110 + 120 * a.Bass), (520f + 380f * a.Bass) * U, 1.9f, 0.05f * Rng.Next(), 1.2f);
                    sinceKick = 0f;
                }
                float hi = 0f;
                for (int i = 44; i < 62; i++) hi += a.Bands[i];
                hi /= 18f;
                if (hi > hiAvg * 1.5f + 0.07f && sinceHat > 0.14f)
                {
                    Burst((int)(25 + 50 * hi), 300f * U, 0.9f, 0.40f + 0.25f * Rng.Next(), 0.8f);
                    sinceHat = 0f;
                }
                hiAvg += (hi - hiAvg) * (1f - (float)Math.Exp(-dt * 3f));
                // Music without clear kicks (pads, classical): a burst on the swell every so often.
                if (sinceKick > 1.4f && Energy > 0.12f)
                {
                    Burst((int)(60 + 200 * Energy), (300f + 500f * Energy) * U, 2.2f, 0.1f + 0.2f * Rng.Next(), 1.1f);
                    sinceKick = 0f;
                }
                idleTimer = 1.5f;
            }
            else
            {
                idleTimer -= dt;
                if (idleTimer <= 0f)
                {
                    Burst(70, 260f * U, 2.6f, 0.15f + 0.6f * Rng.Next(), 1f);
                    idleTimer = 2.2f + 2.5f * Rng.Next();
                }
            }
            lastBeat = a.Beat;
            flash *= (float)Math.Exp(-dt * 3f);
            SetEx(0, flashX, flashY, flash, flashHue);

            // Move: drag, a little gravity; draw as streaks from the last position.
            sprites.Clear();
            float drag = (float)Math.Exp(-dt * 2.2f), grav = 60f * U * dt;
            for (int i = 0; i < count; )
            {
                age[i] += dt / life[i];
                if (age[i] >= 1f)
                {
                    int l = --count;
                    x[i] = x[l]; y[i] = y[l]; vx[i] = vx[l]; vy[i] = vy[l];
                    age[i] = age[l]; life[i] = life[l]; hue[i] = hue[l]; size[i] = size[l];
                    continue;
                }
                vx[i] *= drag; vy[i] = vy[i] * drag + grav;
                x[i] += vx[i] * dt; y[i] += vy[i] * dt;
                float k = 1f - age[i];
                float br = k * k;
                // Streak length follows the speed: fast sparks are long, slow ones are dots.
                float tx = x[i] - vx[i] * 0.035f, ty = y[i] - vy[i] * 0.035f;
                float r, g, b;
                Hsv(Hue + (hue[i] + 0.3f * age[i]) * 0.30f, 0.55f - 0.3f * k * k, 1f, out r, out g, out b);
                sprites.Add(tx, ty, x[i], y[i], size[i] * U * (1.2f + 1.0f * k), 7f * U, 0.75f, 0.6f, r, g, b, 0.95f * br);
                i++;
            }
        }

        static void Hsv(float h, float s, float v, out float r, out float g, out float b)
        {
            h -= (float)Math.Floor(h);
            r = v * (1f - s + s * Sat(Math.Abs(h * 6f - 3f) - 1f));
            g = v * (1f - s + s * Sat(2f - Math.Abs(h * 6f - 2f)));
            b = v * (1f - s + s * Sat(2f - Math.Abs(h * 6f - 4f)));
        }

        static float Sat(float v) { return v < 0f ? 0f : v > 1f ? 1f : v; }

        public override void Render(RenderView view)
        {
            base.Render(view);
            sprites.Draw(BlendMode.Add);
        }
    }
}
