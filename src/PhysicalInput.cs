// Tells real mouse/keyboard input apart from input injected with SendInput (AI agents, automation,
// remote-control tools). Raw Input reports injected events with no source device (hDevice == 0).
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IdleVeil
{
    sealed class PhysicalInput : NativeWindow, IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll")]
        static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        const int WM_INPUT = 0x00FF;
        const uint RID_INPUT = 0x10000003, RIDEV_INPUTSINK = 0x100;
        const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;
        // Mouse movement smaller than this (in raw counts) per event is treated as sensor jitter.
        const int JitterCounts = 3;
        const uint JitterWindowMs = 300;
        int jitterSum;
        uint jitterSince;

        static readonly int HeaderSize = 8 + 2 * IntPtr.Size;   // RAWINPUTHEADER: dwType, dwSize, hDevice, wParam
        IntPtr buffer = Marshal.AllocHGlobal(256);

        public bool Ok { get; private set; }
        public uint LastPhysicalTick { get; private set; }   // same clock as GetLastInputInfo / Environment.TickCount
        public uint LastInjectedTick { get; private set; }

        public PhysicalInput()
        {
            LastPhysicalTick = LastInjectedTick = (uint)Environment.TickCount;
            var cp = new CreateParams();
            cp.Parent = new IntPtr(-3);   // HWND_MESSAGE: an invisible message-only window
            CreateHandle(cp);
            var devs = new[] {
                new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_INPUTSINK, Target = Handle },   // mouse
                new RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = RIDEV_INPUTSINK, Target = Handle },   // keyboard
            };
            Ok = RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            if (!Ok) Program.Log("RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error() + "; falling back to GetLastInputInfo");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                try { OnRawInput(m.LParam); } catch (Exception ex) { Program.Log("raw input error: " + ex.Message); }
            }
            base.WndProc(ref m);
        }

        void OnRawInput(IntPtr hRaw)
        {
            uint size = 0;
            GetRawInputData(hRaw, RID_INPUT, IntPtr.Zero, ref size, (uint)HeaderSize);
            if (size == 0 || size > 256) return;
            if (GetRawInputData(hRaw, RID_INPUT, buffer, ref size, (uint)HeaderSize) != size) return;

            uint type = (uint)Marshal.ReadInt32(buffer, 0);
            IntPtr device = Marshal.ReadIntPtr(buffer, 8);
            uint now = (uint)Environment.TickCount;
            if (device == IntPtr.Zero) { LastInjectedTick = now; return; }

            if (type == RIM_TYPEKEYBOARD)
            {
                LastPhysicalTick = now;
                LastSource = "keyboard " + DeviceName(device) + " vkey=" + Marshal.ReadInt16(buffer, HeaderSize + 6);
                return;
            }
            if (type == RIM_TYPEMOUSE)
            {
                int d = HeaderSize;
                ushort flags = (ushort)Marshal.ReadInt16(buffer, d);           // usFlags
                ushort buttons = (ushort)Marshal.ReadInt16(buffer, d + 4);     // usButtonFlags
                int dx = Marshal.ReadInt32(buffer, d + 12), dy = Marshal.ReadInt32(buffer, d + 16);
                bool absolute = (flags & 1) != 0;                               // MOUSE_MOVE_ABSOLUTE (tablets, pens)
                // A slow nudge arrives as many 1-2 count events. Sum them over a short window so
                // it still counts, while a lone sensor twitch (one tiny event) does not.
                if ((uint)(now - jitterSince) > JitterWindowMs) { jitterSum = 0; jitterSince = now; }
                jitterSum += Math.Abs(dx) + Math.Abs(dy);
                if (buttons != 0 || absolute || jitterSum >= JitterCounts)
                {
                    jitterSum = 0;
                    LastPhysicalTick = now;
                    LastSource = "mouse " + DeviceName(device) + " dx=" + dx + " dy=" + dy + " buttons=" + buttons + " flags=" + flags;
                }
            }
        }

        // Last device that counted as physical input, for the debug log.
        public string LastSource { get; private set; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, System.Text.StringBuilder data, ref uint size);
        readonly System.Collections.Generic.Dictionary<IntPtr, string> names = new System.Collections.Generic.Dictionary<IntPtr, string>();

        string DeviceName(IntPtr device)
        {
            string n;
            if (names.TryGetValue(device, out n)) return n;
            uint size = 512;
            var sb = new System.Text.StringBuilder(512);
            n = GetRawInputDeviceInfo(device, 0x20000007 /* RIDI_DEVICENAME */, sb, ref size) > 0 ? sb.ToString() : "?";
            names[device] = n;
            return n;
        }

        public void Dispose()
        {
            DestroyHandle();
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; }
        }
    }
}
