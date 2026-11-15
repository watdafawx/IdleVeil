// Ripples: every kick drops a big ring, every hi-hat or snare a small quick one, on a random
// spot of a random screen. Rings expand with an ease-out, fade, and carry a fainter echo ring
// inside. With no music a gentle ripple still falls every few seconds.
//
// This is a World scene, so in span mode a ripple is continuous: it grows across the bezels from
// one monitor to the next. State lives on the CPU (8 slots, written to EX), drawn in the shader.
using System;

namespace IdleVeil
{
    sealed class RipplesScene : VizScene
    {
        const int Slots = 8;
        readonly float[] x = new float[Slots], y = new float[Slots], age = new float[Slots], life = new float[Slots];
        readonly float[] hue = new float[Slots], amp = new float[Slots], radius = new float[Slots];
        float lastBeat, hiAvg, sinceKick = 9f, sinceHat = 9f, idleTimer = 1.5f;

        public override SceneLayout Layout { get { return SceneLayout.World; } }

        protected override string Shader { get { return Body; } }

        const string Body = @"
float4 PS(float4 pos : SV_Position) : SV_Target
{
    float2 w = PixelToWorld(pos.xy);
    float px = PixelWorld;
    float3 col = float3(0.0, 0.0, 0.0);
    [loop]
    for (int i = 0; i < 8; i++)
    {
        float4 A = EX[i * 2], B = EX[i * 2 + 1];     // A: x, y, age 0..1 (>= 1: unused), hue   B: amplitude, max radius
        if (A.z >= 1.0) continue;
        float age = A.z;
        float d = length(w - A.xy);
        float rr = (1.0 - pow(1.0 - age, 2.2)) * B.y;
        float q = pow(1.0 - age, 1.6) * B.x;
        float e = abs(d - rr);
        float3 tone = Pal(A.w);
        col += tone * exp(-e / ((7.0 + 22.0 * age) * U)) * 0.42 * q;
        col += Hot(tone, 0.7) * Coverage(e - (1.1 + 2.4 * age) * U, px) * q * 0.9;
        float e2 = abs(d - rr * 0.80);
        col += Pal(A.w + 0.12) * exp(-e2 / ((6.0 + 14.0 * age) * U)) * 0.18 * q;
        float fl = max(1.0 - age * 7.0, 0.0);
        col += Hot(tone, 0.8) * exp(-d / (22.0 * U)) * fl * fl * B.x * 0.9;
    }
    return Out(Tone(col), pos.xy);
}
";

        protected override void OnInitViz()
        {
            for (int i = 0; i < Slots; i++) age[i] = 1f;
        }

        // A random spot on a random screen (weighted by area), away from the edges.
        void Spot(out float px, out float py)
        {
            float total = 0f;
            foreach (RectF r in Setup.Screens) total += r.W * r.H;
            float pick = Rng.Next() * total;
            RectF s = Setup.Screens[0];
            foreach (RectF r in Setup.Screens)
            {
                s = r;
                pick -= r.W * r.H;
                if (pick <= 0f) break;
            }
            px = s.X + s.W * (0.12f + 0.76f * Rng.Next());
            py = s.Y + s.H * (0.14f + 0.72f * Rng.Next());
        }

        void Spawn(float strength, float maxRadius, float seconds, float h)
        {
            int slot = 0;
            float oldest = -1f;
            for (int i = 0; i < Slots; i++)
            {
                if (age[i] >= 1f) { slot = i; break; }
                if (age[i] > oldest) { oldest = age[i]; slot = i; }
            }
            Spot(out x[slot], out y[slot]);
            age[slot] = 0f;
            life[slot] = seconds;
            amp[slot] = strength;
            radius[slot] = maxRadius;
            hue[slot] = h;
        }

        protected override void OnUpdateViz(FrameContext f)
        {
            float dt = f.Dt;
            AudioFrame a = f.Audio;
            sinceKick += dt; sinceHat += dt;

            if (a.Playing)
            {
                if (a.Beat > lastBeat + 0.3f && sinceKick > 0.2f)
                {
                    Spawn(0.85f + 0.4f * a.Bass, 520f * U, 2.4f, 0.0f + 0.15f * Rng.Next());
                    sinceKick = 0f;
                }
                float hi = 0f;
                for (int i = 44; i < 62; i++) hi += a.Bands[i];
                hi /= 18f;
                if (hi > hiAvg * 1.5f + 0.07f && sinceHat > 0.16f)
                {
                    Spawn(0.5f + 0.5f * hi, 240f * U, 1.5f, 0.45f + 0.35f * Rng.Next());
                    sinceHat = 0f;
                }
                hiAvg += (hi - hiAvg) * (1f - (float)Math.Exp(-dt * 3f));
                idleTimer = 2f;
            }
            else
            {
                idleTimer -= dt;
                if (idleTimer <= 0f)
                {
                    Spawn(0.45f, 340f * U, 3.2f, 0.15f + 0.6f * Rng.Next());
                    idleTimer = 2.5f + 2.5f * Rng.Next();
                }
            }
            lastBeat = a.Beat;

            for (int i = 0; i < Slots; i++)
            {
                if (age[i] < 1f) age[i] += dt / life[i];
                SetEx(i * 2, x[i], y[i], age[i], hue[i]);
                SetEx(i * 2 + 1, amp[i], radius[i], 0f, 0f);
            }
        }
    }
}
