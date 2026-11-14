// Self-update from GitHub releases (same scheme as RazerLite and qalam): check the latest release,
// download IdleVeil.exe, verify its SHA-256 against the release's SHA256SUMS.txt, then swap the
// running exe (rename old -> IdleVeil.old.exe, write new, start it with --after-update) and quit.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IdleVeil
{
    class UpdateInfo
    {
        public string Version, ExeUrl, SumsUrl;
    }

    static class Updater
    {
        const string Api = "https://api.github.com/repos/watdafawx/IdleVeil/releases/latest";
        const string ExeName = "IdleVeil.exe";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\IdleVeil";

        static string CurrentExe { get { return Application.ExecutablePath; } }
        static string OldExe { get { return Path.Combine(Path.GetDirectoryName(CurrentExe), "IdleVeil.old.exe"); } }

        static int[] ParseVersion(string s)
        {
            string[] parts = s.Trim().TrimStart('v', 'V').Split('.');
            var v = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) { int n; v[i] = int.TryParse(parts[i], out n) ? n : 0; }
            return v;
        }

        public static bool Newer(string remote, string local)
        {
            int[] a = ParseVersion(remote), b = ParseVersion(local);
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        // A copy built from source (build\IdleVeil.exe next to build.ps1) never replaces itself.
        public static bool IsDevBuild()
        {
            string dir = Path.GetDirectoryName(CurrentExe);
            string parent = Path.GetDirectoryName(dir);
            return string.Equals(Path.GetFileName(dir), "build", StringComparison.OrdinalIgnoreCase)
                && parent != null && File.Exists(Path.Combine(parent, "build.ps1"));
        }

        static byte[] Get(string url, int maxBytes)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2 (not default on .NET 4.0 targets)
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "IdleVeil-updater/" + AppInfo.Version;
            req.Accept = "application/vnd.github+json, application/octet-stream";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                var buf = new byte[64 * 1024];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > maxBytes) throw new Exception("download too big");
                }
                return ms.ToArray();
            }
        }

        // Ask GitHub for the latest release; null if this build is up to date.
        public static UpdateInfo Check()
        {
            string json = Encoding.UTF8.GetString(Get(Api, 4 * 1024 * 1024));
            var release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            string tag = release["tag_name"] as string;
            if (tag == null) throw new Exception("release has no tag");
            if (!Newer(tag, AppInfo.Version)) return null;
            var info = new UpdateInfo { Version = tag.TrimStart('v', 'V') };
            var assets = release.ContainsKey("assets") ? release["assets"] as ArrayList : null;
            if (assets != null)
                foreach (object o in assets)
                {
                    var a = o as Dictionary<string, object>;
                    if (a == null) continue;
                    string name = a["name"] as string, url = a["browser_download_url"] as string;
                    if (name == ExeName) info.ExeUrl = url;
                    else if (name == "SHA256SUMS.txt") info.SumsUrl = url;
                }
            if (info.ExeUrl == null) throw new Exception("release " + tag + " has no " + ExeName);
            if (info.SumsUrl == null) throw new Exception("release " + tag + " has no SHA256SUMS.txt");
            return info;
        }

        static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (byte b in sha.ComputeHash(data)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // Download, verify and swap in the new exe, then start it. The caller quits afterwards.
        public static void Apply(UpdateInfo u)
        {
            if (IsDevBuild()) throw new Exception("this is a build from source; it doesn't replace itself");
            byte[] exe = Get(u.ExeUrl, 32 * 1024 * 1024);
            string sums = Encoding.UTF8.GetString(Get(u.SumsUrl, 64 * 1024));
            string want = null;
            foreach (string line in sums.Split('\n'))
            {
                int sp = line.IndexOf("  ");
                if (sp > 0 && line.Substring(sp).Trim() == ExeName) want = line.Substring(0, sp).Trim().ToLowerInvariant();
            }
            if (want == null) throw new Exception(ExeName + " is missing from SHA256SUMS.txt");
            string got = Sha256Hex(exe);
            if (got != want) throw new Exception("checksum mismatch (got " + got + ")");
            if (exe.Length < 20000 || exe[0] != 'M' || exe[1] != 'Z') throw new Exception("the download isn't an exe");

            string cur = CurrentExe, old = OldExe;
            try { File.Delete(old); } catch { }
            // A running exe can't be overwritten, but it can be renamed.
            File.Move(cur, old);
            try { File.WriteAllBytes(cur, exe); }
            catch { try { File.Move(old, cur); } catch { } throw; }
            UpdateUninstallEntry(u.Version);
            Process.Start(new ProcessStartInfo(cur, "--after-update") { WorkingDirectory = Path.GetDirectoryName(cur) });
        }

        // Keeps Settings > Apps showing the right version when the installed copy updated itself.
        static void UpdateUninstallEntry(string version)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey, true))
                {
                    if (k == null) return;
                    string loc = k.GetValue("InstallLocation") as string;
                    if (loc != null && string.Equals(Path.GetFullPath(loc).TrimEnd('\\'), Path.GetDirectoryName(CurrentExe).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        k.SetValue("DisplayVersion", version);
                }
            }
            catch { }
        }

        // Remove the previous exe left behind by an update.
        public static void Cleanup()
        {
            try { if (File.Exists(OldExe)) File.Delete(OldExe); } catch { }
        }

        // Runs Check on a background thread and reports back on the UI thread.
        public static void CheckAsync(Action<UpdateInfo, Exception> done)
        {
            var ui = SynchronizationContext.Current;
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo u = null; Exception err = null;
                try { u = Check(); } catch (Exception ex) { err = ex; }
                ui.Post(delegate { done(u, err); }, null);
            });
        }

        public static void ApplyAsync(UpdateInfo u, Action<Exception> done)
        {
            var ui = SynchronizationContext.Current;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception err = null;
                try { Apply(u); } catch (Exception ex) { err = ex; }
                ui.Post(delegate { done(err); }, null);
            });
        }
    }
}
