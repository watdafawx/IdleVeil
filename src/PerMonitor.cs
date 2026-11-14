// Runs one copy of an animation per monitor, each built for that monitor's own size, so a scene
// is centered and scaled on every screen instead of being stretched across all of them (and cut
// by the bezels). The overlay window still spans the whole virtual screen; each copy draws with
// the viewport moved to its monitor and clipped to it, so it can draw at (0,0)..(w,h) as if it
// had the screen to itself.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IdleVeil
{
    class PerMonitor : IAnimation
    {
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
        [DllImport("gdi32.dll")] static extern bool SetViewportOrgEx(IntPtr hdc, int x, int y, out PT old);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] static extern int SelectClipRgn(IntPtr hdc, IntPtr rgn);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        readonly IAnimation[] parts;
        readonly Rectangle[] rects;   // each monitor, relative to the overlay's top-left

        // area: the overlay's screen rectangle (the virtual screen). make: builds one animation for a monitor of the given size.
        public PerMonitor(Rectangle area, Func<Size, IAnimation> make)
        {
            Screen[] screens = Screen.AllScreens;
            parts = new IAnimation[screens.Length];
            rects = new Rectangle[screens.Length];
            for (int i = 0; i < screens.Length; i++)
            {
                Rectangle b = screens[i].Bounds;
                rects[i] = new Rectangle(b.X - area.X, b.Y - area.Y, b.Width, b.Height);
                parts[i] = make(b.Size);
            }
        }

        public void Step()
        {
            foreach (IAnimation p in parts) p.Step();
        }

        public void Draw(IntPtr hdc)
        {
            for (int i = 0; i < parts.Length; i++)
            {
                Rectangle r = rects[i];
                PT old;
                SetViewportOrgEx(hdc, r.X, r.Y, out old);
                IntPtr rgn = CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom);   // device coordinates
                SelectClipRgn(hdc, rgn);
                DeleteObject(rgn);
                try { parts[i].Draw(hdc); }
                finally
                {
                    SelectClipRgn(hdc, IntPtr.Zero);
                    SetViewportOrgEx(hdc, old.X, old.Y, out old);
                }
            }
        }

        public void Dispose()
        {
            foreach (IAnimation p in parts) p.Dispose();
        }
    }
}
