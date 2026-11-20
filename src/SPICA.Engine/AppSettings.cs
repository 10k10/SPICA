using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Spica.Core
{
    /// <summary>
    /// Portable key=value store. Lives next to SPICA.exe; falls back to %APPDATA%\SPICA when the
    /// executable folder is read-only.
    /// </summary>
    public class KeyValueFile
    {
        readonly string path;
        protected readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public KeyValueFile(string fileName)
        {
            path = Resolve(fileName);
            try
            {
                if (File.Exists(path))
                    foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0 && !line.StartsWith(";")) Values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
                    }
            }
            catch { }
        }

        /// <summary>The folder of the running exe (SPICA.exe, SPICA-cli.exe or a test runner).</summary>
        public static string AppDir
        {
            get
            {
                var exe = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                return string.IsNullOrEmpty(exe) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(exe);
            }
        }

        public static string Resolve(string fileName)
        {
            string dir = AppDir;
            try
            {
                string probe = Path.Combine(dir, ".spica_write_test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return Path.Combine(dir, fileName);
            }
            catch
            {
                string alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SPICA");
                Directory.CreateDirectory(alt);
                return Path.Combine(alt, fileName);
            }
        }

        public string Get(string key, string def = "") => Values.TryGetValue(key, out var v) ? v : def;
        public void Set(string key, string value) => Values[key] = value ?? "";

        public int GetInt(string key, int def) => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
        public bool GetBool(string key, bool def) => Values.TryGetValue(key, out var v) ? v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) : def;
        public void Set(string key, int v) => Set(key, v.ToString(CultureInfo.InvariantCulture));
        public void Set(string key, double v) => Set(key, v.ToString("R", CultureInfo.InvariantCulture));
        public void Set(string key, bool v) => Set(key, v ? "1" : "0");

        public void Save()
        {
            try
            {
                var lines = new List<string> { "; SPICA settings" };
                lines.AddRange(Values.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(k => k.Key + "=" + k.Value));
                File.WriteAllLines(path, lines, Encoding.UTF8);
            }
            catch { }
        }
    }

    public sealed class AppSettings : KeyValueFile
    {
        public AppSettings() : base("SPICA.ini") { }

        public string LtspicePath { get => Get("LtspicePath"); set => Set("LtspicePath", value); }
        /// <summary>Where the studies' campaigns go; empty = beside the project (or the schematic), in &lt;name&gt;_SPICA.</summary>
        public string ResultsFolder { get => Get("ResultsFolder"); set => Set("ResultsFolder", value); }
        /// <summary>How the waveform panes look (a PlotTheme's name).</summary>
        public string PlotStyle { get => Get("PlotStyle"); set => Set("PlotStyle", value); }
        /// <summary>Where the main window was when it last closed: "x,y,w,h" (empty: never), and whether it was maximized.</summary>
        public string WindowBounds { get => Get("WindowBounds"); set => Set("WindowBounds", value); }
        public bool WindowMaximized { get => GetBool("WindowMaximized", false); set => Set("WindowMaximized", value); }
        public bool AutoParallel { get => GetBool("AutoParallel", true); set => Set("AutoParallel", value); }
        public int ManualInstances { get => GetInt("ManualInstances", 0); set => Set("ManualInstances", value); }
        public int ManualThreads { get => GetInt("ManualThreads", 1); set => Set("ManualThreads", value); }

        public List<string> RecentFiles
        {
            get
            {
                var list = new List<string>();
                for (int i = 1; i <= 6; i++) { var v = Get("Recent" + i); if (v.Length > 0) list.Add(v); }
                return list;
            }
        }

        public void PushRecent(string path)
        {
            var list = RecentFiles;
            list.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            for (int i = 1; i <= 6; i++) Set("Recent" + i, i <= list.Count ? list[i - 1] : "");
        }
    }

    /// <summary>Best scheduler configuration found per (machine, circuit), used to warm-start.</summary>
    public sealed class ProfileStore : KeyValueFile
    {
        public ProfileStore() : base("SPICA.profiles") { }

        public sealed class Profile
        {
            public int Instances, Threads;
            public double SecondsPerRun, OverheadSeconds;
        }

        public Profile Find(string machine, string circuit)
        {
            var v = Get(machine + "|" + circuit, null);
            if (v == null) return null;
            var p = v.Split(';');
            if (p.Length < 4) return null;
            var inv = CultureInfo.InvariantCulture;
            try
            {
                return new Profile
                {
                    Instances = int.Parse(p[0], inv),
                    Threads = int.Parse(p[1], inv),
                    SecondsPerRun = double.Parse(p[2], inv),
                    OverheadSeconds = double.Parse(p[3], inv),
                };
            }
            catch { return null; }
        }

        public void Store(string machine, string circuit, Profile p)
        {
            var inv = CultureInfo.InvariantCulture;
            Set(machine + "|" + circuit, string.Join(";",
                p.Instances.ToString(inv), p.Threads.ToString(inv),
                p.SecondsPerRun.ToString("G6", inv), p.OverheadSeconds.ToString("G6", inv),
                DateTime.Now.ToString("yyyy-MM-dd", inv)));
            Save();
        }
    }
}
