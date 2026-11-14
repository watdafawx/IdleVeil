// Matrix rain: columns of falling glyphs (katakana, digits and symbols) with a bright head and a
// trail that fades out, in the usual green.
//
// Pattern: a glyph atlas is rendered once at Init with GDI+ text at the exact pixel size of a
// cell, so on screen every glyph maps 1:1 to texels and stays crisp. Every frame the CPU writes
// one instance (position, glyph, colour) per visible glyph and a single instanced draw puts them
// on screen; a few soft sprites add the glow of the heads.
//
// Columns are created per monitor, so in span mode the rain covers every screen (each monitor
// keeps its own height; there is nothing in the holes of an uneven layout). Glyphs fall smoothly
// (a continuous scroll snapped to whole pixels, which keeps them sharp). Burn-in: everything is
// moving, columns are mostly dim (the average picture level is low) and each column has its own
// speed, length and brightness.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace IdleVeil
{
    sealed class MatrixScene : GpuScene
    {
        const int Stride = 8;                 // floats per glyph instance: x, y, glyph, pad, r, g, b, a

        // One column of falling glyphs. Glyph k of the stream sits k cells above the head.
        struct Column
        {
            public int Screen, Slot;          // which screen, first index into the glyph ring buffers
            public float X;                   // left edge (whole pixels)
            public float Head;                // y of the head cell's top (world units), continuous
            public float Speed;               // world units per second
            public int Length;                // trail length in cells
            public int HeadIndex;             // ring position of the head glyph
            public float Bright;              // 0.4 .. 1
        }

        Column[] cols;
        int[] ring;                           // glyph ids, ringSize per column
        int ringSize;
        int cell;                             // world units (= atlas pixels) per glyph cell
        int glyphCount, atlasCols, atlasRows;
        GpuTexture atlas;
        IntPtr vs, ps, buffer, srv;
        float[] data;
        int count;
        SceneConstants k;
        SpriteBatch glow;
        float mutate;

        const string Shader = @"
cbuffer Scene : register(b2)
{
    float4 Atlas;    // x atlas columns, y atlas rows, z cell size in world units
};
struct Inst { float2 pos; float glyph; float pad; float4 color; };
StructuredBuffer<Inst> Insts : register(t0);
Texture2D AtlasTex : register(t1);

struct VOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
    nointerpolation float4 color : COLOR0;
};

VOut VSMatrix(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    Inst s = Insts[iid];
    float qx = (vid == 1 || vid == 4 || vid == 5) ? 1.0 : 0.0;
    float qy = (vid == 2 || vid == 3 || vid == 5) ? 1.0 : 0.0;
    float g = floor(s.glyph + 0.5);
    float gy = floor(g / Atlas.x);
    float gx = g - gy * Atlas.x;
    VOut o;
    o.pos = WorldToClip(s.pos + float2(qx, qy) * Atlas.z);
    o.uv = float2(gx + qx, gy + qy) / Atlas.xy;
    o.color = s.color;
    return o;
}

float4 PSMatrix(VOut i) : SV_Target
{
    float cov = AtlasTex.Sample(SampLinear, i.uv).r;
    return float4(i.color.rgb * (cov * i.color.a), 0);
}
";

        protected override void OnInit()
        {
            cell = (int)Math.Round(Math.Max(14f, 19.5f * U));
            BuildAtlas();

            RectF[] sc = Setup.Screens;
            int total = 0;
            int maxRows = 0;
            foreach (RectF r in sc) maxRows = Math.Max(maxRows, (int)Math.Ceiling(r.H / cell) + 2);
            ringSize = maxRows + 2;
            var list = new System.Collections.Generic.List<Column>();
            for (int s = 0; s < sc.Length; s++)
            {
                int n = Math.Max(1, (int)(sc[s].W / cell));
                float left = (float)Math.Floor(sc[s].X + (sc[s].W - n * cell) * 0.5f);
                for (int c = 0; c < n; c++)
                {
                    var col = new Column { Screen = s, Slot = total * ringSize, X = left + c * cell };
                    Reset(ref col, true);
                    list.Add(col);
                    total++;
                }
            }
            cols = list.ToArray();
            ring = new int[total * ringSize];
            for (int i = 0; i < ring.Length; i++) ring[i] = Rng.Int(glyphCount);

            int capacity = total * (ringSize + 1);
            data = new float[capacity * Stride];
            buffer = Gpu.StructuredBuffer(Stride * 4, capacity, out srv);
            vs = Gpu.VertexShader(Shader, "VSMatrix");
            ps = Gpu.PixelShader(Shader, "PSMatrix");
            k = new SceneConstants(Gpu, 4);
            k[0] = atlasCols; k[1] = atlasRows; k[2] = cell;
            glow = new SpriteBatch(Gpu, total);
        }

        void Reset(ref Column c, bool initial)
        {
            RectF s = Setup.Screens[c.Screen];
            int rows = (int)Math.Ceiling(s.H / cell);
            float b = 0.40f + 0.60f * Rng.Next();
            c.Bright = b;
            c.Length = Rng.Int(Math.Max(1, rows * 2 / 3 - 8)) + 8;
            // Brighter columns are "closer": they fall faster.
            c.Speed = (7f + 15f * b + 6f * Rng.Next()) * cell;
            c.Head = initial ? s.Y + (Rng.Next() * 1.4f - 0.3f) * s.H : s.Y - Rng.Next() * s.H * 0.5f - cell;
            c.HeadIndex = 0;
        }

        // Renders every glyph once into a grayscale atlas, one cell per glyph, at the pixel size
        // used on screen.
        void BuildAtlas()
        {
            string chars = "0123456789:.=*+-<>|Z";
            for (char c = '\uFF66'; c <= '\uFF9D'; c++) chars += c;
            glyphCount = chars.Length;
            atlasCols = 10;
            atlasRows = (glyphCount + atlasCols - 1) / atlasCols;
            int w = atlasCols * cell, h = atlasRows * cell;
            using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var font = new Font(PickFont(), cell * 0.92f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var fmt = new StringFormat(StringFormat.GenericTypographic))
                using (var white = new SolidBrush(Color.White))
                {
                    fmt.Alignment = StringAlignment.Center;
                    fmt.LineAlignment = StringAlignment.Center;
                    fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                    g.Clear(Color.Black);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    for (int i = 0; i < glyphCount; i++)
                    {
                        var box = new RectangleF((i % atlasCols) * cell, (i / atlasCols) * cell, cell, cell);
                        g.DrawString(chars[i].ToString(), font, white, box, fmt);
                    }
                }
                atlas = new GpuTexture(Gpu, w, h);
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int p = System.Runtime.InteropServices.Marshal.ReadInt32(bd.Scan0, y * bd.Stride + x * 4);
                            int l = (p >> 8) & 0xFF;     // green channel: the glyph coverage
                            atlas.Pixels[y * w + x] = (255 << 24) | (l << 16) | (l << 8) | l;
                        }
                }
                finally { bmp.UnlockBits(bd); }
                atlas.Upload();
            }
        }

        static string PickFont()
        {
            using (var fonts = new InstalledFontCollection())
            {
                foreach (string want in new[] { "MS Gothic", "Yu Gothic", "Meiryo", "Consolas" })
                    foreach (FontFamily f in fonts.Families)
                        if (f.Name == want) return want;
            }
            return "Courier New";
        }

        public override void Update(FrameContext f)
        {
            float dt = f.Dt;
            // Change a few glyphs everywhere, so the trails shimmer.
            mutate += cols.Length * ringSize * 0.012f * dt * 60f * 0.12f;
            int changes = (int)mutate;
            mutate -= changes;
            for (int i = 0; i < changes; i++) ring[Rng.Int(ring.Length)] = Rng.Int(glyphCount);

            count = 0;
            glow.Clear();
            for (int ci = 0; ci < cols.Length; ci++)
            {
                Column c = cols[ci];
                RectF s = Setup.Screens[c.Screen];
                float prev = c.Head;
                c.Head += c.Speed * dt;
                // Each time the head moves down by a cell, a new glyph enters at the head.
                int steps = (int)(Math.Floor(c.Head / cell) - Math.Floor(prev / cell));
                for (int j = 0; j < steps; j++)
                {
                    c.HeadIndex = (c.HeadIndex + 1) % ringSize;
                    ring[c.Slot + c.HeadIndex] = Rng.Int(glyphCount);
                }
                if (c.Head - c.Length * cell > s.Bottom) { Reset(ref c, false); cols[ci] = c; continue; }
                cols[ci] = c;

                float b = c.Bright;
                for (int k2 = 0; k2 < c.Length; k2++)
                {
                    float y = (float)Math.Floor(c.Head - k2 * cell);
                    if (y < s.Y - cell || y > s.Bottom) continue;
                    // Brightness profile along the trail: bright near the head, fading to nothing.
                    float t = k2 / (float)c.Length;
                    float lv = (float)Math.Pow(1f - t, 1.45f);
                    float r, g, bl, a;
                    if (k2 == 0) { r = 0.80f; g = 1.0f; bl = 0.84f; a = 0.95f * (0.65f + 0.35f * b); }
                    else
                    {
                        float near = k2 <= 2 ? (3f - k2) * 0.18f : 0f;      // the first glyphs behind the head are paler
                        r = near * 0.8f; g = 0.92f + near * 0.08f; bl = 0.27f + near * 0.6f;
                        a = lv * (0.30f + 0.62f * b);
                    }
                    if (a < 0.015f) continue;
                    int o = count++ * Stride;
                    data[o] = c.X; data[o + 1] = y;
                    data[o + 2] = ring[c.Slot + ((c.HeadIndex - k2) % ringSize + ringSize) % ringSize];
                    data[o + 3] = 0f;
                    data[o + 4] = r; data[o + 5] = g; data[o + 6] = bl; data[o + 7] = a;
                }
                float hy = c.Head;
                if (hy > s.Y - cell && hy < s.Bottom + cell)
                    glow.AddDot(c.X + cell * 0.5f, (float)Math.Floor(hy) + cell * 0.5f, 0.01f, cell * 0.9f, 0.55f, 0.30f, 1f, 0.50f, 0.28f + 0.30f * b);
            }
            if (count > 0) Gpu.Upload(buffer, data, count * Stride);
        }

        public override void Render(RenderView view)
        {
            if (count > 0)
            {
                atlas.Bind(1);
                k.Bind();
                Gpu.SetBlend(BlendMode.Add);
                Gpu.SetShaders(vs, ps);
                Gpu.BindResource(0, srv);
                Gpu.Ctx.DrawInstanced(6, (uint)count, 0, 0);
            }
            glow.Draw(BlendMode.Add);
        }
    }
}
