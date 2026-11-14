// Live scene thumbnails for the settings window. A background thread with its own D3D11 device
// runs one small SceneHost per wanted thumbnail and renders it off-screen the way --render-test
// does (render target -> staging copy -> Map), then hands the pixels to the UI thread.
//
// Cheap by design: only thumbnails the window currently shows are rendered ("wants", set per page or
// popup), at ~10 fps; the thread sleeps while nothing is wanted or the window is hidden/minimized,
// and hosts that go unused for a while are freed. Thumbnails hear fake audio (SyntheticAudio) unless
// something is actually playing, then the real loopback feed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace IdleVeil
{
    sealed class Thumb
    {
        public readonly string Id;
        public readonly int W, H;
        public volatile string Error;

        readonly object gate = new object();
        byte[] front, back;   // BGRA rows, W*4 bytes each
        bool fresh;
        Bitmap bmp;           // UI thread only

        public Thumb(string id, int w, int h) { Id = id; W = w; H = h; }

        // Render thread: publish a frame.
        internal byte[] BackBuffer { get { return back ?? (back = new byte[W * H * 4]); } }

        internal void Publish()
        {
            lock (gate)
            {
                byte[] t = front; front = back; back = t;
                fresh = true;
            }
        }

        public bool Fresh { get { lock (gate) return fresh; } }

        // UI thread: the newest frame as a bitmap, or null before the first one.
        public Bitmap Image
        {
            get
            {
                lock (gate)
                {
                    if (fresh && front != null)
                    {
                        if (bmp == null) bmp = new Bitmap(W, H, PixelFormat.Format32bppRgb);
                        BitmapData d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                        try
                        {
                            if (d.Stride == W * 4) Marshal.Copy(front, 0, d.Scan0, front.Length);
                            else for (int y = 0; y < H; y++) Marshal.Copy(front, y * W * 4, d.Scan0 + y * d.Stride, W * 4);
                        }
                        finally { bmp.UnlockBits(d); }
                        fresh = false;
                    }
                }
                return bmp;
            }
        }
    }

    sealed class ThumbRenderer : IDisposable
    {
        public int Fps = 10;
        public volatile bool Paused;
        public volatile string Adapter;   // set once the device exists
        public volatile string Error;     // why there is no device
        public event EventHandler Frame;  // raised on the UI thread after a batch of new frames

        readonly Dictionary<string, Thumb> known = new Dictionary<string, Thumb>();
        readonly Dictionary<object, Thumb[]> wants = new Dictionary<object, Thumb[]>();
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly SynchronizationContext ui;
        readonly Thread thread;
        volatile bool stop;
        int framePosted;

        public ThumbRenderer()
        {
            ui = SynchronizationContext.Current;
            thread = new Thread(Run) { IsBackground = true, Name = "IdleVeil thumbnails" };
            thread.Start();
        }

        // UI thread: the thumbnail for a scene at a size in device pixels (no GPU work until wanted).
        public Thumb Get(string id, int w, int h)
        {
            w = Math.Max(16, w); h = Math.Max(16, h);
            string key = id + "|" + w + "x" + h;
            Thumb t;
            lock (gate)
            {
                if (!known.TryGetValue(key, out t)) { t = new Thumb(id, w, h); known[key] = t; }
            }
            return t;
        }

        // Replaces what `owner` (a page, a popup) wants rendered. Pass null or empty to stop.
        public void Want(object owner, ICollection<Thumb> list)
        {
            lock (gate)
            {
                if (list == null || list.Count == 0) wants.Remove(owner);
                else
                {
                    var a = new Thumb[list.Count];
                    list.CopyTo(a, 0);
                    wants[owner] = a;
                }
            }
            wake.Set();
        }

        public bool Wanted(Thumb t)
        {
            lock (gate)
                foreach (Thumb[] a in wants.Values)
                    if (Array.IndexOf(a, t) >= 0) return true;
            return false;
        }

        Thumb[] Snapshot()
        {
            lock (gate)
            {
                var set = new List<Thumb>();
                foreach (Thumb[] a in wants.Values)
                    foreach (Thumb t in a)
                        if (!set.Contains(t)) set.Add(t);
                return set.ToArray();
            }
        }

        sealed class Slot
        {
            public SceneHost Host;
            public IntPtr Tex, Rtv, Staging;
            public long LastUsed;
            public bool Drawn;
        }

        void Run()
        {
            Gpu gpu = null;
            var slots = new Dictionary<Thumb, Slot>();
            var audio = new ThumbAudio();
            var clock = Stopwatch.StartNew();
            try
            {
                string err;
                gpu = Gpu.Create(out err);
                if (gpu == null) { Error = err ?? "no Direct3D 11 device"; Program.Log("thumbnails: no GPU: " + Error); return; }
                Adapter = gpu.AdapterName;
                double last = clock.Elapsed.TotalSeconds;
                while (!stop)
                {
                    Thumb[] want = Paused ? new Thumb[0] : Snapshot();
                    if (want.Length == 0)
                    {
                        FreeIdle(gpu, slots, clock.ElapsedMilliseconds, 0);
                        audio.Idle();
                        wake.WaitOne(2000);
                        last = clock.Elapsed.TotalSeconds;
                        continue;
                    }
                    double now = clock.Elapsed.TotalSeconds;
                    float dt = (float)Math.Min(0.1, now - last);
                    last = now;
                    audio.Tick();
                    var drawn = new List<KeyValuePair<Thumb, Slot>>();
                    foreach (Thumb t in want)
                    {
                        if (stop) break;
                        if (t.Error != null || t.Id == "blank") continue;
                        Slot s;
                        if (!slots.TryGetValue(t, out s))
                        {
                            s = Create(gpu, t, audio);
                            if (s == null) continue;
                            slots[t] = s;
                        }
                        s.LastUsed = clock.ElapsedMilliseconds;
                        try
                        {
                            s.Host.Update(s.Drawn ? dt : 0f);
                            s.Host.Render(0, s.Rtv);
                            gpu.Ctx.CopyResource(s.Staging, s.Tex);
                            s.Drawn = true;
                            drawn.Add(new KeyValuePair<Thumb, Slot>(t, s));
                        }
                        catch (Exception ex)
                        {
                            t.Error = ex.Message;
                            Program.Log("thumbnail " + t.Id + " failed: " + ex.Message);
                        }
                    }
                    // Read back after all draws, so the CPU waits for the GPU once per batch.
                    foreach (var kv in drawn) ReadBack(gpu, kv.Value.Staging, kv.Key);
                    if (drawn.Count > 0) PostFrame();
                    if (gpu.DeviceLost) { Error = "GPU device lost"; break; }
                    FreeIdle(gpu, slots, clock.ElapsedMilliseconds, 15000);
                    int spent = (int)((clock.Elapsed.TotalSeconds - now) * 1000);
                    int wait = Math.Max(5, 1000 / Math.Max(1, Fps) - spent);
                    Thread.Sleep(wait);
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Program.Log("thumbnails failed: " + ex);
            }
            finally
            {
                foreach (Slot s in slots.Values) Free(s);
                slots.Clear();
                audio.Dispose();
                if (gpu != null) try { gpu.Dispose(); } catch { }
            }
        }

        Slot Create(Gpu gpu, Thumb t, ThumbAudio audio)
        {
            var s = new Slot();
            try
            {
                var screen = new GpuScreen { Bounds = new Rectangle(0, 0, t.W, t.H), Device = "thumb", Id = "", Name = "thumb", Primary = true };
                s.Tex = gpu.RenderTexture(t.W, t.H, out s.Rtv);
                s.Staging = gpu.StagingTexture(t.W, t.H);
                s.Host = new SceneHost(gpu, new[] { screen }, ScreenMode.Each, new[] { t.Id }, GpuScenes.UsesAudio(t.Id) ? audio.View() : null, 0.3f);
                return s;
            }
            catch (Exception ex)
            {
                t.Error = ex.Message;
                Program.Log("thumbnail " + t.Id + " init failed: " + ex.Message);
                Free(s);
                return null;
            }
        }

        static void Free(Slot s)
        {
            if (s.Host != null) try { s.Host.Dispose(); } catch { }
            D3D.Release(ref s.Rtv); D3D.Release(ref s.Tex); D3D.Release(ref s.Staging);
        }

        void FreeIdle(Gpu gpu, Dictionary<Thumb, Slot> slots, long now, long idleMs)
        {
            List<Thumb> drop = null;
            foreach (var kv in slots)
                if (now - kv.Value.LastUsed > idleMs || (idleMs == 0 && now - kv.Value.LastUsed > 30000))
                    (drop ?? (drop = new List<Thumb>())).Add(kv.Key);
            if (drop == null) return;
            foreach (Thumb t in drop) { Free(slots[t]); slots.Remove(t); }
        }

        static void ReadBack(Gpu gpu, IntPtr staging, Thumb t)
        {
            D3D.MAPPED_SUBRESOURCE m;
            if (gpu.Ctx.Map(staging, 0, D3D.MAP_READ, 0, out m) < 0) return;
            try
            {
                byte[] buf = t.BackBuffer;
                int row = t.W * 4;
                if (m.RowPitch == row) Marshal.Copy(m.Data, buf, 0, buf.Length);
                else for (int y = 0; y < t.H; y++) Marshal.Copy(m.Data + (int)(y * m.RowPitch), buf, y * row, row);
            }
            finally { gpu.Ctx.Unmap(staging, 0); }
            t.Publish();
        }

        void PostFrame()
        {
            if (ui == null || Interlocked.Exchange(ref framePosted, 1) == 1) return;
            ui.Post(delegate
            {
                Interlocked.Exchange(ref framePosted, 0);
                if (stop) return;
                EventHandler h = Frame;
                if (h != null) h(this, EventArgs.Empty);
            }, null);
        }

        public void Dispose()
        {
            stop = true;
            wake.Set();
            try { thread.Join(3000); } catch { }
        }
    }

    // Audio for the thumbnails, read once per batch and shared by every host through View(). While
    // nothing plays it is SyntheticAudio; a background probe (the Windows peak meter, no capture)
    // checks every few seconds, and once something plays it switches to the real loopback feed.
    sealed class ThumbAudio : IDisposable
    {
        readonly SyntheticAudio synth = new SyntheticAudio();
        LoopbackAudio loop;
        readonly float[] bands = new float[AudioFrame.BandCount], wave = new float[AudioFrame.WaveCount];
        volatile bool probedPlaying, probing;
        int probeAt, quietSince;

        public void Tick()
        {
            int now = Environment.TickCount;
            if (loop == null)
            {
                if (probedPlaying) { loop = new LoopbackAudio(); quietSince = 0; }
                else if (!probing && now - probeAt >= 0)
                {
                    probing = true;
                    probeAt = now + 3000;
                    ThreadPool.QueueUserWorkItem(delegate { try { probedPlaying = SoundProbe.IsPlayingQuiet(); } finally { probing = false; } });
                }
            }
            if (loop != null)
            {
                bool playing = loop.Read(bands, wave);
                if (playing) quietSince = 0;
                else if (quietSince == 0) quietSince = now == 0 ? 1 : now;
                else if (now - quietSince > 4000) { loop.Dispose(); loop = null; probedPlaying = false; probeAt = now + 3000; }
                if (playing) return;
            }
            synth.Read(bands, wave);
        }

        // Stop the real capture while no thumbnails run.
        public void Idle()
        {
            if (loop != null) { loop.Dispose(); loop = null; probedPlaying = false; }
        }

        public IAudioSource View() { return new Viewer(this); }

        sealed class Viewer : IAudioSource
        {
            readonly ThumbAudio src;
            public Viewer(ThumbAudio s) { src = s; }

            public bool Read(float[] b, float[] w)
            {
                Array.Copy(src.bands, b, Math.Min(b.Length, src.bands.Length));
                Array.Copy(src.wave, w, Math.Min(w.Length, src.wave.Length));
                return true;
            }

            public void Dispose() { }
        }

        public void Dispose() { Idle(); }
    }
}
