// Spectrum history for the visualizers that show time (waterfall, ridges): a ring of rows, one
// row of 64 bands per push, pushed at a fixed rate so the scroll speed does not depend on the
// frame rate. The shader reads it with History(t, rowsAgo) (prepend VizHistory.Hlsl).
//
//   texture t1   64 x Rows, B8G8R8A8, the level in every channel (0..255)
//   EX[15]       x newest row, y progress towards the next push (0..1), z rows, w spare
//
// A row's place on screen is (rowsAgo + progress) rows from the newest edge, so the picture
// scrolls smoothly between pushes.
using System;

namespace IdleVeil
{
    sealed class VizHistory
    {
        public const int Slot = 15;   // the EX slot this uses

        public const string Hlsl = @"
Texture2D HistTex : register(t1);
// t: band 0..1 (low to high); rowsAgo: distance from the newest edge in rows (fractions blend).
float History(float t, float rowsAgo)
{
    float4 H = EX[15];
    float row = H.x - clamp(rowsAgo - H.y, 0.0, H.z - 2.0);
    float u = (saturate(t) * 63.0 + 0.5) / 64.0;
    return HistTex.SampleLevel(SampWrap, float2(u, (row + 0.5) / H.z), 0).r;
}
";
        public readonly int Rows;
        readonly GpuTexture tex;
        readonly float rate;
        int head;
        public int Total;   // rows pushed so far (a stable id for a row: Total - rowsAgo)
        float acc;
        bool dirty = true;

        public VizHistory(Gpu gpu, int rows, float rowsPerSecond)
        {
            Rows = rows;
            rate = rowsPerSecond;
            tex = new GpuTexture(gpu, 64, rows);
            for (int i = 0; i < tex.Pixels.Length; i++) tex.Pixels[i] = unchecked((int)0xFF000000);
        }

        public float RowsPerSecond { get { return rate; } }

        // Pushes rows of `bands` (0..1) as time passes and returns the EX[Slot] values.
        public void Update(float dt, float[] bands, out float newest, out float progress)
        {
            acc += dt * rate;
            int pushes = Math.Min((int)acc, Rows);
            acc -= (int)acc;
            for (int p = 0; p < pushes; p++)
            {
                head = (head + 1) % Rows;
                Total++;
                int o = head * 64;
                for (int i = 0; i < 64; i++)
                {
                    int v = (int)(Math.Max(0f, Math.Min(1f, bands[i])) * 255f + 0.5f);
                    tex.Pixels[o + i] = unchecked((int)0xFF000000) | (v << 16) | (v << 8) | v;
                }
                dirty = true;
            }
            newest = head;
            progress = acc;
        }

        public void Bind()
        {
            if (dirty) { tex.Upload(); dirty = false; }
            tex.Bind(1);
        }
    }
}
