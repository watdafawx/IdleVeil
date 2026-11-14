// The live GPU overlay: one borderless window per monitor, each showing a flip-model swap chain
// through DirectComposition, all driven by one render thread.
//
// Why DirectComposition: the overlay must stay click-through (WS_EX_LAYERED | WS_EX_TRANSPARENT),
// never activate, stay topmost and be excluded from capture. A layered window can't take a normal
// flip-model swap chain, but a WS_EX_NOREDIRECTIONBITMAP window can show a composition swap chain
// through a DComp visual while keeping its layered/transparent hit-testing: input goes straight
// through to whatever is underneath, and DWM still honours WDA_EXCLUDEFROMCAPTURE.
//
// Pacing: every swap chain has a frame-latency waitable object (max latency 1). The loop waits for
// all of them (so Present never blocks, and the slowest monitor's vsync paces the frame), sleeps
// on a high-resolution timer to cap the rate (60 fps by default, for high-refresh panels), updates
// the scenes once, then renders and presents each screen with vsync.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace IdleVeil
{
    // What Controller shows: the GPU overlay or the GDI SaverForm.
    interface IVeil : IDisposable
    {
        Rectangle Area { get; }   // the virtual screen it was built for
        bool Broken { get; }      // the GPU device was lost: rebuild
        void ShowSaver();
    }

    sealed class GpuVeil : IVeil
    {
        // ---- user32 / kernel32 ----
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public uint Size, Style; public WndProc Proc; public int ClsExtra, WndExtra;
            public IntPtr Instance, Icon, Cursor, Background; public string MenuName, ClassName; public IntPtr IconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr W, L; public uint Time; public int X, Y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("kernel32.dll")] static extern uint WaitForMultipleObjects(uint n, IntPtr[] handles, bool all, uint ms);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attr, string name, uint flags, uint access);
        [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr fn, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);

        const uint WS_POPUP = 0x80000000;
        const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000,
                   WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_NOACTIVATE = 0x08000000;
        const uint WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21, WM_ERASEBKGND = 0x14;
        const string ClassName = "IdleVeilGpu";

        static WndProc procKeep;   // registered once per process; the delegate must stay alive
        static bool registered;

        static IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            if (msg == WM_NCHITTEST) return new IntPtr(-1);        // HTTRANSPARENT
            if (msg == WM_MOUSEACTIVATE) return new IntPtr(3);     // MA_NOACTIVATE
            if (msg == WM_ERASEBKGND) return new IntPtr(1);
            return DefWindowProc(h, msg, w, l);
        }

        // ---- per screen ----
        sealed class Output
        {
            public GpuScreen Screen;
            public IntPtr Hwnd;
            public IntPtr SwapPtr, Rtv, Waitable, Target, Visual;
            public IDXGISwapChain2 Swap;
        }

        // ---- state ----
        public Rectangle Area { get; private set; }
        public bool Broken { get { return broken; } }
        public static bool Capturable;   // --capturable: don't exclude from capture (to screenshot the overlay while testing)

        readonly GpuScreen[] screens;
        readonly ScreenMode mode;
        readonly string[] sceneIds;
        readonly int fpsCap;
        readonly Thread thread;
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        volatile bool stop, broken;
        string error;

        Gpu gpu;
        IntPtr dcompPtr;
        IDCompositionDevice dcomp;
        readonly List<Output> outputs = new List<Output>();
        SceneHost host;
        IntPtr timer;

        // After a failure the GPU path rests for a while instead of retrying on every show.
        static int disabledUntil;
        static bool disabled;

        GpuVeil(GpuScreen[] screens, ScreenMode mode, string[] sceneIds, int fpsCap)
        {
            this.screens = screens;
            this.mode = mode;
            this.sceneIds = sceneIds;
            this.fpsCap = Math.Max(10, Math.Min(240, fpsCap));
            Area = SystemInformation.VirtualScreen;
            thread = new Thread(Run) { IsBackground = true, Name = "IdleVeil render" };
            thread.SetApartmentState(ApartmentState.STA);
        }

        // Starts the overlay, or returns null (with the reason logged) so the caller can use GDI.
        public static IVeil TryCreate(GpuScreen[] screens, ScreenMode mode, string[] sceneIds, int fpsCap)
        {
            if (disabled && Environment.TickCount - disabledUntil < 0) return null;
            disabled = false;
            var v = new GpuVeil(screens, mode, sceneIds, fpsCap);
            v.thread.Start();
            if (!v.ready.WaitOne(8000)) v.error = v.error ?? "timed out starting";
            if (v.error == null) return v;
            Program.Log("GPU renderer unavailable, using GDI: " + v.error);
            v.Dispose();
            disabled = true;
            disabledUntil = Environment.TickCount + 10 * 60 * 1000;
            return null;
        }

        public void ShowSaver() { }   // shown by the render thread as soon as the first frame is ready

        void Run()
        {
            try
            {
                Init();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Program.Log("GPU init failed: " + ex);
                Cleanup();
                ready.Set();
                return;
            }
            ready.Set();
            try { Loop(); }
            catch (Exception ex)
            {
                Program.Log("GPU render loop failed: " + ex);
                broken = true;
            }
            Cleanup();
        }

        void Init()
        {
            string err;
            gpu = Gpu.Create(out err);
            if (gpu == null) throw new Exception(err);

            IntPtr dxgiDev = D3D.Query(gpu.DevicePtr, D3D.IID_IDXGIDevice);
            try
            {
                Guid iid = D3D.IID_IDCompositionDevice;
                D3D.Check(D3D.DCompositionCreateDevice(dxgiDev, ref iid, out dcompPtr), "DCompositionCreateDevice");
                dcomp = D3D.Wrap<IDCompositionDevice>(dcompPtr);
            }
            finally { Marshal.Release(dxgiDev); }

            IDXGIFactory2 factory = Factory();
            if (!registered)
            {
                procKeep = Proc;
                var wc = new WNDCLASSEX
                {
                    Size = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)), Proc = procKeep, ClassName = ClassName,
                    Instance = Marshal.GetHINSTANCE(typeof(GpuVeil).Module),
                };
                if (RegisterClassEx(ref wc) == 0) throw new Exception("RegisterClassEx failed (" + Marshal.GetLastWin32Error() + ")");
                registered = true;
            }

            foreach (GpuScreen s in screens)
            {
                var o = new Output { Screen = s };
                outputs.Add(o);
                Rectangle b = s.Bounds;
                o.Hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE,
                                        ClassName, "IdleVeil", WS_POPUP, b.X, b.Y, b.Width, b.Height,
                                        IntPtr.Zero, IntPtr.Zero, Marshal.GetHINSTANCE(typeof(GpuVeil).Module), IntPtr.Zero);
                if (o.Hwnd == IntPtr.Zero) throw new Exception("CreateWindowEx failed (" + Marshal.GetLastWin32Error() + ")");
                // A layered window is only hit-tested (and shown) once its attributes are set.
                Native.SetLayeredWindowAttributes(o.Hwnd, 0, 255, 2);
                if (!Capturable && !Native.SetWindowDisplayAffinity(o.Hwnd, 0x11))   // WDA_EXCLUDEFROMCAPTURE
                    Program.Log("SetWindowDisplayAffinity failed: " + Marshal.GetLastWin32Error());

                var desc = new D3D.SWAP_CHAIN_DESC1
                {
                    Width = (uint)b.Width, Height = (uint)b.Height, Format = D3D.FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
                    BufferUsage = D3D.DXGI_USAGE_RENDER_TARGET_OUTPUT, BufferCount = 2, Scaling = D3D.SCALING_STRETCH,
                    SwapEffect = D3D.SWAP_EFFECT_FLIP_SEQUENTIAL, AlphaMode = D3D.ALPHA_MODE_IGNORE,
                    Flags = D3D.SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT,
                };
                IntPtr sc1;
                D3D.Check(factory.CreateSwapChainForComposition(gpu.DevicePtr, ref desc, IntPtr.Zero, out sc1), "CreateSwapChainForComposition");
                try { o.SwapPtr = D3D.Query(sc1, D3D.IID_IDXGISwapChain2); }
                finally { Marshal.Release(sc1); }
                o.Swap = D3D.Wrap<IDXGISwapChain2>(o.SwapPtr);
                o.Swap.SetMaximumFrameLatency(1);
                o.Waitable = o.Swap.GetFrameLatencyWaitableObject();

                IntPtr back;
                Guid tex = D3D.IID_ID3D11Texture2D;
                D3D.Check(o.Swap.GetBuffer(0, ref tex, out back), "GetBuffer");
                try { D3D.Check(gpu.Device.CreateRenderTargetView(back, IntPtr.Zero, out o.Rtv), "CreateRenderTargetView"); }
                finally { Marshal.Release(back); }

                D3D.Check(dcomp.CreateTargetForHwnd(o.Hwnd, true, out o.Target), "CreateTargetForHwnd");
                D3D.Check(dcomp.CreateVisual(out o.Visual), "CreateVisual");
                IntPtr swap = o.SwapPtr, visual = o.Visual;
                D3D.Check(D3D.Call<IDCompositionVisual>(o.Visual, v => v.SetContent(swap)), "SetContent");
                D3D.Check(D3D.Call<IDCompositionTarget>(o.Target, t => t.SetRoot(visual)), "SetRoot");
            }
            Marshal.ReleaseComObject(factory);
            D3D.Check(dcomp.Commit(), "DComp Commit");

            host = new SceneHost(gpu, screens, mode, sceneIds, AudioInput.Open());
            timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* HIGH_RESOLUTION */, 0x1F0003);
            if (timer == IntPtr.Zero) timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, 0x1F0003);

            // First frame (black, fading in) before the windows appear, so nothing stale flashes.
            host.Update(0f);
            foreach (Output o in outputs) WaitForSingleObject(o.Waitable, 100);
            for (int i = 0; i < outputs.Count; i++) { host.Render(i, outputs[i].Rtv); outputs[i].Swap.Present(0, 0); }
            dcomp.WaitForCommitCompletion();
            foreach (Output o in outputs)
            {
                ShowWindow(o.Hwnd, 4);   // SW_SHOWNOACTIVATE
                RaiseToTop(o.Hwnd);
                MarkFullscreen(o.Hwnd);
            }
            Program.Log("GPU overlay on " + gpu.AdapterName + " (fl " + (gpu.FeatureLevel >> 12) + "." + ((gpu.FeatureLevel >> 8) & 15)
                        + "), " + outputs.Count + " screens, cap " + fpsCap + " fps: " + host.Describe());
        }

        IDXGIFactory2 Factory()
        {
            IntPtr dxgiDev = D3D.Query(gpu.DevicePtr, D3D.IID_IDXGIDevice), adapter, factory;
            try
            {
                var dd = D3D.Wrap<IDXGIDevice>(dxgiDev);
                int hr = dd.GetAdapter(out adapter);
                Marshal.ReleaseComObject(dd);
                D3D.Check(hr, "GetAdapter");
                try
                {
                    Guid iid = D3D.IID_IDXGIFactory2;
                    var ad = D3D.Wrap<IDXGIAdapter>(adapter);
                    hr = ad.GetParent(ref iid, out factory);
                    Marshal.ReleaseComObject(ad);
                    D3D.Check(hr, "GetParent(IDXGIFactory2)");
                }
                finally { Marshal.Release(adapter); }
            }
            finally { Marshal.Release(dxgiDev); }
            var f = D3D.Wrap<IDXGIFactory2>(factory);
            Marshal.Release(factory);
            return f;
        }

        static void RaiseToTop(IntPtr h)
        {
            // HWND_TOPMOST, NOSIZE | NOMOVE | NOACTIVATE | SHOWWINDOW
            Native.SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        class TaskbarList { }

        [ComImport, Guid("602D4995-B13A-429b-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList2
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        }

        // Tell the shell we're a fullscreen window so the taskbar steps aside.
        static void MarkFullscreen(IntPtr h)
        {
            try
            {
                var tb = (ITaskbarList2)new TaskbarList();
                tb.HrInit();
                tb.MarkFullscreenWindow(h, true);
                Marshal.ReleaseComObject(tb);
            }
            catch (Exception ex) { Program.Log("MarkFullscreenWindow failed: " + ex.Message); }
        }

        void Pump()
        {
            MSG m;
            while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
            {
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
        }

        void Loop()
        {
            var waits = new IntPtr[outputs.Count];
            for (int i = 0; i < waits.Length; i++) waits[i] = outputs[i].Waitable;
            var clock = Stopwatch.StartNew();
            long freq = Stopwatch.Frequency, period = freq / fpsCap;
            long last = clock.ElapsedTicks, lastRaise = last, statsStart = last;
            double workSum = 0, workMax = 0, gapMax = 0;
            int frames = 0;
            var proc = Process.GetCurrentProcess();
            TimeSpan cpuStart = proc.TotalProcessorTime;

            while (!stop)
            {
                Pump();
                // Cap: on a 60 Hz screen vsync already paces us and this never sleeps.
                long now = clock.ElapsedTicks;
                long due = last + period - freq / 2000;   // aim half a millisecond early, vsync does the rest
                if (now < due)
                {
                    long wait = -(due - now) * 10000000L / freq;   // relative, 100 ns units
                    if (timer != IntPtr.Zero && SetWaitableTimer(timer, ref wait, 0, IntPtr.Zero, IntPtr.Zero, false))
                        WaitForSingleObject(timer, 100);
                    else Thread.Sleep((int)((due - now) * 1000 / freq));
                }
                WaitForMultipleObjects((uint)waits.Length, waits, true, 100);
                if (stop) break;

                long start = clock.ElapsedTicks;
                float dt = (start - last) / (float)freq;
                gapMax = Math.Max(gapMax, (start - last) * 1000.0 / freq);
                last = start;

                host.Update(dt);
                for (int i = 0; i < outputs.Count; i++)
                {
                    host.Render(i, outputs[i].Rtv);
                    int hr = outputs[i].Swap.Present(1, 0);
                    if (hr == D3D.DXGI_ERROR_DEVICE_REMOVED || hr == D3D.DXGI_ERROR_DEVICE_RESET)
                    {
                        Program.Log("GPU device lost (0x" + hr.ToString("X8") + ", reason 0x" + gpu.Device.GetDeviceRemovedReason().ToString("X8") + ")");
                        broken = true;
                        return;
                    }
                }
                double work = (clock.ElapsedTicks - start) * 1000.0 / freq;
                workSum += work; workMax = Math.Max(workMax, work);
                frames++;

                // The taskbar is topmost too and can push itself above us, so re-raise once a second.
                if (start - lastRaise > freq)
                {
                    lastRaise = start;
                    foreach (Output o in outputs) RaiseToTop(o.Hwnd);
                }
                if (SaverForm.Stats && start - statsStart > 5 * freq)
                {
                    double secs = (start - statsStart) / (double)freq;
                    proc.Refresh();
                    TimeSpan cpu = proc.TotalProcessorTime;
                    Program.Log("gpu stats: fps=" + (frames / secs).ToString("0.0") + " frame ms avg=" + (workSum / frames).ToString("0.00")
                                + " max=" + workMax.ToString("0.00") + " longest gap ms=" + gapMax.ToString("0.0")
                                + " cpu=" + ((cpu - cpuStart).TotalMilliseconds / (secs * 1000.0) * 100.0 / Environment.ProcessorCount).ToString("0.0") + "%"
                                + " screens=" + outputs.Count + " " + ScreenModes.Name(host.Mode) + " " + string.Join(",", sceneIds));
                    cpuStart = cpu;
                    frames = 0; workSum = 0; workMax = 0; gapMax = 0; statsStart = start;
                }
            }
        }

        void Cleanup()
        {
            if (host != null) { try { host.Dispose(); } catch { } host = null; }
            foreach (Output o in outputs)
            {
                if (o.Hwnd != IntPtr.Zero) DestroyWindow(o.Hwnd);
                if (o.Waitable != IntPtr.Zero) CloseHandle(o.Waitable);
                D3D.Release(ref o.Rtv);
                D3D.Release(ref o.Visual);
                D3D.Release(ref o.Target);
                if (o.Swap != null) { try { Marshal.ReleaseComObject(o.Swap); } catch { } o.Swap = null; }
                D3D.Release(ref o.SwapPtr);
            }
            outputs.Clear();
            if (dcomp != null) { try { dcomp.Commit(); Marshal.ReleaseComObject(dcomp); } catch { } dcomp = null; }
            D3D.Release(ref dcompPtr);
            if (gpu != null) { try { gpu.Dispose(); } catch { } gpu = null; }
            if (timer != IntPtr.Zero) { CloseHandle(timer); timer = IntPtr.Zero; }
        }

        public void Dispose()
        {
            stop = true;
            if (thread.IsAlive && !thread.Join(3000)) Program.Log("GPU render thread did not stop in time");
            ready.Close();
        }
    }
}
