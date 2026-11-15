// The settings window: left navigation, one page at a time on the right. It edits the same Options
// object the tray app runs on and calls ISettingsHost.Changed() after every change (which saves the
// ini, updates the tray tooltip and raises StateChanged), so there is no OK/Cancel and the window
// and tray always agree. Opened from the tray (Settings..., double-click) or with --settings.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace IdleVeil
{
    // What the window needs from the running app (Controller).
    interface ISettingsHost
    {
        Options Options { get; }
        void Changed();                         // save + tell everyone (raises StateChanged)
        event EventHandler StateChanged;        // options, pause, update or renderer status changed
        void Preview(string mode);              // show the real veil now (null = current settings); input hides it
        bool VeilShowing { get; }
        bool Paused { get; }
        string PauseText { get; }
        void Pause(DateTime until);
        void Resume();
        void CheckForUpdates();
        string UpdateStatus { get; }
        string RendererStatus { get; }
        string LastFocusedApp { get; }
    }

    abstract class Page
    {
        public SettingsWindow Window;
        public abstract string Title { get; }
        public abstract string Glyph { get; }
        public abstract void Build(Layout l);
        public virtual void Shown() { }
        public virtual void Hidden() { }
        public virtual void Tick() { }           // ~10 times a second while the page is visible
        public virtual void Scrolled() { }

        protected Ui UI { get { return Window.UI; } }
        protected ISettingsHost Host { get { return Window.Host; } }
        protected Options Opt { get { return Window.Host.Options; } }
        protected void Changed() { Host.Changed(); }
    }

    sealed class SettingsWindow : Form
    {
        public readonly Ui UI = new Ui();
        public readonly ISettingsHost Host;
        public readonly ThumbRenderer Thumbs;
        readonly Surface nav, content;
        readonly List<Page> pages = new List<Page>();
        readonly Timer tick = new Timer();
        Page current;
        bool rebuildQueued;

        const int WM_DPICHANGED = 0x02E0;

        public SettingsWindow(ISettingsHost host, Icon icon)
        {
            Host = host;
            Text = "IdleVeil Settings";
            Icon = icon;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Ui.Window;
            ForeColor = Ui.Text;
            KeyPreview = true;
            Thumbs = new ThumbRenderer();
            Thumbs.Frame += delegate { OnThumbFrame(); };

            nav = new Surface(UI) { Dock = DockStyle.Left, Back = Ui.Nav };
            content = new Surface(UI) { Dock = DockStyle.Fill, Back = Ui.Window };
            content.Scrolled += delegate { if (current != null) current.Scrolled(); };
            content.Resize += delegate { QueueRebuild(); };
            Controls.Add(content);
            Controls.Add(nav);

            pages.Add(new ScenesPage());
            pages.Add(new ScreensPage());
            pages.Add(new TimingPage());
            pages.Add(new MusicPage());
            pages.Add(new AppsPage());
            pages.Add(new GeneralPage());
            foreach (Page p in pages) p.Window = this;

            host.StateChanged += OnStateChanged;
            tick.Interval = 100;
            tick.Tick += delegate { OnTick(); };

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Bounds = new Rectangle(wa.X + wa.Width / 4, wa.Y + wa.Height / 4, 800, 600);   // real size once the DPI is known
            current = pages[0];
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Dwm.DarkTitleBar(Handle);
            Dwm.Mica(Handle);
            UI.SetDpi(Dwm.DpiOf(Handle));
            Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
            int w = Math.Min(UI.S(1160), wa.Width - UI.S(40)), h = Math.Min(UI.S(800), wa.Height - UI.S(40));
            Bounds = new Rectangle(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
            ApplyScale();
        }

        void ApplyScale()
        {
            MinimumSize = new Size(UI.S(760), UI.S(520));
            nav.Width = UI.S(290);
            RebuildAll();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                int dpi = (int)((long)m.WParam & 0xFFFF);
                var r = (Native.RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                UI.SetDpi(dpi);
                Bounds = new Rectangle(r.L, r.T, r.R - r.L, r.B - r.T);
                ApplyScale();
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            tick.Start();
            current.Shown();
            UpdateActivity();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateActivity();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdateActivity();
        }

        // Nothing animates while the window is minimized or hidden, or while the real veil is up.
        public bool Active { get { return Visible && WindowState != FormWindowState.Minimized && !Host.VeilShowing; } }

        bool wasActive = true;

        void UpdateActivity()
        {
            bool a = Active;
            Thumbs.Paused = !a;
            if (a == wasActive) return;
            wasActive = a;
            if (a) { tick.Start(); if (current != null) current.Shown(); }
            else { tick.Stop(); if (current != null) current.Hidden(); }
            // A slow check while inactive, to notice the veil going away.
            if (!a && Visible) { tick.Interval = 1000; tick.Start(); }
            else tick.Interval = 100;
        }

        void OnTick()
        {
            bool a = Active;
            if (a != wasActive) UpdateActivity();
            if (a && current != null) current.Tick();
        }

        void OnThumbFrame()
        {
            if (!Active) return;
            InvalidateThumbs(content);
            foreach (Form f in OwnedForms)
            {
                var p = f as PopupForm;
                if (p != null && p.Visible) InvalidateThumbs(p.Body);
            }
        }

        static void InvalidateThumbs(Surface s)
        {
            Rectangle view = s.Viewport;
            foreach (Widget w in s.Widgets)
            {
                var t = w as IThumbView;
                if (t != null && w.Bounds.IntersectsWith(view) && t.HasFreshFrame) w.Invalidate();
            }
        }

        void OnStateChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            QueueRebuild();
        }

        // Rebuilds the current page on the next message (coalesces bursts of changes).
        public void QueueRebuild()
        {
            if (rebuildQueued || !IsHandleCreated) return;
            rebuildQueued = true;
            BeginInvoke(new MethodInvoker(delegate { rebuildQueued = false; if (!IsDisposed) RebuildAll(); }));
        }

        public void ShowPage(Page p)
        {
            if (p == current) return;
            if (current != null) current.Hidden();
            current = p;
            content.Scroll = 0;
            RebuildAll();
            if (Active) current.Shown();
        }

        void RebuildAll()
        {
            BuildNav();
            BuildContent();
        }

        void BuildNav()
        {
            var items = new List<Widget>();
            int y = UI.S(16);
            items.Add(new AppHeader(Icon) { Bounds = new Rectangle(UI.S(16), y, nav.Width - UI.S(32), UI.S(56)) });
            y += UI.S(72);
            foreach (Page p in pages)
            {
                Page page = p;
                items.Add(new NavItem(p.Glyph, p.Title, p == current, delegate { ShowPage(page); }) { Bounds = new Rectangle(UI.S(8), y, nav.Width - UI.S(16), UI.S(40)) });
                y += UI.S(44);
            }
            int bottom = Math.Max(y + UI.S(24), nav.Height - UI.S(16));
            int bw = nav.Width - UI.S(32);
            string status = Host.Paused ? Host.PauseText : null;
            if (status != null)
            {
                bottom -= UI.S(40);
                var resume = new PushButton(Glyphs.Play, "Resume", false, delegate { Host.Resume(); }) { Bounds = new Rectangle(UI.S(16), bottom, bw, UI.S(32)) };
                items.Add(resume);
                bottom -= UI.S(28);
                items.Add(new TextBlock(status, UI.Caption, Ui.Caution) { Bounds = new Rectangle(UI.S(18), bottom, bw, UI.S(24)) });
                bottom -= UI.S(8);
            }
            bottom -= UI.S(36);
            items.Add(new PushButton(Glyphs.Play, "Preview", true, delegate { Host.Preview(null); }) { Bounds = new Rectangle(UI.S(16), bottom, bw, UI.S(36)) });
            nav.SetWidgets(items, 0);
        }

        void BuildContent()
        {
            int pad = UI.S(40);
            int width = Math.Min(content.Width - pad * 2 + UI.S(12), UI.S(1100));
            var l = new Layout(UI, pad - UI.S(12), UI.S(28), Math.Max(UI.S(300), width));
            l.Add(new TextBlock(current.Title, UI.Title, Ui.Text), UI.S(44));
            l.Gap(16);
            current.Build(l);
            l.Gap(32);
            content.SetWidgets(l.Items, l.Y);
            current.Scrolled();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.Control && e.KeyCode == Keys.Tab)
            {
                int i = pages.IndexOf(current);
                ShowPage(pages[(i + (e.Shift ? pages.Count - 1 : 1)) % pages.Count]);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Host.StateChanged -= OnStateChanged;
            tick.Stop();
            if (current != null) current.Hidden();
            Thumbs.Dispose();
        }

        public Rectangle ContentViewport { get { return content.Viewport; } }
        public Surface Content { get { return content; } }
    }

    interface IThumbView
    {
        bool HasFreshFrame { get; }
    }

    sealed class AppHeader : Widget
    {
        readonly Icon icon;
        public AppHeader(Icon icon) { this.icon = icon; }

        public override void Paint(Graphics g, Ui ui)
        {
            int s = ui.S(32);
            var r = new Rectangle(Bounds.X + ui.S(4), Bounds.Y + (Bounds.Height - s) / 2, s, s);
            if (icon != null)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawIcon(icon, r);
            }
            int x = r.Right + ui.S(14);
            ui.Line(g, "IdleVeil", ui.Subtitle, Ui.Text, new Rectangle(x, Bounds.Y + ui.S(4), Bounds.Right - x, ui.S(28)));
            ui.Line(g, "OLED burn-in protection  \u00B7  v" + AppInfo.Version, ui.Caption, Ui.TextTertiary, new Rectangle(x, Bounds.Y + ui.S(32), Bounds.Right - x, ui.S(18)));
        }
    }

    sealed class NavItem : Widget
    {
        readonly string glyph, text;
        readonly bool selected;
        readonly Action onClick;

        public NavItem(string glyph, string text, bool selected, Action onClick) { this.glyph = glyph; this.text = text; this.selected = selected; this.onClick = onClick; }

        public override bool Interactive { get { return true; } }

        public override void Paint(Graphics g, Ui ui)
        {
            if (selected || Hot) ui.Fill(g, Bounds, ui.SF(5), selected ? (Hot ? Color.FromArgb(50, 50, 50) : Ui.NavSelected) : Pressed ? Color.FromArgb(36, 36, 36) : Ui.NavHover);
            if (selected)
            {
                int h = ui.S(16);
                ui.Fill(g, new Rectangle(Bounds.X, Bounds.Y + (Bounds.Height - h) / 2, ui.S(3), h), ui.SF(1.5f), ui.Accent);
            }
            ui.Glyph(g, glyph, ui.Icon, Ui.Text, new Rectangle(Bounds.X + ui.S(14), Bounds.Y, ui.S(20), Bounds.Height));
            ui.Line(g, text, ui.Body, Ui.Text, new Rectangle(Bounds.X + ui.S(48), Bounds.Y, Bounds.Width - ui.S(52), Bounds.Height));
        }

        public override void Click(Point p) { onClick(); }
    }
}
