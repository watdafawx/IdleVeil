// Seven-segment clock (id "clock", same as the old GDI clock): big cool-teal digits made of
// distance-field glyphs, with a faint unlit "8" behind each digit like a real display, a colon
// that breathes once a second, small seconds at the lower right, and a soft fade-slide when a
// digit changes. The whole clock drifts slowly (see ClockScene), so no stroke stays put.
namespace IdleVeil
{
    sealed class ClockSevenSegScene : SegClockScene
    {
        protected override void OnStyleInit()
        {
            HeightU = 300f;
            Thick = 0.115f;
            GlowSigma = 0.035f;
            GlowAmp = 0.28f;
            Ghost = 0.04f;
            Bright = 0.80f;
        }

        protected override void SlotColor(int slot, out float r, out float g, out float b)
        {
            // Cool teal; the colon and seconds lean towards green so the digits stand out.
            Hsv(0.50f + HueShift + (slot >= 4 ? -0.05f : 0f), slot >= 4 ? 0.50f : 0.60f, 1f, out r, out g, out b);
        }
    }
}
