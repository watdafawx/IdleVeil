// Neon clock: thin glass-tube digits in pink with a cyan colon and seconds, a wide soft halo
// around every tube, and the way real neon behaves: it stutters on when it appears (every tube
// at its own moment) and now and then one tube flickers for a few frames.
using System;

namespace IdleVeil
{
    sealed class ClockNeonScene : SegClockScene
    {
        protected override float AmbientHue { get { return 0.93f; } }
        protected override void OnStyleInit()
        {
            HeightU = 300f;
            Thick = 0.072f;
            GlowSigma = 0.050f;
            GlowAmp = 0.55f;
            Bloom = 0.10f;
            Ghost = 0f;
            Bright = 0.85f;
        }

        protected override void SlotColor(int slot, out float r, out float g, out float b)
        {
            if (slot >= 4) Hsv(0.52f + HueShift, 0.65f, 1f, out r, out g, out b);          // cyan
            else Hsv(0.93f + HueShift + slot * 0.012f, 0.65f, 1f, out r, out g, out b);    // pink
        }

        static float Hash(float a, float b)
        {
            double v = Math.Sin(a * 12.9898 + b * 78.233) * 43758.547;
            return (float)(v - Math.Floor(v));
        }

        // Power-on stutter, then rare short flickers (per tube).
        protected override float SlotLevel(int slot)
        {
            float on = 0.25f + Hash(slot, 0.5f) * 1.4f;
            if (Shown < on) return Hash(slot * 7f, (float)Math.Floor(Shown * 20f)) > 0.6f ? 0.8f : 0f;
            return Hash(slot * 3f + 1f, (float)Math.Floor(Shown * 12f)) > 0.997f ? 0.15f : 1f;
        }
    }
}
