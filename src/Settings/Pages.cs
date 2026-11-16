// The six pages of the settings window and the widgets only they use (scene cards, the monitor
// map, screen-mode cards, the music meter) plus the scene and app picker popups.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace IdleVeil
{
    // ---------------------------------------------------------------- Scenes

    sealed class ScenesPage : Page
    {
        public override string Title { get { return "Scenes"; } }
        public override string Glyph { get { return Glyphs.Scenes; } }

        static string lastSingle;   // the scene "One scene" returns to after Random
        readonly List<SceneCard> cards = new List<SceneCard>();

        public override void Build(Layout l)
        {
            cards.Clear();
            bool random = Opt.Mode == "random";
            if (!random) lastSingle = Opt.Mode;
            if (lastSingle == null) lastSingle = "aurora";

            var seg = new Segmented(new[] { "One scene", "Random" }, random ? 1 : 0, delegate(int i)
            {
                Opt.Mode = i == 1 ? "random" : lastSingle;
                Changed();
            });
            l.Row(random ? Glyphs.Shuffle : Glyphs.Scenes, "How the scene is chosen",
                  random ? "Each time, Random picks one of the scenes ticked below. Music visualizers only join while something is playing."
                         : "The scene you click below shows every time.", seg);
            if (random)
            {
                int n = 0, total = 0;
                foreach (string[] g in SceneCatalog.Groups)
                    foreach (string id in g)
                        if (SceneCatalog.Poolable(id)) { total++; if (SceneCatalog.InPool(Opt.RandomPool, id)) n++; }
                var reset = new PushButton(null, "Reset to default", false, delegate { Opt.RandomPool = null; Changed(); });
                reset.Enabled = Opt.RandomPool != null;
                l.Row(Glyphs.Check, "Random pool", n + " of " + total + " scenes ticked" + (Opt.RandomPool == null ? " (the default set)" : ""), reset);
            }
            if (Opt.ForceGdi)
            {
                string msg = "The GPU renderer is turned off (General), so scenes without a GDI version show as Mystify or the digital clock.";
                l.Gap(4);
                l.Add(new InfoBar(msg, true), InfoBar.Height(UI, msg, l.Width));
            }

            int gap = UI.S(12);
            int cols = Math.Max(2, (l.Width + gap) / (UI.S(210) + gap));
            int cardW = (l.Width - gap * (cols - 1)) / cols;
            int thumbW = cardW - UI.S(8), thumbH = thumbW * 9 / 16;
            int cardH = thumbH + UI.S(48);
            for (int gi = 0; gi < SceneCatalog.Groups.Length; gi++)
            {
                l.Header(SceneCatalog.GroupNames[gi]);
                l.Gap(4);
                string[] ids = SceneCatalog.Groups[gi];
                for (int i = 0; i < ids.Length; i++)
                {
                    string id = ids[i];
                    int col = i % cols;
                    if (col == 0 && i > 0) l.Y += cardH + gap;
                    var card = new SceneCard(Window.Thumbs.Get(id, thumbW, thumbH), SceneCatalog.Label(id));
                    if (random)
                    {
                        card.Checkable = SceneCatalog.Poolable(id);
                        card.Checked = card.Checkable && SceneCatalog.InPool(Opt.RandomPool, id);
                        card.Dim = !card.Checked;
                        if (!card.Checkable) card.Note2 = "Not in Random";
                        if (card.Checkable) card.OnClick = delegate { TogglePool(id); };
                    }
                    else
                    {
                        card.Selected = Opt.Mode == id;
                        card.OnClick = delegate { Opt.Mode = id; lastSingle = id; Changed(); };
                    }
                    l.Place(card, new Rectangle(l.X + col * (cardW + gap), l.Y, cardW, cardH));
                    cards.Add(card);
                }
                l.Y += cardH;
            }
        }

        void TogglePool(string id)
        {
            if (Opt.RandomPool == null) Opt.RandomPool = new List<string>(SceneCatalog.DefaultPool);
            if (!Opt.RandomPool.Remove(id)) Opt.RandomPool.Add(id);
            Changed();
        }

        public override void Scrolled() { Window.Thumbs.Want(this, VisibleThumbs(cards, Window.ContentViewport)); }
        public override void Shown() { Scrolled(); }
        public override void Hidden() { Window.Thumbs.Want(this, null); }

        public static List<Thumb> VisibleThumbs(IEnumerable<SceneCard> cards, Rectangle view)
        {
            var list = new List<Thumb>();
            foreach (SceneCard c in cards)
                if (c.Thumb != null && c.Bounds.IntersectsWith(view)) list.Add(c.Thumb);
            return list;
        }
    }

    // A scene tile: live thumbnail, name, note icon for audio scenes; selectable or tickable.
    sealed class SceneCard : Widget, IThumbView
    {
        public readonly Thumb Thumb;
        public readonly string Label;
        public bool Selected, Checkable, Checked, Dim, Compact;
        public string Note2;
        public Action OnClick;

        public SceneCard(Thumb thumb, string label) { Thumb = thumb; Label = label; }

        public override bool Interactive { get { return OnClick != null; } }
        public bool HasFreshFrame { get { return Thumb != null && Thumb.Fresh; } }

        public override void Paint(Graphics g, Ui ui)
        {
            Color back = Hot && OnClick != null ? (Pressed ? Ui.CardPressed : Ui.CardHover) : Ui.Card;
            ui.Fill(g, Bounds, ui.SF(8), back);
            ui.Border(g, Bounds, ui.SF(8), Selected ? ui.Accent : Ui.CardBorder, Selected ? ui.SF(2) : 1f);
            Rectangle tr = ThumbRect(ui);
            DrawThumb(g, ui, Thumb, tr, back);
            ui.MaskCorners(g, tr, ui.SF(5), back);
            if (Selected)
            {
                var badge = new Rectangle(tr.Right - ui.S(30), tr.Y + ui.S(8), ui.S(22), ui.S(22));
                ui.Fill(g, badge, ui.SF(11), ui.Accent);
                ui.Glyph(g, Glyphs.Check, ui.IconSmall, ui.AccentText, badge);
            }

            var row = new Rectangle(Bounds.X + ui.S(12), tr.Bottom, Bounds.Width - ui.S(20), Bounds.Bottom - tr.Bottom);
            int right = row.Right;
            if (Checkable)
            {
                int s = ui.S(20);
                var box = new Rectangle(right - s - ui.S(2), row.Y + (row.Height - s) / 2, s, s);
                if (Checked)
                {
                    ui.Fill(g, box, ui.SF(4), Hot ? ui.AccentHover : ui.Accent);
                    ui.Glyph(g, Glyphs.Check, ui.IconSmall, ui.AccentText, box);
                }
                else
                {
                    ui.Fill(g, box, ui.SF(4), Color.FromArgb(36, 36, 36));
                    ui.Border(g, box, ui.SF(4), Hot ? Ui.Text : Ui.TextSecondary, 1f);
                }
                right = box.X - ui.S(8);
            }
            bool audio = Thumb != null && GpuScenes.UsesAudio(Thumb.Id);
            if (audio)
            {
                var n = new Rectangle(right - ui.S(18), row.Y, ui.S(18), row.Height);
                ui.Glyph(g, Glyphs.Note, ui.Icon, ui.Accent, n);
                right = n.X - ui.S(6);
            }
            Color fg = Dim && Checkable ? Ui.TextSecondary : Ui.Text;
            if (Note2 != null)
            {
                ui.Line(g, Label, Compact ? ui.Caption : ui.Body, fg, new Rectangle(row.X, row.Y - ui.S(8), right - row.X, row.Height));
                ui.Line(g, Note2, ui.Caption, Ui.TextTertiary, new Rectangle(row.X, row.Y + ui.S(10), right - row.X, row.Height - ui.S(10)));
            }
            else ui.Line(g, Label, Compact ? ui.Caption : ui.Body, fg, new Rectangle(row.X, row.Y, right - row.X, row.Height));
        }

        public Rectangle ThumbRect(Ui ui)
        {
            int inset = ui.S(4);
            return new Rectangle(Bounds.X + inset, Bounds.Y + inset,
                                 Thumb != null ? Thumb.W : Bounds.Width - inset * 2,
                                 Thumb != null ? Thumb.H : (Bounds.Width - inset * 2) * 9 / 16);
        }

        // The live picture, over the cached card. Clipped to the image's rounded corners, and the
        // dim veil for unticked scenes is drawn here too (it is over the picture, not under it).
        public override void PaintLive(Graphics g, Ui ui)
        {
            if (Thumb == null) return;
            Rectangle tr = ThumbRect(ui);
            if (Thumb.Id != "blank" && !ui.Caching)
            {
                using (GraphicsPath path = Ui.Round(tr, ui.SF(5)))
                {
                    Region old = g.Clip;
                    g.SetClip(path, CombineMode.Intersect);
                    try { Thumb.Draw(g, tr); }
                    finally { g.Clip = old; old.Dispose(); }
                }
            }
            if (Dim) ui.Fill(g, tr, ui.SF(5), Color.FromArgb(150, 24, 24, 24));
        }

        // The live frame, or a placeholder while it starts (and black for "blank").
        public static void DrawThumb(Graphics g, Ui ui, Thumb t, Rectangle r, Color back)
        {
            if (t != null && t.Id != "blank" && !ui.Caching)
            {
                if (t.Draw(g, r)) return;
            }
            using (var b = new SolidBrush(t != null && t.Id == "blank" ? Color.Black : Color.FromArgb(18, 18, 18))) g.FillRectangle(b, r);
            if (t == null) return;
            string msg = t.Id == "blank" ? "Pure black" : t.Error != null ? "No preview" : "";
            if (msg != "") ui.Glyph(g, msg, ui.Caption, Ui.TextTertiary, r);
        }

        public override void Click(Point p) { if (OnClick != null) OnClick(); }
    }

    // A row of mutually exclusive options (Windows 11 segmented control look).
    sealed class Segmented : Widget, IMeasured
    {
        readonly string[] items;
        int selected, hotIndex = -1;
        readonly Action<int> changed;

        public Segmented(string[] items, int selected, Action<int> changed) { this.items = items; this.selected = selected; this.changed = changed; }

        public override bool Interactive { get { return true; } }

        int ItemWidth(Ui ui)
        {
            int w = 0;
            foreach (string s in items) w = Math.Max(w, ui.Measure(s, ui.Body).Width + ui.S(32));
            return w;
        }

        public int MeasureWidth(Ui ui) { return ItemWidth(ui) * items.Length + ui.S(8); }
        public int MeasureHeight(Ui ui) { return ui.S(36); }

        Rectangle ItemRect(Ui ui, int i)
        {
            int w = ItemWidth(ui);
            return new Rectangle(Bounds.X + ui.S(4) + i * w, Bounds.Y + ui.S(4), w, Bounds.Height - ui.S(8));
        }

        public override void Paint(Graphics g, Ui ui)
        {
            ui.Fill(g, Bounds, ui.SF(6), Color.FromArgb(36, 36, 36));
            ui.Border(g, Bounds, ui.SF(6), Ui.ControlBorder, 1f);
            for (int i = 0; i < items.Length; i++)
            {
                Rectangle r = ItemRect(ui, i);
                if (i == selected) ui.Fill(g, r, ui.SF(4), ui.Accent);
                else if (i == hotIndex && Hot) ui.Fill(g, r, ui.SF(4), Ui.ControlHover);
                ui.DrawText(g, items[i], i == selected ? ui.BodyStrong : ui.Body, i == selected ? ui.AccentText : Ui.Text, r,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        public override void MouseMove(Point p)
        {
            int h = -1;
            for (int i = 0; i < items.Length; i++) if (ItemRect(UI, i).Contains(p)) h = i;
            if (h != hotIndex) { hotIndex = h; Invalidate(); }
        }

        public override void Click(Point p)
        {
            for (int i = 0; i < items.Length; i++)
                if (ItemRect(UI, i).Contains(p) && i != selected) { selected = i; Invalidate(); changed(i); return; }
        }
    }

    // ---------------------------------------------------------------- Screens

    sealed class ScreensPage : Page
    {
        public override string Title { get { return "Screens"; } }
        public override string Glyph { get { return Glyphs.Screens; } }

        MonitorMap map;

        public override void Build(Layout l)
        {
            GpuScreen[] screens = new GpuScreen[0];
            try { screens = GpuScreen.Current(); } catch (Exception ex) { Program.Log("screen list failed: " + ex.Message); }
            ScreenMode mode = ScreenModes.Parse(Opt.Screens);
            if (Opt.ForceGdi)
            {
                string msg = "Screen modes and per-screen scenes need the GPU renderer, which is turned off (General). The GDI renderer always draws a copy per screen.";
                l.Add(new InfoBar(msg, true), InfoBar.Height(UI, msg, l.Width));
                l.Gap(12);
            }
            map = l.Add(new MonitorMap(this, screens), UI.S(360));
            l.Gap(8);
            l.Text(mode == ScreenMode.PerScreen ? "Click a screen to choose its scene." : "Live preview of what each screen shows. In Span mode the scene really continues from one screen to the next: it stands on the real floors and keeps clear of the gaps.", Ui.TextTertiary);

            if (mode == ScreenMode.Span && !Opt.ForceGdi && screens.Length > 1)
            {
                // Clocks never span across a bezel: the time sits whole on one screen, and the
                // others get a quiet ambient layer. Automatic avoids OLED panels.
                int auto = SpanConfig.AutoClockScreen(screens);
                var values = new List<string> { "" };
                var labels = new List<string> { "Automatic (" + screens[auto].Name + ")" };
                string selected = "";
                for (int i = 0; i < screens.Length; i++)
                {
                    values.Add(screens[i].Key);
                    labels.Add((i + 1) + "  " + screens[i].Name + (screens[i].IsOled ? " (OLED)" : ""));
                    if (string.Equals(screens[i].Key, SpanConfig.ClockScreen, StringComparison.OrdinalIgnoreCase)) selected = screens[i].Key;
                }
                l.Gap(8);
                l.Row(Glyphs.Clock, "Clock screen", "In Span mode the clocks (and music mode's clock) show whole on this screen; the other screens get a dim ambient layer. Automatic picks the largest screen that isn't an OLED.",
                      new DropDown(values.ToArray(), labels.ToArray(), selected, delegate(string v) { SpanConfig.ClockScreen = v; Changed(); }) { MinWidth = 220 });
            }

            l.Header("Layout");
            l.Gap(4);
            int gap = UI.S(12);
            int cols = l.Width >= UI.S(760) ? 4 : 2;
            int w = (l.Width - gap * (cols - 1)) / cols, h = UI.S(186);
            for (int i = 0; i < ScreenModes.Names.Length; i++)
            {
                string name = ScreenModes.Names[i];
                int col = i % cols;
                if (col == 0 && i > 0) l.Y += h + gap;
                var card = new ModeCard((ScreenMode)i, screens, mode == (ScreenMode)i, delegate { Opt.Screens = name; Changed(); });
                card.Enabled = !Opt.ForceGdi;
                l.Place(card, new Rectangle(l.X + col * (w + gap), l.Y, w, h));
            }
            l.Y += h;

            if (mode == ScreenMode.PerScreen)
            {
                l.Header("Scene per screen");
                for (int i = 0; i < screens.Length; i++)
                {
                    GpuScreen s = screens[i];
                    string cur = SceneOf(s, true);
                    int index = i;
                    var pick = new PushButton(Glyphs.ChevronDown, cur == null ? "Same as Scenes page" : SceneCatalog.Label(cur), false, null);
                    pick.OnClick = delegate { OpenPicker(index, Window.Content.ScreenRect(pick.Bounds)); };
                    l.Row(Glyphs.Monitor, (i + 1) + "  " + s.Name, s.Bounds.Width + " \u00D7 " + s.Bounds.Height + (s.Id != "" ? "  \u00B7  " + s.Id : "") + (s.Primary ? "  \u00B7  primary" : ""), pick);
                }
            }
        }

        // The scene a screen shows: its own (per-screen mode), else the main choice. null = "same as
        // the Scenes page" (only asked with own = true).
        public string SceneOf(GpuScreen s, bool own)
        {
            string id;
            if (ScreenModes.Parse(Opt.Screens) == ScreenMode.PerScreen && Opt.SceneFor.TryGetValue(s.Key, out id) && GpuScenes.Has(id)) return id;
            return own ? null : Opt.Mode;
        }

        public void OpenPicker(int index, Rectangle anchor)
        {
            GpuScreen s = map.Screens[index];
            string key = s.Key;
            var picker = new ScenePicker(Window, SceneOf(s, true), "Same as Scenes page", delegate(string id)
            {
                if (id == null) Opt.SceneFor.Remove(key); else Opt.SceneFor[key] = id;
                Changed();
            });
            picker.Text = "Scene for " + s.Name;
            picker.Open(anchor);
        }

        public override void Scrolled()
        {
            if (map == null) return;
            Window.Thumbs.Want(this, map.Bounds.IntersectsWith(Window.ContentViewport) ? map.Thumbs : null);
        }

        public override void Shown() { Scrolled(); }
        public override void Hidden() { Window.Thumbs.Want(this, null); }
    }

    // The monitors drawn to scale like Windows' Display settings, each with a live preview of its scene.
    sealed class MonitorMap : Widget, IThumbView
    {
        readonly ScreensPage page;
        public readonly GpuScreen[] Screens;
        Rectangle[] rects;
        Thumb[] thumbs;
        Thumb spanThumb;          // Span mode: one preview over all screens, as the veil composes it
        Point spanOrigin;
        int hotIndex = -1;
        Rectangle laidOutFor;

        public MonitorMap(ScreensPage page, GpuScreen[] screens) { this.page = page; Screens = screens; }

        // The map redraws its own pictures every frame, so it is painted live rather than cached.
        public override bool Dynamic { get { return true; } }

        bool PerScreen { get { return ScreenModes.Parse(page.Window.Host.Options.Screens) == ScreenMode.PerScreen && !page.Window.Host.Options.ForceGdi; } }
        public override bool Interactive { get { return PerScreen; } }

        public List<Thumb> Thumbs
        {
            get
            {
                LayoutRects();
                var list = new List<Thumb>();
                if (spanThumb != null) list.Add(spanThumb);
                foreach (Thumb t in thumbs) if (t != null) list.Add(t);
                return list;
            }
        }

        public bool HasFreshFrame
        {
            get
            {
                if (thumbs == null) return false;
                if (spanThumb != null && spanThumb.Fresh) return true;
                foreach (Thumb t in thumbs) if (t != null && t.Fresh) return true;
                return false;
            }
        }

        void LayoutRects()
        {
            if (rects != null && laidOutFor == Bounds) return;
            laidOutFor = Bounds;
            Ui ui = page.Window.UI;
            rects = new Rectangle[Screens.Length];
            thumbs = new Thumb[Screens.Length];
            spanThumb = null;
            if (Screens.Length == 0) return;
            Rectangle u = Screens[0].Bounds;
            foreach (GpuScreen s in Screens) u = Rectangle.Union(u, s.Bounds);
            int pad = ui.S(28);
            float k = Math.Min((Bounds.Width - pad * 2) / (float)u.Width, (Bounds.Height - pad * 2) / (float)u.Height);
            int ox = Bounds.X + (Bounds.Width - (int)(u.Width * k)) / 2, oy = Bounds.Y + (Bounds.Height - (int)(u.Height * k)) / 2;
            int g = ui.S(3);   // the gap between screens, like Display settings
            for (int i = 0; i < Screens.Length; i++)
            {
                Rectangle b = Screens[i].Bounds;
                var r = new Rectangle(ox + (int)((b.X - u.X) * k) + g / 2, oy + (int)((b.Y - u.Y) * k) + g / 2, (int)(b.Width * k) - g, (int)(b.Height * k) - g);
                rects[i] = r;
                string scene = page.SceneOf(Screens[i], false);
                if (scene != null && scene != "random" && GpuScenes.Has(scene))
                    thumbs[i] = page.Window.Thumbs.Get(scene, r.Width - ui.S(4), r.Height - ui.S(4));
            }
            // Span: one scene over all screens. Render it once over the inner rects (to scale, gaps
            // included), so the map shows exactly how the scene crosses bezels and avoids holes.
            Options o = page.Window.Host.Options;
            if (ScreenModes.Parse(o.Screens) == ScreenMode.Span && !o.ForceGdi && GpuScenes.Has(o.Mode) && o.Mode != "blank")
            {
                var inner = new Rectangle[Screens.Length];
                Rectangle all = Rectangle.Empty;
                for (int i = 0; i < Screens.Length; i++)
                {
                    inner[i] = new Rectangle(rects[i].X + ui.S(2), rects[i].Y + ui.S(2), rects[i].Width - ui.S(4), rects[i].Height - ui.S(4));
                    all = i == 0 ? inner[i] : Rectangle.Union(all, inner[i]);
                }
                spanOrigin = all.Location;
                var parts = new GpuScreen[Screens.Length];
                for (int i = 0; i < Screens.Length; i++)
                {
                    GpuScreen s = Screens[i];
                    parts[i] = new GpuScreen { Bounds = new Rectangle(inner[i].X - all.X, inner[i].Y - all.Y, Math.Max(8, inner[i].Width), Math.Max(8, inner[i].Height)), Device = s.Device, Id = s.Id, Name = s.Name, Primary = s.Primary };
                }
                spanThumb = page.Window.Thumbs.GetSpan(o.Mode, parts);
                for (int i = 0; i < thumbs.Length; i++) thumbs[i] = null;
            }
        }

        public override void Paint(Graphics g, Ui ui)
        {
            LayoutRects();
            ui.Fill(g, Bounds, ui.SF(8), Color.FromArgb(28, 28, 28));
            ui.Border(g, Bounds, ui.SF(8), Ui.CardBorder, 1f);
            Options opt = page.Window.Host.Options;
            for (int i = 0; i < Screens.Length; i++)
            {
                GpuScreen s = Screens[i];
                Rectangle r = rects[i];
                bool hot = PerScreen && Hot && i == hotIndex;
                ui.Fill(g, r, ui.SF(6), Color.FromArgb(12, 12, 12));
                var inner = new Rectangle(r.X + ui.S(2), r.Y + ui.S(2), r.Width - ui.S(4), r.Height - ui.S(4));
                string scene = page.SceneOf(s, false);
                if (spanThumb != null)
                {
                    var src = new Rectangle(inner.X - spanOrigin.X, inner.Y - spanOrigin.Y, inner.Width, inner.Height);
                    if (!spanThumb.Draw(g, inner, src))
                        using (var b = new SolidBrush(Color.Black)) g.FillRectangle(b, inner);
                }
                else if (thumbs[i] != null) SceneCard.DrawThumb(g, ui, thumbs[i], inner, Color.Black);
                else
                {
                    using (var b = new SolidBrush(Color.FromArgb(20, 20, 20))) g.FillRectangle(b, inner);
                    ui.Glyph(g, scene == "random" ? Glyphs.Shuffle : Glyphs.Monitor, ui.IconLarge, Ui.TextTertiary,
                             new Rectangle(inner.X, inner.Y + inner.Height / 2 - ui.S(26), inner.Width, ui.S(24)));
                }
                ui.MaskCorners(g, inner, ui.SF(4), Color.FromArgb(12, 12, 12));
                ui.MaskCorners(g, r, ui.SF(6), Color.FromArgb(28, 28, 28));
                bool off = opt.OffMonitor != "" && string.Equals(opt.OffMonitor, s.Id, StringComparison.OrdinalIgnoreCase);
                ui.Border(g, r, ui.SF(6), hot ? ui.Accent : off ? Ui.Mix(ui.Accent, Color.Black, 0.35f) : Color.FromArgb(70, 70, 70), hot ? ui.SF(2) : 1f);

                // number
                var num = new Rectangle(r.X + ui.S(8), r.Y + ui.S(8), ui.S(24), ui.S(24));
                ui.Fill(g, num, ui.SF(12), Color.FromArgb(220, 32, 32, 32));
                ui.Glyph(g, (i + 1).ToString(), ui.BodyStrong, Ui.Text, num);
                if (off)
                {
                    string text = "Turns off after " + Minutes(opt.OffAfterSeconds);
                    int tw = ui.Measure(text, ui.Caption).Width + ui.S(34);
                    bool fits = tw < r.Width - ui.S(44);
                    var badge = new Rectangle(fits ? r.Right - tw - ui.S(8) : r.Right - ui.S(32), r.Y + ui.S(8), fits ? tw : ui.S(24), ui.S(24));
                    ui.Fill(g, badge, ui.SF(12), ui.Accent);
                    ui.Glyph(g, Glyphs.Power, ui.IconSmall, ui.AccentText, new Rectangle(badge.X + (fits ? ui.S(6) : 0), badge.Y, fits ? ui.S(16) : badge.Width, badge.Height));
                    if (fits) ui.Line(g, text, ui.Caption, ui.AccentText, new Rectangle(badge.X + ui.S(26), badge.Y, badge.Width - ui.S(28), badge.Height));
                }

                // name, resolution, scene
                string line2 = s.Bounds.Width + " \u00D7 " + s.Bounds.Height + (s.Primary ? "  \u00B7  primary" : "");
                string line3 = scene == null ? null : scene == "random" ? "Random" : SceneCatalog.Label(scene);
                int lines = line3 != null ? 3 : 2;
                int lh = ui.S(17);
                var label = new Rectangle(r.X + ui.S(8), r.Bottom - ui.S(8) - lines * lh - ui.S(8), Math.Min(r.Width - ui.S(16), ui.S(230)), lines * lh + ui.S(8));
                ui.Fill(g, label, ui.SF(5), Color.FromArgb(210, 24, 24, 24));
                ui.Line(g, s.Name, ui.BodyStrong, Ui.Text, new Rectangle(label.X + ui.S(8), label.Y + ui.S(4), label.Width - ui.S(12), lh));
                ui.Line(g, line2, ui.Caption, Ui.TextSecondary, new Rectangle(label.X + ui.S(8), label.Y + ui.S(4) + lh, label.Width - ui.S(12), lh));
                if (line3 != null)
                    ui.Line(g, (PerScreen ? "\u25B8 " : "") + line3, ui.Caption, PerScreen ? ui.Accent : Ui.TextSecondary,
                            new Rectangle(label.X + ui.S(8), label.Y + ui.S(4) + lh * 2, label.Width - ui.S(12), lh));
            }
        }

        static string Minutes(int seconds)
        {
            int m = Math.Max(1, seconds / 60);
            return m + " min";
        }

        int IndexAt(Point p)
        {
            if (rects == null) return -1;
            for (int i = 0; i < rects.Length; i++) if (rects[i].Contains(p)) return i;
            return -1;
        }

        public override void MouseMove(Point p)
        {
            int h = IndexAt(p);
            if (h != hotIndex) { hotIndex = h; Invalidate(); }
        }

        public override void MouseLeave() { hotIndex = -1; }

        public override void Click(Point p)
        {
            int i = IndexAt(p);
            if (i >= 0) page.OpenPicker(i, Surface.ScreenRect(rects[i]));
        }
    }

    // One of the four screen modes, with a little diagram drawn on the user's real monitor layout.
    sealed class ModeCard : Widget
    {
        readonly ScreenMode mode;
        readonly GpuScreen[] screens;
        readonly bool selected;
        readonly Action onClick;

        public ModeCard(ScreenMode mode, GpuScreen[] screens, bool selected, Action onClick) { this.mode = mode; this.screens = screens; this.selected = selected; this.onClick = onClick; }

        public override bool Interactive { get { return !selected; } }

        static string Title(ScreenMode m)
        {
            switch (m)
            {
                case ScreenMode.Span: return "Span";
                case ScreenMode.Mirror: return "Mirror";
                case ScreenMode.PerScreen: return "Per screen";
                default: return "Each";
            }
        }

        static string Desc(ScreenMode m)
        {
            switch (m)
            {
                case ScreenMode.Span: return "One scene stretched across every screen.";
                case ScreenMode.Mirror: return "The same picture on every screen.";
                case ScreenMode.PerScreen: return "Choose a scene for each screen.";
                default: return "Its own copy of the scene on each screen.";
            }
        }

        public override void Paint(Graphics g, Ui ui)
        {
            Color back = !Enabled ? Ui.Card : Hot ? (Pressed ? Ui.CardPressed : Ui.CardHover) : Ui.Card;
            ui.Fill(g, Bounds, ui.SF(8), back);
            ui.Border(g, Bounds, ui.SF(8), selected ? ui.Accent : Ui.CardBorder, selected ? ui.SF(2) : 1f);
            var diagram = new Rectangle(Bounds.X + ui.S(14), Bounds.Y + ui.S(14), Bounds.Width - ui.S(28), ui.S(80));
            DrawDiagram(g, ui, diagram);
            int y = diagram.Bottom + ui.S(12);
            var radio = new Rectangle(Bounds.X + ui.S(14), y + ui.S(1), ui.S(18), ui.S(18));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (selected)
            {
                using (var b = new SolidBrush(ui.Accent)) g.FillEllipse(b, radio);
                float d = ui.SF(8);
                using (var b = new SolidBrush(ui.AccentText)) g.FillEllipse(b, radio.X + (radio.Width - d) / 2f, radio.Y + (radio.Height - d) / 2f, d, d);
            }
            else using (var p = new Pen(Enabled ? Ui.TextSecondary : Ui.TextDisabled, 1f)) g.DrawEllipse(p, radio.X + 0.5f, radio.Y + 0.5f, radio.Width - 1, radio.Height - 1);
            Color fg = Enabled ? Ui.Text : Ui.TextDisabled;
            ui.Line(g, Title(mode), ui.BodyStrong, fg, new Rectangle(radio.Right + ui.S(10), y, Bounds.Right - radio.Right - ui.S(20), ui.S(20)));
            ui.DrawText(g, Desc(mode), ui.Caption, Enabled ? Ui.TextSecondary : Ui.TextDisabled,
                    new Rectangle(Bounds.X + ui.S(14), y + ui.S(26), Bounds.Width - ui.S(28), Bounds.Bottom - y - ui.S(30)), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        void DrawDiagram(Graphics g, Ui ui, Rectangle area)
        {
            if (screens.Length == 0) return;
            Rectangle u = screens[0].Bounds;
            foreach (GpuScreen s in screens) u = Rectangle.Union(u, s.Bounds);
            float k = Math.Min(area.Width / (float)u.Width, area.Height / (float)u.Height);
            float ox = area.X + (area.Width - u.Width * k) / 2, oy = area.Y + (area.Height - u.Height * k) / 2;
            var span = new RectangleF(ox, oy, u.Width * k, u.Height * k);
            Color a = Enabled ? ui.Accent : Ui.TextDisabled;
            Color[] per = { a, Color.FromArgb(108, 203, 95), Color.FromArgb(255, 170, 90), Color.FromArgb(120, 170, 255) };
            float gap = ui.SF(2);
            GpuScreen big = screens[0];
            foreach (GpuScreen s in screens) if ((long)s.Bounds.Width * s.Bounds.Height > (long)big.Bounds.Width * big.Bounds.Height) big = s;
            for (int i = 0; i < screens.Length; i++)
            {
                Rectangle b = screens[i].Bounds;
                var r = new RectangleF(ox + (b.X - u.X) * k + gap / 2, oy + (b.Y - u.Y) * k + gap / 2, b.Width * k - gap, b.Height * k - gap);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = Ui.Round(r, ui.SF(3)))
                {
                    using (var bg = new SolidBrush(Color.FromArgb(18, 18, 18))) g.FillPath(bg, path);
                    RectangleF grad;
                    Color c0 = a, c1 = Color.FromArgb(40, a);
                    if (mode == ScreenMode.Span) grad = span;
                    else if (mode == ScreenMode.Mirror)
                    {
                        float kk = Math.Min(r.Width / big.Bounds.Width, r.Height / big.Bounds.Height);
                        float w = big.Bounds.Width * kk, h = big.Bounds.Height * kk;
                        grad = new RectangleF(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
                    }
                    else grad = r;
                    if (mode == ScreenMode.PerScreen) { c0 = Enabled ? per[i % per.Length] : Ui.TextDisabled; c1 = Color.FromArgb(40, c0); }
                    g.SetClip(path);
                    using (var lb = new LinearGradientBrush(new RectangleF(grad.X - 1, grad.Y - 1, grad.Width + 2, grad.Height + 2), c0, c1, 35f))
                        g.FillRectangle(lb, grad);
                    if (mode == ScreenMode.Mirror || mode == ScreenMode.Each)
                    {
                        // the same "subject" in each copy
                        float d = Math.Min(grad.Width, grad.Height) * 0.35f;
                        using (var dot = new SolidBrush(Color.FromArgb(220, 255, 255, 255))) g.FillEllipse(dot, grad.X + grad.Width * 0.3f - d / 2, grad.Y + grad.Height * 0.5f - d / 2, d, d);
                    }
                    else if (mode == ScreenMode.Span)
                    {
                        float d = span.Height * 0.3f;
                        using (var dot = new SolidBrush(Color.FromArgb(220, 255, 255, 255))) g.FillEllipse(dot, span.X + span.Width * 0.55f - d / 2, span.Y + span.Height * 0.25f - d / 2, d, d);
                    }
                    g.ResetClip();
                    using (var pen = new Pen(Color.FromArgb(90, 90, 90), 1f)) g.DrawPath(pen, path);
                }
            }
        }

        public override void Click(Point p) { if (Enabled && onClick != null) onClick(); }
    }

    // A popup grid of live scene tiles; picking one calls back and closes.
    sealed class ScenePicker : PopupForm
    {
        readonly SettingsWindow window;
        readonly List<SceneCard> cards = new List<SceneCard>();
        readonly Action<string> picked;
        readonly string current, sameLabel;

        public ScenePicker(SettingsWindow window, string current, string sameLabel, Action<string> picked) : base(window.UI)
        {
            this.window = window; this.current = current; this.sameLabel = sameLabel; this.picked = picked;
            Body.Scrolled += delegate { UpdateWants(); };
        }

        public void Open(Rectangle anchor)
        {
            Ui ui = window.UI;
            int cols = 4, gap = ui.S(8), tw = ui.S(150), th = tw * 9 / 16, cw = tw + ui.S(8), ch = th + ui.S(36);
            int width = cols * cw + (cols - 1) * gap + ui.S(32);
            var l = new Layout(ui, ui.S(16), ui.S(12), width - ui.S(32));
            l.Add(new TextBlock(Text, ui.BodyStrong, Ui.Text), ui.S(30));
            if (sameLabel != null)
            {
                var same = new PushButton(current == null ? Glyphs.Check : null, sameLabel, current == null, delegate { Pick(null); });
                l.Gap(4);
                l.Place(same, new Rectangle(l.X, l.Y, same.MeasureWidth(ui), ui.S(32)));
                l.Y += ui.S(36);
            }
            for (int gi = 0; gi < SceneCatalog.Groups.Length; gi++)
            {
                l.Gap(8);
                l.Add(new TextBlock(SceneCatalog.GroupNames[gi], ui.Caption, Ui.TextSecondary), ui.S(22));
                string[] ids = SceneCatalog.Groups[gi];
                for (int i = 0; i < ids.Length; i++)
                {
                    string id = ids[i];
                    if (i > 0 && i % cols == 0) l.Y += ch + gap;
                    var card = new SceneCard(window.Thumbs.Get(id, tw, th), SceneCatalog.Label(id)) { Compact = true, Selected = id == current };
                    card.OnClick = delegate { Pick(id); };
                    l.Place(card, new Rectangle(l.X + (i % cols) * (cw + gap), l.Y, cw, ch));
                    cards.Add(card);
                }
                l.Y += ch;
            }
            l.Gap(16);
            Body.SetWidgets(l.Items, l.Y);
            ShowAt(window, anchor, new Size(width, Math.Min(l.Y, ui.S(600))));
            UpdateWants();
        }

        void UpdateWants() { window.Thumbs.Want(this, ScenesPage.VisibleThumbs(cards, Body.Viewport)); }

        void Pick(string id)
        {
            Close();
            picked(id);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            window.Thumbs.Want(this, null);
        }
    }

    // ---------------------------------------------------------------- Timing & OLED

    sealed class TimingPage : Page
    {
        public override string Title { get { return "Timing & OLED"; } }
        public override string Glyph { get { return Glyphs.Timing; } }

        static readonly int[] StartMinutes = { 1, 2, 3, 5, 10, 15, 20, 30, 45, 60, 90, 120 };
        static readonly int[] OffMinutes = { 1, 2, 5, 10, 15, 30, 60 };

        static string MinLabel(int m) { return m + (m == 1 ? " minute" : " minutes"); }

        static DropDown SecondsChoice(int[] minutes, int current, Action<int> set)
        {
            var values = new List<string>();
            var labels = new List<string>();
            bool found = false;
            foreach (int m in minutes)
            {
                values.Add((m * 60).ToString());
                labels.Add(MinLabel(m));
                if (m * 60 == current) found = true;
            }
            if (!found)
            {
                values.Insert(0, current.ToString());
                labels.Insert(0, current % 60 == 0 ? MinLabel(current / 60) : current + " seconds");
            }
            return new DropDown(values.ToArray(), labels.ToArray(), current.ToString(), delegate(string v) { set(int.Parse(v)); });
        }

        public override void Build(Layout l)
        {
            l.Row(Glyphs.Timing, "Start after", "How long the PC has to be idle before the veil appears.",
                  SecondsChoice(StartMinutes, Opt.IdleSeconds, delegate(int s) { Opt.IdleSeconds = s; Changed(); }));

            l.Header("OLED care");
            var values = new List<string> { "" };
            var labels = new List<string> { "None" };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MonitorInfo m in DisplayDetach.List())
            {
                if (m.Id == "" || !seen.Add(m.Id)) continue;
                values.Add(m.Id);
                labels.Add(m.Name + " (" + m.Id + ")");
            }
            string off = Opt.OffMonitor ?? "";
            if (off != "" && !seen.Contains(off))
            {
                values.Add(off);
                labels.Add((DisplayDetach.IsDetached ? DisplayDetach.DetachedName + ", off now" : off + ", not connected"));
            }
            string selected = off;
            foreach (string v in values) if (string.Equals(v, off, StringComparison.OrdinalIgnoreCase)) selected = v;
            l.Row(Glyphs.Power, "Turn off a monitor",
                  "Detaches it from the desktop once the veil has shown for a while. It sees no signal and goes into its own standby, where OLEDs run their pixel refresh. Input brings it back, with your windows where they were.",
                  new DropDown(values.ToArray(), labels.ToArray(), selected, delegate(string v) { Opt.OffMonitor = v; Changed(); }) { MinWidth = 220 });
            var after = SecondsChoice(OffMinutes, Opt.OffAfterSeconds, delegate(int s) { Opt.OffAfterSeconds = s; Changed(); });
            after.Enabled = off != "";
            SettingRow afterRow = l.Row(Glyphs.Clock, "Turn it off after", "Time the veil has been showing. It waits while an app (e.g. an AI agent) is still clicking around.", after);
            afterRow.Enabled = off != "";

            l.Header("Activity");
            l.Row(Glyphs.Video, "Keep screen on",
                  "A fullscreen app in front (a game or a video) stops the veil. Always also stops it for windowed apps that ask Windows to keep the display on (video players, calls); Never ignores both.",
                  new DropDown(new[] { "fullscreen", "always", "never" }, new[] { "Fullscreen apps", "Always", "Never" }, Opt.KeepOnFor,
                               delegate(string v) { Opt.KeepOnFor = v; Changed(); }));
            l.Row(Glyphs.Cursor, "Hide cursor", "Hide the mouse pointer while the veil is showing.",
                  new Toggle(Opt.HideCursor, delegate(bool on) { Opt.HideCursor = on; Changed(); }));
            l.Row(Glyphs.Robot, "Ignore input from apps", "Only real mouse, keyboard and controller input counts. Clicks and keys injected by AI agents and automation don't keep the screen awake. Turn off for remote-control tools (Parsec, Sunshine) that should count as you.",
                  new Toggle(Opt.IgnoreInjected, delegate(bool on) { Opt.IgnoreInjected = on; Changed(); }));
        }
    }

    // ---------------------------------------------------------------- Music

    sealed class MusicPage : Page
    {
        public override string Title { get { return "Music"; } }
        public override string Glyph { get { return Glyphs.Music; } }

        MusicMeter meter;
        readonly List<SceneCard> cards = new List<SceneCard>();
        LoopbackAudio audio;

        static string lastViz;   // the visualizer "One" returns to after Random

        public override void Build(Layout l)
        {
            cards.Clear();
            l.Text("Music mode shows a visualizer while something is playing and a clock when it's quiet. It listens to the audio source below (nothing is recorded or sent anywhere).", Ui.TextSecondary);
            l.Gap(12);
            meter = l.Add(new MusicMeter(), UI.S(84));
            l.Gap(4);
            BuildSource(l);
            BuildSensitivity(l);

            bool random = MusicConfig.Viz == "random";
            if (!random) lastViz = MusicConfig.Viz;
            if (lastViz == null) lastViz = "spectrum";
            var seg = new Segmented(new[] { "One visualizer", "Random" }, random ? 1 : 0, delegate(int i)
            {
                MusicConfig.Viz = i == 1 ? "random" : lastViz;
                Changed();
            });
            l.Row(random ? Glyphs.Shuffle : Glyphs.Note, "While music plays",
                  random ? "Each time the music starts, one of the visualizers ticked below takes over."
                         : "The visualizer you click below shows while music plays.", seg);

            int gap = UI.S(12);
            int cols = Math.Max(2, (l.Width + gap) / (UI.S(210) + gap));
            int cardW = (l.Width - gap * (cols - 1)) / cols;
            int thumbW = cardW - UI.S(8), thumbH = thumbW * 9 / 16;
            int cardH = thumbH + UI.S(48);

            l.Header("Visualizers");
            l.Gap(4);
            Grid(l, MusicConfig.Visualizers, cols, cardW, cardH, gap, thumbW, thumbH, delegate(SceneCard card, string id)
            {
                if (random)
                {
                    card.Checkable = true;
                    card.Checked = MusicConfig.InPool(id);
                    card.Dim = !card.Checked;
                    card.OnClick = delegate { TogglePool(id); };
                }
                else
                {
                    card.Selected = MusicConfig.Viz == id;
                    card.OnClick = delegate { MusicConfig.Viz = id; lastViz = id; Changed(); };
                }
            });

            l.Header("When it's quiet");
            l.Gap(4);
            Grid(l, MusicConfig.Clocks, cols, cardW, cardH, gap, thumbW, thumbH, delegate(SceneCard card, string id)
            {
                card.Selected = MusicConfig.Clock == id;
                card.OnClick = delegate { MusicConfig.Clock = id; Changed(); };
            });

            // Per-screen mode: every monitor can have its own visualizer (or its own random pick).
            if (ScreenModes.Parse(Opt.Screens) == ScreenMode.PerScreen && !Opt.ForceGdi)
            {
                l.Header("Visualizer per screen");
                GpuScreen[] screens = new GpuScreen[0];
                try { screens = GpuScreen.Current(); } catch (Exception ex) { Program.Log("screen list failed: " + ex.Message); }
                var values = new List<string> { "", "random" };
                var labels = new List<string> { "Same as above", "Random from pool" };
                foreach (string id in MusicConfig.Visualizers) { values.Add(id); labels.Add(SceneCatalog.Label(id)); }
                for (int i = 0; i < screens.Length; i++)
                {
                    GpuScreen s = screens[i];
                    string key = s.Key, cur;
                    if (!MusicConfig.VizFor.TryGetValue(key, out cur)) cur = "";
                    l.Row(Glyphs.Monitor, (i + 1) + "  " + s.Name, s.Bounds.Width + " × " + s.Bounds.Height + (s.Primary ? "  ·  primary" : ""),
                          new DropDown(values.ToArray(), labels.ToArray(), cur, delegate(string v)
                          {
                              if (v == "") MusicConfig.VizFor.Remove(key); else MusicConfig.VizFor[key] = v;
                              Changed();
                          }) { MinWidth = 200 });
                }
            }

            l.Header("Try it");
            l.Row(Glyphs.Play, "Test music mode", "Shows the real veil in music mode now. Move the mouse or press a key to end it.",
                  new PushButton(Glyphs.Play, "Test", false, delegate { Host.Preview("music"); }));
            if (Opt.Mode == "music")
                l.Row(Glyphs.Check, "Music mode is your scene", "Chosen on the Scenes page.", null);
            else
                l.Row(Glyphs.Scenes, "Use music mode", "Make music mode the scene that shows when the PC is idle (now: " + (Opt.Mode == "random" ? "Random" : SceneCatalog.Label(Opt.Mode)) + ").",
                      new PushButton(null, "Use music mode", true, delegate { Opt.Mode = "music"; Changed(); }));
        }

        void Grid(Layout l, string[] ids, int cols, int cardW, int cardH, int gap, int thumbW, int thumbH, Action<SceneCard, string> setup)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                int col = i % cols;
                if (col == 0 && i > 0) l.Y += cardH + gap;
                var card = new SceneCard(Window.Thumbs.Get(id, thumbW, thumbH), SceneCatalog.Label(id));
                setup(card, id);
                l.Place(card, new Rectangle(l.X + col * (cardW + gap), l.Y, cardW, cardH));
                cards.Add(card);
            }
            l.Y += cardH;
        }

        // Audio source: Default, Auto, or one playback / recording device, with live levels so the
        // device that really carries the music is easy to spot (Voicemeeter has many).
        void BuildSource(Layout l)
        {
            if (devices == null) LoadDevices();
            string def = null;
            foreach (AudioDevice d in devices) if (d.IsDefault && !d.Capture) def = d.Name;
            var values = new List<string> { "default", "auto" };
            var labels = new List<string> { "Default output" + (def != null ? " (" + def + ")" : ""), "Auto (the loudest playback device)" };
            bool known = AudioConfig.Device == "default" || AudioConfig.Device == "auto";
            for (int pass = 0; pass < 2; pass++)
            {
                bool cap = pass == 1;
                bool any = false;
                foreach (AudioDevice d in devices)
                {
                    if (d.Capture != cap) continue;
                    if (!any) { values.Add(null); labels.Add(cap ? "Recording devices" : "Playback devices"); any = true; }
                    values.Add(d.Id);
                    labels.Add(d.Name);
                    if (string.Equals(d.Id, AudioConfig.Device, StringComparison.OrdinalIgnoreCase)) known = true;
                }
            }
            if (!known) { values.Add(AudioConfig.Device); labels.Add("Saved device (not connected)"); }
            l.Header("Audio source");
            l.Row(Glyphs.Speaker, "Listen to",
                  "Default follows Windows' default output. A recording device (e.g. Voicemeeter Out B1) is captured directly; a playback device as what it plays.",
                  new DropDown(values.ToArray(), labels.ToArray(), AudioConfig.Device, delegate(string v) { AudioConfig.Device = v; Changed(); }) { MinWidth = 280 });
            levels = l.Add(new DeviceLevels(this), DeviceLevels.Height(UI));
            l.Gap(4);
            l.Row(Glyphs.Update, "Devices", devices.Count + " active. The list updates by itself when a device is added or removed.",
                  new PushButton(Glyphs.Update, "Refresh", false, delegate { LoadDevices(); Window.QueueRebuild(); }));
        }

        // Sensitivity: the dB window the bands are mapped onto. Deckviz has the same five steps.
        // The slider previews live (the shared AudioFeed reads the value every frame); the meter
        // right below shows what the visualizers get, so tuning is a matter of watching it.
        void BuildSensitivity(Layout l)
        {
            l.Header("Sensitivity");
            l.Row(Glyphs.Sensitivity, "Sensitivity",
                  "How hard the visualizers react. Watch the meter: loud parts should reach near the top. Turn it up if the bars stay low, down if they are always maxed.",
                  new Slider(1, 5, AudioConfig.Sensitivity, AudioConfig.SensitivityNames,
                             delegate(int v) { AudioConfig.SetSensitivity(v); },
                             delegate(int v) { AudioConfig.SetSensitivity(v); Changed(); }));
            sensitivity = l.Add(new SensitivityMeter(this), SensitivityMeter.Height(UI));
            l.Gap(8);
        }

        List<AudioDevice> devices;
        AudioMeters meters;
        DeviceLevels levels;
        SensitivityMeter sensitivity;
        string signature;
        int signatureAt;

        void LoadDevices()
        {
            if (meters != null) meters.Dispose();
            devices = AudioDevices.List();
            meters = new AudioMeters(devices);
            signature = AudioDevices.Signature();
            signatureAt = Environment.TickCount;
        }

        public void UseDevice(string id) { AudioConfig.Device = id; Changed(); }

        // What DeviceLevels draws: the device being captured and the loudest ones right now.
        public AudioFeed Feed { get { return audio != null ? audio.Feed : null; } }
        public AudioMeters Meters { get { return meters; } }

        void TogglePool(string id)
        {
            if (MusicConfig.Pool == null) MusicConfig.Pool = new List<string>(MusicConfig.Visualizers);
            if (!MusicConfig.Pool.Remove(id)) MusicConfig.Pool.Add(id);
            Changed();
        }

        public override void Shown()
        {
            if (audio == null) audio = new LoopbackAudio();
            if (devices == null) LoadDevices();
            Scrolled();
        }

        public override void Hidden()
        {
            if (audio != null) { audio.Dispose(); audio = null; }
            if (meters != null) { meters.Dispose(); meters = null; }
            devices = null;
            Window.Thumbs.Want(this, null);
        }

        public override void Scrolled() { Window.Thumbs.Want(this, ScenesPage.VisibleThumbs(cards, Window.ContentViewport)); }

        public override void Tick()
        {
            if (audio == null || meter == null) return;
            meter.Playing = audio.Read(meter.Bands, meter.Wave);
            meter.Invalidate();
            if (sensitivity != null) sensitivity.Invalidate();
            if (meters != null) meters.Read(1f / 30f);
            if (levels != null) levels.Invalidate();
            // Devices plugged in or removed: rebuild the list (cheap: ids only, every 3 s).
            if (devices != null && Environment.TickCount - signatureAt > 3000)
            {
                signatureAt = Environment.TickCount;
                string now = AudioDevices.Signature();
                if (now != signature) { LoadDevices(); Window.QueueRebuild(); }
            }
        }
    }

    // The device music mode hears now with its level, then the loudest devices at the moment;
    // click one to listen to it.
    sealed class DeviceLevels : Widget
    {
        const int Rows = 5;
        readonly MusicPage page;
        readonly int[] shown = new int[Rows];
        int count, sortedAt;

        public DeviceLevels(MusicPage page) { this.page = page; }

        public override bool Dynamic { get { return true; } }

        public static int Height(Ui ui) { return ui.S(16 + 30 + 22 + Rows * 26 + 12); }

        public override bool Interactive { get { return true; } }

        int RowTop(Ui ui, int i) { return Bounds.Y + ui.S(16 + 30 + 22) + i * ui.S(26); }

        // Peak 0..1 shown on a dB scale (-60..0 dB), so quiet sources still move.
        static float Db(float p) { return p <= 0.001f ? 0f : Math.Min(1f, (float)(20.0 * Math.Log10(p) + 60.0) / 60f); }

        float feedShown;   // VU-style: instant rise, slow fall

        void Bar(Graphics g, Ui ui, Rectangle r, float v, Color c)
        {
            ui.Fill(g, r, ui.SF(2), Color.FromArgb(58, 58, 58));
            int w = (int)(r.Width * v);
            if (w > 1) ui.Fill(g, new Rectangle(r.X, r.Y, w, r.Height), ui.SF(2), c);
        }

        public override void Paint(Graphics g, Ui ui)
        {
            ui.DrawCard(g, Bounds, Ui.Card);
            int x = Bounds.X + ui.S(20), right = Bounds.Right - ui.S(20);
            int barW = Math.Min(ui.S(260), (right - x) / 2), barX = right - barW;
            AudioFeed feed = page.Feed;
            string name = feed != null ? feed.DeviceName : null;
            float lv = feed != null && name != null ? Db(feed.Level) : 0f;
            feedShown = lv > feedShown ? lv : Math.Max(lv, feedShown - 0.012f);
            ui.Line(g, "Listening to", ui.Caption, Ui.TextSecondary, new Rectangle(x, Bounds.Y + ui.S(16), ui.S(100), ui.S(30)));
            ui.Line(g, name ?? "Waiting for the device…", ui.BodyStrong, Ui.Text, new Rectangle(x + ui.S(100), Bounds.Y + ui.S(16), barX - x - ui.S(112), ui.S(30)));
            Bar(g, ui, new Rectangle(barX, Bounds.Y + ui.S(16 + 11), barW, ui.S(8)), feedShown, ui.Accent);
            ui.Line(g, "Loudest devices right now (click one to use it)", ui.Caption, Ui.TextTertiary,
                    new Rectangle(x, Bounds.Y + ui.S(16 + 30), right - x, ui.S(22)));

            AudioMeters m = page.Meters;
            if (m == null) return;
            // Re-rank every few seconds, so the rows do not jump under the mouse.
            if (count == 0 || Environment.TickCount - sortedAt > 2500)
            {
                sortedAt = Environment.TickCount;
                var order = new List<int>();
                for (int i = 0; i < m.Devices.Count; i++) order.Add(i);
                order.Sort(delegate(int a, int b) { return m.Levels[b].CompareTo(m.Levels[a]); });
                count = Math.Min(Rows, order.Count);
                for (int i = 0; i < count; i++) shown[i] = order[i];
            }
            for (int r = 0; r < count; r++)
            {
                int i = shown[r];
                if (i >= m.Devices.Count) continue;
                AudioDevice d = m.Devices[i];
                int y = RowTop(ui, r);
                bool chosen = string.Equals(d.Id, AudioConfig.Device, StringComparison.OrdinalIgnoreCase);
                if (chosen) ui.Glyph(g, Glyphs.Check, ui.IconSmall, ui.Accent, new Rectangle(x - ui.S(4), y, ui.S(18), ui.S(26)));
                ui.Line(g, d.Name + (d.Capture ? "  · recording" : ""), ui.Body, chosen ? Ui.Text : Ui.TextSecondary,
                        new Rectangle(x + ui.S(18), y, barX - x - ui.S(30), ui.S(26)));
                Bar(g, ui, new Rectangle(barX, y + ui.S(10), barW, ui.S(6)), Db(m.Levels[i]), Ui.Good);
            }
        }

        public override void Click(Point p)
        {
            AudioMeters m = page.Meters;
            if (m == null) return;
            for (int r = 0; r < count; r++)
            {
                int y = RowTop(UI, r);
                if (p.Y >= y && p.Y < y + UI.S(26) && shown[r] < m.Devices.Count) { page.UseDevice(m.Devices[shown[r]].Id); return; }
            }
        }
    }

    // "Music detected" indicator with a small live spectrum.
    sealed class MusicMeter : Widget
    {
        public readonly float[] Bands = new float[AudioFrame.BandCount];
        public readonly float[] Wave = new float[AudioFrame.WaveCount];
        public bool Playing;

        public override bool Dynamic { get { return true; } }

        public override void Paint(Graphics g, Ui ui)
        {
            ui.DrawCard(g, Bounds, Ui.Card);
            var dot = new Rectangle(Bounds.X + ui.S(20), Bounds.Y + (Bounds.Height - ui.S(14)) / 2, ui.S(14), ui.S(14));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Playing)
                using (var halo = new SolidBrush(Color.FromArgb(60, Ui.Good))) g.FillEllipse(halo, dot.X - ui.S(5), dot.Y - ui.S(5), dot.Width + ui.S(10), dot.Height + ui.S(10));
            using (var b = new SolidBrush(Playing ? Ui.Good : Color.FromArgb(90, 90, 90))) g.FillEllipse(b, dot);
            int x = dot.Right + ui.S(20);
            ui.Line(g, Playing ? "Music detected" : "Nothing playing", ui.BodyStrong, Ui.Text, new Rectangle(x, Bounds.Y + ui.S(20), ui.S(260), ui.S(22)));
            ui.Line(g, Playing ? "Music mode would show the visualizer now." : "Music mode would show the clock now.", ui.Caption, Ui.TextSecondary,
                    new Rectangle(x, Bounds.Y + ui.S(42), ui.S(300), ui.S(20)));
            // bars
            int n = 32;
            int left = Math.Max(x + ui.S(300), Bounds.Right - ui.S(16) - n * ui.S(7));
            int bw = Math.Max(2, (Bounds.Right - ui.S(16) - left) / n - ui.S(2));
            int maxH = Bounds.Height - ui.S(32);
            int baseY = Bounds.Bottom - ui.S(16);
            using (var br = new SolidBrush(Playing ? ui.Accent : Color.FromArgb(70, 70, 70)))
                for (int i = 0; i < n; i++)
                {
                    float v = Math.Max(Bands[i * 2], Bands[i * 2 + 1]);
                    int h = Math.Max(ui.S(2), (int)(v * maxH));
                    g.FillRectangle(br, left + i * (bw + ui.S(2)), baseY - h, bw, h);
                }
        }
    }

    // The bands the visualizers get after the sensitivity mapping: a wide, thin strip that makes
    // tuning obvious while the slider is dragged. Painted live, like MusicMeter.
    sealed class SensitivityMeter : Widget
    {
        readonly MusicPage page;

        public SensitivityMeter(MusicPage page) { this.page = page; }

        public override bool Dynamic { get { return true; } }
        public static int Height(Ui ui) { return ui.S(76); }

        public override void Paint(Graphics g, Ui ui)
        {
            ui.DrawCard(g, Bounds, Ui.Card);
            int left = Bounds.X + ui.S(20), right = Bounds.Right - ui.S(20);
            ui.Line(g, "Levels after sensitivity", ui.Caption, Ui.TextTertiary,
                    new Rectangle(left, Bounds.Y + ui.S(8), right - left, ui.S(18)));
            int top = Bounds.Y + ui.S(32), bottom = Bounds.Bottom - ui.S(12);
            int h = Math.Max(ui.S(6), bottom - top);
            using (var guide = new Pen(Color.FromArgb(52, 52, 52), 1f))
                for (int k = 1; k <= 3; k++)
                {
                    int y = bottom - h * k / 4;
                    g.DrawLine(guide, left, y, right, y);
                }
            AudioFeed feed = page.Feed;
            float[] bands = feed != null ? feed.Bands : null;
            const int n = AudioFrame.BandCount;
            int gap = Math.Max(1, ui.S(2)), w = Math.Max(1, (right - left - gap * (n - 1)) / n);
            bool playing = feed != null && feed.Playing;
            using (var br = new SolidBrush(playing ? ui.Accent : Color.FromArgb(72, 72, 72)))
            {
                for (int i = 0; i < n; i++)
                {
                    float v = bands != null ? bands[i] : 0f;
                    int bh = Math.Max(ui.S(1), (int)(v * h));
                    g.FillRectangle(br, left + i * (w + gap), bottom - bh, w, bh);
                }
            }
        }
    }

    // ---------------------------------------------------------------- Apps

    sealed class AppsPage : Page
    {
        public override string Title { get { return "Apps"; } }
        public override string Glyph { get { return Glyphs.Apps; } }

        public override void Build(Layout l)
        {
            l.Text("The veil never starts while one of these apps is in front, e.g. a game you watch AFK or a player that doesn't ask Windows to keep the display on.", Ui.TextSecondary);
            l.Gap(12);
            PushButton add = null;
            add = new PushButton(Glyphs.Add, "Add app", true, delegate { new AppPicker(Window).Open(Window.Content.ScreenRect(add.Bounds)); });
            l.Row(Glyphs.Apps2, "Don't show while using", "Pick from the apps running now.", add);
            string last = Host.LastFocusedApp;
            if (last != null && !Opt.NoVeilApps.Contains(last))
                l.Row(Glyphs.Star, last, "The app you used last.", new PushButton(Glyphs.Add, "Add", false, delegate { AddApp(Opt, last); Changed(); }));

            l.Header(Opt.NoVeilApps.Count == 0 ? "No apps yet" : "Apps (" + Opt.NoVeilApps.Count + ")");
            var titles = RunningTitles();
            foreach (string a in Opt.NoVeilApps.ToArray())
            {
                string app = a;
                string title;
                titles.TryGetValue(app, out title);
                l.Row(Glyphs.Apps2, app, title != null ? "Running: " + title : "Not running", new PushButton(Glyphs.Delete, "Remove", false, delegate { Opt.NoVeilApps.Remove(app); Changed(); }));
            }
        }

        public static void AddApp(Options o, string exe)
        {
            exe = exe.Trim().ToLowerInvariant();
            if (exe != "" && !o.NoVeilApps.Contains(exe)) o.NoVeilApps.Add(exe);
        }

        // exe name (lower case, with .exe) -> main window title, for apps with a visible window.
        public static Dictionary<string, string> RunningTitles()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int own = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == own || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrEmpty(p.MainWindowTitle)) continue;
                    string exe = p.ProcessName.ToLowerInvariant() + ".exe";
                    if (!map.ContainsKey(exe)) map[exe] = p.MainWindowTitle;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return map;
        }
    }

    sealed class AppPicker : PopupForm
    {
        readonly SettingsWindow window;

        public AppPicker(SettingsWindow window) : base(window.UI) { this.window = window; }

        public void Open(Rectangle anchor)
        {
            Ui ui = window.UI;
            Options o = window.Host.Options;
            int width = ui.S(460);
            var l = new Layout(ui, ui.S(8), ui.S(8), width - ui.S(16));
            l.Add(new TextBlock("   Running apps", ui.BodyStrong, Ui.Text), ui.S(36));
            var apps = new List<KeyValuePair<string, string>>(AppsPage.RunningTitles());
            apps.Sort(delegate(KeyValuePair<string, string> a, KeyValuePair<string, string> b) { return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase); });
            foreach (var kv in apps)
            {
                string exe = kv.Key;
                bool added = o.NoVeilApps.Contains(exe);
                var item = new ListItem(added ? Glyphs.Check : Glyphs.Apps2, exe, kv.Value) { Enabled = !added };
                item.OnClick = delegate { Close(); AppsPage.AddApp(o, exe); window.Host.Changed(); };
                l.Add(item, ui.S(52));
            }
            if (apps.Count == 0) l.Add(new TextBlock("   No apps with a window are running.", ui.Body, Ui.TextSecondary), ui.S(40));
            l.Gap(8);
            Body.SetWidgets(l.Items, l.Y);
            ShowAt(window, anchor, new Size(width, Math.Min(l.Y, ui.S(520))));
        }
    }

    // A clickable list line: icon, title, secondary text.
    sealed class ListItem : Widget
    {
        readonly string glyph, title, sub;
        public Action OnClick;

        public ListItem(string glyph, string title, string sub) { this.glyph = glyph; this.title = title; this.sub = sub; }

        public override bool Interactive { get { return true; } }

        public override void Paint(Graphics g, Ui ui)
        {
            if (Hot && Enabled) ui.Fill(g, Bounds, ui.SF(4), Pressed ? Color.FromArgb(52, 52, 52) : Color.FromArgb(58, 58, 58));
            Color fg = Enabled ? Ui.Text : Ui.TextTertiary;
            ui.Glyph(g, glyph, ui.Icon, fg, new Rectangle(Bounds.X + ui.S(10), Bounds.Y, ui.S(20), Bounds.Height));
            int x = Bounds.X + ui.S(42);
            ui.Line(g, title, ui.Body, fg, new Rectangle(x, Bounds.Y + ui.S(7), Bounds.Right - x - ui.S(8), ui.S(20)));
            ui.Line(g, sub, ui.Caption, Ui.TextTertiary, new Rectangle(x, Bounds.Y + ui.S(27), Bounds.Right - x - ui.S(8), ui.S(18)));
        }

        public override void Click(Point p) { if (OnClick != null) OnClick(); }
    }

    // ---------------------------------------------------------------- General

    sealed class GeneralPage : Page
    {
        public override string Title { get { return "General"; } }
        public override string Glyph { get { return Glyphs.General; } }

        public override void Build(Layout l)
        {
            bool auto = false;
            try { auto = Autostart.Enabled; } catch { }
            l.Row(Glyphs.Startup, "Start with Windows", "Launch IdleVeil when you sign in.", new Toggle(auto, delegate(bool on)
            {
                try { Autostart.Enabled = on; Program.Log("autostart: " + on); }
                catch (Exception ex) { Program.Log("autostart error: " + ex.Message); }
                Window.QueueRebuild();
            }));
            l.Row(Glyphs.Gpu, "GPU renderer (Direct3D 11)", RendererText(),
                  new Toggle(!Opt.ForceGdi, delegate(bool on) { Opt.ForceGdi = !on; Changed(); }));

            l.Header("Updates");
            l.Row(Glyphs.Update, "Install updates automatically", "New releases from GitHub, checked against their SHA-256 checksums. Checked shortly after start, then every 12 hours; installs only while the veil isn't showing.",
                  new Toggle(Opt.AutoUpdate, delegate(bool on) { Opt.AutoUpdate = on; Changed(); }));
            l.Row(Glyphs.Info, "Version " + AppInfo.Version, Host.UpdateStatus ?? "Check GitHub for a newer release.",
                  new PushButton(Glyphs.Update, "Check now", false, delegate { Host.CheckForUpdates(); }));

            l.Header("Logs");
            l.Row(Glyphs.Folder, "Log files", AppPaths.Logs + "  (app, perf and crash logs, one file per day)",
                  new PushButton(null, "Open logs folder", false, delegate { Open(AppPaths.Logs); }));
            l.Row(Glyphs.Document, "Detailed logging", "Also log debug details (idle state every 10 s and more). Performance samples are always logged while the veil shows.",
                  new Toggle(Opt.Debug, delegate(bool on) { Opt.Debug = on; Changed(); }));
            l.Row(Glyphs.Delete, "Keep logs for", "Older app and perf logs are deleted once a day. Crash reports are kept 30 days; the folder never grows past 200 MB.",
                  new DropDown(new[] { "3", "7", "14", "30" }, new[] { "3 days", "7 days", "14 days", "30 days" }, AppLog.Days.ToString(),
                               delegate(string v) { AppLog.Days = int.Parse(v); Changed(); }));
            l.Row(Glyphs.Copy, "Copy diagnostics", "Version, system, screens, audio device and the last 200 log lines, for a bug report.",
                  new PushButton(Glyphs.Copy, "Copy", false, delegate { CopyDiagnostics(); }));

            l.Header("Files");
            l.Row(Glyphs.Folder, "Settings", AppPaths.Settings, new PushButton(null, "Open folder", false, delegate { Open(AppPaths.Dir); }));

            l.Header("About");
            l.Row(Glyphs.Info, "IdleVeil " + AppInfo.Version,
                  "Burn-in protection for OLED screens that stays out of the way: invisible to screen capture, never steals focus, and ignores input from AI agents and automation.",
                  new PushButton(Glyphs.Link, "GitHub", false, delegate { Open("https://github.com/watdafawx/IdleVeil"); }));
        }

        string RendererText()
        {
            string s = Host.RendererStatus;
            string thumbs = Window.Thumbs.Adapter != null ? "Direct3D 11 works on " + Window.Thumbs.Adapter + "." : Window.Thumbs.Error != null ? "No Direct3D 11 device: " + Window.Thumbs.Error + "." : null;
            if (Opt.ForceGdi) return "Off: every scene uses the older GDI renderer." + (thumbs != null ? " " + thumbs : "");
            return s ?? ("Not used yet in this session. " + (thumbs ?? ""));
        }

        static void CopyDiagnostics()
        {
            try
            {
                Clipboard.SetText(AppLog.Diagnostics());
                AppLog.Info("settings", "diagnostics copied to the clipboard");
            }
            catch (Exception ex) { AppLog.Warn("settings", "copy diagnostics failed: " + ex.Message); }
        }

        static void Open(string target)
        {
            try { Process.Start(target); }
            catch (Exception ex) { Program.Log("open " + target + " failed: " + ex.Message); }
        }
    }
}
