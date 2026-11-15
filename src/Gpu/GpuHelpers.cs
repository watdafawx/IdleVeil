// Shared building blocks for GPU scenes (see docs/renderer.md):
//   Hlsl.Prelude    constant buffers + helper functions, put in front of every shader
//   FullscreenPass  a pixel shader run over the whole viewport (aurora, plasma, spectrum)
//   SpriteBatch     instanced soft dots / streaks / lines in world coordinates (stars, fireflies)
//   TextBatch       seven-segment digits drawn as distance fields (clocks, counters)
//   SceneConstants  a scene's own constant buffer (register b2)
//   GpuTexture      a small CPU-written texture (cellular grids, fire buffers)
using System;

namespace IdleVeil
{
    static class Hlsl
    {
        public const string Prelude = @"
cbuffer Frame : register(b0)
{
    float  Time;        // seconds since the scene started
    float  Dt;          // seconds since the last frame
    float  U;           // world units per design unit: 1 on a 1080p screen, 1.333 on 1440p
    float  Seed;        // random 0..1, fixed per scene instance
    float4 World;       // x, y, w, h of the whole world (span: the virtual desktop; else one screen)
    float4 View;        // x, y, w, h of the part of the world this viewport shows
    float4 Viewport;    // x, y, w, h of the viewport in render-target pixels
    float4 Audio;       // x bass, y energy (0..1), z playing (0 or 1), w beat pulse (1 on a kick, decays)
    float2 Burn;        // slow burn-in drift in world units: add it to anything that would sit still
    float  Fade;        // 0..1 fade-in; the host applies it, read it only if you need it
    float  PixelWorld;  // world units per screen pixel (1, or more when mirrored onto a smaller screen)
    float  ViewIndex;   // which screen this view is, 0..n-1
    float3 FramePad;
    float4 Focus;       // x, y, w, h of the largest hole-free rectangle (span) or the world: centre compositions here
};

cbuffer AudioData : register(b1)
{
    float4 Bands4[16];  // 64 log-spaced bands, smoothed, 0..1, low to high
    float4 Peaks4[16];  // slowly falling peak per band
    float4 Wave4[128];  // 512 waveform samples, -1..1
};

SamplerState SampLinear : register(s0);   // clamp
SamplerState SampPoint  : register(s1);   // clamp
SamplerState SampWrap   : register(s2);   // linear, wrap

static const float PI = 3.14159265;
static const float TAU = 6.28318531;

// ---- coordinates ----
// SV_Position (render-target pixels) -> world units.
float2 PixelToWorld(float2 svPos) { return View.xy + (svPos - Viewport.xy) * PixelWorld; }
// World units -> clip space of the current viewport (for vertex shaders).
float4 WorldToClip(float2 w)
{
    float2 n = (w - View.xy) / View.zw;
    return float4(n.x * 2.0 - 1.0, 1.0 - n.y * 2.0, 0.0, 1.0);
}
// 0..1 across this view / across the whole world.
float2 ViewUV(float2 w)  { return (w - View.xy) / View.zw; }
float2 WorldUV(float2 w) { return (w - World.xy) / World.zw; }

// ---- color ----
float3 hsv(float h, float s, float v)
{
    float3 k = fmod(float3(5.0, 3.0, 1.0) + frac(h) * 6.0, 6.0);
    return v - v * s * saturate(min(k, 4.0 - k));
}
// Triangular dither of +-1 LSB: dark gradients stay smooth on an 8-bit swap chain.
float3 Dither(float3 c, float2 svPos)
{
    float a = frac(52.9829189 * frac(dot(svPos, float2(0.06711056, 0.00583715))));
    float b = frac(52.9829189 * frac(dot(svPos + 17.0, float2(0.00583715, 0.06711056))));
    return c + (a + b - 1.0) / 255.0;
}
// Final pixel of a full-screen pass: clamp, dither, opaque.
float4 Out(float3 c, float2 svPos) { return float4(Dither(max(c, 0.0), svPos), 1.0); }

// ---- noise ----
float hash11(float p) { p = frac(p * 0.1031); p *= p + 33.33; p *= p + p; return frac(p); }
float hash12(float2 p) { float3 q = frac(float3(p.xyx) * 0.1031); q += dot(q, q.yzx + 33.33); return frac((q.x + q.y) * q.z); }
float2 hash22(float2 p)
{
    float3 q = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
    q += dot(q, q.yzx + 33.33);
    return frac((q.xx + q.yz) * q.zy);
}
float noise(float2 p)
{
    float2 i = floor(p), f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(hash12(i), hash12(i + float2(1, 0)), u.x),
                lerp(hash12(i + float2(0, 1)), hash12(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p, int octaves)
{
    float v = 0.0, a = 0.5;
    for (int i = 0; i < octaves; i++) { v += a * noise(p); p = p * 2.03 + 17.1; a *= 0.5; }
    return v;
}

// ---- shapes (distances in whatever units you pass in) ----
float sdSegment(float2 p, float2 a, float2 b)
{
    float2 pa = p - a, ba = b - a;
    float h = saturate(dot(pa, ba) / max(dot(ba, ba), 1e-6));
    return length(pa - ba * h);
}
float sdRoundBox(float2 p, float2 halfSize, float r)
{
    float2 q = abs(p) - halfSize + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}
// Anti-aliased coverage of a distance field: d < 0 inside, `px` = one pixel in the same units.
float Coverage(float d, float px) { return saturate(0.5 - d / px); }

// ---- audio ----
float BandAt(int i) { i = clamp(i, 0, 63); return Bands4[i >> 2][i & 3]; }
float PeakAt(int i) { i = clamp(i, 0, 63); return Peaks4[i >> 2][i & 3]; }
float WaveAt(int i) { i = clamp(i, 0, 511); return Wave4[i >> 2][i & 3]; }
// Interpolated, t = 0..1.
float Band(float t) { float x = saturate(t) * 63.0; int i = (int)x; return lerp(BandAt(i), BandAt(i + 1), x - i); }
float Peak(float t) { float x = saturate(t) * 63.0; int i = (int)x; return lerp(PeakAt(i), PeakAt(i + 1), x - i); }
float Wave(float t) { float x = saturate(t) * 511.0; int i = (int)x; return lerp(WaveAt(i), WaveAt(i + 1), x - i); }

// ---- full-screen triangle ----
float4 VSFull(uint id : SV_VertexID) : SV_Position
{
    float2 p = float2((id << 1) & 2, id & 2);
    return float4(p * 2.0 - 1.0, 0.0, 1.0);
}
";

        // Instanced capsules: a soft-edged core from A to B with a gaussian glow around it.
        public const string Sprites = @"
struct Sprite { float2 a; float2 b; float radius; float glow; float glowAmp; float tail; float4 color; };
StructuredBuffer<Sprite> Sprites : register(t0);

struct SpriteOut
{
    float4 pos : SV_Position;
    float2 local : TEXCOORD0;
    nointerpolation float4 shape : TEXCOORD1;   // length, radius, pixel, tail
    nointerpolation float4 color : COLOR0;
    nointerpolation float2 glow : TEXCOORD2;    // sigma, amplitude
};

SpriteOut VSSprite(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    Sprite s = Sprites[iid];
    float px = PixelWorld;
    float r = max(s.radius, 0.75 * px);
    float energy = saturate(s.radius / r);          // sub-pixel sprites dim instead of shrinking: no shimmer
    float2 d = s.b - s.a;
    float len = length(d);
    float2 dir = len > 1e-4 ? d / len : float2(1, 0);
    float2 nrm = float2(-dir.y, dir.x);
    float ext = r + (s.glowAmp > 0 ? s.glow * 2.6 : 0) + px;
    float qx = (vid == 1 || vid == 4 || vid == 5) ? 1.0 : 0.0;
    float qy = (vid == 2 || vid == 3 || vid == 5) ? 1.0 : 0.0;
    float lx = lerp(-ext, len + ext, qx), ly = lerp(-ext, ext, qy);
    SpriteOut o;
    o.pos = WorldToClip(s.a + dir * lx + nrm * ly);
    o.local = float2(lx, ly);
    o.shape = float4(len, r, px, s.tail);
    o.color = float4(s.color.rgb * energy, s.color.a);
    o.glow = float2(max(s.glow, 1e-4), s.glowAmp);
    return o;
}

float SpriteLight(SpriteOut i)
{
    float len = i.shape.x, r = i.shape.y, px = i.shape.z;
    float qx = clamp(i.local.x, 0.0, len);
    float d = length(float2(i.local.x - qx, i.local.y));
    float core = saturate((r - d) / px + 0.5);
    float g = i.glow.y > 0 ? exp(-(d * d) / (i.glow.x * i.glow.x)) * i.glow.y : 0.0;
    float along = len > 0 ? qx / len : 1.0;
    return (core + g) * lerp(1.0, along * along, i.shape.w);
}

float4 PSSpriteAdd(SpriteOut i) : SV_Target { return float4(i.color.rgb * (SpriteLight(i) * i.color.a), 0); }
float4 PSSpriteAlpha(SpriteOut i) : SV_Target { return float4(i.color.rgb, saturate(SpriteLight(i) * i.color.a)); }
";

        // Seven-segment glyphs as distance fields. Codes: 0-9 digits, 10 ':', 11 '.', 12 '-', 13 ' '.
        public const string Glyphs = @"
struct Glyph { float4 box; float4 color; float4 style; };   // box: x, y, height, width; style: code, thickness, glow, glowAmp
StructuredBuffer<Glyph> Glyphs : register(t0);

struct GlyphOut
{
    float4 pos : SV_Position;
    float2 local : TEXCOORD0;                     // glyph units: x 0..width/height, y 0..1
    nointerpolation float4 color : COLOR0;
    nointerpolation float4 style : TEXCOORD1;
    nointerpolation float2 size : TEXCOORD2;      // height (world), width/height
};

GlyphOut VSGlyph(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    Glyph g = Glyphs[iid];
    float h = g.box.z, aspect = g.box.w / h;
    float pad = (g.style.w > 0 ? g.style.z * 2.6 : 0) / h + g.style.y + PixelWorld / h;
    float qx = (vid == 1 || vid == 4 || vid == 5) ? 1.0 : 0.0;
    float qy = (vid == 2 || vid == 3 || vid == 5) ? 1.0 : 0.0;
    float2 l = float2(lerp(-pad, aspect + pad, qx), lerp(-pad, 1.0 + pad, qy));
    GlyphOut o;
    o.pos = WorldToClip(g.box.xy + l * h);
    o.local = l;
    o.color = g.color;
    o.style = g.style;
    o.size = float2(h, aspect);
    return o;
}

static const uint SegMask[13] = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F, 0, 0, 0x40 };

float GlyphDist(float2 p, uint code, float aspect)
{
    float2 c = p - float2(aspect * 0.5, 0.5);
    if (code == 10) return min(length(c - float2(0, -0.18)), length(c - float2(0, 0.18)));
    if (code == 11) return length(c - float2(0, 0.40));
    if (code > 12) return 1e3;
    const float hw = 0.19, hh = 0.40, gap = 0.045;
    uint m = SegMask[code];
    float d = 1e3;
    if (m & 0x01) d = min(d, sdSegment(c, float2(-hw + gap, -hh), float2(hw - gap, -hh)));
    if (m & 0x02) d = min(d, sdSegment(c, float2(hw, -hh + gap), float2(hw, -gap)));
    if (m & 0x04) d = min(d, sdSegment(c, float2(hw, gap), float2(hw, hh - gap)));
    if (m & 0x08) d = min(d, sdSegment(c, float2(-hw + gap, hh), float2(hw - gap, hh)));
    if (m & 0x10) d = min(d, sdSegment(c, float2(-hw, gap), float2(-hw, hh - gap)));
    if (m & 0x20) d = min(d, sdSegment(c, float2(-hw, -hh + gap), float2(-hw, -gap)));
    if (m & 0x40) d = min(d, sdSegment(c, float2(-hw + gap, 0), float2(hw - gap, 0)));
    return d;
}

float4 PSGlyph(GlyphOut i) : SV_Target
{
    float h = i.size.x;
    float d = (GlyphDist(i.local, (uint)(i.style.x + 0.5), i.size.y) - i.style.y * 0.5) * h;   // world units, < 0 inside
    float core = Coverage(d, PixelWorld);
    float g = i.style.w > 0 ? exp(-max(d, 0) * max(d, 0) / (i.style.z * i.style.z)) * i.style.w : 0;
    return float4(i.color.rgb * (core + g) * i.color.a, 0);
}
";
    }

    // A pixel shader over the whole viewport. The source must define `float4 PS(float4 pos : SV_Position) : SV_Target`
    // (or pass another entry name); the prelude is added in front.
    sealed class FullscreenPass
    {
        readonly Gpu gpu;
        readonly IntPtr vs, ps;

        public FullscreenPass(Gpu gpu, string pixelShaderSource, string entry = "PS")
        {
            this.gpu = gpu;
            vs = gpu.VertexShader("", "VSFull");
            ps = gpu.PixelShader(pixelShaderSource, entry);
        }

        public void Draw(BlendMode blend = BlendMode.Opaque)
        {
            gpu.SetBlend(blend);
            gpu.SetShaders(vs, ps);
            gpu.Ctx.Draw(3, 0);
        }
    }

    // Instanced soft capsules in world coordinates. Fill once per frame in Update (Clear, Add...),
    // draw in Render; the upload happens on the first Draw after a change.
    sealed class SpriteBatch
    {
        const int Stride = 12;   // floats per sprite, matches `struct Sprite`
        readonly Gpu gpu;
        readonly IntPtr vs, psAdd, psAlpha;
        IntPtr buffer, srv;
        float[] data;
        int capacity, count;
        bool dirty;

        public int Count { get { return count; } }

        public SpriteBatch(Gpu gpu, int capacity)
        {
            this.gpu = gpu;
            vs = gpu.VertexShader(Hlsl.Sprites, "VSSprite");
            psAdd = gpu.PixelShader(Hlsl.Sprites, "PSSpriteAdd");
            psAlpha = gpu.PixelShader(Hlsl.Sprites, "PSSpriteAlpha");
            Allocate(Math.Max(16, capacity));
        }

        void Allocate(int n)
        {
            gpu.Free(ref srv);
            gpu.Free(ref buffer);
            capacity = n;
            data = data == null ? new float[n * Stride] : Resize(data, n * Stride);
            buffer = gpu.StructuredBuffer(Stride * 4, n, out srv);
        }

        static float[] Resize(float[] a, int n) { var b = new float[n]; Array.Copy(a, b, Math.Min(a.Length, n)); return b; }

        public void Clear() { count = 0; dirty = true; }

        // A segment from (ax,ay) to (bx,by) (equal points: a dot). radius: solid core half-width;
        // glow: gaussian sigma of the halo; glowAmp: halo brightness (0: no halo); tail: 0 even,
        // 1 fades towards A (motion streaks). Colors are linear 0..1, `a` scales everything.
        public void Add(float ax, float ay, float bx, float by, float radius, float glow, float glowAmp, float tail,
                        float r, float g, float b, float a)
        {
            if (count == capacity) Allocate(capacity * 2);
            int o = count++ * Stride;
            float[] d = data;
            d[o] = ax; d[o + 1] = ay; d[o + 2] = bx; d[o + 3] = by;
            d[o + 4] = radius; d[o + 5] = glow; d[o + 6] = glowAmp; d[o + 7] = tail;
            d[o + 8] = r; d[o + 9] = g; d[o + 10] = b; d[o + 11] = a;
            dirty = true;
        }

        public void AddDot(float x, float y, float radius, float glow, float glowAmp, float r, float g, float b, float a)
        {
            Add(x, y, x, y, radius, glow, glowAmp, 0f, r, g, b, a);
        }

        public void Draw(BlendMode blend = BlendMode.Add)
        {
            if (count == 0) return;
            if (dirty) { gpu.Upload(buffer, data, count * Stride); dirty = false; }
            gpu.SetBlend(blend);
            gpu.SetShaders(vs, blend == BlendMode.Alpha ? psAlpha : psAdd);
            gpu.BindResource(0, srv);
            gpu.Ctx.DrawInstanced(6, (uint)count, 0, 0);
        }
    }

    // Seven-segment digits, ':' '.' '-' and ' ' as crisp distance fields at any size. Positions and
    // sizes are world units; (x, y) is the top-left of the first glyph.
    sealed class TextBatch
    {
        const int Stride = 12;
        public const float Spacing = 0.14f;   // gap between glyphs, in heights
        readonly Gpu gpu;
        readonly IntPtr vs, ps;
        IntPtr buffer, srv;
        readonly float[] data;
        readonly int capacity;
        int count;
        bool dirty;

        public TextBatch(Gpu gpu, int capacity = 64)
        {
            this.gpu = gpu;
            this.capacity = capacity;
            vs = gpu.VertexShader(Hlsl.Glyphs, "VSGlyph");
            ps = gpu.PixelShader(Hlsl.Glyphs, "PSGlyph");
            data = new float[capacity * Stride];
            buffer = gpu.StructuredBuffer(Stride * 4, capacity, out srv);
        }

        static int Code(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c == ':') return 10;
            if (c == '.') return 11;
            if (c == '-') return 12;
            return 13;
        }

        // Width of one glyph in heights.
        public static float GlyphWidth(char c)
        {
            int code = Code(c);
            return code == 10 || code == 11 ? 0.22f : code == 13 ? 0.40f : 0.56f;
        }

        public static float Measure(string s, float height)
        {
            float w = 0;
            for (int i = 0; i < s.Length; i++) w += (GlyphWidth(s[i]) + (i > 0 ? Spacing : 0)) * height;
            return w;
        }

        public void Clear() { count = 0; dirty = true; }

        // thickness: stroke width in heights (0.08 thin .. 0.16 bold). Returns the drawn width.
        public float Add(string s, float x, float y, float height, float thickness, float glow, float glowAmp,
                         float r, float g, float b, float a)
        {
            float start = x;
            for (int i = 0; i < s.Length && count < capacity; i++)
            {
                float w = GlyphWidth(s[i]) * height;
                int o = count++ * Stride;
                data[o] = x; data[o + 1] = y; data[o + 2] = height; data[o + 3] = w;
                data[o + 4] = r; data[o + 5] = g; data[o + 6] = b; data[o + 7] = a;
                data[o + 8] = Code(s[i]); data[o + 9] = thickness; data[o + 10] = glow; data[o + 11] = glowAmp;
                x += w + Spacing * height;
            }
            dirty = true;
            return x - start - Spacing * height;
        }

        public void Draw()
        {
            if (count == 0) return;
            if (dirty) { gpu.Upload(buffer, data, count * Stride); dirty = false; }
            gpu.SetBlend(BlendMode.Add);
            gpu.SetShaders(vs, ps);
            gpu.BindResource(0, srv);
            gpu.Ctx.DrawInstanced(6, (uint)count, 0, 0);
        }
    }

    // A scene's own constant buffer at register b2 (declare a matching `cbuffer Scene : register(b2)`).
    // Values are float4-packed in declaration order: keep HLSL and C# indices in sync.
    sealed class SceneConstants
    {
        readonly Gpu gpu;
        readonly IntPtr buffer;
        public readonly float[] Values;
        bool dirty = true;

        public SceneConstants(Gpu gpu, int floats)
        {
            this.gpu = gpu;
            Values = new float[(floats + 3) & ~3];
            buffer = gpu.ConstantBuffer(Values.Length * 4);
        }

        public float this[int i] { get { return Values[i]; } set { Values[i] = value; dirty = true; } }

        public void Bind(int slot = 2)
        {
            if (dirty) { gpu.Upload(buffer, Values, Values.Length); dirty = false; }
            gpu.BindConstants(slot, buffer);
        }
    }

    // A small texture the CPU writes (B8G8R8A8: pixel = 0xAARRGGBB as an int). Sample it with
    // SampLinear for smooth fields or SampPoint for crisp cells, and draw the detail in the shader.
    sealed class GpuTexture
    {
        readonly Gpu gpu;
        readonly IntPtr texture, srv;
        public readonly int W, H;
        public readonly int[] Pixels;

        public GpuTexture(Gpu gpu, int w, int h)
        {
            this.gpu = gpu;
            W = w; H = h;
            Pixels = new int[w * h];
            texture = gpu.DynamicTexture(w, h, D3D.FORMAT_B8G8R8A8_UNORM, out srv);
        }

        public void Upload() { gpu.UploadTexture(texture, Pixels, W, H); }
        public void Bind(int slot) { gpu.BindResource(slot, srv); }
    }
}
