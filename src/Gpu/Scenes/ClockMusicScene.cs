// Music clock: the seven-segment clock that moves with the music. The whole clock swells with the
// bass, every kick sends an echo of the digits outward and flares their glow, each digit is
// lit by its own slice of the spectrum, and a row of thin spectrum bars runs under the time.
// With no music it is a calm clock with a row of tiny dots.
using System;

namespace IdleVeil
{
    sealed class ClockMusicScene : SegClockScene
    {
        const int Bars = 40;
        static readonly string[] Digits = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };
        SpriteBatch sprites;
        float bassSm, beatSm;
        readonly float[] level = new float[4];
        readonly float[] barLevel = new float[Bars];

        protected override void OnStyleInit()
        {
            HeightU = 280f;
            Thick = 0.12f;
            GlowSigma = 0.03f;
            GlowAmp = 0.3f;
            Ghost = 0.035f;
            ShowSeconds = false;
            Bright = 0.80f;
            sprites = new SpriteBatch(Gpu, 128);
        }

        protected override float ExtentH { get { return HeightU * U * 1.65f; } }

        protected override void OnStyle(FrameContext f)
        {
            AudioFrame a = f.Audio;
            float dt = f.Dt;
            bassSm += (a.Bass - bassSm) * (1f - (float)Math.Exp(-dt * 14f));
            beatSm = Math.Max(a.Beat, beatSm * (float)Math.Exp(-dt * 6f));
            Scale = 1f + 0.05f * bassSm + 0.05f * beatSm;
            GlowAmp = 0.28f + 0.9f * beatSm + 0.3f * bassSm;
            Bloom = 0.05f + 0.3f * beatSm;
            // Each digit takes the loudest band of its own quarter of the spectrum (low to high).
            for (int d = 0; d < 4; d++)
            {
                float m = 0f;
                for (int i = d * 12; i < d * 12 + 14; i++) m = Math.Max(m, a.Bands[i]);
                level[d] = 0.55f + 0.6f * m;
            }
        }

        protected override void SlotColor(int slot, out float r, out float g, out float b)
        {
            Hsv(0.52f + HueShift + (slot < 4 ? slot * 0.07f : 0.1f) + 0.12f * bassSm, 0.60f, 1f, out r, out g, out b);
        }

        protected override float SlotLevel(int slot) { return slot < 4 ? level[slot] : 0.9f; }

        // Echo of the digits on every kick, and the spectrum row under the time.
        protected override void OnDrawExtras(FrameContext f)
        {
            sprites.Clear();
            if (Mix <= 0.001f) return;
            AudioFrame a = f.Audio;
            float h = H;

            // Echo: the digits again, growing and fading, one per kick.
            if (beatSm > 0.04f)
            {
                float s = 1f + 0.32f * (1f - beatSm);
                float mx = OriginX + Width * 0.5f, my = OriginY + h * 0.5f;
                float x = OriginX, er, eg, eb;
                Hsv(0.52f + HueShift + 0.1f, 0.5f, 1f, out er, out eg, out eb);
                for (int i = 0; i < 5; i++)
                {
                    char c = i == 2 ? ':' : Cur[i < 2 ? i : i - 1];
                    float gw = TextBatch.GlyphWidth(c) * h;
                    if (i != 2)
                        Text.Add(Digits[c - '0'], mx + (x - mx) * s, my + (OriginY - my) * s, h * s, Thick * 0.55f, 0f, 0f, er, eg, eb, beatSm * beatSm * 0.30f * Mix);
                    x += gw + TextBatch.Spacing * h;
                }
            }
            float left = OriginX, right = OriginX + Width;
            float baseY = OriginY + h * 1.30f;
            float step = (right - left) / (Bars - 1);
            for (int i = 0; i < Bars; i++)
            {
                float v = a.Bands[(int)(i * 63f / (Bars - 1))];
                barLevel[i] += (v - barLevel[i]) * (1f - (float)Math.Exp(-f.Dt * 20f));
                float len = (0.012f + 0.30f * barLevel[i]) * h;
                float r, g, b;
                Hsv(0.52f + HueShift + i * 0.004f, 0.6f, 1f, out r, out g, out b);
                float x = left + i * step;
                sprites.Add(x, baseY - len, x, baseY + len, 0.012f * h + 1.2f * U, 0.02f * h, 0.30f, 0f, r, g, b, 0.75f * Mix * (0.35f + 0.65f * (i % 2 == 0 ? 1f : 0.8f)));
            }
        }

        public override void Render(RenderView view)
        {
            sprites.Draw(BlendMode.Add);
            base.Render(view);
        }
    }
}
