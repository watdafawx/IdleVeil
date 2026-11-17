// --render-test <dir>: renders GPU scenes off-screen at the real monitor resolutions and writes
// PNGs, so effects can be judged without the live overlay (which screenshots can't see).
//
//   IdleVeil.exe --render-test out [--mode starfield] [--screens span] [--frames 3]
//
// For every scene x screen mode it simulates from t = 0 at a fixed 60 fps and saves `frames`
// samples spread over the first ~20 s: one PNG per screen (<scene>_<mode>_s<screen>_f<n>.png) and
// a half-size picture of the whole desktop layout (<scene>_<mode>_layout_f<n>.png; grey = no screen).
// A summary with the average GPU time per frame goes to <dir>\render-test.txt.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace IdleVeil
{
    static class RenderTest
    {
        public static int Run(string dir, string onlyScene, string onlyScreens, int frames)
        {
            var report = new StringBuilder();
            string err;
            Gpu gpu = Gpu.Create(out err);
            if (gpu == null)
            {
                Program.Log("render test: no GPU: " + err);
                return 1;
            }
            try
            {
                Directory.CreateDirectory(dir);
                GpuScreen[] screens = GpuScreen.Current();
                report.AppendLine("adapter: " + gpu.AdapterName);
                foreach (GpuScreen s in screens) report.AppendLine("screen " + s.Key + " " + s.Name + " " + s.Bounds);

                var scenes = new List<string>();
                if (onlyScene != null) scenes.Add(onlyScene);
                else foreach (string id in GpuScenes.Ids) if (id != "blank") scenes.Add(id);
                var modes = new List<ScreenMode>();
                if (onlyScreens != null) modes.Add(ScreenModes.Parse(onlyScreens));
                else foreach (string m in ScreenModes.Names) modes.Add(ScreenModes.Parse(m));

                foreach (string scene in scenes)
                    foreach (ScreenMode mode in modes)
                        RunOne(gpu, screens, scene, mode, Math.Max(1, frames), dir, report);
                File.WriteAllText(Path.Combine(dir, "render-test.txt"), report.ToString());
                Program.Log("render test: wrote " + dir);
                return 0;
            }
            catch (Exception ex)
            {
                Program.Log("render test failed: " + ex);
                File.WriteAllText(Path.Combine(dir, "render-test.txt"), report + "\r\nFAILED: " + ex);
                return 1;
            }
            finally { gpu.Dispose(); }
        }

        static void RunOne(Gpu gpu, GpuScreen[] screens, string scene, ScreenMode mode, int frames, string dir, StringBuilder report)
        {
            var ids = new string[screens.Length];
            int first = Array.IndexOf(GpuScenes.Ids, scene);
            for (int i = 0; i < ids.Length; i++)
                ids[i] = mode == ScreenMode.PerScreen ? GpuScenes.Ids[(first + i) % (GpuScenes.Ids.Length - 1)] : scene;   // skip "blank"

            var synth = new SyntheticAudio();
            // Groups: every screen but the last spans one scene; the last is left out (black).
            GroupPlan[] plan = null;
            if (mode == ScreenMode.Groups && screens.Length > 1)
            {
                var members = new int[screens.Length - 1];
                for (int i = 0; i < members.Length; i++) members[i] = i;
                plan = new[] { new GroupPlan { Mode = ScreenMode.Span, Members = members } };
            }
            var host = new SceneHost(gpu, screens, mode, ids, synth, 0f, plan);
            var tex = new IntPtr[screens.Length];
            var rtv = new IntPtr[screens.Length];
            var staging = new IntPtr[screens.Length];
            try
            {
                for (int i = 0; i < screens.Length; i++)
                {
                    tex[i] = gpu.RenderTexture(screens[i].Bounds.Width, screens[i].Bounds.Height, out rtv[i]);
                    staging[i] = gpu.StagingTexture(screens[i].Bounds.Width, screens[i].Bounds.Height);
                }
                string name = scene + "_" + ScreenModes.Name(mode);
                const float dt = 1f / 60f;
                double gpuMs = 0;
                int timed = 0;
                for (int f = 0; f < frames; f++)
                {
                    float at = frames == 1 ? 8f : 2.5f + f * (18f / (frames - 1));
                    while (host.Time < at)
                    {
                        synth.TimeOverride = host.Time;
                        host.Update(dt);
                    }
                    var images = new Bitmap[screens.Length];
                    for (int i = 0; i < screens.Length; i++)
                    {
                        // Time the draw: render, then force completion with the readback copy.
                        var sw = Stopwatch.StartNew();
                        host.Render(i, rtv[i]);
                        gpu.Ctx.CopyResource(staging[i], tex[i]);
                        images[i] = ReadBack(gpu, staging[i], screens[i].Bounds.Width, screens[i].Bounds.Height);
                        gpuMs += sw.Elapsed.TotalMilliseconds;
                        timed++;
                        images[i].Save(Path.Combine(dir, name + "_s" + i + "_f" + f + ".png"), ImageFormat.Png);
                    }
                    SaveLayout(screens, images, Path.Combine(dir, name + "_layout_f" + f + ".png"));
                    foreach (Bitmap b in images) b.Dispose();
                }
                report.AppendLine(name + ": " + host.Describe() + "  render+readback ms/screen=" + (gpuMs / Math.Max(1, timed)).ToString("0.0"));
            }
            finally
            {
                host.Dispose();
                for (int i = 0; i < screens.Length; i++) { D3D.Release(ref rtv[i]); D3D.Release(ref tex[i]); D3D.Release(ref staging[i]); }
            }
        }

        static Bitmap ReadBack(Gpu gpu, IntPtr staging, int w, int h)
        {
            D3D.MAPPED_SUBRESOURCE m;
            D3D.Check(gpu.Ctx.Map(staging, 0, D3D.MAP_READ, 0, out m), "Map (readback)");
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(m.Data + (int)(y * m.RowPitch), row, 0, row.Length);
                    Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, row.Length);
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
                gpu.Ctx.Unmap(staging, 0);
            }
            return bmp;
        }

        static void SaveLayout(GpuScreen[] screens, Bitmap[] images, string path)
        {
            Rectangle u = screens[0].Bounds;
            foreach (GpuScreen s in screens) u = Rectangle.Union(u, s.Bounds);
            using (var all = new Bitmap(u.Width / 2, u.Height / 2, PixelFormat.Format32bppRgb))
            using (var g = Graphics.FromImage(all))
            {
                g.Clear(Color.FromArgb(40, 40, 40));
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                for (int i = 0; i < screens.Length; i++)
                {
                    Rectangle b = screens[i].Bounds;
                    g.DrawImage(images[i], new Rectangle((b.X - u.X) / 2, (b.Y - u.Y) / 2, b.Width / 2, b.Height / 2));
                }
                all.Save(path, ImageFormat.Png);
            }
        }
    }
}
