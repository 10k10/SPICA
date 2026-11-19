using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Spica.Core
{
    /// <summary>
    /// What a campaign folder says about when, where and with what it was simulated (campaign.state and the source
    /// bundle's manifest), for the report. Folders from before the bundle existed fall back to file times.
    /// </summary>
    public sealed class Trace
    {
        public string When = "unknown";
        public string Status, Machine, Seed, Generator, Bundle;
        public List<KeyValuePair<string, string>> Environment = new List<KeyValuePair<string, string>>();
        public List<string[]> Files = new List<string[]>();     // kind, file, bytes, sha256, original
        public List<string> Missing = new List<string>();

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static Trace Of(ResultSet rs)
        {
            var t = new Trace();
            string folder = rs.Folder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return t;
            CampaignState st = null;
            try { st = CampaignState.Load(folder); } catch { }

            string zip = SourceBundle.Find(folder);
            Dictionary<string, List<KeyValuePair<string, string>>> man = null;
            if (zip != null) { try { man = SourceBundle.ReadManifest(zip); } catch { man = null; } }

            // when
            DateTime? start = st?.Started;
            if (start == null && DateTime.TryParseExact(SourceBundle.Value(man, "Campaign", "Started") ?? "", "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out var ms)) start = ms;
            DateTime? end = st?.Ended;
            bool approx = false;
            string log = Path.Combine(folder, "campaign.log"), csv = Path.Combine(folder, "results.csv");
            if (start == null && File.Exists(log)) { start = File.GetCreationTime(log); approx = true; }
            if (end == null && st != null && st.Status != "Running" && File.Exists(csv)) { end = File.GetLastWriteTime(csv); approx = true; }
            if (start != null)
            {
                t.When = start.Value.ToString("yyyy-MM-dd HH:mm:ss", Inv);
                if (end != null) t.When += " to " + end.Value.ToString(end.Value.Date == start.Value.Date ? "HH:mm:ss" : "yyyy-MM-dd HH:mm:ss", Inv);
                if (st != null && st.ElapsedSeconds > 0) t.When += $" ({Duration(st.ElapsedSeconds)} simulating)";
                if (approx) t.When += ", from file times";
            }
            if (st != null)
                t.Status = (st.Status.Length > 0 ? st.Status : "?") + $": {st.Done} of {st.Jobs} simulations" +
                           (st.Status == "Running" ? " (interrupted, or still running)" : "");

            if (man == null) return t;
            t.Bundle = Path.GetFileName(zip) + " (in the results folder)";
            if (man.TryGetValue("Environment", out var env)) t.Environment = env;
            string Env(string k) => env?.FirstOrDefault(kv => kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value;
            var parts = new[] { Env("CPU"), Env("RAM") }.Where(x => !string.IsNullOrEmpty(x)).ToList();
            if (Env("Computer") != null) t.Machine = Env("Computer") + (parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "") +
                                                   (Env("LTspice") != null ? $", LTspice {Env("LTspice")}" : "");
            t.Seed = SourceBundle.Value(man, "Campaign", "Seed");
            t.Generator = SourceBundle.Value(man, "Campaign", "Generator");
            if (man.TryGetValue("Files", out var files))
                foreach (var kv in files)
                {
                    var f = (kv.Value.Length > 0 ? kv.Key + "=" + kv.Value : kv.Key).Split('|');
                    if (f.Length >= 5) t.Files.Add(f.Take(5).ToArray());
                }
            if (man.TryGetValue("Missing", out var miss)) t.Missing = miss.Select(kv => kv.Value.Length > 0 ? kv.Key + "=" + kv.Value : kv.Key).ToList();

            return t;
        }

        static string Duration(double s)
        {
            var ts = TimeSpan.FromSeconds(s);
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours} h {ts.Minutes:00} min";
            if (ts.TotalMinutes >= 1) return $"{ts.Minutes} min {ts.Seconds:00} s";
            return $"{ts.Seconds} s";
        }
    }
}
