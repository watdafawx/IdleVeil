// Game controllers (XInput: Xbox pads, and PlayStation/other pads mapped by Steam Input or DS4Windows).
// Mouse/keyboard idle detection doesn't see them, so playing with a pad would otherwise look idle.
using System;
using System.Runtime.InteropServices;

namespace IdleVeil
{
    static class Controllers
    {
        [StructLayout(LayoutKind.Sequential)]
        struct XINPUT_STATE
        {
            public uint Packet;
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short LX, LY, RX, RY;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        static extern uint GetState14(uint index, out XINPUT_STATE state);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        static extern uint GetState910(uint index, out XINPUT_STATE state);

        const int StickDeadzone = 8000, TriggerDeadzone = 30;
        const int EmptySlotRecheckMs = 3000;   // polling an empty slot is slow, so only do it now and then

        static bool useOld, unavailable;
        static readonly bool[] connected = new bool[4];
        static readonly int[] nextCheck = new int[4];
        static readonly XINPUT_STATE[] last = new XINPUT_STATE[4];

        public static uint LastActiveTick { get; private set; }
        public static string LastSource { get; private set; }

        static uint Get(uint i, out XINPUT_STATE s)
        {
            if (!useOld)
            {
                try { return GetState14(i, out s); }
                catch (DllNotFoundException) { useOld = true; }
                catch (EntryPointNotFoundException) { useOld = true; }
            }
            return GetState910(i, out s);
        }

        // Call every tick. Records activity when a button or trigger changes, or a stick is held off-centre.
        public static void Poll()
        {
            if (unavailable) return;
            int now = Environment.TickCount;
            for (uint i = 0; i < 4; i++)
            {
                if (!connected[i] && now - nextCheck[i] < 0) continue;
                XINPUT_STATE s;
                uint r;
                try { r = Get(i, out s); }
                catch (Exception ex) { unavailable = true; Program.Log("XInput unavailable: " + ex.Message); return; }
                if (r != 0)
                {
                    if (connected[i]) Program.Log("controller " + (i + 1) + " disconnected");
                    connected[i] = false;
                    nextCheck[i] = now + EmptySlotRecheckMs;
                    continue;
                }
                if (!connected[i]) { connected[i] = true; last[i] = s; Program.Log("controller " + (i + 1) + " connected"); continue; }

                var p = last[i];
                bool active = s.Buttons != p.Buttons
                    || Math.Abs(s.LeftTrigger - p.LeftTrigger) > TriggerDeadzone || Math.Abs(s.RightTrigger - p.RightTrigger) > TriggerDeadzone
                    || OffCentre(s.LX, s.LY) || OffCentre(s.RX, s.RY);
                last[i] = s;
                if (active) { LastActiveTick = (uint)now; LastSource = "controller " + (i + 1); }
            }
        }

        static bool OffCentre(short x, short y)
        {
            return (long)x * x + (long)y * y > (long)StickDeadzone * StickDeadzone;
        }
    }
}
