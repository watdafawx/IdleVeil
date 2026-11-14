// The D3D11 device plus the small set of operations scenes and helpers need: compile shaders,
// make buffers and textures, set the shared pipeline states, upload constants. Everything runs on
// one thread (the render thread, or the --render-test thread).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace IdleVeil
{
    enum BlendMode { Opaque, Add, Alpha }

    sealed class Gpu : IDisposable
    {
        public readonly ID3D11Device Device;
        public readonly ID3D11DeviceContext Ctx;
        public readonly IntPtr DevicePtr, ContextPtr;
        public readonly string AdapterName;
        public readonly uint FeatureLevel;

        IntPtr blendOpaque, blendAdd, blendAlpha, raster, samplerLinear, samplerPoint, samplerWrap;
        IntPtr frameCb, audioCb;
        readonly List<IntPtr> owned = new List<IntPtr>();   // released on Dispose
        readonly IntPtr[] one = new IntPtr[1];
        readonly IntPtr[] three = new IntPtr[3];
        static readonly float[] NoFactor = { 0, 0, 0, 0 };

        // Compiled bytecode by (profile, entry, source), shared by every Gpu in the process, so
        // rebuilding the overlay on a display change doesn't recompile anything.
        static readonly Dictionary<string, byte[]> CodeCache = new Dictionary<string, byte[]>();

        Gpu(IntPtr dev, IntPtr ctx, uint level)
        {
            DevicePtr = dev; ContextPtr = ctx; FeatureLevel = level;
            Device = D3D.Wrap<ID3D11Device>(dev);
            Ctx = D3D.Wrap<ID3D11DeviceContext>(ctx);
            AdapterName = "?";
            try
            {
                IntPtr dxgiDev = D3D.Query(dev, D3D.IID_IDXGIDevice), adapter = IntPtr.Zero;
                if (D3D.Call<IDXGIDevice>(dxgiDev, d => d.GetAdapter(out adapter)) >= 0)
                {
                    D3D.ADAPTER_DESC desc = new D3D.ADAPTER_DESC();
                    if (D3D.Call<IDXGIAdapter>(adapter, a => a.GetDesc(out desc)) >= 0) AdapterName = desc.Description;
                    Marshal.Release(adapter);
                }
                Marshal.Release(dxgiDev);
            }
            catch { }
            CreateStates();
            frameCb = ConstantBuffer(FrameConstants.Floats * 4);
            audioCb = ConstantBuffer(AudioFrame.CbFloats * 4);
        }

        // Hardware device at feature level 11 or better, or null with the reason in `error`.
        public static Gpu Create(out string error)
        {
            error = null;
            try
            {
                IntPtr dev, ctx;
                uint level;
                var levels = new[] { D3D.FEATURE_LEVEL_11_1, D3D.FEATURE_LEVEL_11_0 };
                int hr = D3D.D3D11CreateDevice(IntPtr.Zero, D3D.DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D.CREATE_DEVICE_BGRA_SUPPORT,
                                               levels, (uint)levels.Length, D3D.SDK_VERSION, out dev, out level, out ctx);
                if (hr < 0)   // 11_1 is unknown to plain Windows 7-era runtimes: retry with 11_0 only
                    hr = D3D.D3D11CreateDevice(IntPtr.Zero, D3D.DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D.CREATE_DEVICE_BGRA_SUPPORT,
                                               new[] { D3D.FEATURE_LEVEL_11_0 }, 1, D3D.SDK_VERSION, out dev, out level, out ctx);
                if (hr < 0) { error = "D3D11CreateDevice failed (0x" + hr.ToString("X8") + ")"; return null; }
                return new Gpu(dev, ctx, level);
            }
            catch (DllNotFoundException ex) { error = "d3d11.dll missing: " + ex.Message; }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            return null;
        }

        public bool DeviceLost { get { return Device.GetDeviceRemovedReason() < 0; } }

        // ---- shaders ----

        // Compiles HLSL with the shared prelude (Hlsl.Prelude: constant buffers and helper functions)
        // in front of it. Throws with the compiler's message on errors, so a broken shader fails init
        // and the overlay falls back to GDI instead of showing garbage.
        public IntPtr VertexShader(string source, string entry)
        {
            byte[] code = Compile(Hlsl.Prelude + source, entry, "vs_5_0");
            IntPtr s;
            D3D.Check(Device.CreateVertexShader(code, (UIntPtr)code.Length, IntPtr.Zero, out s), "CreateVertexShader " + entry);
            return Own(s);
        }

        public IntPtr PixelShader(string source, string entry)
        {
            byte[] code = Compile(Hlsl.Prelude + source, entry, "ps_5_0");
            IntPtr s;
            D3D.Check(Device.CreatePixelShader(code, (UIntPtr)code.Length, IntPtr.Zero, out s), "CreatePixelShader " + entry);
            return Own(s);
        }

        static byte[] Compile(string source, string entry, string profile)
        {
            string key = profile + "|" + entry + "|" + source;
            byte[] code;
            lock (CodeCache) if (CodeCache.TryGetValue(key, out code)) return code;
            byte[] src = Encoding.ASCII.GetBytes(source);
            IntPtr blob, errors;
            int hr = D3D.D3DCompile(src, (UIntPtr)src.Length, entry, IntPtr.Zero, IntPtr.Zero, entry, profile,
                                    D3D.COMPILE_OPTIMIZATION_LEVEL3, 0, out blob, out errors);
            string msg = null;
            if (errors != IntPtr.Zero) { msg = Encoding.ASCII.GetString(D3D.BlobBytes(errors)).Trim(); Marshal.Release(errors); }
            if (hr < 0 || blob == IntPtr.Zero) throw new Exception("shader " + entry + " (" + profile + ") failed to compile: " + msg);
            code = D3D.BlobBytes(blob);
            Marshal.Release(blob);
            lock (CodeCache) CodeCache[key] = code;
            return code;
        }

        // ---- resources ----

        public IntPtr ConstantBuffer(int bytes)
        {
            var d = new D3D.BUFFER_DESC
            {
                ByteWidth = (uint)((bytes + 15) & ~15), Usage = D3D.USAGE_DYNAMIC,
                BindFlags = D3D.BIND_CONSTANT_BUFFER, CPUAccessFlags = D3D.CPU_ACCESS_WRITE,
            };
            IntPtr b;
            D3D.Check(Device.CreateBuffer(ref d, IntPtr.Zero, out b), "CreateBuffer (constants)");
            return Own(b);
        }

        // A dynamic structured buffer of `count` elements of `stride` bytes, readable from shaders.
        public IntPtr StructuredBuffer(int stride, int count, out IntPtr srv)
        {
            var d = new D3D.BUFFER_DESC
            {
                ByteWidth = (uint)(stride * count), Usage = D3D.USAGE_DYNAMIC, BindFlags = D3D.BIND_SHADER_RESOURCE,
                CPUAccessFlags = D3D.CPU_ACCESS_WRITE, MiscFlags = D3D.MISC_BUFFER_STRUCTURED, StructureByteStride = (uint)stride,
            };
            IntPtr b;
            D3D.Check(Device.CreateBuffer(ref d, IntPtr.Zero, out b), "CreateBuffer (structured)");
            var sd = new D3D.SRV_DESC_BUFFER { Format = D3D.FORMAT_UNKNOWN, ViewDimension = D3D.SRV_DIMENSION_BUFFER, FirstElement = 0, NumElements = (uint)count };
            IntPtr pd = Marshal.AllocHGlobal(Marshal.SizeOf(sd));
            try
            {
                Marshal.StructureToPtr(sd, pd, false);
                D3D.Check(Device.CreateShaderResourceView(b, pd, out srv), "CreateShaderResourceView (structured)");
            }
            finally { Marshal.FreeHGlobal(pd); }
            Own(srv);
            return Own(b);
        }

        // A texture the CPU rewrites (Map/discard) and shaders sample. format: D3D.FORMAT_*.
        public IntPtr DynamicTexture(int w, int h, uint format, out IntPtr srv)
        {
            var d = new D3D.TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1,
                Usage = D3D.USAGE_DYNAMIC, BindFlags = D3D.BIND_SHADER_RESOURCE, CPUAccessFlags = D3D.CPU_ACCESS_WRITE,
            };
            IntPtr t;
            D3D.Check(Device.CreateTexture2D(ref d, IntPtr.Zero, out t), "CreateTexture2D (dynamic)");
            D3D.Check(Device.CreateShaderResourceView(t, IntPtr.Zero, out srv), "CreateShaderResourceView (texture)");
            Own(srv);
            return Own(t);
        }

        // An off-screen render target (render test, mirror sources). Not owned: the caller releases.
        public IntPtr RenderTexture(int w, int h, out IntPtr rtv)
        {
            var d = new D3D.TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = D3D.FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
                Usage = D3D.USAGE_DEFAULT, BindFlags = D3D.BIND_RENDER_TARGET | D3D.BIND_SHADER_RESOURCE,
            };
            IntPtr t;
            D3D.Check(Device.CreateTexture2D(ref d, IntPtr.Zero, out t), "CreateTexture2D (target)");
            D3D.Check(Device.CreateRenderTargetView(t, IntPtr.Zero, out rtv), "CreateRenderTargetView");
            return t;
        }

        public IntPtr StagingTexture(int w, int h)
        {
            var d = new D3D.TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = D3D.FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
                Usage = D3D.USAGE_STAGING, CPUAccessFlags = D3D.CPU_ACCESS_READ,
            };
            IntPtr t;
            D3D.Check(Device.CreateTexture2D(ref d, IntPtr.Zero, out t), "CreateTexture2D (staging)");
            return t;
        }

        // Writes floats into a dynamic buffer (constant or structured), replacing its contents.
        public void Upload(IntPtr buffer, float[] data, int count)
        {
            D3D.MAPPED_SUBRESOURCE m;
            if (Ctx.Map(buffer, 0, D3D.MAP_WRITE_DISCARD, 0, out m) < 0) return;
            Marshal.Copy(data, 0, m.Data, count);
            Ctx.Unmap(buffer, 0);
        }

        // Writes rows of 32-bit pixels (or bytes for R8) into a dynamic texture.
        public void UploadTexture(IntPtr texture, int[] pixels, int w, int h)
        {
            D3D.MAPPED_SUBRESOURCE m;
            if (Ctx.Map(texture, 0, D3D.MAP_WRITE_DISCARD, 0, out m) < 0) return;
            for (int y = 0; y < h; y++) Marshal.Copy(pixels, y * w, m.Data + (int)(y * m.RowPitch), w);
            Ctx.Unmap(texture, 0);
        }

        public void UploadTexture(IntPtr texture, byte[] pixels, int w, int h)
        {
            D3D.MAPPED_SUBRESOURCE m;
            if (Ctx.Map(texture, 0, D3D.MAP_WRITE_DISCARD, 0, out m) < 0) return;
            for (int y = 0; y < h; y++) Marshal.Copy(pixels, y * w, m.Data + (int)(y * m.RowPitch), w);
            Ctx.Unmap(texture, 0);
        }

        public IntPtr Own(IntPtr p) { owned.Add(p); return p; }

        // Releases a resource made by this Gpu before Dispose (e.g. a scene's buffer it re-creates).
        public void Free(ref IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            if (owned.Remove(p)) Marshal.Release(p);
            p = IntPtr.Zero;
        }

        // ---- pipeline state ----

        void CreateStates()
        {
            blendOpaque = MakeBlend(false, D3D.BLEND_ONE, D3D.BLEND_ZERO);
            blendAdd = MakeBlend(true, D3D.BLEND_ONE, D3D.BLEND_ONE);
            blendAlpha = MakeBlend(true, D3D.BLEND_SRC_ALPHA, D3D.BLEND_INV_SRC_ALPHA);
            var r = new D3D.RASTERIZER_DESC { FillMode = D3D.FILL_SOLID, CullMode = D3D.CULL_NONE, DepthClipEnable = 1 };
            D3D.Check(Device.CreateRasterizerState(ref r, out raster), "CreateRasterizerState");
            Own(raster);
            samplerLinear = MakeSampler(D3D.FILTER_MIN_MAG_MIP_LINEAR, D3D.ADDRESS_CLAMP);
            samplerPoint = MakeSampler(D3D.FILTER_MIN_MAG_MIP_POINT, D3D.ADDRESS_CLAMP);
            samplerWrap = MakeSampler(D3D.FILTER_MIN_MAG_MIP_LINEAR, D3D.ADDRESS_WRAP);
        }

        IntPtr MakeBlend(bool on, uint src, uint dst)
        {
            var d = new D3D.BLEND_DESC();
            d.RT0 = new D3D.RT_BLEND_DESC
            {
                BlendEnable = on ? 1 : 0, SrcBlend = src, DestBlend = dst, BlendOp = D3D.BLEND_OP_ADD,
                SrcBlendAlpha = D3D.BLEND_ONE, DestBlendAlpha = D3D.BLEND_ZERO, BlendOpAlpha = D3D.BLEND_OP_ADD, WriteMask = 0x7,
            };
            IntPtr s;
            D3D.Check(Device.CreateBlendState(ref d, out s), "CreateBlendState");
            return Own(s);
        }

        IntPtr MakeSampler(uint filter, uint address)
        {
            var d = new D3D.SAMPLER_DESC { Filter = filter, AddressU = address, AddressV = address, AddressW = address, MaxAnisotropy = 1, ComparisonFunc = D3D.COMPARISON_NEVER, MaxLOD = float.MaxValue };
            IntPtr s;
            D3D.Check(Device.CreateSamplerState(ref d, out s), "CreateSamplerState");
            return Own(s);
        }

        public void SetBlend(BlendMode m)
        {
            Ctx.OMSetBlendState(m == BlendMode.Add ? blendAdd : m == BlendMode.Alpha ? blendAlpha : blendOpaque, NoFactor, 0xffffffff);
        }

        public void SetShaders(IntPtr vs, IntPtr ps)
        {
            Ctx.VSSetShader(vs, IntPtr.Zero, 0);
            Ctx.PSSetShader(ps, IntPtr.Zero, 0);
        }

        // Binds a constant buffer to the same slot of both stages. Slots 0 and 1 belong to the host
        // (Frame, Audio); scenes use 2 and up.
        public void BindConstants(int slot, IntPtr buffer)
        {
            one[0] = buffer;
            Ctx.VSSetConstantBuffers((uint)slot, 1, one);
            Ctx.PSSetConstantBuffers((uint)slot, 1, one);
        }

        // Binds a shader resource (structured buffer or texture) to the same t-slot of both stages.
        public void BindResource(int slot, IntPtr srv)
        {
            one[0] = srv;
            Ctx.VSSetShaderResources((uint)slot, 1, one);
            Ctx.PSSetShaderResources((uint)slot, 1, one);
        }

        // ---- per view (called by SceneHost before each Render) ----

        public void BeginTarget(IntPtr rtv, float r, float g, float b)
        {
            one[0] = rtv;
            Ctx.OMSetRenderTargets(1, one, IntPtr.Zero);
            Ctx.ClearRenderTargetView(rtv, new[] { r, g, b, 1f });
        }

        public void BeginView(RenderView v, FrameConstants frame, AudioFrame audio)
        {
            var vp = new D3D.VIEWPORT { X = v.Viewport.X, Y = v.Viewport.Y, Width = v.Viewport.W, Height = v.Viewport.H, MaxDepth = 1 };
            Ctx.RSSetViewports(1, ref vp);
            Ctx.RSSetState(raster);
            Ctx.IASetInputLayout(IntPtr.Zero);
            Ctx.IASetPrimitiveTopology(D3D.TOPOLOGY_TRIANGLELIST);
            frame.Write(v);
            Upload(frameCb, frame.Data, FrameConstants.Floats);
            three[0] = frameCb; three[1] = audioCb; three[2] = IntPtr.Zero;
            Ctx.VSSetConstantBuffers(0, 2, three);
            Ctx.PSSetConstantBuffers(0, 2, three);
            three[0] = samplerLinear; three[1] = samplerPoint; three[2] = samplerWrap;
            Ctx.PSSetSamplers(0, 3, three);
            Ctx.VSSetSamplers(0, 3, three);
            SetBlend(BlendMode.Opaque);
        }

        public void UploadAudio(AudioFrame a)
        {
            a.Pack();
            Upload(audioCb, a.Cb, AudioFrame.CbFloats);
        }

        public void Dispose()
        {
            try { Ctx.ClearState(); Ctx.Flush(); } catch { }
            for (int i = owned.Count - 1; i >= 0; i--) Marshal.Release(owned[i]);
            owned.Clear();
            try { Marshal.ReleaseComObject(Ctx); Marshal.ReleaseComObject(Device); } catch { }
            Marshal.Release(ContextPtr);
            Marshal.Release(DevicePtr);
        }
    }

    // The Frame constant buffer (register b0). Layout must match Hlsl.Prelude.
    sealed class FrameConstants
    {
        public const int Floats = 32;
        public readonly float[] Data = new float[Floats];
        public float Time, Dt, U, Seed;
        public RectF World;
        public RectF Focus;          // SceneSetup.Focus
        public float Fade = 1f;
        public float BurnX, BurnY;   // slow burn-in drift (world units)

        public void Write(RenderView v)
        {
            float[] d = Data;
            d[0] = Time; d[1] = Dt; d[2] = U; d[3] = Seed;
            d[4] = World.X; d[5] = World.Y; d[6] = World.W; d[7] = World.H;
            d[8] = v.View.X; d[9] = v.View.Y; d[10] = v.View.W; d[11] = v.View.H;
            d[12] = v.Viewport.X; d[13] = v.Viewport.Y; d[14] = v.Viewport.W; d[15] = v.Viewport.H;
            // 16..19 audio scalars, filled by SceneHost through SetAudio
            d[20] = BurnX; d[21] = BurnY; d[22] = Fade; d[23] = v.View.W / Math.Max(1f, v.Viewport.W);
            d[24] = v.Index; d[25] = 0; d[26] = 0; d[27] = 0;
            d[28] = Focus.X; d[29] = Focus.Y; d[30] = Focus.W; d[31] = Focus.H;
        }

        public void SetAudio(AudioFrame a)
        {
            Data[16] = a.Bass; Data[17] = a.Energy; Data[18] = a.Playing ? 1f : 0f; Data[19] = a.Beat;
        }
    }

    struct RectF
    {
        public float X, Y, W, H;
        public RectF(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
        public float Right { get { return X + W; } }
        public float Bottom { get { return Y + H; } }
        public float CenterX { get { return X + W * 0.5f; } }
        public float CenterY { get { return Y + H * 0.5f; } }
        public bool Contains(float x, float y) { return x >= X && y >= Y && x < X + W && y < Y + H; }
        public override string ToString() { return X + "," + Y + " " + W + "x" + H; }
    }
}
