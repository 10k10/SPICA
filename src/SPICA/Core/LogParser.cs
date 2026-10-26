using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Spica.Core
{
    /// <summary>Contents of one chunk .log.</summary>
    public sealed class ChunkLog
    {
        /// <summary>step index (1-based, in chunk order) -> measurement name -> value text</summary>
        public readonly Dictionary<int, Dictionary<string, string>> Steps = new Dictionary<int, Dictionary<string, string>>();
        public readonly List<int> StepRuns = new List<int>();     // value of "run" for each ".step run=N" line
        public readonly List<string> Errors = new List<string>();
        public double ElapsedSeconds = double.NaN;
        public bool Completed;                                     // "Total elapsed time" was reached
    }

    public static class LogParser
    {
        static readonly Regex ElapsedRx = new Regex(@"^Total elapsed time:\s*([0-9.eE+-]+)\s*seconds", RegexOptions.IgnoreCase);
        static readonly Regex StepRx = new Regex(@"^\.step\s+run=(\S+)", RegexOptions.IgnoreCase);
        static readonly Regex MeasHeaderRx = new Regex(@"^Measurement:\s*(\S+)", RegexOptions.IgnoreCase);
        static readonly Regex RowRx = new Regex(@"^\s*(\d+)\t(.*)$");

        static readonly string[] ErrorMarkers =
        {
            "error", "singular matrix", "time step too small", "convergence failed", "unknown", "can't find", "fail",
        };

        /// <param name="measNames">The .meas names of the circuit (needed to read single-run logs).</param>
        public static ChunkLog Parse(string path, IList<string> measNames)
        {
            var log = new ChunkLog();
            if (!File.Exists(path)) { log.Errors.Add("No .log file was produced."); return log; }
            string text = Ltspice.ReadText(path);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var names = new HashSet<string>(measNames ?? new string[0], StringComparer.OrdinalIgnoreCase);

            string currentMeas = null;
            bool inTable = false;
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();

                var me = MeasHeaderRx.Match(line);
                if (me.Success) { currentMeas = me.Groups[1].Value; inTable = false; continue; }

                if (currentMeas != null)
                {
                    if (line.Trim().Length == 0) { if (inTable) currentMeas = null; continue; }
                    if (!inTable && line.TrimStart().StartsWith("step", StringComparison.OrdinalIgnoreCase)) { inTable = true; continue; }
                    var row = RowRx.Match(line);
                    if (inTable && row.Success)
                    {
                        int step = int.Parse(row.Groups[1].Value, CultureInfo.InvariantCulture);
                        var cols = row.Groups[2].Value.Split('\t');
                        Put(log, step, currentMeas, cols[0].Trim());
                        continue;
                    }
                    if (inTable) { currentMeas = null; inTable = false; }
                }

                var em = ElapsedRx.Match(line);
                if (em.Success)
                {
                    log.Completed = true;
                    if (double.TryParse(em.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var el)) log.ElapsedSeconds = el;
                    continue;
                }
                var sm = StepRx.Match(line);
                if (sm.Success)
                {
                    if (double.TryParse(sm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rv)) log.StepRuns.Add((int)Math.Round(rv));
                    continue;
                }

                // Single-run format: "name: EXPR=value FROM ..." or "name=value FROM ..."
                if (names.Count > 0)
                {
                    int sep = line.IndexOfAny(new[] { ':', '=' });
                    if (sep > 0)
                    {
                        string n = line.Substring(0, sep).Trim();
                        if (names.Contains(n))
                        {
                            string rest = line.Substring(sep + 1);
                            int cut = IndexOfWord(rest, " FROM ");
                            int at = IndexOfWord(rest, " AT ");
                            if (at >= 0 && (cut < 0 || at < cut)) cut = at;
                            if (cut >= 0) rest = rest.Substring(0, cut);
                            int eq = rest.LastIndexOf('=');
                            string val = (eq >= 0 ? rest.Substring(eq + 1) : rest).Trim();
                            Put(log, 1, n, val);
                            continue;
                        }
                    }
                }

                var lower = line.ToLowerInvariant();
                if (log.Errors.Count < 20)
                    foreach (var mk in ErrorMarkers)
                        if (lower.Contains(mk)) { log.Errors.Add(line.Trim()); break; }
            }
            return log;
        }

        static int IndexOfWord(string s, string w) => s.IndexOf(w, StringComparison.OrdinalIgnoreCase);

        static void Put(ChunkLog log, int step, string meas, string val)
        {
            if (!log.Steps.TryGetValue(step, out var d)) log.Steps[step] = d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            d[meas] = val;
        }

        /// <summary>Parses a .meas value (plain number, or the magnitude part of an AC "(xdB,y°)" pair).</summary>
        public static bool TryNumber(string s, out double v)
        {
            v = double.NaN;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.StartsWith("("))
            {
                int c = s.IndexOf(',');
                s = s.Substring(1, (c > 0 ? c : s.Length - 1) - 1);
                s = s.Replace("dB", "");
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
