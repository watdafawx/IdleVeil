// Screen groups (screens=groups): named sets of monitors, each with its own screen mode (span,
// each or mirror) and scene. A monitor in no group is "left out": it shows black (so an OLED there
// is still protected) or, with leftout=none, gets no veil at all.
//
// ini:  group.1=span|tunnel|AUS2740,GSM5B09      mode | scene ("" = the Scenes page choice, "random") | screen ids
//       leftout=black|none
//       place.AUS2740=1920,0                      where a screen sits inside a span group (desktop coordinates)
using System;
using System.Collections.Generic;
using System.Drawing;

namespace IdleVeil
{
    sealed class ScreenGroup
    {
        public string Mode = "span";                       // span | each | mirror
        public string Scene = "";                          // "" = same as the Scenes page
        public List<string> Keys = new List<string>();     // GpuScreen.Key of each member

        public static readonly string[] Modes = { "span", "each", "mirror" };

        public ScreenMode ScreenMode
        {
            get
            {
                ScreenMode m = ScreenModes.Parse(Mode);
                return m == ScreenMode.Span || m == ScreenMode.Mirror ? m : ScreenMode.Each;
            }
        }

        public bool Has(string key)
        {
            foreach (string k in Keys) if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public string Serialize() { return Mode + "|" + Scene + "|" + string.Join(",", Keys.ToArray()); }

        public static ScreenGroup Parse(string v)
        {
            string[] p = (v ?? "").Split('|');
            if (p.Length < 3) return null;
            var g = new ScreenGroup { Mode = ScreenModes.Name(new ScreenGroup { Mode = p[0].Trim().ToLowerInvariant() }.ScreenMode) };
            string scene = p[1].Trim().ToLowerInvariant();
            g.Scene = scene == "random" || GpuScenes.Has(scene) ? scene : "";
            foreach (string k in p[2].Split(',')) if (k.Trim() != "" && !g.Keys.Contains(k.Trim())) g.Keys.Add(k.Trim());
            return g;
        }
    }

    static class ScreenGroups
    {
        // Moves each screen's Layout to its saved place (span groups use Layout instead of Bounds).
        public static void ApplyPlacement(GpuScreen[] screens, Dictionary<string, Point> place)
        {
            foreach (GpuScreen s in screens)
            {
                Point p;
                s.Layout = s.Bounds;
                if (place != null && place.TryGetValue(s.Key, out p)) s.Layout = new Rectangle(p, s.Bounds.Size);
            }
        }

        // Which group (index into opt.Groups) a screen is in, or -1 (left out). The first match wins.
        public static int GroupOf(Options opt, GpuScreen s)
        {
            for (int i = 0; i < opt.Groups.Count; i++) if (opt.Groups[i].Has(s.Key)) return i;
            return -1;
        }

        // Turns the settings into what the overlay needs. `screens` is replaced by the screens that get
        // a window (left-out ones are dropped when leftout=none); `ids` holds the scene per screen and
        // `plan` the groups, with member indexes into the new screen list. With no groups defined every
        // screen is one "each" group, so a fresh install never shows black everywhere.
        public static void Resolve(Options opt, ref GpuScreen[] screens, string defaultScene, Random rng, out string[] ids, out GroupPlan[] plan)
        {
            ApplyPlacement(screens, opt.Place);
            var kept = new List<GpuScreen>();
            var groupOf = new List<int>();
            bool any = opt.Groups.Count > 0;
            foreach (GpuScreen s in screens)
            {
                int g = any ? GroupOf(opt, s) : -2;
                if (g == -1 && opt.LeftOut == "none") continue;
                kept.Add(s);
                groupOf.Add(g);
            }
            screens = kept.ToArray();
            ids = new string[screens.Length];
            var plans = new List<GroupPlan>();
            if (!any)
            {
                var all = new int[screens.Length];
                for (int i = 0; i < all.Length; i++) { all[i] = i; ids[i] = defaultScene; }
                plans.Add(new GroupPlan { Mode = ScreenMode.Each, Members = all });
                plan = plans.ToArray();
                return;
            }
            for (int gi = 0; gi < opt.Groups.Count; gi++)
            {
                var members = new List<int>();
                for (int i = 0; i < screens.Length; i++) if (groupOf[i] == gi) members.Add(i);
                if (members.Count == 0) continue;
                ScreenGroup g = opt.Groups[gi];
                string scene = g.Scene == "" ? defaultScene : g.Scene == "random" ? MusicConfig.RandomGpu(rng) : g.Scene;
                if (!GpuScenes.Has(scene)) scene = defaultScene;
                foreach (int i in members) ids[i] = scene;
                plans.Add(new GroupPlan { Mode = g.ScreenMode, Members = members.ToArray() });
            }
            for (int i = 0; i < screens.Length; i++) if (ids[i] == null) ids[i] = "blank";   // left out: black
            plan = plans.ToArray();
        }
    }
}
