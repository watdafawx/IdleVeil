// Hand-written COM interop for the parts of D3D11, DXGI and DirectComposition the GPU renderer
// uses. No NuGet, no SharpDX: every interface is declared flat (base-interface methods repeated)
// with the exact vtable order of the Windows SDK headers. Methods nobody calls are declared as
// placeholders (_slotNN) only so that later methods land on the right vtable slot.
//
// Conventions:
//  - Every method is [PreserveSig]: HRESULTs come back as int, void methods stay void.
//  - Resources, views, shaders and states are raw IntPtr (COM pointers we own one reference to,
//    released with Marshal.Release). Only the device, context, swap chain, factory and DComp
//    objects are wrapped as typed interfaces, because those are the ones we call methods on.
//  - Scene code never needs this file: it goes through Gpu (GpuDevice.cs) and the helpers.
using System;
using System.Runtime.InteropServices;

namespace IdleVeil
{
    static class D3D
    {
        // ---- formats, flags, enums (values from d3d11.h / dxgiformat.h) ----
        public const uint FORMAT_UNKNOWN = 0, FORMAT_R32G32B32A32_FLOAT = 2, FORMAT_R8G8B8A8_UNORM = 28,
                          FORMAT_R8_UNORM = 61, FORMAT_B8G8R8A8_UNORM = 87;

        public const uint USAGE_DEFAULT = 0, USAGE_IMMUTABLE = 1, USAGE_DYNAMIC = 2, USAGE_STAGING = 3;
        public const uint BIND_VERTEX_BUFFER = 0x1, BIND_CONSTANT_BUFFER = 0x4, BIND_SHADER_RESOURCE = 0x8, BIND_RENDER_TARGET = 0x20;
        public const uint CPU_ACCESS_WRITE = 0x10000, CPU_ACCESS_READ = 0x20000;
        public const uint MISC_BUFFER_STRUCTURED = 0x40;
        public const uint MAP_READ = 1, MAP_WRITE_DISCARD = 4;
        public const uint TOPOLOGY_TRIANGLELIST = 4, TOPOLOGY_TRIANGLESTRIP = 5;

        public const uint BLEND_ZERO = 1, BLEND_ONE = 2, BLEND_SRC_ALPHA = 5, BLEND_INV_SRC_ALPHA = 6;
        public const uint BLEND_OP_ADD = 1;
        public const uint FILL_SOLID = 3, CULL_NONE = 1;
        public const uint FILTER_MIN_MAG_MIP_POINT = 0, FILTER_MIN_MAG_MIP_LINEAR = 0x15;
        public const uint ADDRESS_WRAP = 1, ADDRESS_CLAMP = 3;
        public const uint COMPARISON_NEVER = 1;
        public const uint SRV_DIMENSION_BUFFER = 1;

        public const uint CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        public const int DRIVER_TYPE_HARDWARE = 1;
        public const uint FEATURE_LEVEL_11_0 = 0xb000, FEATURE_LEVEL_11_1 = 0xb100;
        public const uint SDK_VERSION = 7;

        public const uint DXGI_USAGE_RENDER_TARGET_OUTPUT = 0x20;
        public const uint SWAP_EFFECT_FLIP_SEQUENTIAL = 3, SWAP_EFFECT_FLIP_DISCARD = 4;
        public const uint SCALING_STRETCH = 0;
        public const uint ALPHA_MODE_IGNORE = 3;
        public const uint SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT = 0x40;

        public const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
        public const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);
        public const int DXGI_STATUS_OCCLUDED = 0x087A0001;

        // ---- structs ----
        [StructLayout(LayoutKind.Sequential)]
        public struct BUFFER_DESC { public uint ByteWidth, Usage, BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SUBRESOURCE_DATA { public IntPtr SysMem; public uint Pitch, SlicePitch; }

        [StructLayout(LayoutKind.Sequential)]
        public struct TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MAPPED_SUBRESOURCE { public IntPtr Data; public uint RowPitch, DepthPitch; }

        [StructLayout(LayoutKind.Sequential)]
        public struct VIEWPORT { public float X, Y, Width, Height, MinDepth, MaxDepth; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RT_BLEND_DESC
        {
            public int BlendEnable;
            public uint SrcBlend, DestBlend, BlendOp, SrcBlendAlpha, DestBlendAlpha, BlendOpAlpha;
            public byte WriteMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLEND_DESC
        {
            public int AlphaToCoverage, IndependentBlend;
            public RT_BLEND_DESC RT0, RT1, RT2, RT3, RT4, RT5, RT6, RT7;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RASTERIZER_DESC
        {
            public uint FillMode, CullMode;
            public int FrontCounterClockwise, DepthBias;
            public float DepthBiasClamp, SlopeScaledDepthBias;
            public int DepthClipEnable, ScissorEnable, MultisampleEnable, AntialiasedLineEnable;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SAMPLER_DESC
        {
            public uint Filter, AddressU, AddressV, AddressW;
            public float MipLODBias;
            public uint MaxAnisotropy, ComparisonFunc;
            public float Border0, Border1, Border2, Border3, MinLOD, MaxLOD;
        }

        // D3D11_SHADER_RESOURCE_VIEW_DESC for a buffer: format, dimension, then a 16-byte union.
        [StructLayout(LayoutKind.Sequential)]
        public struct SRV_DESC_BUFFER { public uint Format, ViewDimension, FirstElement, NumElements, Pad0, Pad1; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SWAP_CHAIN_DESC1
        {
            public uint Width, Height, Format;
            public int Stereo;
            public uint SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ADAPTER_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow; public int LuidHigh;
        }

        // ---- entry points ----
        [DllImport("d3d11.dll")]
        public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            uint[] featureLevels, uint numLevels, uint sdkVersion, out IntPtr device, out uint level, out IntPtr context);

        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
        public static extern int D3DCompile(byte[] src, UIntPtr srcSize, string sourceName, IntPtr defines, IntPtr include,
            string entry, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

        [DllImport("dcomp.dll")]
        public static extern int DCompositionCreateDevice(IntPtr dxgiDevice, ref Guid iid, out IntPtr device);

        public const uint COMPILE_OPTIMIZATION_LEVEL3 = 1 << 15, COMPILE_ENABLE_STRICTNESS = 1 << 11;

        public static readonly Guid IID_ID3D11Device = new Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140");
        public static readonly Guid IID_ID3D11DeviceContext = new Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da");
        public static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        public static readonly Guid IID_IDXGIDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        public static readonly Guid IID_IDXGIFactory2 = new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        public static readonly Guid IID_IDXGISwapChain2 = new Guid("a8be2ac4-199f-4946-b331-79599fb98de7");
        public static readonly Guid IID_IDCompositionDevice = new Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3");

        // ID3DBlob is two methods past IUnknown; called through the raw vtable.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr BlobPtrFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate UIntPtr BlobSizeFn(IntPtr self);

        static IntPtr Slot(IntPtr obj, int slot) { return Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size); }

        public static byte[] BlobBytes(IntPtr blob)
        {
            var ptr = (BlobPtrFn)Marshal.GetDelegateForFunctionPointer(Slot(blob, 3), typeof(BlobPtrFn));
            var size = (BlobSizeFn)Marshal.GetDelegateForFunctionPointer(Slot(blob, 4), typeof(BlobSizeFn));
            var bytes = new byte[(int)size(blob).ToUInt64()];
            Marshal.Copy(ptr(blob), bytes, 0, bytes.Length);
            return bytes;
        }

        public static void Release(ref IntPtr p)
        {
            if (p != IntPtr.Zero) { Marshal.Release(p); p = IntPtr.Zero; }
        }

        public static T Wrap<T>(IntPtr p) where T : class
        {
            return (T)Marshal.GetTypedObjectForIUnknown(p, typeof(T));
        }

        // Wraps a raw pointer, makes one call, and drops the wrapper again (the raw reference stays ours).
        public static int Call<T>(IntPtr p, Func<T, int> f) where T : class
        {
            T o = Wrap<T>(p);
            try { return f(o); }
            finally { Marshal.ReleaseComObject(o); }
        }

        public static IntPtr Query(IntPtr p, Guid iid)
        {
            IntPtr q;
            int hr = Marshal.QueryInterface(p, ref iid, out q);
            if (hr < 0) throw new COMException("QueryInterface " + iid + " failed", hr);
            return q;
        }

        public static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + " failed (0x" + hr.ToString("X8") + ")", hr);
        }
    }

    [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ID3D11Device
    {
        [PreserveSig] int CreateBuffer(ref D3D.BUFFER_DESC desc, IntPtr initialData, out IntPtr buffer);          // 3
        void _slot4();
        [PreserveSig] int CreateTexture2D(ref D3D.TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr texture);  // 5
        void _slot6();
        [PreserveSig] int CreateShaderResourceView(IntPtr resource, IntPtr desc, out IntPtr view);              // 7
        void _slot8();
        [PreserveSig] int CreateRenderTargetView(IntPtr resource, IntPtr desc, out IntPtr view);                // 9
        void _slot10(); void _slot11();
        [PreserveSig] int CreateVertexShader([MarshalAs(UnmanagedType.LPArray)] byte[] code, UIntPtr length, IntPtr linkage, out IntPtr shader);  // 12
        void _slot13(); void _slot14();
        [PreserveSig] int CreatePixelShader([MarshalAs(UnmanagedType.LPArray)] byte[] code, UIntPtr length, IntPtr linkage, out IntPtr shader);   // 15
        void _slot16(); void _slot17(); void _slot18(); void _slot19();
        [PreserveSig] int CreateBlendState(ref D3D.BLEND_DESC desc, out IntPtr state);                         // 20
        void _slot21();
        [PreserveSig] int CreateRasterizerState(ref D3D.RASTERIZER_DESC desc, out IntPtr state);               // 22
        [PreserveSig] int CreateSamplerState(ref D3D.SAMPLER_DESC desc, out IntPtr state);                     // 23
        void _slot24(); void _slot25(); void _slot26(); void _slot27(); void _slot28(); void _slot29();
        void _slot30(); void _slot31(); void _slot32(); void _slot33(); void _slot34(); void _slot35(); void _slot36();
        [PreserveSig] uint GetFeatureLevel();                                                                  // 37
        [PreserveSig] uint GetCreationFlags();                                                                 // 38
        [PreserveSig] int GetDeviceRemovedReason();                                                            // 39
    }

    [ComImport, Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ID3D11DeviceContext
    {
        void _slot3(); void _slot4(); void _slot5(); void _slot6();   // ID3D11DeviceChild
        [PreserveSig] void VSSetConstantBuffers(uint start, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] buffers);                     // 7
        [PreserveSig] void PSSetShaderResources(uint start, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] views);                       // 8
        [PreserveSig] void PSSetShader(IntPtr shader, IntPtr classInstances, uint count);                      // 9
        [PreserveSig] void PSSetSamplers(uint start, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] samplers);                           // 10
        [PreserveSig] void VSSetShader(IntPtr shader, IntPtr classInstances, uint count);                      // 11
        void _slot12();
        [PreserveSig] void Draw(uint vertexCount, uint startVertex);                                           // 13
        [PreserveSig] int Map(IntPtr resource, uint subresource, uint mapType, uint flags, out D3D.MAPPED_SUBRESOURCE mapped); // 14
        [PreserveSig] void Unmap(IntPtr resource, uint subresource);                                           // 15
        [PreserveSig] void PSSetConstantBuffers(uint start, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] buffers);                     // 16
        [PreserveSig] void IASetInputLayout(IntPtr layout);                                                    // 17
        void _slot18(); void _slot19(); void _slot20();
        [PreserveSig] void DrawInstanced(uint vertexCountPerInstance, uint instanceCount, uint startVertex, uint startInstance); // 21
        void _slot22(); void _slot23();
        [PreserveSig] void IASetPrimitiveTopology(uint topology);                                              // 24
        [PreserveSig] void VSSetShaderResources(uint start, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] views);                       // 25
        [PreserveSig] void VSSetSamplers(uint start, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] samplers);                           // 26
        void _slot27(); void _slot28(); void _slot29(); void _slot30(); void _slot31(); void _slot32();
        [PreserveSig] void OMSetRenderTargets(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] views, IntPtr depthStencil);                // 33
        void _slot34();
        [PreserveSig] void OMSetBlendState(IntPtr state, [MarshalAs(UnmanagedType.LPArray)] float[] blendFactor, uint sampleMask);                // 35
        void _slot36(); void _slot37(); void _slot38(); void _slot39(); void _slot40(); void _slot41(); void _slot42();
        [PreserveSig] void RSSetState(IntPtr state);                                                           // 43
        [PreserveSig] void RSSetViewports(uint count, ref D3D.VIEWPORT viewport);                              // 44
        void _slot45(); void _slot46();
        [PreserveSig] void CopyResource(IntPtr dst, IntPtr src);                                               // 47
        [PreserveSig] void UpdateSubresource(IntPtr dst, uint subresource, IntPtr box, IntPtr data, uint rowPitch, uint depthPitch); // 48
        void _slot49();
        [PreserveSig] void ClearRenderTargetView(IntPtr view, [MarshalAs(UnmanagedType.LPArray)] float[] color);                                  // 50
        void _slot51(); void _slot52(); void _slot53(); void _slot54(); void _slot55(); void _slot56(); void _slot57();
        void _slot58(); void _slot59(); void _slot60(); void _slot61(); void _slot62(); void _slot63(); void _slot64();
        void _slot65(); void _slot66(); void _slot67(); void _slot68(); void _slot69(); void _slot70(); void _slot71();
        void _slot72(); void _slot73(); void _slot74(); void _slot75(); void _slot76(); void _slot77(); void _slot78();
        void _slot79(); void _slot80(); void _slot81(); void _slot82(); void _slot83(); void _slot84(); void _slot85();
        void _slot86(); void _slot87(); void _slot88(); void _slot89(); void _slot90(); void _slot91(); void _slot92();
        void _slot93(); void _slot94(); void _slot95(); void _slot96(); void _slot97(); void _slot98(); void _slot99();
        void _slot100(); void _slot101(); void _slot102(); void _slot103(); void _slot104(); void _slot105(); void _slot106();
        void _slot107(); void _slot108(); void _slot109();
        [PreserveSig] void ClearState();                                                                       // 110
        [PreserveSig] void Flush();                                                                            // 111
    }

    [ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIDevice
    {
        void _slot3(); void _slot4(); void _slot5(); void _slot6();   // IDXGIObject
        [PreserveSig] int GetAdapter(out IntPtr adapter);                                                      // 7
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter
    {
        void _slot3(); void _slot4(); void _slot5();
        [PreserveSig] int GetParent(ref Guid iid, out IntPtr parent);                                          // 6
        void _slot7();
        [PreserveSig] int GetDesc(out D3D.ADAPTER_DESC desc);                                                  // 8
    }

    [ComImport, Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory2
    {
        void _slot3(); void _slot4(); void _slot5(); void _slot6();   // IDXGIObject
        void _slot7();
        [PreserveSig] int MakeWindowAssociation(IntPtr hwnd, uint flags);                                     // 8
        void _slot9(); void _slot10(); void _slot11(); void _slot12(); void _slot13(); void _slot14();
        [PreserveSig] int CreateSwapChainForHwnd(IntPtr device, IntPtr hwnd, ref D3D.SWAP_CHAIN_DESC1 desc, IntPtr fullscreenDesc, IntPtr restrictToOutput, out IntPtr swapChain); // 15
        void _slot16(); void _slot17(); void _slot18(); void _slot19(); void _slot20(); void _slot21(); void _slot22(); void _slot23();
        [PreserveSig] int CreateSwapChainForComposition(IntPtr device, ref D3D.SWAP_CHAIN_DESC1 desc, IntPtr restrictToOutput, out IntPtr swapChain); // 24
    }

    [ComImport, Guid("a8be2ac4-199f-4946-b331-79599fb98de7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGISwapChain2
    {
        void _slot3(); void _slot4(); void _slot5(); void _slot6();   // IDXGIObject
        void _slot7();                                                // IDXGIDeviceSubObject::GetDevice
        [PreserveSig] int Present(uint syncInterval, uint flags);                                             // 8
        [PreserveSig] int GetBuffer(uint index, ref Guid iid, out IntPtr surface);                            // 9
        void _slot10(); void _slot11(); void _slot12(); void _slot13(); void _slot14(); void _slot15(); void _slot16(); void _slot17();
        void _slot18(); void _slot19(); void _slot20(); void _slot21(); void _slot22(); void _slot23(); void _slot24(); void _slot25();
        void _slot26(); void _slot27(); void _slot28(); void _slot29(); void _slot30();
        [PreserveSig] int SetMaximumFrameLatency(uint maxLatency);                                            // 31
        void _slot32();
        [PreserveSig] IntPtr GetFrameLatencyWaitableObject();                                                 // 33
    }

    [ComImport, Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDCompositionDevice
    {
        [PreserveSig] int Commit();                                                                            // 3
        [PreserveSig] int WaitForCommitCompletion();                                                           // 4
        void _slot5();
        [PreserveSig] int CreateTargetForHwnd(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target); // 6
        [PreserveSig] int CreateVisual(out IntPtr visual);                                                     // 7
    }

    [ComImport, Guid("eacdd04c-117e-4e17-88f4-d1b12b0e3d89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDCompositionTarget
    {
        [PreserveSig] int SetRoot(IntPtr visual);                                                              // 3
    }

    [ComImport, Guid("4d93059d-097b-4651-9a60-f0f25116e2f3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDCompositionVisual
    {
        void _slot3(); void _slot4(); void _slot5(); void _slot6(); void _slot7(); void _slot8(); void _slot9();
        void _slot10(); void _slot11(); void _slot12(); void _slot13(); void _slot14();
        [PreserveSig] int SetContent(IntPtr content);                                                          // 15
    }
}
