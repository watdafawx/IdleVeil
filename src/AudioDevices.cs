// Which audio device the music scenes listen to.
//
// audiodevice= in the ini (or --audio-device on the command line):
//   default    loopback of Windows' default output; follows it when it changes (the old behaviour)
//   auto       loopback of the playback device that has been loudest over the last few seconds,
//              judged from the endpoints' peak meters (IAudioMeterInformation), so nothing extra
//              is captured; re-evaluated every couple of seconds, with hysteresis
//   <id>       one endpoint by its id: a playback device (captured as loopback) or a recording
//              device (captured directly, e.g. Voicemeeter Out B1, often the cleanest full mix).
//              If it is not there, the default output is used until it comes back (logged).
//
// The command line also accepts a part of the device's name (--audio-device "Out B1").
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace IdleVeil
{
    sealed class AudioDevice
    {
        public string Id, Name;
        public bool Capture;     // a recording device (captured directly), else playback (loopback)
        public bool IsDefault;   // Windows' default device of its kind
    }

    static class AudioConfig
    {
        public static string Device = "default";   // ini audiodevice=
        public static string Override;             // --audio-device, wins for this run, never saved

        public static string Effective { get { return Override ?? Device; } }

        public static void Set(string value) { Device = Normalize(value); }

        // "default" and "auto" in lower case, an id or name as written.
        public static string Normalize(string value)
        {
            string v = (value ?? "").Trim(), l = v.ToLowerInvariant();
            return v == "" ? "default" : l == "default" || l == "auto" ? l : v;
        }
    }

    // The device the capture should open right now.
    sealed class AudioTarget
    {
        public string Id, Name;
        public bool Capture;
        public string Note;      // why this one, for the log ("auto: loudest", "not found: ...")
    }

    static class AudioDevices
    {
        const int DEVICE_STATE_ACTIVE = 1;
        static readonly PropertyKey FriendlyName = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

        [StructLayout(LayoutKind.Sequential)]
        public struct PropertyKey
        {
            public Guid Fmtid;
            public int Pid;
            public PropertyKey(Guid g, int pid) { Fmtid = g; Pid = pid; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PropVariant
        {
            public ushort Vt, R1, R2, R3;
            public IntPtr A, B;
        }

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant v);

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        public interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out Wasapi.IMMDevice device);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        public interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1BE09788-6894-4089-8586-9A2A6C265AC5")]
        interface IMMEndpoint
        {
            [PreserveSig] int GetDataFlow(out int flow);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
        public interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
            [PreserveSig] int GetMeteringChannelCount(out uint count);
            [PreserveSig] int GetChannelsPeakValues(uint count, IntPtr peaks);
            [PreserveSig] int QueryHardwareSupport(out uint mask);
        }

        static void Free(object o) { if (o != null) try { Marshal.ReleaseComObject(o); } catch { } }

        public static Wasapi.IMMDeviceEnumerator NewEnumerator()
        {
            return (Wasapi.IMMDeviceEnumerator)new Wasapi.MMDeviceEnumeratorComObject();
        }

        // Active playback devices, then active recording devices, each with its friendly name.
        public static List<AudioDevice> List()
        {
            var list = new List<AudioDevice>();
            Wasapi.IMMDeviceEnumerator e = null;
            try
            {
                e = NewEnumerator();
                Add(e, Wasapi.eRender, list);
                Add(e, Wasapi.eCapture, list);
            }
            catch (Exception ex) { Program.Log("audio: device list failed: " + ex.Message); }
            finally { Free(e); }
            return list;
        }

        static void Add(Wasapi.IMMDeviceEnumerator e, int flow, List<AudioDevice> list)
        {
            string def = DefaultId(e, flow);
            IMMDeviceCollection c;
            if (e.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out c) != 0 || c == null) return;
            try
            {
                uint n;
                c.GetCount(out n);
                for (uint i = 0; i < n; i++)
                {
                    Wasapi.IMMDevice d;
                    if (c.Item(i, out d) != 0 || d == null) continue;
                    try
                    {
                        string id;
                        if (d.GetId(out id) != 0 || id == null) continue;
                        list.Add(new AudioDevice { Id = id, Name = NameOf(d) ?? id, Capture = flow == Wasapi.eCapture, IsDefault = id == def });
                    }
                    finally { Free(d); }
                }
            }
            finally { Free(c); }
        }

        // Just the ids, cheap enough to poll for plugged / unplugged devices.
        public static string Signature()
        {
            var sb = new System.Text.StringBuilder();
            Wasapi.IMMDeviceEnumerator e = null;
            try
            {
                e = NewEnumerator();
                for (int flow = 0; flow < 2; flow++)
                {
                    sb.Append(DefaultId(e, flow)).Append('|');
                    IMMDeviceCollection c;
                    if (e.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out c) != 0 || c == null) continue;
                    uint n;
                    c.GetCount(out n);
                    for (uint i = 0; i < n; i++)
                    {
                        Wasapi.IMMDevice d;
                        if (c.Item(i, out d) != 0 || d == null) continue;
                        string id;
                        if (d.GetId(out id) == 0) sb.Append(id).Append(';');
                        Free(d);
                    }
                    Free(c);
                }
            }
            catch { }
            finally { Free(e); }
            return sb.ToString();
        }

        public static string NameOf(Wasapi.IMMDevice d)
        {
            IPropertyStore store = null;
            try
            {
                if (d.OpenPropertyStore(0, out store) != 0 || store == null) return null;   // STGM_READ
                PropertyKey k = FriendlyName;
                PropVariant v;
                if (store.GetValue(ref k, out v) != 0) return null;
                string s = v.Vt == 31 ? Marshal.PtrToStringUni(v.A) : null;   // VT_LPWSTR
                PropVariantClear(ref v);
                return s;
            }
            catch { return null; }
            finally { Free(store); }
        }

        public static string DefaultId(Wasapi.IMMDeviceEnumerator e, int flow)
        {
            Wasapi.IMMDevice d = null;
            try
            {
                if (e == null || e.GetDefaultAudioEndpoint(flow, Wasapi.eMultimedia, out d) != 0 || d == null) return null;
                string id;
                return d.GetId(out id) == 0 ? id : null;
            }
            catch { return null; }
            finally { Free(d); }
        }

        // The device with this id if it is active, with its kind and name; else null.
        public static AudioTarget Get(Wasapi.IMMDeviceEnumerator e, string id)
        {
            Wasapi.IMMDevice d = null;
            try
            {
                if (e.GetDevice(id, out d) != 0 || d == null) return null;
                int state;
                if (d.GetState(out state) != 0 || state != DEVICE_STATE_ACTIVE) return null;
                int flow = Wasapi.eRender;
                var end = d as IMMEndpoint;
                if (end != null) end.GetDataFlow(out flow);
                return new AudioTarget { Id = id, Name = NameOf(d) ?? id, Capture = flow == Wasapi.eCapture };
            }
            catch { return null; }
            finally { Free(d); }
        }

        // A spec from the command line: an endpoint id, or a part of a device's name.
        public static AudioDevice Find(List<AudioDevice> all, string spec)
        {
            foreach (AudioDevice d in all) if (string.Equals(d.Id, spec, StringComparison.OrdinalIgnoreCase)) return d;
            foreach (AudioDevice d in all) if (d.Name.IndexOf(spec, StringComparison.OrdinalIgnoreCase) >= 0) return d;
            return null;
        }

        static string lastMissing;
        static readonly Dictionary<string, string> nameToId = new Dictionary<string, string>();

        // What to capture for the current setting. auto may be null (then "auto" means the default).
        public static AudioTarget Resolve(Wasapi.IMMDeviceEnumerator e, string spec, AutoPicker auto, string currentId)
        {
            if (spec == "auto" && auto != null)
            {
                string id = auto.Choose(currentId, DefaultId(e, Wasapi.eRender));
                AudioTarget t = id != null ? Get(e, id) : null;
                if (t != null) { t.Note = "auto: loudest playback device"; return t; }
            }
            else if (spec != "default" && spec != "auto" && spec != "")
            {
                string id = spec, mapped;
                lock (nameToId) if (nameToId.TryGetValue(spec, out mapped)) id = mapped;
                AudioTarget t = Get(e, id);
                if (t == null && id == spec)
                {
                    // Not an id we know: try it as a name (command line), once.
                    AudioDevice d = Find(List(), spec);
                    if (d != null) { lock (nameToId) nameToId[spec] = d.Id; t = Get(e, d.Id); }
                }
                if (t != null) { lastMissing = null; t.Note = "chosen in settings"; return t; }
                if (lastMissing != spec)
                {
                    lastMissing = spec;
                    Program.Log("audio: device " + spec + " not found, using the default output until it is back");
                }
            }
            string def = DefaultId(e, Wasapi.eRender);
            if (def == null) return null;
            AudioTarget dt = Get(e, def);
            if (dt != null) dt.Note = "default output";
            return dt;
        }

        // A peak meter on one device (0..1), or null if it has none.
        public static IAudioMeterInformation Meter(Wasapi.IMMDeviceEnumerator e, string id)
        {
            Wasapi.IMMDevice d = null;
            try
            {
                if (e.GetDevice(id, out d) != 0 || d == null) return null;
                Guid iid = typeof(IAudioMeterInformation).GUID;
                object o;
                if (d.Activate(ref iid, Wasapi.CLSCTX_ALL, IntPtr.Zero, out o) != 0 || o == null) return null;
                return (IAudioMeterInformation)o;
            }
            catch { return null; }
            finally { Free(d); }
        }

        public static float Peak(IAudioMeterInformation m)
        {
            float p;
            try { return m != null && m.GetPeakValue(out p) == 0 ? p : 0f; }
            catch { return 0f; }
        }
    }

    // Peak meters on a fixed set of devices, read on the thread that made them.
    sealed class AudioMeters : IDisposable
    {
        readonly AudioDevices.IAudioMeterInformation[] meters;
        public readonly List<AudioDevice> Devices;
        public readonly float[] Levels;   // smoothed peaks, 0..1

        public AudioMeters(List<AudioDevice> devices)
        {
            Devices = devices;
            meters = new AudioDevices.IAudioMeterInformation[devices.Count];
            Levels = new float[devices.Count];
            Wasapi.IMMDeviceEnumerator e = null;
            try
            {
                e = AudioDevices.NewEnumerator();
                for (int i = 0; i < devices.Count; i++) meters[i] = AudioDevices.Meter(e, devices[i].Id);
            }
            catch { }
            finally { if (e != null) try { Marshal.ReleaseComObject(e); } catch { } }
        }

        public float PeakAt(int i) { return AudioDevices.Peak(meters[i]); }

        // Fast rise, slow fall, like a VU meter.
        public void Read(float dt)
        {
            float fall = (float)Math.Exp(-dt * 4.0);
            for (int i = 0; i < meters.Length; i++)
            {
                float p = AudioDevices.Peak(meters[i]);
                Levels[i] = p > Levels[i] ? p : Math.Max(p, Levels[i] * fall);
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < meters.Length; i++)
                if (meters[i] != null) { try { Marshal.ReleaseComObject(meters[i]); } catch { } meters[i] = null; }
        }
    }

    // audiodevice=auto: keeps a few seconds' average of every playback device's peak meter and
    // names the loudest. Lives on the capture thread.
    sealed class AutoPicker : IDisposable
    {
        AudioMeters meters;
        readonly Dictionary<string, float> avg = new Dictionary<string, float>();
        int listedAt, sampledAt;

        public void Sample()
        {
            int now = Environment.TickCount;
            if (meters == null || now - listedAt > 10000)
            {
                listedAt = now;
                var render = new List<AudioDevice>();
                foreach (AudioDevice d in AudioDevices.List()) if (!d.Capture) render.Add(d);
                if (meters != null) meters.Dispose();
                meters = new AudioMeters(render);
            }
            if (now - sampledAt < 250) return;
            float dt = sampledAt == 0 ? 0.25f : Math.Min(1f, (now - sampledAt) / 1000f);
            sampledAt = now;
            float k = 1f - (float)Math.Exp(-dt / 3.0);   // about three seconds
            for (int i = 0; i < meters.Devices.Count; i++)
            {
                string id = meters.Devices[i].Id;
                float a;
                avg.TryGetValue(id, out a);
                avg[id] = a + (meters.PeakAt(i) - a) * k;
            }
        }

        // The loudest device, but only switch away from the current one when another is clearly
        // louder, so two outputs carrying the same mix (Voicemeeter in and the speakers) do not flap.
        public string Choose(string current, string fallback)
        {
            if (meters == null) Sample();
            string best = null;
            float bestLevel = 0f, curLevel = 0f;
            foreach (AudioDevice d in meters.Devices)
            {
                float a;
                avg.TryGetValue(d.Id, out a);
                if (a > bestLevel) { bestLevel = a; best = d.Id; }
                if (d.Id == current) curLevel = a;
            }
            bool currentThere = false;
            foreach (AudioDevice d in meters.Devices) if (d.Id == current) currentThere = true;
            if (best == null || bestLevel < 0.004f) return currentThere ? current : fallback;
            if (!currentThere) return best;
            return best != current && bestLevel > curLevel * 2f + 0.002f ? best : current;
        }

        public void Dispose() { if (meters != null) { meters.Dispose(); meters = null; } }
    }
}
