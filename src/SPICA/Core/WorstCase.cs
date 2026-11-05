using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Spica.Core
{
    /// <summary>Off = Monte Carlo. Auto picks Corners for few tolerances, Sensitivity otherwise.</summary>
    public enum WcMethod { Off, Auto, Sensitivity, Corners }

    /// <summary>
    /// Worst-case analysis on SPICA's paired parameters: instead of random values, every tolerance site is set
    /// to an edge of its range (uniform: -1 / +1, gaussian: -k / +k sigma).
    ///   Corners:     nominal + every combination of edges (2^n runs): exact for monotonic circuits.
    ///   Sensitivity: nominal + one run per tolerance at its upper edge (the sign of each effect), then for each
    ///                measurement and temperature the two combinations that push it up and down (verification).
    /// Run 1 is always the nominal unit (every site 0).
    /// </summary>
    public static class WorstCase
    {
        public const int MaxCornerSites = 14;      // 2^14 = 16384 runs per temperature
        public const int AutoCornerSites = 10;     // Auto: corners up to 1024 runs, sensitivity above

        public static WcMethod Resolve(WcMethod m, int sites) =>
            m == WcMethod.Auto ? (sites <= AutoCornerSites ? WcMethod.Corners : WcMethod.Sensitivity) : m;

        public static string Describe(WcMethod m) =>
            m == WcMethod.Corners ? "all corners" : m == WcMethod.Sensitivity ? "sensitivity + verification" : m == WcMethod.Auto ? "auto" : "off";

        public static double Edge(SiteKind kind, int sign, double k) => sign * (kind == SiteKind.Gaussian ? k : 1);

        public static List<double[]> CornerDesign(IList<McSite> sites, double k)
        {
            int n = sites.Count;
            var rows = new List<double[]> { new double[n] };
            for (long mask = 0; mask < 1L << n; mask++)
                rows.Add(sites.Select((s, i) => Edge(s.Kind, (mask >> i & 1) == 1 ? 1 : -1, k)).ToArray());
            return rows;
        }

        /// <summary>Nominal, then site i alone at its upper edge (rows 2 .. n+1).</summary>
        public static List<double[]> ScreenDesign(IList<McSite> sites, double k)
        {
            int n = sites.Count;
            var rows = new List<double[]> { new double[n] };
            for (int i = 0; i < n; i++)
            {
                var r = new double[n];
                r[i] = Edge(sites[i].Kind, 1, k);
                rows.Add(r);
            }
            return rows;
        }

        /// <summary>
        /// The verification rows: for every measurement and temperature, all sites at the edge that moves it up
        /// (and the opposite for down), from the signs seen in the screening runs. Identical rows are run once.
        /// Null when the screening runs are not all available.
        /// </summary>
        public static List<double[]> VerifyDesign(IList<RunResult> results, IList<string> meas, IList<McSite> sites, double k, int corners)
        {
            int n = sites.Count;
            var byKey = new Dictionary<string, RunResult>();
            foreach (var r in results) byKey[r.Corner + ":" + r.Run] = r;
            var rows = new List<double[]>();
            var seen = new HashSet<string>();
            for (int c = 0; c < corners; c++)
                foreach (var m in meas)
                {
                    double y0 = Value(byKey, c, 1, m);
                    if (double.IsNaN(y0)) continue;
                    var sign = new int[n];
                    bool ok = true;
                    for (int i = 0; i < n && ok; i++)
                    {
                        double y = Value(byKey, c, i + 2, m);
                        if (double.IsNaN(y)) ok = false;
                        else sign[i] = y >= y0 ? 1 : -1;
                    }
                    if (!ok) continue;
                    foreach (int dir in new[] { 1, -1 })
                    {
                        var row = sites.Select((s, i) => Edge(s.Kind, dir * sign[i], k)).ToArray();
                        string key = string.Join(",", row.Select(v => Math.Sign(v)));
                        if (seen.Add(key)) rows.Add(row);
                    }
                }
            return rows;
        }

        static double Value(Dictionary<string, RunResult> map, int corner, int run, string meas) =>
            map.TryGetValue(corner + ":" + run, out var r) && r.Status == RunStatus.Ok && r.Values.TryGetValue(meas, out var s) &&
            LogParser.TryNumber(s, out var d) ? d : double.NaN;
    }

    /// <summary>One tolerance's share of the worst-case swing of a measurement.</summary>
    public sealed class WcContribution
    {
        public string Param;
        public double Effect;        // change of the measurement when this tolerance goes from nominal to its upper edge
        public double Share;         // |Effect| / sum of |Effect|
    }

    /// <summary>The worst-case result of one measurement over all runs and temperatures.</summary>
    public sealed class WcMeas
    {
        public string Meas;
        public SpecLimit Spec;
        public double NomLo = double.NaN, NomHi = double.NaN;      // nominal unit (over the temperatures)
        public double Min = double.NaN, Max = double.NaN;          // worst simulated values
        public int MinRun, MaxRun;
        public double MinTemp = double.NaN, MaxTemp = double.NaN;
        public double LinLo = double.NaN, LinHi = double.NaN;      // linear estimate: nominal +/- sum |effect|
        public double RssLo = double.NaN, RssHi = double.NaN;      // root-sum-square estimate
        public readonly List<WcContribution> Top = new List<WcContribution>();
        public int Runs;

        public bool HasSpec => Spec != null && Spec.IsSet;
        public double LowMargin => HasSpec && !double.IsNaN(Spec.Lsl) ? Min - Spec.Lsl : double.NaN;
        public double HighMargin => HasSpec && !double.IsNaN(Spec.Usl) ? Spec.Usl - Max : double.NaN;
        public bool? Pass => !HasSpec || double.IsNaN(Min) ? (bool?)null : !(LowMargin < 0) && !(HighMargin < 0);
    }

    public sealed class WcReport
    {
        public WcMethod Method;
        public double K = 3;
        public int Sites, Runs, Screening, Verification, Corners;
        public bool Verified;
        public readonly List<WcMeas> Items = new List<WcMeas>();

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string F(double v) => MeasStats.F(v);

        /// <summary>Recognises the runs of a worst-case campaign from their parameter values.</summary>
        public static WcReport Of(ResultSet rs)
        {
            var rep = new WcReport { Method = rs.Setup.WorstCase, K = rs.Setup.WcSigma, Sites = rs.Params.Count };
            if (rep.Method == WcMethod.Off || rs.Params.Count == 0) return null;
            int n = rs.Params.Count;
            var runs = rs.Rows.Select(r => r.Run).Distinct().OrderBy(r => r).ToList();
            rep.Runs = runs.Count;
            rep.Corners = rs.Mode == TempMode.Corners ? rs.Corners.Count : 1;

            // run 1 = nominal; a run with exactly one non-zero parameter = screening of that parameter
            var single = new Dictionary<int, int>();     // param index -> run
            int combos = 0;
            foreach (int run in runs)
            {
                var p = rs.ParamRow(run);
                if (p == null) continue;
                int nz = p.Count(v => v != 0 && !double.IsNaN(v));
                if (nz == 1) { int i = Array.FindIndex(p, v => v != 0 && !double.IsNaN(v)); if (!single.ContainsKey(i) && p[i] > 0) single[i] = run; }
                else if (nz > 1) combos++;
            }
            bool screen = single.Count == n && n > 0;
            rep.Method = screen ? WcMethod.Sensitivity : WcMethod.Corners;
            rep.Screening = screen ? n + 1 : 0;
            rep.Verification = screen ? combos : 0;
            rep.Verified = !screen || combos > 0;

            foreach (var m in rs.Meas)
            {
                var w = new WcMeas { Meas = m, Spec = rs.Setup.Spec(m) };
                var effects = new double[n];
                var counted = new int[n];
                foreach (var r in rs.Rows)
                {
                    double v = rs.Get(r, m);
                    if (double.IsNaN(v)) continue;
                    w.Runs++;
                    if (double.IsNaN(w.Min) || v < w.Min) { w.Min = v; w.MinRun = r.Run; w.MinTemp = r.Temp; }
                    if (double.IsNaN(w.Max) || v > w.Max) { w.Max = v; w.MaxRun = r.Run; w.MaxTemp = r.Temp; }
                }

                // nominal, linear and RSS estimates per temperature
                var groups = rs.Mode == TempMode.Corners ? Enumerable.Range(0, rs.Corners.Count).ToList() : new List<int> { -1 };
                foreach (int c in groups)
                {
                    var rows = rs.Rows.Where(r => c < 0 || r.Corner == c).ToDictionary(r => r.Run, r => r);
                    double y0 = rows.TryGetValue(1, out var nom) ? rs.Get(nom, m) : double.NaN;
                    if (double.IsNaN(y0)) continue;
                    w.NomLo = double.IsNaN(w.NomLo) ? y0 : Math.Min(w.NomLo, y0);
                    w.NomHi = double.IsNaN(w.NomHi) ? y0 : Math.Max(w.NomHi, y0);
                    var e = new double[n];
                    bool all = true;
                    for (int i = 0; i < n; i++)
                    {
                        if (screen)
                        {
                            double y = single.TryGetValue(i, out int run) && rows.TryGetValue(run, out var rr) ? rs.Get(rr, m) : double.NaN;
                            if (double.IsNaN(y)) { all = false; continue; }
                            e[i] = y - y0;
                        }
                        else
                        {
                            // full factorial: half the difference between the runs with this parameter high and low
                            double hi = 0, lo = 0; int nh = 0, nl = 0;
                            foreach (var rr in rows.Values)
                            {
                                var p = rs.ParamRow(rr.Run);
                                double y = rs.Get(rr, m);
                                if (p == null || rr.Run == 1 || double.IsNaN(y)) continue;
                                if (p[i] > 0) { hi += y; nh++; } else if (p[i] < 0) { lo += y; nl++; }
                            }
                            if (nh == 0 || nl == 0) { all = false; continue; }
                            e[i] = (hi / nh - lo / nl) / 2;
                        }
                        if (Math.Abs(e[i]) > Math.Abs(effects[i])) effects[i] = e[i];
                        counted[i]++;
                    }
                    if (!all) continue;
                    double sum = e.Sum(x => Math.Abs(x)), rss = Math.Sqrt(e.Sum(x => x * x));
                    w.LinLo = double.IsNaN(w.LinLo) ? y0 - sum : Math.Min(w.LinLo, y0 - sum);
                    w.LinHi = double.IsNaN(w.LinHi) ? y0 + sum : Math.Max(w.LinHi, y0 + sum);
                    w.RssLo = double.IsNaN(w.RssLo) ? y0 - rss : Math.Min(w.RssLo, y0 - rss);
                    w.RssHi = double.IsNaN(w.RssHi) ? y0 + rss : Math.Max(w.RssHi, y0 + rss);
                }
                double total = effects.Sum(x => Math.Abs(x));
                if (total > 0)
                    foreach (var i in Enumerable.Range(0, n).Where(i => counted[i] > 0).OrderByDescending(i => Math.Abs(effects[i])))
                        w.Top.Add(new WcContribution { Param = rs.Params[i], Effect = effects[i], Share = Math.Abs(effects[i]) / total });
                rep.Items.Add(w);
            }
            return rep;
        }

        /// <summary>Plain-language report of one measurement.</summary>
        public string Summary(WcMeas w, ResultSet rs)
        {
            var sb = new StringBuilder();
            string verdict = !w.HasSpec ? "no requirement set (enter limits on the Statistics tab)"
                           : w.Pass == false ? "FAILS in the worst case"
                           : Tight(w) ? "MEETS SPEC in the worst case, small margin"
                           : "MEETS SPEC in the worst case";
            sb.AppendLine($"{w.Meas}:  {verdict}");
            sb.AppendLine();
            sb.AppendLine("What the extreme units do");
            sb.AppendLine($"  nominal       {Range(w.NomLo, w.NomHi)}{(NominalVaries(w) ? "  (over the temperatures)" : "")}");
            sb.AppendLine($"  worst low     {F(w.Min)}   (run {w.MinRun}{TempText(w.MinTemp)})");
            sb.AppendLine($"  worst high    {F(w.Max)}   (run {w.MaxRun}{TempText(w.MaxTemp)})");
            if (!Verified) sb.AppendLine("  !             verification runs missing: these are the screening runs only");
            if (!double.IsNaN(w.LinLo))
                sb.AppendLine($"  linear est.   {F(w.LinLo)} .. {F(w.LinHi)}   (nominal +/- sum of the effects)");
            if (!double.IsNaN(w.RssLo))
                sb.AppendLine($"  RSS est.      {F(w.RssLo)} .. {F(w.RssHi)}   (root-sum-square: a statistical range, not a guarantee)");
            if (!double.IsNaN(w.LinLo) && !double.IsNaN(w.Min) && Method == WcMethod.Sensitivity && Verified)
            {
                double span = w.LinHi - w.LinLo, miss = Math.Max(Math.Abs(w.Min - w.LinLo), Math.Abs(w.Max - w.LinHi));
                if (span > 0 && miss > 0.2 * span)
                    sb.AppendLine("  note          simulated extremes differ from the linear estimate by > 20 %: the circuit is not linear in its\n" +
                                  "                tolerances. Use All corners (if it has few tolerances) to be sure.");
            }
            sb.AppendLine();

            if (w.HasSpec)
            {
                sb.AppendLine($"Against the requirement  {(double.IsNaN(w.Spec.Lsl) ? "-" : F(w.Spec.Lsl))} .. {(double.IsNaN(w.Spec.Usl) ? "-" : F(w.Spec.Usl))}");
                if (!double.IsNaN(w.LowMargin)) sb.AppendLine($"  low margin    {Margin(w.LowMargin)}");
                if (!double.IsNaN(w.HighMargin)) sb.AppendLine($"  high margin   {Margin(w.HighMargin)}");
                sb.AppendLine();
            }

            if (w.Top.Count > 0)
            {
                sb.AppendLine("What causes the swing (tighten the top ones first)");
                foreach (var c in w.Top.Take(8))
                    sb.AppendLine($"  {Short(c.Param),-20} {c.Share * 100,5:0.0} %   {(c.Effect >= 0 ? "+" : "-")}{F(Math.Abs(c.Effect))} at its upper edge");
                sb.AppendLine();
            }

            foreach (var x in new[] { (w.MinRun, "low"), (w.MaxRun, "high") })
            {
                var p = rs.ParamRow(x.Item1);
                if (p == null) continue;
                var up = rs.Params.Where((n, i) => p[i] > 0).Select(Short).ToList();
                var dn = rs.Params.Where((n, i) => p[i] < 0).Select(Short).ToList();
                if (up.Count + dn.Count == 0) continue;
                sb.AppendLine($"Worst {x.Item2} unit (run {x.Item1}): upper edge: {(up.Count > 0 ? string.Join(", ", up) : "-")}");
                sb.AppendLine($"{new string(' ', 18 + x.Item1.ToString(Inv).Length + x.Item2.Length)}lower edge: {(dn.Count > 0 ? string.Join(", ", dn) : "-")}");
            }
            sb.AppendLine();
            sb.AppendLine("How this was found");
            sb.AppendLine(Method == WcMethod.Corners
                ? $"  all corners: every combination of the {Sites} tolerances at its edges ({Runs} runs per temperature)."
                : $"  sensitivity: {Screening} screening runs (nominal + each tolerance alone), then {Verification} runs with every\n" +
                  "  tolerance pushed the way that moves a measurement up or down.");
            sb.AppendLine($"  edges: uniform tolerances at their limits, gaussian ones at +/-{K.ToString("0.##", Inv)} sigma.");
            sb.AppendLine("  Both methods assume each tolerance moves the result one way only (monotonic). Random values inside\n" +
                          "  .subckt models stay random (LTspice): check the Setup > Circuit box for warnings.");
            return sb.ToString();
        }

        bool NominalVaries(WcMeas w) =>Corners > 1 && w.NomLo != w.NomHi;

        static bool Tight(WcMeas w)
        {
            double width = w.Max - w.Min;
            double m = Math.Min(double.IsNaN(w.LowMargin) ? double.PositiveInfinity : w.LowMargin, double.IsNaN(w.HighMargin) ? double.PositiveInfinity : w.HighMargin);
            return width > 0 && m < 0.1 * width;
        }

        static string Margin(double m) => m >= 0 ? $"{F(m)} to spare" : $"SHORT by {F(-m)}";
        static string Range(double a, double b) => double.IsNaN(a) ? "?" : a == b ? F(a) : $"{F(a)} .. {F(b)}";
        static string TempText(double t) => double.IsNaN(t) ? "" : $" at {t.ToString("0.###", Inv)} °C";
        static string Short(string p) { int i = p.LastIndexOf(" ["); return i > 0 ? p.Substring(0, i) : p; }
    }
}
