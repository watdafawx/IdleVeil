// Screensaver scenes ported from deckviz (Rust, app/src/saver.rs): starfield, aurora,
// fireflies, fire, snow, Game of Life, plasma, plus a drifting digital clock.
//
// deckviz draws a ~800x400 float canvas for a Stream Deck. These scenes are built for a normal
// 16:9 monitor instead: every size is expressed in u = height / 1080, so a 1080p and a 1440p
// screen show the same composition, and PerMonitor builds one copy per monitor so nothing is
// stretched across the bezels. Speeds are in u per second.
//
// A 1440p desktop is 3.7 million pixels per screen, so nothing shades the screen per pixel:
//   - shapes and lines are drawn with plain GDI on the full-size back buffer (starfield, life,
//     clock, and the soft glows, which are pre-rendered sprites blended with AlphaBlend), or
//   - a small DIB section (about 1/8 of the screen) is shaded and StretchBlts up to size.
//
// Colours are dimmed relative to deckviz. The overlay exists to rest an OLED, so it must never
// light up a large area brightly, and nothing here is ever completely static.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace IdleVeil
{
    // Mode ids, the tray list order, and the one place that maps ids to scenes.
    static class Savers
    {
        // Everything Random can pick from (no blank, no random itself).
        public static readonly string[] Animations =
        {
            "matrix", "mystify", "starfield", "aurora", "fireflies", "fire", "snow", "life", "plasma", "clock",
        };

        public static IAnimation Create(string mode, Size size)
        {
            switch (mode)
            {
                case "matrix": return new MatrixRain(size);
                case "mystify": return new Mystify(size);
                case "blank": return new Blank(size);
                case "starfield": return new Starfield(size);
                case "aurora": return new Aurora(size);
                case "fireflies": return new Fireflies(size);
                case "fire": return new Fire(size);
                case "snow": return new Snow(size);
                case "life": return new Life(size);
                case "plasma": return new Plasma(size);
                case "clock": return new DigitalClock(size);
                default: return null;
            }
        }

        public static string Label(string mode)
        {
            switch (mode)
            {
                case "matrix": return "Matrix rain";
                case "mystify": return "Mystify";
                case "blank": return "Blank (black)";
                case "starfield": return "Starfield";
                case "aurora": return "Aurora";
                case "fireflies": return "Fireflies";
                case "fire": return "Fire";
                case "snow": return "Snow";
                case "life": return "Game of Life";
                case "plasma": return "Plasma";
                case "clock": return "Digital clock";
                default: return "Random";
            }
        }

        public static string Pick(Random rng) { return Animations[rng.Next(Animations.Length)]; }
    }

    // xorshift32, the same generator deckviz uses, so the motion matches the original.
    sealed class FastRandom
    {
        uint s;
        public FastRandom(uint seed) { s = seed == 0 ? 0x2545f491u : seed; }
        public float Next()
        {
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            return (s >> 8) * (1f / 16777216f);
        }
        public int Int(int max) { return (int)(Next() * max); }
    }

    static class Fx
    {
        // exp(-u) * 256 for u in 0..8, so per-pixel gaussian falloff needs no Math.Exp.
        static readonly int[] ExpTab = BuildExp();

        static int[] BuildExp()
        {
            var t = new int[257];
            for (int i = 0; i <= 256; i++) t[i] = (int)Math.Round(Math.Exp(-i / 32.0) * 256.0);
            return t;
        }

        // The table by pre-multiplied index (u * 32), to skip a multiply in the hot loops.
        public static int ExpAt(int u)
        {
            if (u <= 0) return 256;
            if (u >= 256) return 0;
            return ExpTab[u];
        }

        public static int Byte(float v)
        {
            int i = (int)(v * 255f + 0.5f);
            return i < 0 ? 0 : i > 255 ? 255 : i;
        }

        public static int Pack(float r, float g, float b) { return (Byte(r) << 16) | (Byte(g) << 8) | Byte(b); }

        public static int Scale(int px, float k)
        {
            int r = (int)(((px >> 16) & 255) * k), g = (int)(((px >> 8) & 255) * k), b = (int)((px & 255) * k);
            if (r > 255) r = 255;
            if (g > 255) g = 255;
            if (b > 255) b = 255;
            return (r << 16) | (g << 8) | b;
        }

        // Saturating add, for additive glows on a canvas that is already lit.
        public static int Add(int px, int dr, int dg, int db)
        {
            int r = ((px >> 16) & 255) + dr, g = ((px >> 8) & 255) + dg, b = (px & 255) + db;
            if (r > 255) r = 255;
            if (g > 255) g = 255;
            if (b > 255) b = 255;
            return (r << 16) | (g << 8) | b;
        }

        // HSV like deckviz's hsv(): hue wraps, sat/val 0..1.
        public static int Hsv(float h, float s, float v)
        {
            h = h - (float)Math.Floor(h);
            float h6 = h * 6f;
            return Pack(Chan(h6 + 5f, s, v), Chan(h6 + 3f, s, v), Chan(h6 + 1f, s, v));
        }

        static float Chan(float n, float s, float v)
        {
            float k = n % 6f;
            float m = k < 4f - k ? k : 4f - k;
            if (m < 0f) m = 0f;
            if (m > 1f) m = 1f;
            return v - v * s * m;
        }
    }

    // A 32-bit top-down DIB section with its pixels exposed as an int[]: scenes write plain ints,
    // then either stretch it over the screen (shaded backgrounds) or blend it as a sprite (glows).
    sealed class MiniDib : IDisposable
    {
        public readonly int W, H;
        public readonly int[] Px;
        public readonly IntPtr DC;
        readonly IntPtr bmp, oldBmp, bits;

        public MiniDib(int w, int h)
        {
            W = w; H = h;
            Px = new int[w * h];
            var bi = new Native.BITMAPINFO();
            bi.bmiHeader.biSize = 40;
            bi.bmiHeader.biWidth = w;
            bi.bmiHeader.biHeight = -h;   // top-down, matching Px
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = 0;   // BI_RGB
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            DC = Native.CreateCompatibleDC(screen);
            bmp = Native.CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
            Native.ReleaseDC(IntPtr.Zero, screen);
            oldBmp = Native.SelectObject(DC, bmp);
        }

        // Push Px into the DIB. Only needed when the pixels changed (sprites upload once, at build).
        public void Upload() { Marshal.Copy(Px, 0, bits, Px.Length); }

        // Nearest-neighbour on purpose: HALFTONE costs ~35 ms a frame at 3.7 M pixels, and one
        // canvas pixel is already 1/8 of a screen pixel.
        public void Fill(int c)
        {
            for (int i = 0; i < Px.Length; i++) Px[i] = c;
        }

        public void StretchTo(IntPtr hdc, int dw, int dh)
        {
            Upload();
            Native.SetStretchBltMode(hdc, Native.COLORONCOLOR);
            Native.StretchBlt(hdc, 0, 0, dw, dh, DC, 0, 0, W, H, Native.SRCCOPY);
        }

        // Same picture as an alpha blend of this sprite on black, but a plain BitBlt: the pixels are
        // premultiplied, so a sprite's edges are already black and it is 7x cheaper than AlphaBlend.
        public void CopyTo(IntPtr hdc, int x, int y)
        {
            Native.BitBlt(hdc, x, y, W, H, DC, 0, 0, Native.SRCCOPY);
        }

        // Stretch the bottom `srcRows` rows of this canvas over the destination rect.
        public void StretchBottomTo(IntPtr hdc, int dx, int dy, int dw, int dh, int srcRows)
        {
            Upload();
            Native.SetStretchBltMode(hdc, Native.COLORONCOLOR);
            Native.StretchBlt(hdc, dx, dy, dw, dh, DC, 0, H - srcRows, W, srcRows, Native.SRCCOPY);
        }

        // A soft round glow: wide faint halo plus a bright core, in premultiplied alpha.
        // radiusFrac is the core radius as a fraction of the sprite size.
        public static MiniDib MakeGlow(int size, float radiusFrac, float tintR, float tintG, float tintB)
        {
            var d = new MiniDib(size, size);
            float c = size * 0.5f, r = size * radiusFrac, inv = 1f / (r * r);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - c, dy = y + 0.5f - c;
                    float q = (dx * dx + dy * dy) * inv;
                    float v = 0.34f * (float)Math.Exp(-q * 0.5f) + 0.66f * (float)Math.Exp(-q * 8f);
                    int a = (int)(v * 255f + 0.5f);
                    if (a <= 1) { d.Px[y * size + x] = 0; continue; }
                    if (a > 255) a = 255;
                    d.Px[y * size + x] = (a << 24) | ((int)(a * tintR) << 16) | ((int)(a * tintG) << 8) | (int)(a * tintB);
                }
            d.Upload();
            return d;
        }

        public void Dispose()
        {
            Native.SelectObject(DC, oldBmp);
            Native.DeleteDC(DC);
            Native.DeleteObject(bmp);
        }
    }

    // Shared clock and the u = height/1080 unit, so every scene is the same on every monitor.
    abstract class TimedAnimation : IAnimation
    {
        protected readonly Stopwatch Clock = Stopwatch.StartNew();
        protected readonly int W, H;
        protected readonly float U;      // 1.0 on a 1080p screen
        protected readonly float Area;   // screen area relative to 1080p, for particle counts
        protected float T, Dt = 1f / 30f;
        float last;

        protected TimedAnimation(Size size)
        {
            W = size.Width; H = size.Height;
            U = H / 1080f;
            Area = W * (float)H / (1920f * 1080f);
        }

        protected void Tick()
        {
            float now = Clock.ElapsedMilliseconds * 0.001f;
            Dt = now - last;
            last = now;
            if (Dt < 0.001f) Dt = 0.001f;
            else if (Dt > 0.1f) Dt = 0.1f;
            T += Dt;
        }

        public abstract void Step();
        public abstract void Draw(IntPtr hdc);
        public virtual void Dispose() { }
    }

    // -------------------------------------------------------------------------------------------
    // Starfield: 3D stars streaming out of the centre, with a rare warp jump. The whole scene is
    // built around the middle of the screen, so it looks the same on any monitor.
    // Straight GDI lines: crisp, and only a few hundred of them.
    // -------------------------------------------------------------------------------------------
    sealed class Starfield : TimedAnimation
    {
        const int Levels = 10, Widths = 3;

        readonly int count;
        readonly float[] st;      // x, y, z
        readonly float[] px0, py0, px1, py1, bright;
        readonly int[] width;
        readonly IntPtr[] pens = new IntPtr[Levels * Widths];
        readonly FastRandom rng = new FastRandom(0x2545f491);
        readonly Native.POINT[] pts = new Native.POINT[2];

        public Starfield(Size size) : base(size)
        {
            count = Math.Max(90, (int)(220 * Area));
            st = new float[count * 3];
            px0 = new float[count]; py0 = new float[count];
            px1 = new float[count]; py1 = new float[count];
            bright = new float[count];
            width = new int[count];
            for (int i = 0; i < count; i++) Spawn(i, rng.Next());
            // A handful of pens, made once: brightness x line width. No per-frame GDI objects.
            for (int l = 0; l < Levels; l++)
            {
                float b = (l + 1) / (float)Levels;
                uint cref = (uint)(Fx.Byte(0.80f * b) | (Fx.Byte(0.88f * b) << 8) | (Fx.Byte(1.0f * b) << 16));
                for (int j = 0; j < Widths; j++)
                    pens[l * Widths + j] = Native.CreatePen(0, Math.Max(1, (int)Math.Round((j + 1) * U)), cref);
            }
        }

        void Spawn(int i, float z)
        {
            st[i * 3] = rng.Next() * 2f - 1f;
            st[i * 3 + 1] = rng.Next() * 2f - 1f;
            st[i * 3 + 2] = z < 0.05f ? 0.05f : z;
        }

        public override void Step()
        {
            Tick();
            // 1.6 s burst of speed every 40 s, like deckviz's "rare" warp jumps.
            const float period = 40f;
            float u = (T % period - (period - 1.6f)) / 1.6f;
            float warp = u > 0f ? (float)Math.Sin(u * Math.PI) : 0f;
            float speed = 0.22f * (1f + 14f * warp);
            float cx = W * 0.5f, cy = H * 0.5f, reach = H * 0.55f;
            for (int i = 0; i < count; i++)
            {
                float z0 = st[i * 3 + 2];
                float z = z0 - Dt * speed;
                if (z <= 0.02f) { Spawn(i, 1f); bright[i] = 0f; continue; }
                st[i * 3 + 2] = z;
                float a = cx + st[i * 3] / z0 * reach, b = cy + st[i * 3 + 1] / z0 * reach;
                float c = cx + st[i * 3] / z * reach, d = cy + st[i * 3 + 1] / z * reach;
                float k = 1f - z;
                bright[i] = Math.Min(1f, k * k * 1.6f * (1f + warp));
                width[i] = Math.Min(Widths - 1, (int)(k * Widths));
                px0[i] = a; py0[i] = b; px1[i] = c; py1[i] = d;
                if (c < -20f || c > W + 20f || d < -20f || d > H + 20f)
                {
                    Spawn(i, 1f);
                    bright[i] = 0f;
                }
            }
        }

        public override void Draw(IntPtr hdc)
        {
            Native.PatBlt(hdc, 0, 0, W, H, Native.BLACKNESS);
            for (int i = 0; i < count; i++)
            {
                if (bright[i] <= 0.02f) continue;
                int lv = Math.Min(Levels - 1, (int)(bright[i] * Levels));
                IntPtr old = Native.SelectObject(hdc, pens[lv * Widths + width[i]]);
                pts[0].X = (int)px0[i]; pts[0].Y = (int)py0[i];
                pts[1].X = (int)px1[i]; pts[1].Y = (int)py1[i];
                Native.Polyline(hdc, pts, 2);
                Native.SelectObject(hdc, old);
            }
        }

        public override void Dispose()
        {
            foreach (IntPtr p in pens) Native.DeleteObject(p);
        }
    }

    // -------------------------------------------------------------------------------------------
    // Aurora: green/violet curtains drifting across a starry sky, hung from the upper half so the
    // composition sits inside the screen. Per-pixel shading on the small canvas, one exp() per
    // pixel through a table.
    // -------------------------------------------------------------------------------------------
    sealed class Aurora : TimedAnimation
    {
        const float Dim = 0.60f;
        const float BgR = 0.012f, BgG = 0.012f, BgB = 0.02f;

        readonly MiniDib c;
        readonly float stepX, stepY;
        readonly float[] cols;          // per column: base, 32/height, intensity, lo rgb, hi rgb
        readonly float[] stx, sty, stp; // stars
        readonly FastRandom rng = new FastRandom(0x9e3779b9);

        public Aurora(Size size) : base(size)
        {
            int cw = Math.Max(64, W / 10), ch = Math.Max(24, H / 10);
            c = new MiniDib(cw, ch);
            stepX = W / (float)cw;
            stepY = H / (float)ch;
            cols = new float[cw * 9];
            int n = Math.Max(30, (int)(90 * Area));
            stx = new float[n]; sty = new float[n]; stp = new float[n];
            for (int i = 0; i < n; i++)
            {
                stx[i] = rng.Next() * cw;
                sty[i] = rng.Next() * ch * 0.55f;
                stp[i] = rng.Next();
            }
        }

        public override void Step()
        {
            Tick();
            float t = T;
            for (int xi = 0; xi < c.W; xi++)
            {
                float xn = (xi + 0.5f) * stepX / W;   // 0..1 across the screen
                float baseY = H * (0.62f + 0.12f * (float)Math.Sin(xn * 6.28f + t * 0.25f)
                                          + 0.06f * (float)Math.Sin(xn * 16.9f - t * 0.4f));
                float inten = Math.Max(0f, 0.55f + 0.45f * (float)Math.Sin(xn * 10.9f + t * 0.6f)
                                                           * (float)Math.Sin(xn * 3.97f - t * 0.17f)) * 0.95f;
                float rays = 0.6f + 0.4f * (float)Math.Sin(xn * 89f + (float)Math.Sin(xn * 12.9f + t * 0.5f) * 3f);
                float hue = 0.36f + 0.22f * (float)Math.Sin(xn * 2.98f + t * 0.07f);
                int lo = Fx.Hsv(hue, 0.75f, 1f), hi = Fx.Hsv(hue + 0.18f, 0.75f, 1f);
                int o = xi * 9;
                cols[o] = baseY;
                cols[o + 1] = 32f / (0.27f * H * rays);
                cols[o + 2] = inten;
                cols[o + 3] = ((lo >> 16) & 255) / 255f; cols[o + 4] = ((lo >> 8) & 255) / 255f; cols[o + 5] = (lo & 255) / 255f;
                cols[o + 6] = ((hi >> 16) & 255) / 255f; cols[o + 7] = ((hi >> 8) & 255) / 255f; cols[o + 8] = (hi & 255) / 255f;
            }

            int[] p = c.Px;
            int cw = c.W, chh = c.H;
            float below = 32f / (0.021f * H), upK = 1f / (0.208f * H), skyK = 1f / H;
            for (int x = 0; x < cw; x++)
            {
                int o = x * 9;
                float baseY = cols[o], inv32 = cols[o + 1], inten = cols[o + 2];
                float loR = cols[o + 3], loG = cols[o + 4], loB = cols[o + 5];
                float hiR = cols[o + 6] - loR, hiG = cols[o + 7] - loG, hiB = cols[o + 8] - loB;
                int i = x;
                float yv = 0.5f * stepY;
                for (int y = 0; y < chh; y++, yv += stepY, i += cw)
                {
                    float dy = yv - baseY;
                    int k = dy < 0f ? Fx.ExpAt((int)(-dy * inv32)) : Fx.ExpAt((int)(dy * below));
                    float v = k * (1f / 256f) * inten;
                    float up = -dy * upK;
                    if (up < 0f) up = 0f; else if (up > 1f) up = 1f;
                    float sky = 0.02f * (1f - yv * skyK);
                    p[i] = Fx.Pack((BgR + (loR + hiR * up) * v) * Dim,
                                   (BgG + (loG + hiG * up) * v) * Dim,
                                   (BgB + (loB + hiB * up) * v + sky) * Dim);
                }
            }

            for (int i = 0; i < stx.Length; i++)
            {
                float tw = (0.5f + 0.5f * (float)Math.Sin(t * 2f + stp[i] * 50f)) * 0.6f;
                int xi = (int)stx[i], yi = (int)sty[i];
                if (xi < 0 || xi >= cw || yi < 0 || yi >= chh) continue;
                int add = (int)(tw * 90f);
                p[yi * cw + xi] = Fx.Add(p[yi * cw + xi], add, add, add);
            }
        }

        public override void Draw(IntPtr hdc) { c.StretchTo(hdc, W, H); }

        public override void Dispose() { c.Dispose(); }
    }

    // -------------------------------------------------------------------------------------------
    // Fireflies: soft glowing dots wandering and pulsing. Pre-rendered glow sprites blended at
    // full resolution, so they stay soft points instead of 8 px blocks.
    // -------------------------------------------------------------------------------------------
    sealed class Fireflies : TimedAnimation
    {
        const int Sizes = 4, Levels = 4;

        readonly MiniDib[,] sprites = new MiniDib[Sizes, Levels];
        readonly int[] spriteSize = new int[Sizes];
        readonly float[] f;     // x, y, heading, pulse phase, pulse speed
        readonly FastRandom rng = new FastRandom(0x1234567u);

        public Fireflies(Size size) : base(size)
        {
            int n = Math.Min(120, Math.Max(40, (int)(50 * Area)));
            f = new float[n * 5];
            for (int i = 0; i < n; i++)
            {
                f[i * 5] = rng.Next() * W;
                f[i * 5 + 1] = rng.Next() * H;
                f[i * 5 + 2] = rng.Next() * 6.2831853f;
                f[i * 5 + 3] = rng.Next() * 6.2831853f;
                f[i * 5 + 4] = 0.6f + rng.Next() * 1.2f;
            }
            // Glow diameters for a core radius of 3..6 u; brightness is pre-baked per level, so the
            // pulse costs a table lookup instead of a per-sprite alpha blend.
            for (int i = 0; i < Sizes; i++)
            {
                spriteSize[i] = Math.Max(6, (int)Math.Round((15f + i * 5f) * U));
                for (int l = 0; l < Levels; l++)
                {
                    float k = (l + 1) / (float)Levels;
                    sprites[i, l] = MiniDib.MakeGlow(spriteSize[i], 0.2f, 0.75f * k, 0.95f * k, 0.25f * k);
                }
            }
        }

        public override void Step()
        {
            Tick();
            float dt = Dt, sp = 55f * U, margin = 4f * U;
            for (int i = 0; i < f.Length; i += 5)
            {
                f[i + 2] += (rng.Next() - 0.5f) * 3f * dt;
                f[i] += (float)Math.Cos(f[i + 2]) * sp * dt;
                f[i + 1] += (float)Math.Sin(f[i + 2]) * sp * dt;
                if (f[i] < margin || f[i] > W - margin || f[i + 1] < margin || f[i + 1] > H - margin)
                {
                    float want = (float)Math.Atan2(H * 0.5f - f[i + 1], W * 0.5f - f[i]);
                    f[i + 2] += (float)Math.Sin(want - f[i + 2]) * 2f * dt;
                    f[i] = Math.Min(Math.Max(f[i], 0f), W);
                    f[i + 1] = Math.Min(Math.Max(f[i + 1], 0f), H);
                }
                f[i + 3] += dt * f[i + 4];
            }
        }

        public override void Draw(IntPtr hdc)
        {
            Native.PatBlt(hdc, 0, 0, W, H, Native.BLACKNESS);
            for (int i = 0; i < f.Length; i += 5)
            {
                float pulse = 0.5f + 0.5f * (float)Math.Sin(f[i + 3]);
                pulse = pulse * pulse * pulse;
                if (pulse <= 0.06f) continue;
                int s = Math.Min(Sizes - 1, (int)(pulse * Sizes));
                int l = Math.Min(Levels - 1, (int)(pulse * Levels));
                int px = spriteSize[s];
                sprites[s, l].CopyTo(hdc, (int)(f[i] - px * 0.5f), (int)(f[i + 1] - px * 0.5f));
            }
        }

        public override void Dispose()
        {
            for (int i = 0; i < Sizes; i++)
                for (int l = 0; l < Levels; l++) sprites[i, l].Dispose();
        }
    }

    // -------------------------------------------------------------------------------------------
    // Fire: pixel flames rising from the bottom edge, on a heat grid half the canvas size.
    // -------------------------------------------------------------------------------------------
    sealed class Fire : TimedAnimation
    {
        const float Dim = 0.55f;
        const float Cool = 0.44f;      // deckviz's "medium" flame height

        readonly MiniDib c;
        readonly int fw, fh, heatMax;
        readonly byte[] heat;
        readonly int[] pal;
        readonly FastRandom rng = new FastRandom(0x2545f491);
        float step;

        public Fire(Size size) : base(size)
        {
            int cw = Math.Max(64, W / 8), ch = Math.Max(24, H / 8);
            c = new MiniDib(cw, ch);
            fw = Math.Max(8, cw / 2);
            fh = Math.Max(8, ch / 2);
            heat = new byte[fw * fh];
            // deckviz's flames start at heat 28..36 and cover about two thirds of a 480 px canvas.
            // Scaling the heat range with the screen keeps them near a third of the height instead,
            // which is far less lit area for an OLED.
            int rows = Math.Max(6, (int)Math.Round(0.36f * H / (2f * (H / (float)ch))));
            heatMax = Math.Max(6, (int)Math.Round(rows * Cool));
            pal = new int[heatMax + 1];
            for (int v = 0; v <= heatMax; v++)
            {
                float f = v / (float)heatMax;
                // dark red -> orange -> pale yellow; only the hottest cells get near white
                float r = Math.Min(1f, f * 2.2f);
                float g = Math.Min(1f, Math.Max(0f, f * 2.2f - 0.8f)) * 0.9f;
                float b = Math.Min(0.6f, Math.Max(0f, f * 3f - 2.4f));
                pal[v] = Fx.Pack(r * Dim, g * 0.85f * Dim, b * Dim);
            }
        }

        public override void Step()
        {
            Tick();
            // deckviz runs the heat simulation at a fixed 40 Hz regardless of the draw rate.
            step = Math.Min(step + Dt, 0.2f);
            while (step >= 1f / 40f)
            {
                step -= 1f / 40f;
                for (int x = 0; x < fw; x++) heat[(fh - 1) * fw + x] = (byte)(heatMax - 4 + rng.Int(5));
                for (int x = 0; x < fw; x++)
                    for (int y = 1; y < fh; y++)
                    {
                        int r = rng.Int(3);
                        int cool = rng.Next() < Cool ? 1 : 0;
                        int v = heat[y * fw + x] - cool;
                        heat[(y - 1) * fw + ((x + fw + 1 - r) % fw)] = (byte)(v < 0 ? 0 : v);
                    }
            }
        }

        public override void Draw(IntPtr hdc)
        {
            c.Fill(Fx.Pack(0.012f * Dim, 0.012f * Dim, 0.02f * Dim));
            int[] p = c.Px;
            int bw = c.W / fw, bh = c.H / fh;
            for (int y = 0; y < fh; y++)
                for (int x = 0; x < fw; x++)
                {
                    byte v = heat[y * fw + x];
                    if (v == 0) continue;
                    int col = pal[v];
                    int x0 = x * bw, y0 = y * bh;
                    for (int py = y0; py < y0 + bh && py < c.H; py++)
                    {
                        int row = py * c.W;
                        for (int px = x0; px < x0 + bw && px < c.W; px++) p[row + px] = col;
                    }
                }
            c.StretchTo(hdc, W, H);
        }

        public override void Dispose() { c.Dispose(); }
    }

    // -------------------------------------------------------------------------------------------
    // Snow: flakes drifting down and piling up along the bottom. The pile is drawn on the small
    // canvas (it is a broad, soft shape); the flakes are glow sprites so they stay small and round.
    // The pile is capped lower and dimmer than deckviz's: a bright white band that size would be
    // exactly the burn-in the overlay is supposed to prevent.
    // -------------------------------------------------------------------------------------------
    sealed class Snow : TimedAnimation
    {
        const float Dim = 0.85f, PileDim = 0.5f, CapFrac = 0.22f, MeltFrac = 0.16f;
        const int SpriteSizes = 5;

        readonly MiniDib pileCanvas;
        readonly float stepX;
        readonly MiniDib[] sprites = new MiniDib[SpriteSizes];
        readonly int[] spriteSize = new int[SpriteSizes];
        readonly float[] f;    // x, y, fall speed, radius, sway phase
        readonly float[] pile;
        readonly int nb;
        readonly FastRandom rng = new FastRandom(0xc0ffeeu);
        readonly float[] fall;
        bool melt;

        public Snow(Size size) : base(size)
        {
            int cw = Math.Max(64, W / 8), ch = Math.Max(24, H / 8);
            pileCanvas = new MiniDib(cw, ch);
            stepX = W / (float)cw;
            int n = Math.Max(60, (int)(110 * Area));
            f = new float[n * 5];
            for (int i = 0; i < n; i++)
            {
                f[i * 5] = rng.Next() * W;
                f[i * 5 + 1] = rng.Next() * H;
                f[i * 5 + 2] = (50f + rng.Next() * 90f) * U;
                f[i * 5 + 3] = (2f + rng.Next() * 3f) * U;
                f[i * 5 + 4] = rng.Next() * 6.2831853f;
            }
            nb = Math.Max(2, cw / 3);
            pile = new float[nb];
            fall = new float[ch + 2];
            for (int y = 0; y < fall.Length; y++)
            {
                float d = y * (H / (float)ch);
                fall[y] = d < 4f ? 1f : 0.72f + 0.12f * (float)Math.Exp(-d * (0.05f * 1080f / H));
            }
            for (int i = 0; i < SpriteSizes; i++)
            {
                spriteSize[i] = Math.Max(4, (int)Math.Round((4f + i * 2f) * U));
                sprites[i] = MiniDib.MakeGlow(spriteSize[i], 0.24f, 0.85f, 0.90f, 1.0f);
            }
        }

        public override void Step()
        {
            Tick();
            float dt = Dt, cap = H * CapFrac;
            float sum = 0f;
            for (int i = 0; i < nb; i++) sum += pile[i];
            float mean = sum / nb;
            if (mean > H * MeltFrac) melt = true;
            if (melt)
            {
                for (int i = 0; i < nb; i++) pile[i] = Math.Max(0f, pile[i] - dt * 30f * U);
                if (mean < 1f) melt = false;
            }
            for (int i = 1; i < nb - 1; i++)
            {
                float avg = (pile[i - 1] + pile[i + 1]) * 0.5f;
                if (pile[i] > avg + 4f * U)
                {
                    float d = (pile[i] - avg) * 0.25f;
                    pile[i] -= d;
                    pile[i - 1] += d * 0.5f;
                    pile[i + 1] += d * 0.5f;
                }
            }
            for (int i = 0; i < f.Length; i += 5)
            {
                f[i + 1] += f[i + 2] * dt;
                f[i] += ((float)Math.Sin(T * 0.6f + f[i + 4]) * 36f + 11f) * U * dt;
                if (f[i] < 0f) f[i] += W; else if (f[i] >= W) f[i] -= W;
                int b = Math.Min(nb - 1, (int)(f[i] / (3f * stepX)));
                if (f[i + 1] > H - pile[b])
                {
                    if (!melt) pile[b] = Math.Min(cap, pile[b] + f[i + 3] * 0.7f);
                    f[i] = rng.Next() * W;
                    f[i + 1] = -6f * U;
                }
            }
        }

        public override void Draw(IntPtr hdc)
        {
            Native.PatBlt(hdc, 0, 0, W, H, Native.BLACKNESS);
            // Flakes are premultiplied, so a plain copy over black is the same picture as a blend.
            for (int i = 0; i < f.Length; i += 5)
            {
                int s = Math.Max(0, Math.Min(SpriteSizes - 1, (int)((f[i + 3] / (5f * U)) * SpriteSizes)));
                int px = spriteSize[s];
                sprites[s].CopyTo(hdc, (int)(f[i] - px * 0.5f), (int)(f[i + 1] - px * 0.5f));
            }

            // The bank only ever reaches CapFrac of the height, so just that band is drawn.
            int cw = pileCanvas.W, ch = pileCanvas.H;
            int bandRows = Math.Max(8, (int)(ch * 0.28f));
            int first = ch - bandRows;
            int[] p = pileCanvas.Px;
            int bg = 0;   // black, so the band has no visible seam against the black sky above it
            for (int i = first * cw; i < p.Length; i++) p[i] = bg;
            // Colour per depth below the surface: a bright edge that dims with depth.
            int[] col = new int[bandRows + 1];
            for (int d = 0; d <= bandRows; d++)
            {
                float v = fall[Math.Min(fall.Length - 1, d)] * PileDim;
                col[d] = Fx.Pack(0.78f * v, 0.84f * v, 0.95f * v);
            }
            float rowH = H / (float)ch;
            // Sloping surface: interpolate between neighbouring buckets instead of a 3 px staircase.
            for (int b = 0; b < nb; b++)
            {
                float ph0 = pile[b], ph1 = pile[b + 1 < nb ? b + 1 : nb - 1];
                if (ph0 < 0.5f && ph1 < 0.5f) continue;
                int x0 = b * 3, x1 = Math.Min(cw, x0 + 3);
                for (int x = x0; x < x1; x++)
                {
                    float ph = ph0 + (ph1 - ph0) * ((x - x0 + 1f) / 3f);
                    int top = (int)Math.Max(first, (H - ph) / rowH);
                    for (int y = top; y < ch; y++) p[y * cw + x] = col[y - top];
                }
            }
            int bandPx = Math.Max(8, (int)Math.Round(H * 0.28f));
            pileCanvas.StretchBottomTo(hdc, 0, H - bandPx, W, bandPx, bandRows);
        }

        public override void Dispose()
        {
            pileCanvas.Dispose();
            foreach (MiniDib d in sprites) d.Dispose();
        }
    }

    // -------------------------------------------------------------------------------------------
    // Game of Life: one generation every so often on a wrapping grid, drawn into a grid-sized DIB
    // and stretched with nearest-neighbour so the cells stay square.
    // -------------------------------------------------------------------------------------------
    sealed class Life : TimedAnimation
    {
        readonly MiniDib c;
        readonly int gw, gh;
        byte[] alive, next;
        readonly ushort[] age;
        readonly float[] glow;
        readonly int[] palAlive, palDead;   // quantised age 0..40
        readonly FastRandom rng = new FastRandom(0x5eed);
        float step, lowPop;

        public Life(Size size) : base(size)
        {
            int cell = Math.Max(6, (int)Math.Round(12f * U));
            gw = Math.Max(8, W / cell);
            gh = Math.Max(8, H / cell);
            c = new MiniDib(gw, gh);
            alive = new byte[gw * gh];
            next = new byte[gw * gh];
            age = new ushort[gw * gh];
            glow = new float[gw * gh];
            for (int i = 0; i < alive.Length; i++)
            {
                alive[i] = (byte)(rng.Next() < 0.28f ? 1 : 0);
                next[i] = alive[i];
            }
            palAlive = new int[41];
            palDead = new int[41];
            BuildPalette(0f);
        }

        // Palette 0: newborn cells bright cyan, older ones drifting to violet; dead cells leave a
        // fading violet ember. Both tables are quantised (age 0..40, glow 0..40).
        void BuildPalette(float t)
        {
            for (int i = 0; i <= 40; i++)
            {
                float af = i / 40f;
                palAlive[i] = Fx.Hsv(0.5f + af * 0.32f + t * 0.01f, 0.65f, 0.85f);
                palDead[i] = Fx.Hsv(0.9f + t * 0.01f, 0.8f, (i / 40f) * 0.45f);
            }
        }

        public override void Step()
        {
            Tick();
            const float rate = 10f;
            step = Math.Min(step + Dt, 0.5f);
            while (step >= 1f / rate)
            {
                step -= 1f / rate;
                int pop = 0;
                for (int y = 0; y < gh; y++)
                {
                    int ym = (y == 0 ? gh - 1 : y - 1) * gw, yp = (y == gh - 1 ? 0 : y + 1) * gw, row = y * gw;
                    for (int x = 0; x < gw; x++)
                    {
                        int xm = x == 0 ? gw - 1 : x - 1, xp = x == gw - 1 ? 0 : x + 1;
                        int nb = alive[ym + xm] + alive[ym + x] + alive[ym + xp]
                               + alive[row + xm] + alive[row + xp]
                               + alive[yp + xm] + alive[yp + x] + alive[yp + xp];
                        int i = row + x;
                        bool live = nb == 3 || (alive[i] != 0 && nb == 2);
                        next[i] = (byte)(live ? 1 : 0);
                        age[i] = live ? (ushort)Math.Min(40, age[i] + 1) : (ushort)0;
                        if (live) pop++;
                    }
                }
                byte[] tmp = alive;
                alive = next; next = tmp;
                // keep it going: drop in a glider or an R-pentomino when it gets sparse
                lowPop = pop < gw * gh / 18 ? lowPop + 1f / rate : 0f;
                if (lowPop > 1.5f || rng.Next() < 0.02f)
                {
                    lowPop = 0f;
                    int sx = rng.Int(gw), sy = rng.Int(gh);
                    int[] shape = rng.Next() < 0.5f
                        ? new[] { 1, 0, 2, 1, 0, 2, 1, 2, 2, 2 }     // glider
                        : new[] { 1, 0, 2, 0, 0, 1, 1, 1, 1, 2 };    // R-pentomino
                    for (int k = 0; k < shape.Length; k += 2)
                        alive[((sy + shape[k + 1]) % gh) * gw + (sx + shape[k]) % gw] = 1;
                }
            }
            BuildPalette(T);
        }

        public override void Draw(IntPtr hdc)
        {
            int[] p = c.Px;
            float fade = (float)Math.Exp(-Dt * 5f);
            for (int i = 0; i < alive.Length; i++)
            {
                glow[i] = alive[i] != 0 ? 1f : glow[i] * fade;
                if (glow[i] < 0.03f) { p[i] = 0; continue; }
                p[i] = alive[i] != 0
                    ? palAlive[Math.Min(40, (int)age[i])]
                    : palDead[Math.Min(40, (int)(glow[i] * 40f))];
            }
            c.StretchTo(hdc, W, H);
        }

        public override void Dispose() { c.Dispose(); }
    }

    // -------------------------------------------------------------------------------------------
    // Plasma: classic four-sine field, precomputed tables, hue LUT. Dim by design.
    // -------------------------------------------------------------------------------------------
    sealed class Plasma : TimedAnimation
    {
        const int HueN = 1024;
        const float Dim = 0.30f;

        readonly MiniDib c;
        readonly int[] dist;
        readonly int maxD;
        readonly float[] tx, ty, td, tds;
        readonly int[] lut = new int[HueN];
        readonly float kx, ky, kd;

        public Plasma(Size size) : base(size)
        {
            int cw = Math.Max(64, W / 8), ch = Math.Max(24, H / 8);
            c = new MiniDib(cw, ch);
            // About two soft bands across the width and 1.3 down the height, like deckviz's canvas.
            kx = 12.6f / W;
            ky = 8.2f / H;
            kd = 12.0f / (W + H);
            float stepX = W / (float)cw, stepY = H / (float)ch;
            float cx = W * 0.5f, cy = H * 0.5f;
            float diag = (float)Math.Sqrt((float)W * W + (float)H * H);
            float kdist = 22f / diag;
            dist = new int[cw * ch];
            int md = 0;
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    float dx = (x + 0.5f) * stepX - cx, dy = (y + 0.5f) * stepY - cy;
                    int d = (int)Math.Round(Math.Sqrt(dx * dx + dy * dy) * kdist * 0.5f);
                    dist[y * cw + x] = d;
                    if (d > md) md = d;
                }
            maxD = md + 2;
            tx = new float[cw];
            ty = new float[ch];
            td = new float[cw + ch];
            tds = new float[maxD + 1];
            for (int i = 0; i < HueN; i++) lut[i] = Fx.Hsv(i / (float)HueN, 0.7f, 1f);
        }

        public override void Step()
        {
            Tick();
            float tp = T * 0.6f, hue0 = T * 0.02f;
            float stepX = W / (float)c.W, stepY = H / (float)c.H;
            for (int x = 0; x < tx.Length; x++) tx[x] = (float)Math.Sin((x + 0.5f) * stepX * kx + tp);
            for (int y = 0; y < ty.Length; y++) ty[y] = (float)Math.Sin((y + 0.5f) * stepY * ky + tp * 1.3f);
            for (int s = 0; s < td.Length; s++) td[s] = (float)Math.Sin(s * stepX * kd + tp * 0.7f);
            for (int d = 0; d <= maxD; d++) tds[d] = (float)Math.Sin(d * 0.04f - tp * 1.6f);

            int[] p = c.Px;
            for (int y = 0; y < c.H; y++)
            {
                int row = y * c.W;
                float tyv = ty[y];
                for (int x = 0; x < c.W; x++)
                {
                    int i = row + x;
                    float v = tx[x] + tyv + td[x + y] + tds[dist[i]];
                    float hue = v * 0.12f + hue0;
                    int idx = ((int)(hue * HueN) + HueN) & (HueN - 1);
                    p[i] = Fx.Scale(lut[idx], (0.55f + 0.08f * v) * Dim);
                }
            }
        }

        public override void Draw(IntPtr hdc) { c.StretchTo(hdc, W, H); }

        public override void Dispose() { c.Dispose(); }
    }

    // -------------------------------------------------------------------------------------------
    // Clock: big, dim, 24-hour time in the middle of the screen, drifting slowly so no pixel ever
    // stays lit and staying well inside the edges. Drawn with GDI text on the full-size buffer.
    // -------------------------------------------------------------------------------------------
    sealed class DigitalClock : TimedAnimation
    {
        readonly IntPtr font;
        string text = "";
        int textW, textH;
        int lastMinute = -1;

        public DigitalClock(Size size) : base(size)
        {
            int height = (int)(H * 0.29f);   // digits come out about a fifth of the screen
            // ANTIALIASED_QUALITY | OUT_TT_PRECIS | CLIP_DEFAULT_PRECIS, bold, Segoe UI.
            font = Native.CreateFont(-height, 0, 0, 0, 700, 0, 0, 0, 1, 4, 2, 4, 0, "Segoe UI");
        }

        void UpdateText(IntPtr hdc)
        {
            DateTime now = DateTime.Now;
            int minute = now.Hour * 60 + now.Minute;
            if (minute == lastMinute && textW > 0) return;
            lastMinute = minute;
            text = now.ToString("HH:mm");
            var sz = new Native.SIZE();
            Native.GetTextExtentPoint32W(hdc, text, text.Length, out sz);
            textW = sz.CX;
            textH = sz.CY;
        }

        public override void Step() { Tick(); }

        public override void Draw(IntPtr hdc)
        {
            Native.PatBlt(hdc, 0, 0, W, H, Native.BLACKNESS);
            IntPtr old = Native.SelectObject(hdc, font);
            UpdateText(hdc);

            // Two periods that don't line up, so the position never repeats for hours. The drift
            // shrinks if the digits are wide, so they always stay inside the screen.
            float driftX = Math.Min(W * 0.10f, (W - textW) * 0.4f);
            float driftY = Math.Min(H * 0.06f, (H - textH) * 0.4f);
            float cx = W * 0.5f + (float)Math.Cos(T * 0.019f) * driftX;
            float cy = H * 0.5f + (float)Math.Sin(T * 0.0117f) * driftY;
            // Very slow breathing, so even the glyph edges never sit still.
            float b = 0.40f + 0.06f * (float)Math.Sin(T * 0.07f);
            // GDI wants 0x00BBGGRR: a dim teal, low red so it rests red subpixels.
            uint col = (uint)(((int)(0.72f * b * 255) << 16) | ((int)(0.85f * b * 255) << 8) | (int)(0.30f * b * 255));

            Native.SetBkMode(hdc, 1);   // TRANSPARENT
            Native.SetTextColor(hdc, col);
            Native.SetTextAlign(hdc, 0x18 | 0x06);   // TA_BASELINE | TA_CENTER
            Native.TextOutW(hdc, (int)cx, (int)(cy + textH * 0.35f), text, text.Length);
            Native.SelectObject(hdc, old);
        }

        public override void Dispose()
        {
            if (font != IntPtr.Zero) Native.DeleteObject(font);
        }
    }
}
