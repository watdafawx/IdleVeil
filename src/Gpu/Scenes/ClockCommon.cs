// Shared parts of the clock scenes:
//   ClockScene     time of day, the slow drift that keeps every pixel moving, a Mix level the
//                  music mode uses to crossfade, and the minute-change animation
//   SegClockScene  the clock drawn with the engine's seven-segment glyphs (TextBatch); the plain
//                  clock, the neon clock and the music clock only differ in a few settings
//
// Layout is per screen: in span mode every monitor shows its own clock (see docs/music-scenes.md
// for why). Each clock wanders on two slow Lissajous curves (periods of 3 to 6 minutes, so it
// never repeats a path), plus the host's Burn drift, so a pixel inside a stroke is only lit for
// a few seconds. Colours are dim and turn slowly.
using System;

namespace IdleVeil
{
    abstract class ClockScene : GpuScene
    {
        // 0..1: how visible the clock is. The music mode fades it against the visualizer.
        public float Mix = 1f;

        protected int Hour, Minute;
        protected float Seconds;                    // 0..60 with the fraction
        protected readonly char[] Cur = new char[4], Prev = new char[4];
        protected float Change = 1f;                // minute-change transition, 0..1 (1: done)
        protected float Shown;                      // seconds since the clock became visible
        protected float Cx, Cy;                     // centre of the clock in world units, wandering
        protected float HueShift;                   // +-0.04 turn over several minutes

        float px1, px2, py1, py2, hueOff;
        int lastKey = -1;
        float lastMix;
        static double testTime = -2;

        public override SceneLayout Layout { get { return SceneLayout.PerScreen; } }

        // Extent of the whole clock in world units (so the drift keeps it on screen).
        protected abstract float ExtentW { get; }
        protected abstract float ExtentH { get; }
        protected virtual void OnUpdateClock(FrameContext f) { }
        protected virtual void OnInitClock() { }

        protected override void OnInit()
        {
            px1 = Rng.Next() * 6.2832f; px2 = Rng.Next() * 6.2832f;
            py1 = Rng.Next() * 6.2832f; py2 = Rng.Next() * 6.2832f;
            hueOff = Rng.Next() * 6.2832f;
            OnInitClock();
        }

        // Test hook: IDLEVEIL_TEST_TIME=HH:mm:ss starts the clock there instead of at the real time.
        static double TestTime()
        {
            if (testTime == -2)
            {
                testTime = -1;
                string s = Environment.GetEnvironmentVariable("IDLEVEIL_TEST_TIME");
                TimeSpan ts;
                if (!string.IsNullOrEmpty(s) && TimeSpan.TryParse(s, out ts)) testTime = ts.TotalSeconds;
            }
            return testTime;
        }

        public override void Update(FrameContext f)
        {
            float t = f.Time, dt = f.Dt;
            if (Mix > 0.001f && lastMix <= 0.001f) Shown = 0f;
            lastMix = Mix;
            Shown += dt;

            double tt = TestTime();
            double day;
            if (tt >= 0) day = (tt + t) % 86400.0;
            else { DateTime n = DateTime.Now; day = n.TimeOfDay.TotalSeconds; }
            Hour = (int)(day / 3600.0);
            Minute = (int)((day % 3600.0) / 60.0);
            Seconds = (float)(day % 60.0);

            int key = Hour * 60 + Minute;
            if (key != lastKey)
            {
                if (lastKey >= 0)
                {
                    Array.Copy(Cur, Prev, 4);
                    Change = 0f;
                }
                else Change = 1f;
                lastKey = key;
                Cur[0] = (char)('0' + Hour / 10); Cur[1] = (char)('0' + Hour % 10);
                Cur[2] = (char)('0' + Minute / 10); Cur[3] = (char)('0' + Minute % 10);
                if (Change >= 1f) Array.Copy(Cur, Prev, 4);
            }
            if (Change < 1f) Change = Math.Min(1f, Change + dt / 0.8f);

            // Drift: two periods per axis that don't line up. The amplitude keeps the clock on the screen.
            float ax = Math.Min(World.W * 0.20f, Math.Max(0f, (World.W - ExtentW) * 0.5f - 24f * U)) * 0.92f;
            float ay = Math.Min(World.H * 0.20f, Math.Max(0f, (World.H - ExtentH) * 0.5f - 24f * U)) * 0.92f;
            float sx = 0.65f * (float)Math.Sin(t * 6.2831853f / 197f + px1) + 0.35f * (float)Math.Sin(t * 6.2831853f / 311f + px2);
            float sy = 0.65f * (float)Math.Sin(t * 6.2831853f / 241f + py1) + 0.35f * (float)Math.Sin(t * 6.2831853f / 373f + py2);
            Cx = World.CenterX + sx * ax + f.BurnX;
            Cy = World.CenterY + sy * ay + f.BurnY;
            HueShift = 0.04f * (float)Math.Sin(t / 110f + hueOff);

            OnUpdateClock(f);
        }

        protected static float Smooth(float k) { k = k < 0f ? 0f : k > 1f ? 1f : k; return k * k * (3f - 2f * k); }

        // h, s, v in 0..1 to linear-ish rgb (same as the shader's hsv).
        protected static void Hsv(float h, float s, float v, out float r, out float g, out float b)
        {
            h -= (float)Math.Floor(h);
            float hh = h * 6f;
            r = v - v * s * Clamp01(Math.Min(Mod(5f + hh, 6f), 4f - Mod(5f + hh, 6f)));
            g = v - v * s * Clamp01(Math.Min(Mod(3f + hh, 6f), 4f - Mod(3f + hh, 6f)));
            b = v - v * s * Clamp01(Math.Min(Mod(1f + hh, 6f), 4f - Mod(1f + hh, 6f)));
        }

        static float Mod(float a, float m) { return a - m * (float)Math.Floor(a / m); }
        static float Clamp01(float v) { return v < 0f ? 0f : v > 1f ? 1f : v; }
    }

    // A clock made of seven-segment glyphs. Subclasses set the look in OnInitClock / OnStyle.
    abstract class SegClockScene : ClockScene
    {
        static readonly string[] Ch = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", ":" };

        TextBatch text;

        // Look (set these in OnInitClock; change them per frame in OnStyle if they react to something).
        protected float HeightU = 300f;          // digit height in design units
        protected float Thick = 0.115f;          // stroke width in digit heights
        protected float GlowSigma = 0.035f;      // halo size in digit heights
        protected float GlowAmp = 0.28f;
        protected float Bloom;                   // > 0: a second, wide halo of this strength
        protected float Ghost = 0.04f;           // brightness of the unlit "8" behind each digit (0: none)
        protected bool ShowSeconds = true;
        protected float Bright = 0.80f;
        protected float Scale = 1f;              // pulse: music clock scales the whole clock

        protected float H;                       // current digit height in world units
        protected float OriginX, OriginY, Width; // top-left and width of the main HH:MM block

        const float SecScale = 0.30f, SecGap = 0.20f;

        protected override float ExtentW
        {
            get { return TextBatch.Measure("00:00", HeightU * U) * 1.04f + (ShowSeconds ? (SecGap + 0f) * HeightU * U + TextBatch.Measure("00", SecScale * HeightU * U) : 0f); }
        }
        protected override float ExtentH { get { return HeightU * U * 1.1f; } }

        protected override void OnInitClock()
        {
            text = new TextBatch(Gpu, 160);
            OnStyleInit();
        }

        protected virtual void OnStyleInit() { }
        // Called every frame before drawing: adjust Scale, Bright, GlowAmp... (music clock).
        protected virtual void OnStyle(FrameContext f) { }
        // Colour of a slot: 0..3 digits, 4 colon, 5 seconds.
        protected abstract void SlotColor(int slot, out float r, out float g, out float b);
        // Extra brightness factor of a slot (neon flicker, music level).
        protected virtual float SlotLevel(int slot) { return 1f; }
        // Anything to draw around the digits (music clock): called with the block's geometry.
        protected virtual void OnDrawExtras(FrameContext f) { }

        protected override void OnUpdateClock(FrameContext f)
        {
            OnStyle(f);
            H = HeightU * U * Scale;
            float secW = ShowSeconds ? SecGap * H + TextBatch.Measure("00", SecScale * H) : 0f;
            Width = TextBatch.Measure("00:00", H);
            float total = Width + secW;
            OriginX = Cx - total * 0.5f;
            OriginY = Cy - H * 0.5f;

            text.Clear();
            OnDrawExtras(f);
            if (Mix <= 0.001f) return;

            float x = OriginX;
            for (int i = 0; i < 5; i++)
            {
                int slot = i == 2 ? 4 : (i < 2 ? i : i - 1);
                char c = i == 2 ? ':' : Cur[slot];
                float w = TextBatch.GlyphWidth(c) * H;
                if (i == 2)
                {
                    float pulse = 0.5f + 0.5f * (float)Math.Cos(Seconds * 6.2831853f);   // breathes once a second
                    DrawGlyph(4, ':', x, OriginY, 0.25f + 0.75f * pulse, 0f);
                }
                else
                {
                    if (Ghost > 0f) AddGlyph('8', x, OriginY, H, slot, Ghost, true);
                    if (Change < 1f && Prev[slot] != Cur[slot])
                    {
                        float k = Smooth(Change);
                        DrawGlyph(slot, Prev[slot], x, OriginY, 1f - k, -k * 0.12f);
                        DrawGlyph(slot, c, x, OriginY, k, (1f - k) * 0.12f);
                    }
                    else DrawGlyph(slot, c, x, OriginY, 1f, 0f);
                }
                x += w + TextBatch.Spacing * H;
            }

            if (ShowSeconds)
            {
                float sh = SecScale * H;
                float sx = OriginX + Width + SecGap * H;
                float sy = OriginY + H - sh;
                string s0 = Ch[(int)(Seconds / 10f) % 10], s1 = Ch[(int)Seconds % 10];
                float w0 = TextBatch.GlyphWidth(s0[0]) * sh;
                float r, g, b;
                SlotColor(5, out r, out g, out b);
                float lv = SlotLevel(5) * 0.55f;
                AddRaw(s0, sx, sy, sh, r, g, b, lv, true);
                AddRaw(s1, sx + w0 + TextBatch.Spacing * sh, sy, sh, r, g, b, lv, true);
            }
        }

        void DrawGlyph(int slot, char c, float x, float y, float alpha, float yShift)
        {
            AddGlyph(c, x, y + yShift * H, H, slot, alpha, false);
        }

        void AddGlyph(char c, float x, float y, float h, int slot, float alpha, bool ghost)
        {
            int idx = c == ':' ? 10 : c - '0';
            float r, g, b;
            SlotColor(slot, out r, out g, out b);
            float a = alpha * Mix * Bright * (ghost ? 1f : SlotLevel(slot));
            if (a <= 0.002f) return;
            if (ghost)
            {
                text.Add(Ch[idx], x, y, h, Thick, 0f, 0f, r, g, b, a);
                return;
            }
            text.Add(Ch[idx], x, y, h, Thick, GlowSigma * h, GlowAmp, r, g, b, a);
            if (Bloom > 0f) text.Add(Ch[idx], x, y, h, Thick, GlowSigma * h * 4.5f, Bloom, r, g, b, a * 0.5f);
        }

        void AddRaw(string s, float x, float y, float h, float r, float g, float b, float a, bool glow)
        {
            a *= Mix * Bright;
            if (a <= 0.002f) return;
            text.Add(s, x, y, h, Thick, glow ? GlowSigma * h * 1.2f : 0f, glow ? GlowAmp : 0f, r, g, b, a);
        }

        // For subclasses that draw extra text.
        protected TextBatch Text { get { return text; } }

        public override void Render(RenderView view)
        {
            text.Draw();
        }
    }
}
