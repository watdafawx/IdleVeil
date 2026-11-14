// System audio capture for music-reactive scenes.
//
// WASAPI loopback of the default render device on a background thread, then an FFT on the render
// thread: log-spaced bands, a waveform and a "music is playing" detector with hysteresis.
//
// The feed is a shared, reference-counted singleton (Acquire/Release) so several scenes or
// monitors can use the same capture and nothing is recorded while the last user is gone.
// Readers take AudioFeed.Current once per frame; a published AudioSnapshot is never modified
// until the following Update(), which alternates between two buffers.
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace IdleVeil
{
    static class Wasapi
    {
        public const int eRender = 0, eMultimedia = 1;
        public const int CLSCTX_ALL = 0x17;
        public const int AUDCLNT_SHAREMODE_SHARED = 0;
        public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

        public const ushort WAVE_FORMAT_PCM = 1, WAVE_FORMAT_IEEE_FLOAT = 3, WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

        public static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        [DllImport("ole32.dll")]
        public static extern int CoInitializeEx(IntPtr reserved, uint mode);

        [DllImport("ole32.dll")]
        public static extern void CoUninitialize();

        [DllImport("ole32.dll")]
        public static extern void CoTaskMemFree(IntPtr p);

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        public class MMDeviceEnumeratorComObject
        {
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        public interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig]
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig]
            int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig]
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        public interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig]
            int OpenPropertyStore(int access, out IntPtr store);
            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig]
            int GetState(out int state);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
        public interface IAudioClient
        {
            [PreserveSig]
            int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity,
                IntPtr format, IntPtr sessionGuid);
            [PreserveSig]
            int GetBufferSize(out uint frames);
            [PreserveSig]
            int GetStreamLatency(out long latency);
            [PreserveSig]
            int GetCurrentPadding(out uint padding);
            [PreserveSig]
            int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig]
            int GetMixFormat(out IntPtr format);
            [PreserveSig]
            int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
            [PreserveSig]
            int Start();
            [PreserveSig]
            int Stop();
            [PreserveSig]
            int Reset();
            [PreserveSig]
            int SetEventHandle(IntPtr handle);
            [PreserveSig]
            int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
        public interface IAudioCaptureClient
        {
            [PreserveSig]
            int GetBuffer(out IntPtr data, out uint frames, out uint flags,
                out long devicePosition, out long qpcPosition);
            [PreserveSig]
            int ReleaseBuffer(uint frames);
            [PreserveSig]
            int GetNextPacketSize(out uint frames);
        }
    }

    // One frame of analysis, published by AudioFeed.Update(). Treat it as read-only.
    public sealed class AudioSnapshot
    {
        public readonly float[] Bands = new float[AudioFeed.BandCount];   // 0..1, low to high
        public readonly float[] Wave = new float[AudioFeed.WaveCount];     // -1.2..1.2, oldest first
        public float Bass;      // average of the lowest six bands
        public float Peak;      // loudest band this frame
        public bool Playing;    // sound for ~2 s, then quiet for ~2 s to go back
    }

    // Loopback capture + spectrum analysis. Acquire it, call Update() once per frame, read Current.
    public sealed class AudioFeed
    {
        public const int BandCount = 64;
        public const int WaveCount = 512;
        const int FftSize = 2048;             // capture ring length
        const float RangeDb = 40f;            // dB window mapped to 0..1
        const float SilenceMs = 500f;         // no packets this long: treat as silence
        const float PlayLevel = 0.03f;        // a band above this counts as sound
        const float HysteresisSeconds = 2f;   // sound / quiet time before Playing flips

        static readonly object Gate = new object();
        static AudioFeed shared;
        static int users;

        readonly AudioSnapshot snapA = new AudioSnapshot();
        readonly AudioSnapshot snapB = new AudioSnapshot();
        volatile AudioSnapshot current;
        AudioSnapshot write;                  // the buffer Update fills, never published

        // ---- public snapshot (read once per frame) ----
        public AudioSnapshot Current { get { return current; } }
        public float[] Bands { get { return current.Bands; } }
        public float[] Wave { get { return current.Wave; } }
        public float Bass { get { return current.Bass; } }
        public float Peak { get { return current.Peak; } }
        public bool Playing { get { return current.Playing; } }
        public bool Capturing { get { return running && lastDataTick != 0; } }

        public int Packets;                   // packets pulled since start (diagnostics)
        public int SampleRate { get; private set; }

        readonly object ringLock = new object();
        readonly float[] ring = new float[FftSize];
        int ringPos;

        readonly float[] frame = new float[FftSize];
        readonly float[] fftRe = new float[FftSize];
        readonly float[] fftIm = new float[FftSize];
        readonly float[] window = new float[FftSize];
        readonly float[] target = new float[BandCount];
        readonly int[] bandLo = new int[BandCount];
        readonly int[] bandHi = new int[BandCount];
        float ceilingDb = -30f;
        float waveGain = 0.002f;
        float loudFor, quietFor;
        bool playing;
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        double lastUpdate;

        // ---- capture thread ----
        volatile bool running;
        Thread thread;
        volatile int lastDataTick;
        long lastLogTick;
        bool released;

        AudioFeed()
        {
            for (int i = 0; i < FftSize; i++) window[i] = 0.5f - 0.5f * (float)Math.Cos(2.0 * Math.PI * i / FftSize);
            BuildBands(48000);
            SampleRate = 48000;
            current = snapA;
            write = snapB;
            running = true;
            thread = new Thread(CaptureLoop);
            thread.IsBackground = true;
            thread.Name = "IdleVeil audio";
            thread.Start();
        }

        // Ref-counted singleton: capture starts with the first user and stops with the last.
        public static AudioFeed Acquire()
        {
            lock (Gate)
            {
                if (shared == null) shared = new AudioFeed();
                users++;
                return shared;
            }
        }

        public void Release()
        {
            lock (Gate)
            {
                if (released) return;
                released = true;
                if (users > 0) users--;
                if (users > 0) return;
                if (shared == this) shared = null;
            }
            running = false;
            var t = thread;
            thread = null;
            if (t != null)
            {
                try { t.Join(2000); } catch { }
            }
        }

        // Log-spaced bands from 40 Hz to 16 kHz (deckviz's mapping).
        void BuildBands(int rate)
        {
            SampleRate = rate;
            float binHz = rate / (float)FftSize;
            for (int i = 0; i < BandCount; i++)
            {
                float f0 = 40f * (float)Math.Pow(16000.0 / 40.0, i / (float)BandCount);
                float f1 = 40f * (float)Math.Pow(16000.0 / 40.0, (i + 1) / (float)BandCount);
                int b0 = Math.Max(1, (int)Math.Floor(f0 / binHz));
                int b1 = Math.Min(FftSize / 2, Math.Max(b0 + 1, (int)Math.Ceiling(f1 / binHz)));
                bandLo[i] = b0;
                bandHi[i] = b1;
            }
        }

        // Called once per frame by the renderer. The only writer of the published snapshots. The feed
        // is shared (the overlay, settings-window thumbnails, its music indicator): if another thread is
        // already updating, its result is used.
        public void Update()
        {
            if (!Monitor.TryEnter(updateGate)) return;
            try { UpdateCore(); }
            finally { Monitor.Exit(updateGate); }
        }

        readonly object updateGate = new object();

        void UpdateCore()
        {
            double now = clock.Elapsed.TotalSeconds;
            float raw = (float)Math.Max(0.0, now - lastUpdate);
            float dt = Math.Min(0.2f, Math.Max(0.001f, raw));
            lastUpdate = now;
            int tick = Environment.TickCount;
            bool fresh = running && lastDataTick != 0 && (uint)(tick - lastDataTick) < SilenceMs;
            float peakTarget = 0f;
            if (fresh)
            {
                lock (ringLock)
                {
                    for (int i = 0; i < FftSize; i++) frame[i] = ring[(ringPos + i) % FftSize];
                }
                Analyze(dt);
            }
            else
            {
                for (int i = 0; i < BandCount; i++) target[i] = 0f;
            }

            AudioSnapshot s = write;
            // fast rise, gravity fall (deckviz)
            float energy = 0f;
            for (int i = 0; i < BandCount; i++)
            {
                float t = target[i];
                float l = s.Bands[i];
                l = t > l ? l + (t - l) * 0.7f : Math.Max(t, l - 1.6f * dt);
                s.Bands[i] = l;
                energy += l;
                if (t > peakTarget) peakTarget = t;
            }
            s.Bass = 0f;
            for (int i = 0; i < 6; i++) s.Bass += s.Bands[i];
            s.Bass /= 6f;
            s.Peak = peakTarget;

            // waveform with auto gain (instant up, slow down) so quiet music still fills a scene
            int start = FftSize - WaveCount;
            if (fresh)
            {
                float wavePeak = 0f;
                for (int i = start; i < FftSize; i++)
                {
                    float a = Math.Abs(frame[i]);
                    if (a > wavePeak) wavePeak = a;
                }
                waveGain = wavePeak > waveGain ? wavePeak : Math.Max(0.002f, waveGain * (1f - 0.5f * dt));
                float k = wavePeak < 1e-5f ? 0f : 1f / waveGain;
                for (int i = 0; i < WaveCount; i++)
                    s.Wave[i] = Math.Max(-1.2f, Math.Min(1.2f, frame[start + i] * k));
            }
            else
            {
                for (int i = 0; i < WaveCount; i++) s.Wave[i] = 0f;
            }

            // "music is playing": a level above the threshold for ~2 s, quiet for ~2 s to stop
            if (peakTarget > PlayLevel) { loudFor += raw; quietFor = 0f; }
            else { quietFor += raw; loudFor = 0f; }
            if (!playing && loudFor >= HysteresisSeconds)
            {
                playing = true;
                Program.Log("audio: music playing");
            }
            else if (playing && quietFor >= HysteresisSeconds)
            {
                playing = false;
                Program.Log("audio: quiet");
            }
            s.Playing = playing;

            current = s;
            write = s == snapA ? snapB : snapA;
        }

        void Analyze(float dt)
        {
            for (int i = 0; i < FftSize; i++)
            {
                fftRe[i] = frame[i] * window[i];
                fftIm[i] = 0f;
            }
            Fft(fftRe, fftIm, FftSize);
            float frameMax = -120f;
            for (int i = 0; i < BandCount; i++)
            {
                int b0 = bandLo[i], b1 = bandHi[i];
                float p = 0f;
                for (int b = b0; b < b1; b++)
                {
                    float re = fftRe[b], im = fftIm[b];
                    p += re * re + im * im;
                }
                p /= (b1 - b0);
                // +3 dB/octave tilt: music energy falls with frequency
                float tilt = 3f * (i / (float)BandCount) * 8.643856f;   // log2(16000/40)
                float db = 10f * (float)Math.Log10(p + 1e-12) + tilt;
                target[i] = db;
                if (db > frameMax) frameMax = db;
            }
            ceilingDb = frameMax > ceilingDb ? frameMax : Math.Max(-70f, ceilingDb - 3f * dt);
            for (int i = 0; i < BandCount; i++)
                target[i] = Clamp01((target[i] - (ceilingDb - RangeDb)) / RangeDb);
        }

        static float Clamp01(float v) { return v < 0f ? 0f : v > 1f ? 1f : v; }

        // In-place iterative radix-2 FFT (negative exponent).
        static void Fft(float[] re, float[] im, int n)
        {
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    float t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1f, ci = 0f;
                    int half = len >> 1;
                    for (int j = 0; j < half; j++)
                    {
                        int a = i + j, b = a + half;
                        float tr = re[b] * cr - im[b] * ci;
                        float ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        float nr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = nr;
                    }
                }
            }
        }

        // ---- capture ----

        void CaptureLoop()
        {
            Wasapi.CoInitializeEx(IntPtr.Zero, 0);   // MTA
            object enumeratorObj = null;
            Wasapi.IMMDeviceEnumerator enumerator = null;
            Wasapi.IAudioClient client = null;
            Wasapi.IAudioCaptureClient capture = null;
            string deviceId = null;
            int fmtTag = 0, channels = 0, bits = 0;
            float[] fBuf = null;
            short[] sBuf = null;
            int[] iBuf = null;
            byte[] bBuf = null;
            int lastAttempt = 0;
            int lastDeviceCheck = 0;
            try
            {
                while (running)
                {
                    if (client == null)
                    {
                        if (Environment.TickCount - lastAttempt < 3000) { Thread.Sleep(200); continue; }
                        lastAttempt = Environment.TickCount;
                        try
                        {
                            if (enumeratorObj != null)
                            {
                                try { Marshal.ReleaseComObject(enumeratorObj); } catch { }
                                enumeratorObj = null; enumerator = null;
                            }
                            enumeratorObj = new Wasapi.MMDeviceEnumeratorComObject();
                            enumerator = (Wasapi.IMMDeviceEnumerator)enumeratorObj;
                            Wasapi.IMMDevice device;
                            int hr = enumerator.GetDefaultAudioEndpoint(Wasapi.eRender, Wasapi.eMultimedia, out device);
                            if (hr != 0 || device == null) throw new InvalidOperationException("no default audio output (0x" + hr.ToString("x8") + ")");
                            hr = device.GetId(out deviceId);
                            if (hr != 0) deviceId = "?";
                            Guid iid = Wasapi.IID_IAudioClient;
                            object o;
                            hr = device.Activate(ref iid, Wasapi.CLSCTX_ALL, IntPtr.Zero, out o);
                            Marshal.ReleaseComObject(device);
                            if (hr != 0 || o == null) throw new InvalidOperationException("Activate(IAudioClient) failed (0x" + hr.ToString("x8") + ")");
                            client = (Wasapi.IAudioClient)o;
                            IntPtr fmtPtr;
                            hr = client.GetMixFormat(out fmtPtr);
                            if (hr != 0 || fmtPtr == IntPtr.Zero) throw new InvalidOperationException("GetMixFormat failed (0x" + hr.ToString("x8") + ")");
                            var fmt = (Wasapi.WAVEFORMATEX)Marshal.PtrToStructure(fmtPtr, typeof(Wasapi.WAVEFORMATEX));
                            fmtTag = fmt.wFormatTag;
                            channels = Math.Max(1, (int)fmt.nChannels);
                            bits = fmt.wBitsPerSample;
                            if (fmtTag == Wasapi.WAVE_FORMAT_EXTENSIBLE)
                            {
                                // SubFormat GUID starts after WAVEFORMATEX + wValidBitsPerSample + dwChannelMask.
                                int sub = Marshal.ReadInt32(fmtPtr, 24);
                                fmtTag = sub == 3 ? Wasapi.WAVE_FORMAT_IEEE_FLOAT : Wasapi.WAVE_FORMAT_PCM;
                            }
                            hr = client.Initialize(Wasapi.AUDCLNT_SHAREMODE_SHARED, Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK,
                                0, 0, fmtPtr, IntPtr.Zero);
                            if (hr != 0)
                            {
                                // some drivers want an explicit buffer time
                                hr = client.Initialize(Wasapi.AUDCLNT_SHAREMODE_SHARED, Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK,
                                    2000000, 0, fmtPtr, IntPtr.Zero);
                            }
                            Wasapi.CoTaskMemFree(fmtPtr);
                            if (hr != 0) throw new InvalidOperationException("Initialize(loopback) failed (0x" + hr.ToString("x8") + ")");
                            Guid cid = Wasapi.IID_IAudioCaptureClient;
                            object cap;
                            hr = client.GetService(ref cid, out cap);
                            if (hr != 0 || cap == null) throw new InvalidOperationException("GetService(IAudioCaptureClient) failed (0x" + hr.ToString("x8") + ")");
                            capture = (Wasapi.IAudioCaptureClient)cap;
                            uint bufFrames;
                            client.GetBufferSize(out bufFrames);
                            client.Start();
                            BuildBands((int)fmt.nSamplesPerSec);
                            fBuf = null; sBuf = null; iBuf = null; bBuf = null;
                            Log("audio: capturing loopback of " + deviceId + " (" + channels + "ch "
                                + fmt.nSamplesPerSec + "Hz " + (fmtTag == Wasapi.WAVE_FORMAT_IEEE_FLOAT ? "float" : bits + "-bit") + ")");
                        }
                        catch (Exception ex)
                        {
                            Log("audio: capture unavailable: " + ex.Message);
                            Close(client, capture);
                            client = null; capture = null;
                            if (enumeratorObj != null) { try { Marshal.ReleaseComObject(enumeratorObj); } catch { } enumeratorObj = null; enumerator = null; }
                            Thread.Sleep(1000);
                            continue;
                        }
                    }
                    try
                    {
                        // Default output can change at any time (headphones, TV, monitor). Poll it.
                        if (Environment.TickCount - lastDeviceCheck > 2000)
                        {
                            lastDeviceCheck = Environment.TickCount;
                            string now = DefaultDeviceId(enumerator);
                            if (now != null && now != deviceId)
                            {
                                Log("audio: default output changed, restarting capture");
                                Close(client, capture);
                                client = null; capture = null;
                                continue;
                            }
                        }
                        Pull(capture, fmtTag, channels, bits, ref fBuf, ref sBuf, ref iBuf, ref bBuf);
                    }
                    catch (Exception ex)
                    {
                        Log("audio: capture stopped: " + ex.Message);
                        Close(client, capture);
                        client = null; capture = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("audio thread error: " + ex.Message);
            }
            finally
            {
                Close(client, capture);
                if (enumeratorObj != null) { try { Marshal.ReleaseComObject(enumeratorObj); } catch { } }
                Wasapi.CoUninitialize();
            }
        }

        static string DefaultDeviceId(Wasapi.IMMDeviceEnumerator e)
        {
            try
            {
                Wasapi.IMMDevice d;
                if (e == null || e.GetDefaultAudioEndpoint(Wasapi.eRender, Wasapi.eMultimedia, out d) != 0 || d == null) return null;
                string id;
                d.GetId(out id);
                Marshal.ReleaseComObject(d);
                return id;
            }
            catch { return null; }
        }

        static void Close(Wasapi.IAudioClient client, Wasapi.IAudioCaptureClient capture)
        {
            try { if (client != null) client.Stop(); } catch { }
            try { if (capture != null) Marshal.ReleaseComObject(capture); } catch { }
            try { if (client != null) Marshal.ReleaseComObject(client); } catch { }
        }

        void Pull(Wasapi.IAudioCaptureClient capture, int fmtTag, int channels, int bits,
                  ref float[] fBuf, ref short[] sBuf, ref int[] iBuf, ref byte[] bBuf)
        {
            uint next;
            int hr = capture.GetNextPacketSize(out next);
            if (hr != 0) throw new InvalidOperationException("GetNextPacketSize 0x" + hr.ToString("x8"));
            if (next == 0)
            {
                Thread.Sleep(6);
                return;
            }
            while (next > 0 && running)
            {
                IntPtr data;
                uint frames, flags;
                long devPos, qpcPos;
                hr = capture.GetBuffer(out data, out frames, out flags, out devPos, out qpcPos);
                if (hr != 0) throw new InvalidOperationException("GetBuffer 0x" + hr.ToString("x8"));
                if (frames > 0)
                {
                    int n = (int)frames;
                    bool silent = (flags & Wasapi.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                    lock (ringLock)
                    {
                        if (silent)
                        {
                            for (int i = 0; i < n; i++) { ring[ringPos] = 0f; ringPos = (ringPos + 1) % FftSize; }
                        }
                        else if (fmtTag == Wasapi.WAVE_FORMAT_IEEE_FLOAT && bits >= 32)
                        {
                            int count = n * channels;
                            if (fBuf == null || fBuf.Length < count) fBuf = new float[count];
                            Marshal.Copy(data, fBuf, 0, count);
                            for (int i = 0; i < n; i++)
                            {
                                float s = 0f;
                                int o = i * channels;
                                for (int c = 0; c < channels; c++) s += fBuf[o + c];
                                ring[ringPos] = s / channels;
                                ringPos = (ringPos + 1) % FftSize;
                            }
                        }
                        else if (bits == 16)
                        {
                            int count = n * channels;
                            if (sBuf == null || sBuf.Length < count) sBuf = new short[count];
                            Marshal.Copy(data, sBuf, 0, count);
                            for (int i = 0; i < n; i++)
                            {
                                float s = 0f;
                                int o = i * channels;
                                for (int c = 0; c < channels; c++) s += sBuf[o + c] / 32768f;
                                ring[ringPos] = s / channels;
                                ringPos = (ringPos + 1) % FftSize;
                            }
                        }
                        else if (bits == 32)
                        {
                            int count = n * channels;
                            if (iBuf == null || iBuf.Length < count) iBuf = new int[count];
                            Marshal.Copy(data, iBuf, 0, count);
                            for (int i = 0; i < n; i++)
                            {
                                float s = 0f;
                                int o = i * channels;
                                for (int c = 0; c < channels; c++) s += iBuf[o + c] / 2147483648f;
                                ring[ringPos] = s / channels;
                                ringPos = (ringPos + 1) % FftSize;
                            }
                        }
                        else
                        {
                            // 24-bit PCM (3 bytes per sample), rare but cheap to support
                            int count = n * channels * 3;
                            if (bBuf == null || bBuf.Length < count) bBuf = new byte[count];
                            Marshal.Copy(data, bBuf, 0, count);
                            for (int i = 0; i < n; i++)
                            {
                                float s = 0f;
                                for (int c = 0; c < channels; c++)
                                {
                                    int o = (i * channels + c) * 3;
                                    int v = bBuf[o] | (bBuf[o + 1] << 8) | (bBuf[o + 2] << 16);
                                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                                    s += v / 8388608f;
                                }
                                ring[ringPos] = s / channels;
                                ringPos = (ringPos + 1) % FftSize;
                            }
                        }
                    }
                    lastDataTick = Environment.TickCount;
                    Packets++;
                }
                capture.ReleaseBuffer(frames);
                hr = capture.GetNextPacketSize(out next);
                if (hr != 0) throw new InvalidOperationException("GetNextPacketSize 0x" + hr.ToString("x8"));
            }
            Thread.Sleep(4);
        }

        void Log(string msg)
        {
            // never spam: state changes and the first failure only
            long now = Environment.TickCount;
            if (now - lastLogTick < 3000 && !msg.StartsWith("audio: capturing")) return;
            lastLogTick = now;
            Program.Log(msg);
        }

        public string StatsText()
        {
            if (!Capturing) return "audio=off";
            int loud = 0;
            for (int i = 1; i < BandCount; i++) if (Bands[i] > Bands[loud]) loud = i;
            var sb = new System.Text.StringBuilder();
            sb.Append("audio=on pkts=").Append(Packets).Append(" playing=").Append(Playing ? 1 : 0);
            sb.Append(" peak=").Append(Peak.ToString("0.00")).Append(" bass=").Append(Bass.ToString("0.00"));
            sb.Append(" max@").Append(loud).Append('=').Append(Bands[loud].ToString("0.00"));
            sb.Append(" bands=");
            for (int k = 0; k < 8; k++)
            {
                if (k > 0) sb.Append(',');
                sb.Append(Bands[k * 8].ToString("0.00"));
            }
            return sb.ToString();
        }

        // Diagnostic used by --audio-test: log bands, bass and playing for a few seconds.
        public static void AudioTest(int seconds)
        {
            AudioFeed feed = Acquire();
            try
            {
                Program.Log("audio-test: start (" + seconds + "s)");
                int end = Environment.TickCount + seconds * 1000;
                int nextLog = 0;
                while ((int)(end - Environment.TickCount) > 0)
                {
                    feed.Update();
                    if (Environment.TickCount >= nextLog)
                    {
                        nextLog = Environment.TickCount + 500;
                        Program.Log("audio-test: " + feed.StatsText());
                    }
                    Thread.Sleep(100);
                }
                Program.Log("audio-test: done");
            }
            finally
            {
                feed.Release();
            }
        }
    }
}
