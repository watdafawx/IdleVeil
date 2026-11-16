// The small owner-drawn UI kit the settings window is built from, styled after Windows 11 (dark):
//   Ui       theme: DPI scale, colours (with the user's accent), fonts in pixels, drawing helpers
//   Surface  a double-buffered, scrollable control that paints and hit-tests a list of Widgets
//   Widget   a lightweight element with Bounds (in content coordinates), Paint and Click
//   widgets  TextBlock, SettingRow (a card: icon, title, description, control), Toggle, DropDown,
//            PushButton, InfoBar, plus the dark ContextMenuStrip renderer and PopupForm
//
// Everything is drawn by hand (GDI+ shapes, TextRenderer text) and sized through Ui.S(), so the whole
// window re-lays itself out at a new scale when it moves to a monitor with a different DPI.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IdleVeil
{
    static class Dwm
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);

        static void Set(IntPtr h, int attr, int value)
        {
            try { DwmSetWindowAttribute(h, attr, ref value, 4); } catch { }
        }

        public static void DarkTitleBar(IntPtr h) { Set(h, 20, 1); }      // DWMWA_USE_IMMERSIVE_DARK_MODE
        public static void Mica(IntPtr h) { Set(h, 38, 2); }              // DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_MAINWINDOW
        public static void Round(IntPtr h, bool small) { Set(h, 33, small ? 3 : 2); }   // DWMWA_WINDOW_CORNER_PREFERENCE
        public static void BorderColor(IntPtr h, Color c) { Set(h, 34, c.R | (c.G << 8) | (c.B << 16)); }

        public static int DpiOf(IntPtr h)
        {
            try
            {
                uint d = GetDpiForWindow(h);
                if (d != 0) return (int)d;
            }
            catch (EntryPointNotFoundException) { }
            using (Graphics g = Graphics.FromHwnd(h)) return (int)g.DpiX;
        }
    }

    sealed class Ui
    {
        public float Scale = 1f;

        // True while a page is being painted into its static cache: widgets skip the parts that
        // change every frame (live thumbnails), which are drawn over the cache afterwards.
        public bool Caching;

        // Windows 11 dark palette.
        public static readonly Color Window = Color.FromArgb(32, 32, 32);
        public static readonly Color Nav = Color.FromArgb(32, 32, 32);
        public static readonly Color Card = Color.FromArgb(43, 43, 43);
        public static readonly Color CardHover = Color.FromArgb(50, 50, 50);
        public static readonly Color CardPressed = Color.FromArgb(39, 39, 39);
        public static readonly Color CardBorder = Color.FromArgb(29, 29, 29);
        public static readonly Color Stroke = Color.FromArgb(58, 58, 58);
        public static readonly Color Control = Color.FromArgb(55, 55, 55);
        public static readonly Color ControlHover = Color.FromArgb(62, 62, 62);
        public static readonly Color ControlBorder = Color.FromArgb(68, 68, 68);
        public static readonly Color Text = Color.FromArgb(255, 255, 255);
        public static readonly Color TextSecondary = Color.FromArgb(200, 200, 200);
        public static readonly Color TextTertiary = Color.FromArgb(150, 150, 150);
        public static readonly Color TextDisabled = Color.FromArgb(110, 110, 110);
        public static readonly Color NavSelected = Color.FromArgb(45, 45, 45);
        public static readonly Color NavHover = Color.FromArgb(40, 40, 40);
        public static readonly Color Good = Color.FromArgb(108, 203, 95);
        public static readonly Color Caution = Color.FromArgb(252, 225, 0);
        public static readonly Color CautionBack = Color.FromArgb(67, 53, 25);
        public static readonly Color InfoBack = Color.FromArgb(39, 39, 39);
        public Color Accent = Color.FromArgb(96, 205, 255);   // replaced by the user's accent (light shade, as dark mode uses)
        public Color AccentHover, AccentText = Color.Black;

        public Font Body, BodyStrong, Caption, Subtitle, Title, Icon, IconSmall, IconLarge;

        public Ui()
        {
            try
            {
                // AccentPalette: 8 RGBA colours, lightest first; dark mode uses the second (Light2).
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    byte[] p = k == null ? null : k.GetValue("AccentPalette") as byte[];
                    if (p != null && p.Length >= 8) Accent = Color.FromArgb(p[4], p[5], p[6]);
                }
            }
            catch { }
            AccentHover = Mix(Accent, Color.Black, 0.1f);
            SetDpi(96);
        }

        public int S(float v) { return (int)Math.Round(v * Scale); }
        public float SF(float v) { return v * Scale; }

        static readonly HashSet<string> Families = LoadFamilies();

        static HashSet<string> LoadFamilies()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var fonts = new InstalledFontCollection())
                    foreach (FontFamily f in fonts.Families) set.Add(f.Name);
            }
            catch { }
            return set;
        }

        static string Pick(params string[] names)
        {
            foreach (string n in names) if (Families.Contains(n)) return n;
            return "Segoe UI";
        }

        public void SetDpi(int dpi)
        {
            Scale = dpi / 96f;
            Font[] old = { Body, BodyStrong, Caption, Subtitle, Title, Icon, IconSmall, IconLarge };
            string text = Pick("Segoe UI Variable Text", "Segoe UI");
            string strong = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
            string display = Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold");
            string icons = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");
            Body = new Font(text, SF(14), GraphicsUnit.Pixel);
            BodyStrong = new Font(strong, SF(14), GraphicsUnit.Pixel);
            Caption = new Font(text, SF(12), GraphicsUnit.Pixel);
            Subtitle = new Font(display, SF(20), GraphicsUnit.Pixel);
            Title = new Font(display, SF(28), GraphicsUnit.Pixel);
            Icon = new Font(icons, SF(16), GraphicsUnit.Pixel);
            IconSmall = new Font(icons, SF(12), GraphicsUnit.Pixel);
            IconLarge = new Font(icons, SF(20), GraphicsUnit.Pixel);
            foreach (Font f in old) if (f != null) f.Dispose();
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // ---- drawing ----

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void Fill(Graphics g, Rectangle r, float radius, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Round(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        // A 1 px border just inside r.
        public void Border(Graphics g, Rectangle r, float radius, Color c, float width)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = width / 2f;
            using (GraphicsPath p = Round(new RectangleF(r.X + k, r.Y + k, r.Width - width, r.Height - width), Math.Max(0, radius - k)))
            using (var pen = new Pen(c, width))
                g.DrawPath(pen, p);
        }

        public void DrawCard(Graphics g, Rectangle r, Color fill)
        {
            Fill(g, r, SF(6), fill);
            Border(g, r, SF(6), CardBorder, 1f);
        }

        // Paints over the four corners of r outside a rounded rectangle, so a square image looks rounded.
        public void MaskCorners(Graphics g, Rectangle r, float radius, Color back)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var outer = new GraphicsPath())
            using (GraphicsPath inner = Round(new RectangleF(r.X, r.Y, r.Width, r.Height), radius))
            using (var b = new SolidBrush(back))
            {
                outer.AddRectangle(new RectangleF(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2));
                outer.AddPath(inner, false);
                outer.FillMode = FillMode.Alternate;
                g.FillPath(b, outer);
            }
        }

        // Text is rasterised by GDI (the same hinted ClearType the Windows shell uses), then turned
        // into a grayscale mask: only the green subpixel channel is kept. That keeps ClearType's
        // crisp, grid-fitted stems while throwing away its orange/blue fringes, which read as a
        // colour shadow on OLED panels. GDI+ DrawString with AntiAliasGridFit was tried and has no
        // fringes either, but its unhinted small text looks noticeably softer than the title bar.
        //
        // Each line goes through a scratch bitmap and comes back as an alpha mask, so it composites
        // correctly on any card, button or hover colour. Pages are cached (see Surface), so the cost
        // is paid on rebuild, not per frame.
        static Bitmap textScratch, textMask;
        static readonly SolidBrush Black = new SolidBrush(Color.Black);
        static readonly Size MaxMeasure = new Size(int.MaxValue, int.MaxValue);

        static void EnsureTextScratch(int w, int h)
        {
            if (textScratch != null && textScratch.Width >= w && textScratch.Height >= h) return;
            int nw = Math.Max(w, textScratch != null ? textScratch.Width : 0);
            int nh = Math.Max(h, textScratch != null ? textScratch.Height : 0);
            if (textScratch != null) { textScratch.Dispose(); textMask.Dispose(); }
            textScratch = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
            textMask = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
        }

        public unsafe void DrawText(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
        {
            if (string.IsNullOrEmpty(s) || r.Width <= 0 || r.Height <= 0) return;
            flags |= TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int w = r.Width, h = r.Height;
            EnsureTextScratch(w, h);
            using (Graphics sg = Graphics.FromImage(textScratch))
            {
                sg.FillRectangle(Black, new Rectangle(0, 0, w, h));
                TextRenderer.DrawText(sg, s, f, new Rectangle(0, 0, w, h), Color.White, flags);
            }
            var used = new Rectangle(0, 0, w, h);
            BitmapData src = textScratch.LockBits(used, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData dst = textMask.LockBits(used, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int scale = c.A;
                for (int y = 0; y < h; y++)
                {
                    byte* sp = (byte*)src.Scan0 + y * src.Stride;
                    byte* dp = (byte*)dst.Scan0 + y * dst.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        int a = sp[x * 4 + 1] * scale / 255;   // green = coverage
                        dp[x * 4 + 0] = c.B;
                        dp[x * 4 + 1] = c.G;
                        dp[x * 4 + 2] = c.R;
                        dp[x * 4 + 3] = (byte)a;
                    }
                }
            }
            finally
            {
                textScratch.UnlockBits(src);
                textMask.UnlockBits(dst);
            }
            g.DrawImage(textMask, new Rectangle(r.X, r.Y, w, h), used, GraphicsUnit.Pixel);
        }

        // One line, vertically centred in r.
        public void Line(Graphics g, string s, Font f, Color c, Rectangle r)
        {
            DrawText(g, s, f, c, r, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        public void Glyph(Graphics g, string glyph, Font f, Color c, Rectangle r)
        {
            DrawText(g, glyph, f, c, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        public Size Measure(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return Size.Empty;
            Size sz = TextRenderer.MeasureText(s, f, MaxMeasure,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            return new Size(sz.Width + 1, sz.Height + 1);
        }

        public int WrapHeight(string s, Font f, int width)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            Size sz = TextRenderer.MeasureText(s, f, new Size(Math.Max(1, width), int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            return sz.Height + 1;
        }
    }

    // Fluent / MDL2 icon code points used by the settings window.
    static class Glyphs
    {
        public const string Scenes = "\uE771", Screens = "\uE7F4", Timing = "\uE823", Music = "\uE8D6", Apps = "\uE71D", General = "\uE713";
        public const string Play = "\uE768", Note = "\uEC4F", Power = "\uE7E8", Check = "\uE73E", ChevronDown = "\uE70D", Close = "\uE711";
        public const string Add = "\uE710", Delete = "\uE74D", Folder = "\uE8B7", Document = "\uE8A5", Info = "\uE946", Shuffle = "\uE8B1";
        public const string Cursor = "\uE7C9", Robot = "\uE99A", Video = "\uE714", Startup = "\uE7B5", Update = "\uE895", Gpu = "\uE950";
        public const string Monitor = "\uE7F8", Warning = "\uE7BA", Link = "\uE71B", Pause = "\uE769", Clock = "\uE917", Speaker = "\uE767";
        public const string Star = "\uE734", Apps2 = "\uECAA", Copy = "\uE8C8", Sensitivity = "\uE9E9";
    }

    abstract class Widget
    {
        public Rectangle Bounds;
        public Surface Surface;
        public bool Hot, Pressed;
        public bool Enabled = true;

        public virtual bool Interactive { get { return false; } }

        // A widget that changes on its own (a meter, the monitor map) is painted live, not cached.
        public virtual bool Dynamic { get { return false; } }

        public abstract void Paint(Graphics g, Ui ui);

        // The part of an otherwise cached widget that changes every frame (a live thumbnail).
        public virtual void PaintLive(Graphics g, Ui ui) { }

        public virtual void Click(Point p) { }
        public virtual void MouseMove(Point p) { }
        public virtual void MouseLeave() { }

        // A widget dragged with the left button held (a slider), and the release that ends it.
        public virtual bool CapturesDrag { get { return false; } }
        public virtual void MouseDrag(Point p) { }
        public virtual void DragFinished() { }

        // The widget under p (content coordinates) that should get the mouse, or null.
        public virtual Widget Hit(Point p) { return Interactive && Enabled && Bounds.Contains(p) ? this : null; }

        // A state change (hover, selection): repaint the cached pixels too.
        public void Invalidate()
        {
            if (Surface == null) return;
            if (Dynamic) Surface.InvalidateLiveContent(Bounds);
            else Surface.InvalidateContent(Bounds);
        }

        // A new live frame only: the static cache is still right.
        public void InvalidateLive() { if (Surface != null) Surface.InvalidateLiveContent(Bounds); }

        protected Ui UI { get { return Surface.Ui; } }
    }

    // A scrollable canvas of widgets. Widgets are laid out in content coordinates; the surface
    // scrolls them by `Scroll` pixels and draws a thin Windows 11 style scroll bar on the right.
    //
    // Painting: the whole page is drawn once into a bitmap ("the cache"); a paint is then a blit
    // of the visible slice plus the few live parts (thumbnails, meters). Hover and clicks repaint
    // only their own region of the cache. That is what keeps scrolling and live previews at
    // display rate: rasterising text through a scratch bitmap is far too costly to redo per frame.
    sealed class Surface : Control
    {
        public readonly Ui Ui;
        public readonly List<Widget> Widgets = new List<Widget>();
        public Color Back = Ui.Window;
        public int ContentHeight;
        int scroll, target;
        Widget hot, pressed, dragging;
        bool dragBar, barHot;
        int dragStartY, dragStartScroll;
        public event EventHandler Scrolled;

        Bitmap[] bands;          // the page, split into horizontal strips (see PaintCache)
        int cacheW, cacheH;
        bool cacheFull = true;
        readonly List<Rectangle> dirty = new List<Rectangle>();
        Timer glide;

        // GDI text costs time proportional to the bitmap it draws into (every DrawText locks the
        // whole DIB). Strips keep each one small, so a full repaint stays in single-digit ms.
        const int BandHeight = 320;

        // Paint stats for the perf log (see SettingsWindow.LogPerf).
        int paints, rebuilds;
        double paintTotal;
        float paintMax;

        public void TakeStats(out int n, out float avg, out float max, out int rebuildsDone)
        {
            n = paints;
            avg = paints == 0 ? 0f : (float)(paintTotal / paints);
            max = paintMax;
            rebuildsDone = rebuilds;
            paints = 0; paintTotal = 0; paintMax = 0; rebuilds = 0;
        }

        public Surface(Ui ui)
        {
            Ui = ui;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        int Clamp(int v) { return Math.Max(0, Math.Min(Math.Max(0, ContentHeight - Height), v)); }

        public int Scroll
        {
            get { return scroll; }
            set
            {
                int v = Clamp(value);
                if (v == scroll)
                {
                    target = v;
                    if (glide != null) glide.Stop();
                    return;
                }
                scroll = v;
                target = v;
                if (glide != null) glide.Stop();
                Invalidate();
                if (Scrolled != null) Scrolled(this, EventArgs.Empty);
            }
        }

        // Where the wheel is heading: the glide timer eases `Scroll` towards it, a frame at a time.
        public int TargetScroll
        {
            get { return target; }
            set
            {
                int v = Clamp(value);
                if (v == target) return;
                target = v;
                if (v == scroll) return;
                if (glide == null)
                {
                    glide = new Timer { Interval = 15 };
                    glide.Tick += delegate { Step(); };
                }
                if (!glide.Enabled) glide.Start();
            }
        }

        void Step()
        {
            if (scroll == target) { glide.Stop(); return; }
            int d = target - scroll;
            int step = (int)(d * 0.34f);
            if (step == 0) step = d > 0 ? 1 : -1;
            scroll = Math.Abs(d) <= 2 ? target : Clamp(scroll + step);
            Invalidate();
            if (Scrolled != null) Scrolled(this, EventArgs.Empty);
            if (scroll == target) glide.Stop();
        }

        public Rectangle Viewport { get { return new Rectangle(0, scroll, Width, Height); } }

        public void SetWidgets(List<Widget> list, int contentHeight)
        {
            Widgets.Clear();
            foreach (Widget w in list) { w.Surface = this; Widgets.Add(w); }
            hot = pressed = null;
            ContentHeight = contentHeight;
            scroll = Clamp(scroll);
            target = scroll;
            dirty.Clear();
            cacheFull = true;
            RecreateCache();
            Invalidate();
        }

        // The cache follows the control size; a resize rebuilds it at the new width.
        void RecreateCache()
        {
            int h = Math.Max(16, Math.Max(Height, ContentHeight));
            int w = Math.Max(16, Width);
            if (bands != null && cacheW == w && cacheH == h) { cacheFull = true; return; }
            DisposeBands();
            cacheW = w;
            cacheH = h;
            int n = (h + BandHeight - 1) / BandHeight;
            bands = new Bitmap[n];
            for (int i = 0; i < n; i++)
                bands[i] = new Bitmap(w, Math.Min(BandHeight, h - i * BandHeight), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            cacheFull = true;
            dirty.Clear();
        }

        void DisposeBands()
        {
            if (bands == null) return;
            foreach (Bitmap b in bands) if (b != null) b.Dispose();
            bands = null;
        }

        public void InvalidateContent(Rectangle r)
        {
            dirty.Add(r);
            if (dirty.Count > 12) { cacheFull = true; dirty.Clear(); }
            else
            {
                // Merge overlapping regions, so a burst of thumbnails is one repaint.
                for (int i = 0; i < dirty.Count; i++)
                    for (int j = i + 1; j < dirty.Count; j++)
                        if (dirty[i].IntersectsWith(dirty[j]))
                        {
                            dirty[i] = Rectangle.Union(dirty[i], dirty[j]);
                            dirty.RemoveAt(j);
                            i = -1;
                            break;
                        }
            }
            r.Offset(0, -scroll);
            r.Inflate(2, 2);
            Invalidate(r);
        }

        public void InvalidateLiveContent(Rectangle r)
        {
            r.Offset(0, -scroll);
            r.Inflate(2, 2);
            Invalidate(r);
        }

        public Point ScreenPoint(Rectangle contentRect, bool below)
        {
            return PointToScreen(new Point(contentRect.X, (below ? contentRect.Bottom : contentRect.Y) - scroll));
        }

        public Rectangle ScreenRect(Rectangle contentRect)
        {
            Point p = PointToScreen(new Point(contentRect.X, contentRect.Y - scroll));
            return new Rectangle(p, contentRect.Size);
        }

        Rectangle BarTrack { get { return new Rectangle(Width - Ui.S(12), Ui.S(4), Ui.S(12), Height - Ui.S(8)); } }

        Rectangle BarThumb
        {
            get
            {
                Rectangle t = BarTrack;
                if (ContentHeight <= Height) return Rectangle.Empty;
                int h = Math.Max(Ui.S(40), t.Height * Height / ContentHeight);
                int y = t.Y + (int)((long)(t.Height - h) * scroll / Math.Max(1, ContentHeight - Height));
                return new Rectangle(t.X, y, t.Width, h);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            long started = Stopwatch.GetTimestamp();
            Graphics g = e.Graphics;
            g.Clear(Back);
            if (bands == null || cacheW != Width || cacheH < Height) RecreateCache();
            if (cacheFull) PaintCache(Rectangle.Empty);
            else FlushDirty();

            Rectangle clip = e.ClipRectangle;
            int from = scroll + clip.Y;
            int to = Math.Min(cacheH, from + clip.Height);
            for (int i = 0; i < bands.Length && from < to; i++)
            {
                int top = i * BandHeight;
                int a = Math.Max(top, from), b = Math.Min(top + bands[i].Height, to);
                if (b <= a) continue;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(bands[i], new Rectangle(0, a - scroll, cacheW, b - a), new Rectangle(0, a - top, cacheW, b - a), GraphicsUnit.Pixel);
            }

            // The parts that move every frame: live thumbnails, meters, the monitor map.
            g.TranslateTransform(0, -scroll);
            Rectangle view = new Rectangle(0, scroll, Width, Height);
            foreach (Widget w in Widgets)
                if (w.Bounds.IntersectsWith(view))
                {
                    try
                    {
                        if (w.Dynamic) w.Paint(g, Ui);
                        else w.PaintLive(g, Ui);
                    }
                    catch (Exception ex) { Program.Log("settings paint error: " + ex.Message); }
                }
            g.ResetTransform();

            Rectangle thumb = BarThumb;
            if (!thumb.IsEmpty)
            {
                bool wide = barHot || dragBar;
                int bw = wide ? Ui.S(6) : Ui.S(3);
                var r = new Rectangle(thumb.Right - Ui.S(3) - bw, thumb.Y, bw, thumb.Height);
                Ui.Fill(g, r, bw / 2f, wide ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110));
            }

            double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            paints++;
            paintTotal += ms;
            if (ms > paintMax) paintMax = (float)ms;
        }

        // Paints the static layer. An empty region means the whole page.
        void PaintCache(Rectangle region)
        {
            if (bands == null) return;
            bool all = region.IsEmpty;
            if (all) region = new Rectangle(0, 0, cacheW, cacheH);
            region.Intersect(new Rectangle(0, 0, cacheW, cacheH));
            if (region.Width <= 0 || region.Height <= 0) return;
            for (int i = 0; i < bands.Length; i++)
            {
                int top = i * BandHeight;
                Rectangle r = Rectangle.Intersect(region, new Rectangle(0, top, cacheW, bands[i].Height));
                if (r.Width <= 0 || r.Height <= 0) continue;
                using (Graphics g = Graphics.FromImage(bands[i]))
                using (var b = new SolidBrush(Back))
                {
                    g.TranslateTransform(0, -top);
                    g.SetClip(r);
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.FillRectangle(b, r);
                    Ui.Caching = true;
                    try
                    {
                        foreach (Widget w in Widgets)
                            if (!w.Dynamic && w.Bounds.IntersectsWith(r))
                            {
                                try { w.Paint(g, Ui); }
                                catch (Exception ex) { Program.Log("settings paint error: " + ex.Message); }
                            }
                    }
                    finally { Ui.Caching = false; }
                }
            }
            if (all) { cacheFull = false; dirty.Clear(); rebuilds++; }
        }

        void FlushDirty()
        {
            if (dirty.Count == 0) return;
            foreach (Rectangle r in dirty) PaintCache(r);
            dirty.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (glide != null) { glide.Dispose(); glide = null; }
                DisposeBands();
            }
            base.Dispose(disposing);
        }

        Widget HitAt(Point client)
        {
            var p = new Point(client.X, client.Y + scroll);
            for (int i = Widgets.Count - 1; i >= 0; i--)
            {
                Widget w = Widgets[i].Hit(p);
                if (w != null) return w;
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging != null)
            {
                try { dragging.MouseDrag(new Point(e.X, e.Y + scroll)); }
                catch (Exception ex) { Program.Log("settings drag error: " + ex.Message); }
                return;
            }
            if (dragBar)
            {
                Rectangle t = BarTrack, th = BarThumb;
                int range = Math.Max(1, t.Height - th.Height);
                Scroll = dragStartScroll + (int)((long)(e.Y - dragStartY) * (ContentHeight - Height) / range);
                return;
            }
            bool overBar = !BarThumb.IsEmpty && BarTrack.Contains(e.Location);
            if (overBar != barHot) { barHot = overBar; Invalidate(BarTrack); }
            SetHot(overBar ? null : HitAt(e.Location));
            if (hot != null) hot.MouseMove(new Point(e.X, e.Y + scroll));
        }

        void SetHot(Widget w)
        {
            if (w == hot) return;
            if (hot != null) { hot.Hot = false; hot.MouseLeave(); hot.Invalidate(); }
            hot = w;
            if (hot != null) { hot.Hot = true; hot.Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hot != null) { hot.Hot = false; hot.MouseLeave(); hot.Invalidate(); hot = null; }
            if (barHot) { barHot = false; Invalidate(BarTrack); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            if (!BarThumb.IsEmpty && BarTrack.Contains(e.Location))
            {
                Rectangle th = BarThumb;
                if (!th.Contains(e.Location)) Scroll += e.Y < th.Y ? -Height : Height;
                dragBar = true; dragStartY = e.Y; dragStartScroll = scroll;
                Capture = true;
                return;
            }
            SetHot(HitAt(e.Location));
            pressed = hot;
            if (pressed != null)
            {
                pressed.Pressed = true;
                pressed.Invalidate();
                if (pressed.CapturesDrag)
                {
                    dragging = pressed;
                    Capture = true;
                    try { pressed.MouseDrag(new Point(e.X, e.Y + scroll)); }
                    catch (Exception ex) { Program.Log("settings drag error: " + ex.Message); }
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging != null)
            {
                Widget d = dragging;
                dragging = null;
                Capture = false;
                d.Pressed = false;
                try { d.DragFinished(); }
                catch (Exception ex) { Program.Log("settings drag error: " + ex.Message); }
                d.Invalidate();
                return;
            }
            if (dragBar) { dragBar = false; Capture = false; Invalidate(); return; }
            Widget p = pressed;
            pressed = null;
            if (p == null) return;
            p.Pressed = false;
            p.Invalidate();
            if (p == HitAt(e.Location) && p.Enabled && e.Button == MouseButtons.Left)
            {
                try { p.Click(new Point(e.X, e.Y + scroll)); }
                catch (Exception ex) { Program.Log("settings click error: " + ex); }
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            TargetScroll -= e.Delta * Ui.S(100) / 120;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.PageDown) TargetScroll += Height - Ui.S(40);
            else if (e.KeyCode == Keys.PageUp) TargetScroll -= Height - Ui.S(40);
            else if (e.KeyCode == Keys.Down) TargetScroll += Ui.S(40);
            else if (e.KeyCode == Keys.Up) TargetScroll -= Ui.S(40);
            else if (e.KeyCode == Keys.Home) TargetScroll = 0;
            else if (e.KeyCode == Keys.End) TargetScroll = ContentHeight;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            scroll = Clamp(scroll);
            target = Clamp(target);
            if (target == scroll && glide != null) glide.Stop();
        }
    }

    // Builds a page top to bottom: each Add places a widget at the current y across the full width.
    sealed class Layout
    {
        public readonly Ui Ui;
        public readonly List<Widget> Items = new List<Widget>();
        public readonly int X, Width;
        public int Y;

        public Layout(Ui ui, int x, int y, int width) { Ui = ui; X = x; Y = y; Width = width; }

        public T Add<T>(T w, int height) where T : Widget
        {
            w.Bounds = new Rectangle(X, Y, Width, height);
            Items.Add(w);
            Y += height;
            return w;
        }

        public T Place<T>(T w, Rectangle r) where T : Widget
        {
            w.Bounds = r;
            Items.Add(w);
            return w;
        }

        public void Gap(float v) { Y += Ui.S(v); }

        public void Header(string text)
        {
            Gap(22);
            Add(new TextBlock(text, Ui.BodyStrong, Ui.Text), Ui.S(30));
        }

        public void Text(string text, Color c)
        {
            int h = Ui.WrapHeight(text, Ui.Body, Width);
            Add(new TextBlock(text, Ui.Body, c) { Wrap = true }, h);
        }

        public SettingRow Row(string glyph, string title, string desc, Widget control)
        {
            var row = new SettingRow(glyph, title, desc, control);
            int h = row.Measure(Ui, Width);
            Add(row, h);
            row.PlaceControl(Ui);
            if (control != null) Items.Add(control);
            Y += Ui.S(4);
            return row;
        }
    }

    sealed class TextBlock : Widget
    {
        public string Value;
        public Font Font;
        public Color Color;
        public bool Wrap;

        public TextBlock(string text, Font font, Color color) { Value = text; Font = font; Color = color; }

        public override void Paint(Graphics g, Ui ui)
        {
            if (Wrap) ui.DrawText(g, Value, Font, Color, Bounds, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            else ui.Line(g, Value, Font, Color, Bounds);
        }
    }

    // A Windows 11 settings card: icon, title, optional description, and a control on the right.
    sealed class SettingRow : Widget
    {
        public readonly string Glyph, Title, Desc;
        public readonly Widget Control;
        public Color DescColor = Ui.TextSecondary;

        public SettingRow(string glyph, string title, string desc, Widget control) { Glyph = glyph; Title = title; Desc = desc; Control = control; }

        int TextLeft(Ui ui) { return Glyph == null ? ui.S(16) : ui.S(56); }

        int ControlWidth(Ui ui)
        {
            var m = Control as IMeasured;
            return Control == null ? 0 : m != null ? m.MeasureWidth(ui) : ui.S(120);
        }

        public int Measure(Ui ui, int width)
        {
            int textW = width - TextLeft(ui) - ui.S(16) - (Control != null ? ControlWidth(ui) + ui.S(24) : 0);
            int h = ui.S(20) + ui.WrapHeight(Desc, ui.Caption, textW);
            int full = Math.Max(ui.S(68), h + ui.S(30));
            return Desc == null ? ui.S(68) : full;
        }

        public void PlaceControl(Ui ui)
        {
            if (Control == null) return;
            var m = Control as IMeasured;
            int w = ControlWidth(ui), h = m != null ? m.MeasureHeight(ui) : ui.S(32);
            Control.Bounds = new Rectangle(Bounds.Right - ui.S(16) - w, Bounds.Y + (Bounds.Height - h) / 2, w, h);
        }

        public override void Paint(Graphics g, Ui ui)
        {
            PlaceControl(ui);
            ui.DrawCard(g, Bounds, Ui.Card);
            Color fg = Enabled ? Ui.Text : Ui.TextDisabled;
            if (Glyph != null) ui.Glyph(g, Glyph, ui.IconLarge, fg, new Rectangle(Bounds.X + ui.S(16), Bounds.Y, ui.S(24), Bounds.Height));
            int left = Bounds.X + TextLeft(ui);
            int right = Control != null ? Control.Bounds.X - ui.S(24) : Bounds.Right - ui.S(16);
            int w = right - left;
            if (Desc == null)
                ui.Line(g, Title, ui.Body, fg, new Rectangle(left, Bounds.Y, w, Bounds.Height));
            else
            {
                int dh = ui.WrapHeight(Desc, ui.Caption, w);
                int top = Bounds.Y + (Bounds.Height - ui.S(20) - dh) / 2;
                ui.Line(g, Title, ui.Body, fg, new Rectangle(left, top, w, ui.S(20)));
                ui.DrawText(g, Desc, ui.Caption, Enabled ? DescColor : Ui.TextDisabled, new Rectangle(left, top + ui.S(20), w, dh + ui.S(2)), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
        }
    }

    interface IMeasured
    {
        int MeasureWidth(Ui ui);
        int MeasureHeight(Ui ui);
    }

    sealed class Toggle : Widget, IMeasured
    {
        public bool On;
        public readonly Action<bool> Changed;

        public Toggle(bool on, Action<bool> changed) { On = on; Changed = changed; }

        public override bool Interactive { get { return true; } }
        public int MeasureWidth(Ui ui) { return ui.S(40) + ui.S(12) + ui.Measure("Off", ui.Body).Width + ui.S(4); }
        public int MeasureHeight(Ui ui) { return ui.S(32); }

        public override void Paint(Graphics g, Ui ui)
        {
            int tw = ui.S(40), th = ui.S(20);
            var track = new Rectangle(Bounds.Right - tw, Bounds.Y + (Bounds.Height - th) / 2, tw, th);
            ui.Line(g, On ? "On" : "Off", ui.Body, Enabled ? Ui.Text : Ui.TextDisabled,
                    new Rectangle(Bounds.X, Bounds.Y, track.X - Bounds.X - ui.S(12), Bounds.Height));
            float knob = Hot ? ui.SF(14) : ui.SF(12);
            if (Pressed) knob = ui.SF(14);
            float cy = track.Y + th / 2f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (On && Enabled)
            {
                ui.Fill(g, track, th / 2f, Hot ? ui.AccentHover : ui.Accent);
                float cx = track.Right - th / 2f;
                using (var b = new SolidBrush(ui.AccentText)) g.FillEllipse(b, cx - knob / 2, cy - knob / 2, knob, knob);
            }
            else
            {
                ui.Fill(g, track, th / 2f, Hot && Enabled ? Color.FromArgb(52, 52, 52) : Color.FromArgb(40, 40, 40));
                ui.Border(g, track, th / 2f, Enabled ? Color.FromArgb(205, 205, 205) : Ui.TextDisabled, 1f);
                float cx = track.X + th / 2f;
                if (!Enabled && On) cx = track.Right - th / 2f;
                using (var b = new SolidBrush(Enabled ? Color.FromArgb(205, 205, 205) : Ui.TextDisabled)) g.FillEllipse(b, cx - knob / 2, cy - knob / 2, knob, knob);
            }
        }

        public override void Click(Point p) { On = !On; Invalidate(); Changed(On); }
    }

    // A combo-box look-alike that opens a dark menu of choices.
    sealed class DropDown : Widget, IMeasured
    {
        public readonly string[] Values, Labels;
        public string Selected;
        public readonly Action<string> Changed;
        public int MinWidth = 160;

        public DropDown(string[] values, string[] labels, string selected, Action<string> changed)
        {
            Values = values; Labels = labels; Selected = selected; Changed = changed;
        }

        public override bool Interactive { get { return true; } }

        public string Current
        {
            get
            {
                int i = Array.IndexOf(Values, Selected);
                return i >= 0 ? Labels[i] : "";
            }
        }

        public int MeasureWidth(Ui ui)
        {
            int w = ui.S(MinWidth);
            foreach (string l in Labels) w = Math.Max(w, ui.Measure(l, ui.Body).Width + ui.S(52));
            return Math.Min(w, ui.S(360));
        }

        public int MeasureHeight(Ui ui) { return ui.S(32); }

        public override void Paint(Graphics g, Ui ui)
        {
            Color fill = !Enabled ? Color.FromArgb(42, 42, 42) : Pressed ? Color.FromArgb(48, 48, 48) : Hot ? Ui.ControlHover : Ui.Control;
            ui.Fill(g, Bounds, ui.SF(4), fill);
            ui.Border(g, Bounds, ui.SF(4), Ui.ControlBorder, 1f);
            Color fg = Enabled ? Ui.Text : Ui.TextDisabled;
            ui.Line(g, Current, ui.Body, fg, new Rectangle(Bounds.X + ui.S(11), Bounds.Y, Bounds.Width - ui.S(44), Bounds.Height));
            ui.Glyph(g, Glyphs.ChevronDown, ui.IconSmall, Enabled ? Ui.TextSecondary : Ui.TextDisabled,
                     new Rectangle(Bounds.Right - ui.S(32), Bounds.Y, ui.S(22), Bounds.Height));
        }

        public override void Click(Point p)
        {
            var menu = DarkMenu.Create(UI);
            for (int i = 0; i < Values.Length; i++)
            {
                string v = Values[i];
                if (v == null)
                {
                    // A group header: a separator and a greyed caption, not selectable.
                    if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add(new ToolStripMenuItem(Labels[i]) { Enabled = false });
                    continue;
                }
                var item = new ToolStripMenuItem(Labels[i], null, delegate { if (v != Selected) { Selected = v; Invalidate(); Changed(v); } });
                item.Checked = v == Selected;
                menu.Items.Add(item);
            }
            menu.MinimumSize = new Size(Bounds.Width, 0);
            DarkMenu.Show(menu, Surface.ScreenPoint(Bounds, true));
        }
    }

    sealed class PushButton : Widget, IMeasured
    {
        public string Label, Glyph;
        public bool Accent;
        public Action OnClick;
        public int MinWidth = 0;

        public PushButton(string glyph, string label, bool accent, Action onClick) { Glyph = glyph; Label = label; Accent = accent; OnClick = onClick; }

        public override bool Interactive { get { return true; } }

        public int MeasureWidth(Ui ui)
        {
            int w = ui.S(24) + (Label != null ? ui.Measure(Label, ui.Body).Width : 0) + (Glyph != null ? ui.S(Label != null ? 24 : 8) : 0);
            return Math.Max(w, Math.Max(ui.S(MinWidth), ui.S(32)));
        }

        public int MeasureHeight(Ui ui) { return ui.S(32); }

        public override void Paint(Graphics g, Ui ui)
        {
            Color fill, fg;
            if (!Enabled) { fill = Color.FromArgb(42, 42, 42); fg = Ui.TextDisabled; }
            else if (Accent) { fill = Pressed ? Ui.Mix(ui.Accent, Color.Black, 0.2f) : Hot ? ui.AccentHover : ui.Accent; fg = ui.AccentText; }
            else { fill = Pressed ? Color.FromArgb(48, 48, 48) : Hot ? Ui.ControlHover : Ui.Control; fg = Ui.Text; }
            ui.Fill(g, Bounds, ui.SF(4), fill);
            if (!Accent || !Enabled) ui.Border(g, Bounds, ui.SF(4), Ui.ControlBorder, 1f);
            int contentW = (Label != null ? ui.Measure(Label, ui.Body).Width : 0) + (Glyph != null ? (Label != null ? ui.S(24) : ui.S(16)) : 0);
            int x = Bounds.X + (Bounds.Width - contentW) / 2;
            if (Glyph != null)
            {
                ui.Glyph(g, Glyph, ui.Icon, fg, new Rectangle(x, Bounds.Y, ui.S(16), Bounds.Height));
                x += ui.S(24);
            }
            if (Label != null) ui.Line(g, Label, Accent ? ui.BodyStrong : ui.Body, fg, new Rectangle(x - ui.S(1), Bounds.Y, Bounds.Right - x, Bounds.Height));
        }

        public override void Click(Point p) { if (OnClick != null) OnClick(); }
    }

    // A Windows 11 style stepped slider. The thumb follows the mouse while the button is held:
    // Preview runs for every step (the live level meter reacts immediately without rebuilding the
    // page) and Commit once on release, so the ini is written once per drag.
    sealed class Slider : Widget, IMeasured
    {
        public readonly int Min, Max;
        public int Value;
        readonly string[] Labels;
        readonly Action<int> Preview, Commit;
        public int MinWidth = 260;

        public Slider(int min, int max, int value, string[] labels, Action<int> preview, Action<int> commit)
        {
            Min = min; Max = max;
            Value = Math.Max(min, Math.Min(max, value));
            Labels = labels; Preview = preview; Commit = commit;
        }

        public override bool Interactive { get { return true; } }
        public override bool CapturesDrag { get { return true; } }

        int LabelWidth(Ui ui)
        {
            int w = 0;
            foreach (string s in Labels) w = Math.Max(w, ui.Measure(s, ui.Body).Width);
            return w;
        }

        public int MeasureWidth(Ui ui) { return ui.S(MinWidth) + LabelWidth(ui); }
        public int MeasureHeight(Ui ui) { return ui.S(40); }

        Rectangle Track(Ui ui)
        {
            int left = Bounds.X + ui.S(8);
            int right = Bounds.Right - LabelWidth(ui) - ui.S(16);
            int h = ui.S(4);
            return new Rectangle(left, Bounds.Y + (Bounds.Height - h) / 2, Math.Max(ui.S(40), right - left), h);
        }

        int ThumbX(Ui ui)
        {
            Rectangle t = Track(ui);
            int steps = Math.Max(1, Max - Min);
            return t.X + t.Width * (Value - Min) / steps;
        }

        void SetFrom(Point p)
        {
            Rectangle t = Track(UI);
            int steps = Math.Max(1, Max - Min);
            float f = (p.X - t.X) / (float)Math.Max(1, t.Width);
            int v = Math.Max(Min, Math.Min(Max, Min + (int)Math.Round(f * steps)));
            if (v == Value) return;
            Value = v;
            Invalidate();
            if (Preview != null) Preview(v);
        }

        public override void MouseDrag(Point p) { SetFrom(p); }
        public override void DragFinished() { if (Commit != null) Commit(Value); }

        public override void Paint(Graphics g, Ui ui)
        {
            Rectangle t = Track(ui);
            int cy = t.Y + t.Height / 2;
            int tx = ThumbX(ui);
            ui.Fill(g, t, ui.SF(2), Color.FromArgb(84, 84, 84));
            if (tx > t.X) ui.Fill(g, new Rectangle(t.X, t.Y, tx - t.X, t.Height), ui.SF(2), ui.Accent);
            int steps = Math.Max(1, Max - Min);
            for (int i = 1; i < steps; i++)
            {
                int x = t.X + t.Width * i / steps;
                ui.Fill(g, new Rectangle(x - ui.S(1), cy - ui.S(1), ui.S(2), ui.S(2)), ui.SF(1), Color.FromArgb(120, 120, 120));
            }
            int d = Hot || Pressed ? ui.S(18) : ui.S(16);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(ui.Accent)) g.FillEllipse(b, tx - d / 2f, cy - d / 2f, d, d);
            using (var b = new SolidBrush(ui.AccentText)) g.FillEllipse(b, tx - ui.S(3), cy - ui.S(3), ui.S(6), ui.S(6));
            ui.DrawText(g, Labels[Value - Min], ui.BodyStrong, Ui.Text,
                    new Rectangle(t.Right + ui.S(12), Bounds.Y, Bounds.Right - t.Right - ui.S(12), Bounds.Height),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // A coloured message strip (info or warning) with an icon.
    sealed class InfoBar : Widget
    {
        public readonly string Message;
        public readonly bool Warning;

        public InfoBar(string message, bool warning) { Message = message; Warning = warning; }

        public static int Height(Ui ui, string message, int width) { return Math.Max(ui.S(48), ui.WrapHeight(message, ui.Body, width - ui.S(64)) + ui.S(28)); }

        public override void Paint(Graphics g, Ui ui)
        {
            ui.Fill(g, Bounds, ui.SF(6), Warning ? Ui.CautionBack : Ui.InfoBack);
            ui.Border(g, Bounds, ui.SF(6), Warning ? Color.FromArgb(80, 66, 34) : Ui.CardBorder, 1f);
            var icon = new Rectangle(Bounds.X + ui.S(16), Bounds.Y + ui.S(14), ui.S(20), ui.S(20));
            ui.Fill(g, icon, ui.SF(10), Warning ? Ui.Caution : ui.Accent);
            ui.Glyph(g, Warning ? "!" : "i", ui.BodyStrong, Color.Black, icon);
            ui.DrawText(g, Message, ui.Body, Ui.Text, new Rectangle(Bounds.X + ui.S(48), Bounds.Y + ui.S(14), Bounds.Width - ui.S(64), Bounds.Height - ui.S(20)),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
    }

    // Dark, rounded context menus in the Windows 11 style (used by drop-downs and the tray icon).
    static class DarkMenu
    {
        public static ContextMenuStrip Create(Ui ui)
        {
            var m = new ContextMenuStrip();
            m.Renderer = new Renderer(ui);
            m.ShowImageMargin = false;
            m.ShowCheckMargin = false;
            if (ui != null)
            {
                m.Font = ui.Body;
                m.Padding = new Padding(ui.S(4), ui.S(4), ui.S(4), ui.S(4));
            }
            m.ItemAdded += delegate(object s, ToolStripItemEventArgs e)
            {
                if (ui != null && e.Item is ToolStripMenuItem)
                    e.Item.Padding = new Padding(ui.S(14), ui.S(6), ui.S(8), ui.S(6));
            };
            m.Closed += delegate { DisposeLater(m); };
            return m;
        }

        static void DisposeLater(ContextMenuStrip m)
        {
            var t = new Timer { Interval = 50 };
            t.Tick += delegate { t.Stop(); t.Dispose(); m.Dispose(); };
            t.Start();
        }

        public static void Show(ContextMenuStrip m, Point screen)
        {
            m.Opened += delegate { Dwm.Round(m.Handle, false); };
            m.Show(screen);
        }

        // Rounded corners for a menu that lives on (the tray menu).
        public static void Style(ToolStripDropDown m)
        {
            m.Opened += delegate { Dwm.Round(m.Handle, false); };
        }

        public sealed class Renderer : ToolStripRenderer
        {
            readonly Ui ui;
            static readonly Color Back = Color.FromArgb(44, 44, 44);

            public Renderer(Ui ui) { this.ui = ui ?? new Ui(); }

            protected override void Initialize(ToolStrip ts) { base.Initialize(ts); ts.BackColor = Back; ts.ForeColor = Ui.Text; }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Back); }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using (var p = new Pen(Color.FromArgb(70, 70, 70)))
                    e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                var r = new Rectangle(ui.S(2), 1, e.Item.Width - ui.S(4), e.Item.Height - 2);
                if (e.Item.Selected && e.Item.Enabled) ui.Fill(e.Graphics, r, ui.SF(4), Color.FromArgb(58, 58, 58));
                var mi = e.Item as ToolStripMenuItem;
                if (mi != null && mi.Checked)
                {
                    if (!e.Item.Selected) ui.Fill(e.Graphics, r, ui.SF(4), Color.FromArgb(52, 52, 52));
                    int h = Math.Max(ui.S(12), r.Height / 2);
                    ui.Fill(e.Graphics, new Rectangle(r.X, r.Y + (r.Height - h) / 2, ui.S(3), h), ui.SF(1.5f), ui.Accent);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? Ui.Text : Ui.TextTertiary;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = Ui.TextSecondary;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (var p = new Pen(Color.FromArgb(64, 64, 64)))
                    e.Graphics.DrawLine(p, 0, y, e.Item.Width, y);
            }
        }
    }

    // A borderless popup (shadow, rounded corners) owned by the settings window; closes when it loses focus.
    class PopupForm : Form
    {
        public readonly Surface Body;

        public PopupForm(Ui ui)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(44, 44, 44);
            KeyPreview = true;
            Body = new Surface(ui) { Dock = DockStyle.Fill, Back = Color.FromArgb(44, 44, 44) };
            Controls.Add(Body);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
                cp.ExStyle |= 0x80;         // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Dwm.DarkTitleBar(Handle);
            Dwm.Round(Handle, false);
            Dwm.BorderColor(Handle, Color.FromArgb(70, 70, 70));
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            BeginInvoke(new MethodInvoker(Close));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
        }

        // Places the popup next to `anchor` (screen coordinates), kept inside that monitor's work area.
        public void ShowAt(IWin32Window owner, Rectangle anchor, Size size)
        {
            Rectangle wa = Screen.FromRectangle(anchor).WorkingArea;
            int x = anchor.X, y = anchor.Bottom + 4;
            if (y + size.Height > wa.Bottom) y = Math.Max(wa.Top, anchor.Y - size.Height - 4);
            var form = owner as Form;   // stay inside the window when we can: right-align to the anchor
            if (form != null && x + size.Width > form.Bounds.Right) x = Math.Max(form.Bounds.Left, anchor.Right - size.Width);
            if (x + size.Width > wa.Right) x = wa.Right - size.Width;
            if (x < wa.Left) x = wa.Left;
            Bounds = new Rectangle(x, y, size.Width, Math.Min(size.Height, wa.Height));
            Show(owner);
        }
    }
}
