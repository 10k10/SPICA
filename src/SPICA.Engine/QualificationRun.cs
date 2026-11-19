using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Spica.Core
{
    /// <summary>Moments of a test a pass/fail window is measured from.</summary>
    public enum QualAnchor { TestStart, EventStart, EventEnd, TestEnd }

    /// <summary>
    /// A pass/fail rule of the qualification: a signal stays above Lo and/or below Hi inside a time window, given from
    /// the moments of each test (e.g. from 100 ms after the supply is back to the end of the test).
    /// </summary>
    public sealed class QualCriterion
    {
        public string Name = "";
        public string Signal = "";                 // V(pgood), I(R1)...
        public double Lo = double.NaN, Hi = double.NaN;
        public QualAnchor From = QualAnchor.TestStart, To = QualAnchor.TestEnd;
        public double FromOffset, ToOffset;        // seconds after the anchor
        public string Tests = "";                  // "" = every test; else words a test's name contains, separated by |

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public bool AppliesTo(string test) =>
            Tests.Trim().Length == 0 ||
            Tests.Split('|').Select(w => w.Trim()).Where(w => w.Length > 0).Any(w => test.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>The .meas names: name_min (against Lo) and name_max (against Hi).</summary>
        public string MinMeas => Safe(Name) + "_min";
        public string MaxMeas => Safe(Name) + "_max";

        public static string Safe(string s)
        {
            var t = Regex.Replace((s ?? "").Trim(), @"[^A-Za-z0-9_]", "_");
            if (t.Length == 0) t = "crit";
            if (!char.IsLetter(t[0])) t = "q" + t;
            return t;
        }

        public string RuleText() =>
            !double.IsNaN(Lo) && !double.IsNaN(Hi) ? $"{Signal} within {ErrorBudget.Eng(Lo)} .. {ErrorBudget.Eng(Hi)}"
            : !double.IsNaN(Lo) ? $"{Signal} ≥ {ErrorBudget.Eng(Lo)}" : !double.IsNaN(Hi) ? $"{Signal} ≤ {ErrorBudget.Eng(Hi)}" : $"{Signal}: no limit";

        public static string AnchorText(QualAnchor a) =>
            a == QualAnchor.TestStart ? "test start" : a == QualAnchor.EventStart ? "event start" : a == QualAnchor.EventEnd ? "event end" : "test end";

        static string At(QualAnchor a, double off) =>
            AnchorText(a) + (off > 0 ? " + " + ErrorBudget.Eng(off) + "s" : off < 0 ? " - " + ErrorBudget.Eng(-off) + "s" : "");

        public string WindowText() => $"{At(From, FromOffset)} .. {At(To, ToOffset)}";

        /// <summary>The window in one test's time, clipped to the test; false when it falls outside the test.</summary>
        public bool Window(QualEvents e, out double t1, out double t2)
        {
            t1 = Math.Max(0, e.Time(From, FromOffset));
            t2 = Math.Min(e.Stop, e.Time(To, ToOffset));
            return t2 > t1;
        }

        public QualCriterion Clone() => (QualCriterion)MemberwiseClone();
    }

    /// <summary>The moments of a test's waveform: the event is the part that leaves the starting level and comes back.</summary>
    public struct QualEvents
    {
        public double Start, End, Stop;

        public static QualEvents Of(List<double[]> pts)
        {
            var e = new QualEvents { Stop = pts.Count > 0 ? pts[pts.Count - 1][0] : 0 };
            if (pts.Count < 2) return e;
            double v0 = pts[0][1], vEnd = pts[pts.Count - 1][1];
            int first = pts.FindIndex(p => p[1] != v0);
            if (first < 0) return e;                               // a steady level: no event, both at 0
            e.Start = pts[first - 1][0];
            int last = pts.FindLastIndex(p => p[1] != vEnd);
            e.End = last >= 0 && last + 1 < pts.Count ? pts[last + 1][0] : e.Start;
            return e;
        }

        public double Time(QualAnchor a, double offset) =>
            (a == QualAnchor.TestStart ? 0 : a == QualAnchor.EventStart ? Start : a == QualAnchor.EventEnd ? End : Stop) + offset;
    }

    /// <summary>What the setup keeps per library (standard): the category, the ticked tests, where the waveform goes.</summary>
    public sealed class QualStdChoice
    {
        public string Category = "";
        public List<string> Tests = new List<string>();
        public string Net = "";
        public double Rser;                        // source resistance in ohms (0 = ideal source)

        public QualStdChoice Clone() => new QualStdChoice { Category = Category, Tests = new List<string>(Tests), Net = Net, Rser = Rser };
    }

    /// <summary>One test made ready to run: its netlist lines and what was changed in the circuit.</summary>
    public sealed class QualCase
    {
        public QualLibrary Library;
        public QualTest Test;
        public string Category;
        public List<double[]> Wave;
        public QualEvents Events;
        public List<string> Lines;
        public List<string> Replaced = new List<string>();
        public List<QualCriterion> Criteria = new List<QualCriterion>();
        public string Error;

        /// <summary>
        /// The test's folder under qualification\: short, as LTspice cannot open files past 260 characters of path
        /// (category and test, at most 40 characters, and 4 hex digits of the library so two standards never share one).
        /// </summary>
        public string FolderName
        {
            get
            {
                string s = System.Text.RegularExpressions.Regex.Replace(QualCriterion.Safe(Category + "_" + Test.Name), "_+", "_").Trim('_');
                if (s.Length > 40) s = s.Substring(0, 40).TrimEnd('_');
                uint h = 2166136261;
                foreach (char ch in (Library.Key ?? "").ToLowerInvariant()) h = (h ^ ch) * 16777619;
                return s + "_" + (h & 0xFFFF).ToString("x4");
            }
        }

        /// <summary>Characters a campaign adds under a test's folder: stamp\chunks\chunk00001_r1-1000_T-40.db and some room.</summary>
        public const int PathReserve = 64;
    }

    public static class QualBuild
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string G(double v) => v.ToString("0.#########", Inv);
        static readonly HashSet<string> Analyses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".tran", ".ac", ".dc", ".op", ".noise", ".tf" };

        /// <summary>The voltage sources from the net to ground (either way round): the supply the test waveform replaces.</summary>
        public static List<string> SourcesAt(IEnumerable<string> lines, string net)
        {
            var found = new List<string>();
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.Length < 2 || char.ToUpperInvariant(t[0]) != 'V') continue;
                var tok = Netlist.Tokenize(t);
                if (tok.Count < 3) continue;
                bool a = tok[1].Equals(net, StringComparison.OrdinalIgnoreCase), b = tok[2].Equals(net, StringComparison.OrdinalIgnoreCase);
                if ((a && tok[2] == "0") || (b && tok[1] == "0")) found.Add(tok[0]);
            }
            return found;
        }

        /// <summary>
        /// The netlist of one test: the sources from <paramref name="net"/> to ground are taken out and the test's PWL put
        /// there; the analysis becomes a .tran over the test; each criterion adds a MIN and/or MAX .meas over its window.
        /// </summary>
        public static QualCase Make(Netlist nl, QualLibrary lib, QualTest test, string category, QualStdChoice ch, IEnumerable<QualCriterion> criteria)
        {
            var c = new QualCase { Library = lib, Test = test, Category = category };
            c.Wave = QualLibrary.Waveform(test, lib.Variables(category), out var err);
            if (err != null) { c.Error = $"{test.Name}: {err}"; return c; }
            if (string.IsNullOrWhiteSpace(ch.Net)) { c.Error = "Choose the net the supply waveform goes into."; return c; }
            c.Events = QualEvents.Of(c.Wave);
            double stop = c.Events.Stop;

            var lines = new List<string>();
            bool uic = false;
            var seen = new HashSet<string>(nl.MeasNames, StringComparer.OrdinalIgnoreCase);
            foreach (var line in nl.Lines)
            {
                var t = line.Trim();
                var low = t.ToLowerInvariant();
                if (low == ".end" || low.StartsWith(".end ")) break;
                if (t.Length > 1 && char.ToUpperInvariant(t[0]) == 'V')
                {
                    var tok = Netlist.Tokenize(t);
                    if (tok.Count >= 3 && ((tok[1].Equals(ch.Net, StringComparison.OrdinalIgnoreCase) && tok[2] == "0") ||
                                           (tok[2].Equals(ch.Net, StringComparison.OrdinalIgnoreCase) && tok[1] == "0")))
                    {
                        c.Replaced.Add(tok[0]);
                        lines.Add("* SPICA qualification took out: " + t);
                        continue;
                    }
                }
                if (t.StartsWith("."))
                {
                    var tok = Netlist.Tokenize(t);
                    string kw = tok[0];
                    if (Analyses.Contains(kw))
                    {
                        uic |= tok.Any(x => x.Equals("uic", StringComparison.OrdinalIgnoreCase));
                        lines.Add("* SPICA qualification: the test's own .tran replaces " + t);
                        continue;
                    }
                    // a .meas of another analysis would fail under .tran
                    if ((kw.Equals(".meas", StringComparison.OrdinalIgnoreCase) || kw.Equals(".measure", StringComparison.OrdinalIgnoreCase)) &&
                        tok.Count > 1 && Analyses.Contains("." + tok[1]) && !tok[1].Equals("tran", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add("* SPICA qualification: not a .tran measurement: " + t);
                        continue;
                    }
                }
                lines.Add(line);
            }

            lines.Add($"* ---- SPICA qualification: {lib.Standard}, {test.Name}, category {category} ----");
            lines.Add($"Vspica_qual {ch.Net} 0 {QualLibrary.PwlValue(c.Wave)}" + (ch.Rser > 0 ? " Rser=" + G(ch.Rser) : ""));
            lines.Add($".tran 0 {G(stop)} 0 {G(MaxStep(c.Wave, test))}" + (uic ? " uic" : ""));
            foreach (var cr in criteria.Where(x => x.AppliesTo(test.Name)))
            {
                if (!cr.Window(c.Events, out double t1, out double t2)) continue;     // the window falls outside this test
                c.Criteria.Add(cr);
                if (!double.IsNaN(cr.Lo) && seen.Add(cr.MinMeas)) lines.Add($".meas tran {cr.MinMeas} MIN {cr.Signal} FROM={G(t1)} TO={G(t2)}");
                if (!double.IsNaN(cr.Hi) && seen.Add(cr.MaxMeas)) lines.Add($".meas tran {cr.MaxMeas} MAX {cr.Signal} FROM={G(t1)} TO={G(t2)}");
            }
            lines.Add(".end");
            c.Lines = lines;
            return c;
        }

        /// <summary>A time step that sees the shortest level of the waveform: a tenth of it, and at most 1/2000 of the test.</summary>
        static double MaxStep(List<double[]> pts, QualTest test)
        {
            double stop = pts[pts.Count - 1][0], shortest = stop;
            for (int i = 1; i < pts.Count; i++)
            {
                double d = pts[i][0] - pts[i - 1][0];
                if (d > 1e-5 && d < shortest) shortest = d;
            }
            return Math.Max(1e-9, Math.Min(stop / 2000, shortest / 10));
        }
    }

    /// <summary>A criterion judged on the runs of one test.</summary>
    public sealed class QualVerdict
    {
        public string Test, Criterion, Rule, Window;
        public int Runs, Failing;
        public double WorstLo = double.NaN, WorstHi = double.NaN;    // lowest minimum, highest maximum seen
        public double Margin = double.NaN;                           // the smaller distance to a limit (negative = outside)
        public string Verdict = "-";                                 // PASS, FAIL, "no data"
        public string Folder;

        /// <summary>Every criterion of a finished test campaign: worst value over the runs against its limits.</summary>
        public static List<QualVerdict> Of(ResultSet rs, string test, IEnumerable<QualCriterion> criteria)
        {
            var list = new List<QualVerdict>();
            foreach (var cr in criteria)
            {
                var v = new QualVerdict { Test = test, Criterion = cr.Name, Rule = cr.RuleText(), Window = cr.WindowText(), Folder = rs.Folder };
                bool any = false, fail = false;
                var margins = new List<double>();
                foreach (var row in rs.Rows)
                {
                    double lo = !double.IsNaN(cr.Lo) && rs.Col(cr.MinMeas) >= 0 ? rs.Get(row, cr.MinMeas) : double.NaN;
                    double hi = !double.IsNaN(cr.Hi) && rs.Col(cr.MaxMeas) >= 0 ? rs.Get(row, cr.MaxMeas) : double.NaN;
                    if (double.IsNaN(lo) && double.IsNaN(hi)) continue;
                    any = true;
                    v.Runs++;
                    bool bad = false;
                    if (!double.IsNaN(lo)) { v.WorstLo = double.IsNaN(v.WorstLo) ? lo : Math.Min(v.WorstLo, lo); margins.Add(lo - cr.Lo); bad |= lo < cr.Lo; }
                    if (!double.IsNaN(hi)) { v.WorstHi = double.IsNaN(v.WorstHi) ? hi : Math.Max(v.WorstHi, hi); margins.Add(cr.Hi - hi); bad |= hi > cr.Hi; }
                    if (bad) { v.Failing++; fail = true; }
                }
                if (!any) { v.Verdict = "no data"; list.Add(v); continue; }
                v.Margin = margins.Min();
                v.Verdict = fail ? "FAIL" : "PASS";
                list.Add(v);
            }
            return list;
        }

        public string WorstText()
        {
            if (!double.IsNaN(WorstLo) && !double.IsNaN(WorstHi)) return $"{ErrorBudget.Eng(WorstLo)} .. {ErrorBudget.Eng(WorstHi)}";
            return !double.IsNaN(WorstLo) ? "min " + ErrorBudget.Eng(WorstLo) : !double.IsNaN(WorstHi) ? "max " + ErrorBudget.Eng(WorstHi) : "-";
        }

        /// <summary>The newest finished campaign under a test's folder (one stamp folder per run), or null.</summary>
        public static string Latest(string testRoot)
        {
            if (!Directory.Exists(testRoot)) return null;
            return Directory.GetDirectories(testRoot).Where(d => File.Exists(Path.Combine(d, "results.csv")))
                            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).FirstOrDefault();
        }
    }
}
