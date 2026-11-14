// How the settings window groups the scenes, and the Random pool (which scenes Random may pick).
using System;
using System.Collections.Generic;

namespace IdleVeil
{
    static class SceneCatalog
    {
        public static readonly string[] GroupNames = { "Ambient", "Music visualizers", "Clocks", "Classic" };

        public static readonly string[][] Groups =
        {
            new[] { "starfield", "aurora", "fireflies", "plasma", "snow", "fire" },
            new[] { "music", "spectrum", "scope", "scopering", "radial", "tunnel", "ripples" },
            new[] { "clock", "clock-led", "clock-neon", "clock-analog", "clock-music" },
            new[] { "matrix", "mystify", "life", "blank" },
        };

        // What Random picks from until the user changes the pool (the classic animations, every
        // clock but the music one, and the visualizers, which only join while something plays).
        public static readonly string[] DefaultPool =
        {
            "matrix", "mystify", "starfield", "aurora", "fireflies", "fire", "snow", "life", "plasma",
            "clock", "clock-led", "clock-neon", "clock-analog",
            "spectrum", "scope", "scopering", "radial", "tunnel", "ripples",
        };

        public static string Label(string id)
        {
            switch (id)
            {
                case "spectrum": return "Spectrum";
                case "music": return "Music mode";
                case "blank": return "Blank";
                default: return Savers.Label(id);
            }
        }

        // Scenes that can't be in the pool (blank is a choice, not a show; music mode is its own thing).
        public static bool Poolable(string id) { return id != "blank" && id != "music"; }

        public static bool InPool(List<string> pool, string id)
        {
            return pool == null ? Array.IndexOf(DefaultPool, id) >= 0 : pool.Contains(id);
        }

        // ini value: comma-separated ids, unknown ones dropped.
        public static List<string> ParsePool(string v)
        {
            var list = new List<string>();
            foreach (string part in v.Split(','))
            {
                string id = part.Trim().ToLowerInvariant();
                if (id != "" && Poolable(id) && GpuScenes.Has(id) && !list.Contains(id)) list.Add(id);
            }
            return list;
        }

        // Random's pick. Without a custom pool this is the classic behaviour (MusicConfig.Pick). With
        // one, audio-reactive scenes only count while something is playing; if that leaves nothing,
        // fall back to the classic pick.
        public static string Pick(Random rng, List<string> pool)
        {
            if (pool == null || pool.Count == 0) return MusicConfig.Pick(rng, Savers.Animations);
            bool needsProbe = false;
            foreach (string id in pool) if (GpuScenes.UsesAudio(id)) needsProbe = true;
            bool playing = needsProbe && SoundProbe.IsPlaying();
            var candidates = new List<string>();
            foreach (string id in pool)
                if (playing || !GpuScenes.UsesAudio(id)) candidates.Add(id);
            if (candidates.Count == 0) return MusicConfig.Pick(rng, Savers.Animations);
            return candidates[rng.Next(candidates.Count)];
        }
    }
}
