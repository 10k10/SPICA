using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Spica.Core
{
    public enum BudgetUnit { Abs, PctReading, PctFs }

    /// <summary>
    /// One error the design must keep inside its budget. Either a measurement that already is the error
    /// (computed by a .meas in LTspice), or an output Y compared with an ideal transfer function of the input X:
    /// error = Y - TF(X), referred to the input by dividing by the TF gain dTF/dx at that input.
    /// X is a measurement (the input varies over its range) or a fixed number.
    /// </summary>
    public sealed class ErrorItem
    {
        public string Name = "";
        public string Y = "";              // measurement: the error itself, or the output
        public string X = "";              // input: measurement name or a number; empty = none
        public string Tf = "";             // ideal TF of x (and T); empty = Y is already the error
        public bool ReferToInput = true;
        public double Budget = double.NaN; // +/- budget, in the units below
        public BudgetUnit Unit = BudgetUnit.Abs;
        public double FullScale = double.NaN;

        public bool UsesTf => !string.IsNullOrWhiteSpace(Tf);

        public ErrorItem Clone() => (ErrorItem)MemberwiseClone();

        public string Definition()
        {
            if (!UsesTf) return Y + " (error from LTspice)";
            string x = X.Length > 0 ? X : "x";
            return ReferToInput ? $"({Y} - TF({x})) / gain, TF = {Tf}" : $"{Y} - TF({x}), TF = {Tf}";
        }

        public string UnitText() => Unit == BudgetUnit.PctReading ? "% of reading" : Unit == BudgetUnit.PctFs ? "% of full scale" : (UsesTf && ReferToInput ? "input units" : "units of " + Y);
        public string Suffix() => Unit == BudgetUnit.Abs ? "" : " %";
    }

    /// <summary>
    /// The error of the units in one condition: one temperature and one band of the input range. Units in the
    /// same condition differ only by their tolerances, so this is the distribution the k-sigma rule applies to.
    /// </summary>
    public sealed class ErrorSlice
    {
        public string Label = "all runs";
        public int Corner = -1;
        public double XLo = double.NaN, XHi = double.NaN;
        public double[] E = new double[0];
        public List<ResultSet.Row> Rows = new List<ResultSet.Row>();
        public Descriptive D;
        public double AdP = double.NaN;
        public bool Normal, TailsOk;
        public double SigmaUp = double.NaN, Sigma = double.NaN;
        public double Lo = double.NaN, Hi = double.NaN;
        public double Margin = double.NaN;
        public int Outside;
        public string Verdict = "-";
    }

    /// <summary>The verdict of one error item against its budget at k sigma: the worst of its slices.</summary>
    public sealed class ErrorResult
    {
        public ErrorItem Item;
        public string Error;
        public double K;
        public bool Pessimistic;
        public double[] E = new double[0], X = new double[0];        // every run (charts)
        public readonly List<ErrorSlice> Slices = new List<ErrorSlice>();
        public ErrorSlice Worst;
        public double FitOffset = double.NaN, FitSlope = double.NaN, ResidualStd = double.NaN;   // e = a + b x
        public SensitivityReport Sens;

        // the worst slice, for tables
        public Descriptive D => Worst?.D ?? Descriptive.Of(new double[0]);
        public double AdP => Worst?.AdP ?? double.NaN;
        public bool Normal => Worst != null && Worst.Normal;
        public double Sigma => Worst?.Sigma ?? double.NaN;
        public double Lo => Worst?.Lo ?? double.NaN;
        public double Hi => Worst?.Hi ?? double.NaN;
        public double Margin => Worst?.Margin ?? double.NaN;
        public string Verdict = "-";
        public int N;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        string F(double v) => ErrorBudget.Fmt(v, Item);
        string K0 => K.ToString("0.#", Inv);
        static string Eng(double v) => ErrorBudget.Eng(v);

        public bool HasBudget => !double.IsNaN(Item.Budget) && Item.Budget > 0;
        public double MarginPct => HasBudget ? Margin / Item.Budget * 100 : double.NaN;
        public bool InputVaries => X.Length > 2 && X.Where(v => !double.IsNaN(v)).DefaultIfEmpty().Max() > X.Where(v => !double.IsNaN(v)).DefaultIfEmpty().Min();
        public int Outside => Slices.Sum(s => s.Outside);

        /// <summary>Runs with no error outside the budget needed to show a coverage by samples alone (95 % confidence).</summary>
        public static double RunsToShow(double coverage) => Math.Log(0.05) / Math.Log(coverage);

        public string Summary()
        {
            if (Error != null) return Item.Name + ": " + Error;
            var w = Worst;
            var sb = new StringBuilder();
            string verdict = Verdict == "PASS" ? $"PASS at {K0} sigma" + (w.Normal ? "" : " (conservative: the real tails are shorter than normal)")
                           : Verdict == "FAIL" ? (Outside > 0 ? $"FAILS: {Outside} of {N} simulated units are already outside the budget" : $"FAILS at {K0} sigma")
                           : Verdict == "CHECK" ? $"CHECK: the {K0}-sigma figure cannot decide ({(w.Margin >= 0 ? "inside" : "outside")} the budget on paper)"
                           : "no budget set";
            sb.AppendLine($"{Item.Name}:  {verdict}");
            sb.AppendLine($"  {Item.Definition()}");
            sb.AppendLine($"  in {Item.UnitText()}");
            if (Slices.Count > 1)
            {
                sb.AppendLine($"  judged in {Slices.Count} conditions (temperature x input band);");
                sb.AppendLine($"  the worst one is {w.Label}");
            }
            sb.AppendLine();
            sb.AppendLine("The error of a production unit" + (Slices.Count > 1 ? $" (worst condition, {w.D.N} units)" : ""));
            sb.AppendLine($"  mean          {F(w.D.Mean)}   (same in every unit: a TF/calibration removes it)");
            sb.AppendLine($"  sigma         {F(w.D.Std)}   (95 % upper bound {F(w.SigmaUp)}{(Pessimistic ? ": used" : "")})");
            sb.AppendLine($"  mean +/-{K0}s   {F(w.Lo)} .. {F(w.Hi)}");
            sb.AppendLine($"  seen          {F(w.D.Min)} .. {F(w.D.Max)}");
            if (HasBudget)
            {
                sb.AppendLine();
                sb.AppendLine($"Against the budget  +/-{F(Item.Budget)}");
                sb.AppendLine(w.Margin >= 0 ? $"  margin        {F(w.Margin)} to spare ({MarginPct:0} % of the budget)"
                                            : $"  margin        SHORT by {F(-w.Margin)} ({-MarginPct:0} % over the budget)");
                if (w.Sigma > 0) sb.AppendLine($"  the budget is {((Item.Budget - Math.Abs(w.D.Mean)) / w.Sigma).ToString("0.0", Inv)} sigma away from the mean (needed: {K0})");
                if (Outside > 0) sb.AppendLine($"  {Outside} simulated units are outside the budget: no extrapolation needed to see it fails");
            }

            sb.AppendLine();
            sb.AppendLine($"Is the {K0}-sigma extrapolation valid?");
            if (w.Normal) sb.AppendLine($"  yes: the error is bell-shaped (normality p = {MeasStats.P(w.AdP)})");
            else if (w.TailsOk)
            {
                sb.AppendLine($"  conservative: not exactly bell-shaped (p = {MeasStats.P(w.AdP)}), but its tails are no longer than a normal's\n" +
                              "  (typical of flat tolerances adding up). The real extremes are inside mean +/- k sigma.");
                if (Verdict == "CHECK")
                    sb.AppendLine("  Over the budget on paper only: the real extremes are shorter. Run a worst case (Setup > Analysis)\n" +
                                  "  for the hard bound of the flat tolerances.");
            }
            else
            {
                var d = w.D;
                string shape = d.ExKurt > 0.5 ? "heavy-tailed: the real tails are LONGER than the sigma figure says"
                             : Math.Abs(d.Skew) > 0.3 ? "lopsided: one side reaches further than sigma says"
                             : "its tails reach further than a normal's";
                sb.AppendLine($"  no: normality p = {MeasStats.P(w.AdP)}, {shape}.");
                sb.AppendLine("  Options:");
                sb.AppendLine("    - worst case (Setup > Analysis > Worst case): every tolerance at its edge, a hard bound;");
                sb.AppendLine($"    - more Monte Carlo: with no unit outside the budget, {RunsToShow(0.9973):N0} runs show 99.73 % (3 sigma),");
                sb.AppendLine($"      {RunsToShow(0.99994):N0} runs 99.994 % (4 sigma); 6 sigma by samples alone is out of reach (~1.5 billion).");
            }

            if (Slices.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine("Every condition");
                int lw = Math.Max(10, Slices.Max(s => s.Label.Length));
                sb.AppendLine($"  {"condition".PadRight(lw)} {"n",5} {"mean",10} {"sigma",10} {"mean +/-" + K0 + "s",22} {"margin",7}  verdict");
                foreach (var s in Slices)
                    sb.AppendLine($"  {s.Label.PadRight(lw)} {s.D.N,5} {F(s.D.Mean),10} {F(s.Sigma),10} {F(s.Lo) + " .. " + F(s.Hi),22} " +
                                  $"{(HasBudget ? (s.Margin / Item.Budget * 100).ToString("0", Inv) + " %" : ""),7}  {s.Verdict}");
            }

            if (!double.IsNaN(FitOffset))
            {
                sb.AppendLine();
                sb.AppendLine(Item.UsesTf ? "What a corrected TF would leave" : "What a calibration of the systematic part would leave");
                if (InputVaries)
                {
                    sb.AppendLine($"  the average error follows  e = {Eng(FitOffset)} + {Eng(FitSlope)} * x   (offset + gain error)");
                    sb.AppendLine($"  a TF corrected for it leaves sigma {F(ResidualStd)}: +/-{K0}s = +/-{F(K * ResidualStd)}" +
                                  (HasBudget ? (K * ResidualStd <= Item.Budget ? "  -> inside the budget" : "  -> still over the budget") : ""));
                }
                else
                    sb.AppendLine($"  removing the mean leaves +/-{K0}s = +/-{F(K * w.Sigma)}" +
                                  (HasBudget ? (K * w.Sigma <= Item.Budget ? "  -> inside the budget" : "  -> still over the budget") : ""));
            }

            if (Sens != null && Sens.Error == null && Sens.Items.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"What drives the spread{(Slices.Count > 1 ? " in the worst condition" : "")} (share of the variance; tighten the top ones first)");
                foreach (var it in Sens.Items.Where(i => !double.IsNaN(i.Share)).Take(6))
                    sb.AppendLine($"  {(100 * it.Share).ToString("0.0", Inv),5} %  {it.Name}");
                if (Sens.R2 < 0.9) sb.AppendLine($"  ({(100 * (1 - Sens.R2)).ToString("0", Inv)} % is not explained: nonlinear effects, or randoms inside .subckt models)");
            }
            return sb.ToString();
        }
    }

    public static class ErrorBudget
    {
        public static string Column(ErrorItem it) => "error: " + it.Name;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Engineering notation with SI prefixes (-1.223m, 872.96u), 4 significant digits.</summary>
        public static string Eng(double v)
        {
            if (double.IsNaN(v)) return "-";
            if (v == 0) return "0";
            string[] pre = { "f", "p", "n", "u", "m", "", "k", "M", "G" };
            int e3 = (int)Math.Floor(Math.Log10(Math.Abs(v)) / 3);
            e3 = Math.Max(-5, Math.Min(3, e3));
            double m = v / Math.Pow(1000, e3);
            string s = m.ToString(Math.Abs(m) >= 100 ? "0.0" : Math.Abs(m) >= 10 ? "0.00" : "0.000", Inv);
            return s + pre[e3 + 5];
        }

        /// <summary>An error value in the units of its item: SI prefixes for absolute budgets, plain % otherwise.</summary>
        public static string Fmt(double v, ErrorItem it) =>
            double.IsNaN(v) ? "-" : it.Unit == BudgetUnit.Abs ? Eng(v) : v.ToString(Math.Abs(v) >= 1 ? "0.000" : "0.0000", Inv) + " %";

        /// <summary>Makes every error item a derived column of the result set, so all pages can use it.</summary>
        public static void Register(ResultSet rs, IEnumerable<ErrorItem> items)
        {
            rs.ClearDerived();
            foreach (var it in items)
            {
                var item = it;
                Expr tf = null;
                if (item.UsesTf && !Expr.TryParse(item.Tf, out tf, out _)) continue;
                bool constX = TryConstant(rs, item.X, out double xc);
                rs.AddDerived(Column(item), r => Eval(rs, r, item, tf, constX, xc, out _));
            }
        }

        static bool TryConstant(ResultSet rs, string x, out double v)
        {
            v = double.NaN;
            if (string.IsNullOrWhiteSpace(x) || rs.Col(x) >= 0 || rs.IsParam(x) || x == ResultSet.TempColumn) return false;
            if (!Expr.TryParse(x, out var e, out _)) return false;
            v = e.Eval(0, double.NaN);
            return !double.IsNaN(v);
        }

        static double Eval(ResultSet rs, ResultSet.Row r, ErrorItem it, Expr tf, bool constX, double xc, out double x)
        {
            x = constX ? xc : string.IsNullOrWhiteSpace(it.X) ? double.NaN : rs.Get(r, it.X);
            double y = rs.Get(r, it.Y);
            if (double.IsNaN(y)) return double.NaN;
            double e, reference;
            if (tf == null) { e = y; reference = x; }
            else
            {
                if (double.IsNaN(x)) return double.NaN;
                double yth = tf.Eval(x, r.Temp);
                e = y - yth;
                reference = yth;
                if (it.ReferToInput)
                {
                    double h = 1e-6 * Math.Max(1e-3, Math.Abs(x));
                    double g = (tf.Eval(x + h, r.Temp) - tf.Eval(x - h, r.Temp)) / (2 * h);
                    if (!(Math.Abs(g) > 0)) return double.NaN;
                    e /= g;
                    reference = x;
                }
            }
            switch (it.Unit)
            {
                case BudgetUnit.PctReading: return Math.Abs(reference) > 0 ? e / Math.Abs(reference) * 100 : double.NaN;
                case BudgetUnit.PctFs: return it.FullScale > 0 ? e / it.FullScale * 100 : double.NaN;
                default: return e;
            }
        }

        public static string Check(ResultSet rs, ErrorItem it)
        {
            if (it.Name.Trim().Length == 0) return "Give the error a name.";
            if (rs.Col(it.Y) < 0) return $"'{it.Y}' is not a measurement of this campaign.";
            if (it.UsesTf && !Expr.TryParse(it.Tf, out _, out var err)) return "Ideal TF: " + err + "  (use x for the input, T for temperature, e.g. 2*x)";
            bool needX = it.UsesTf || it.Unit == BudgetUnit.PctReading;
            if (needX && !TryConstant(rs, it.X, out _) && rs.Col(it.X) < 0 && !rs.IsParam(it.X))
                return "Choose the input: a measurement (e.g. vin) or a fixed number (e.g. 1).";
            if (it.Unit == BudgetUnit.PctFs && !(it.FullScale > 0)) return "Enter the full scale for a % of full scale budget.";
            return null;
        }

        /// <summary>Upper 95 % one-sided bound of sigma (chi-square, Wilson-Hilferty).</summary>
        public static double SigmaUpper(double s, int n)
        {
            if (n < 3 || double.IsNaN(s)) return double.NaN;
            double v = n - 1, z = -1.6449;
            double c = v * Math.Pow(1 - 2 / (9 * v) + z * Math.Sqrt(2 / (9 * v)), 3);
            return s * Math.Sqrt(v / c);
        }

        /// <summary>
        /// Are both tails of the data no longer than a normal's with the same mean and sigma? Then mean +/- k sigma
        /// over-states the real extremes (e.g. flat tolerances adding up), so it is a safe bound even when a normality
        /// test (very sensitive with thousands of runs) rejects the shape.
        /// </summary>
        public static bool TailsNoLongerThanNormal(Descriptive d)
        {
            if (d.N < 30 || !(d.Std > 0)) return false;
            if (d.ExKurt > 0.3 || Math.Abs(d.Skew) > 0.3) return false;
            var ps = new List<double> { 0.05, 0.01 };
            if (d.N >= 740) ps.Add(0.00135);
            foreach (var p in ps)
            {
                double z = -Stats.NormalInv(p);
                if ((d.Quantile(1 - p) - d.Mean) / d.Std > 1.05 * z) return false;
                if ((d.Mean - d.Quantile(p)) / d.Std > 1.05 * z) return false;
            }
            return true;
        }

        public static ErrorResult Compute(ResultSet rs, ErrorItem it, int group, double k, bool pessimistic)
        {
            var res = new ErrorResult { Item = it, K = k, Pessimistic = pessimistic };
            string bad = Check(rs, it);
            if (bad != null) { res.Error = bad; return res; }
            Expr tf = null;
            if (it.UsesTf) Expr.TryParse(it.Tf, out tf, out _);
            bool constX = TryConstant(rs, it.X, out double xc);

            var rows = new List<ResultSet.Row>(); var es = new List<double>(); var xs = new List<double>();
            foreach (var r in rs.InGroup(group))
            {
                double e = Eval(rs, r, it, tf, constX, xc, out double x);
                if (double.IsNaN(e) || double.IsInfinity(e)) continue;
                rows.Add(r); es.Add(e); xs.Add(x);
            }
            res.E = es.ToArray(); res.X = xs.ToArray(); res.N = es.Count;
            if (es.Count < 3) { res.Error = "fewer than 3 runs with data."; return res; }

            // conditions: each temperature corner on its own (when looking at all of them), then input bands
            var corners = rs.Mode == TempMode.Corners && group == 0 ? Enumerable.Range(0, rs.Corners.Count).ToList() : new List<int> { -1 };
            bool varies = res.InputVaries && xs.All(v => !double.IsNaN(v));
            foreach (int c in corners)
            {
                var idx = Enumerable.Range(0, rows.Count).Where(i => c < 0 || rows[i].Corner == c).ToList();
                if (idx.Count < 3) continue;
                string tl = c >= 0 ? ResultSet.TempLabel(rs.Corners[c]) : (rs.Mode == TempMode.Corners && group > 0 ? rs.GroupNames()[group] : "");
                int bands = varies ? Math.Max(1, Math.Min(5, idx.Count / 250)) : 1;
                var ord = idx.OrderBy(i => xs[i]).ToList();
                for (int b = 0; b < bands; b++)
                {
                    var part = ord.Skip(b * ord.Count / bands).Take((b + 1) * ord.Count / bands - b * ord.Count / bands).ToList();
                    if (part.Count < 3) continue;
                    var s = new ErrorSlice { Corner = c, E = part.Select(i => es[i]).ToArray(), Rows = part.Select(i => rows[i]).ToList() };
                    if (bands > 1) { s.XLo = xs[part[0]]; s.XHi = xs[part[part.Count - 1]]; }
                    string xl = bands > 1 ? $"{it.X} {Eng(s.XLo)}..{Eng(s.XHi)}" : "";
                    s.Label = tl.Length > 0 && xl.Length > 0 ? tl + ", " + xl : tl.Length > 0 ? tl : xl.Length > 0 ? xl : "all runs";
                    Judge(s, it, k, pessimistic);
                    res.Slices.Add(s);
                }
            }
            if (res.Slices.Count == 0) { res.Error = "not enough runs in any condition."; return res; }

            // the worst slice: the smallest margin (or the widest bound without a budget); verdict: FAIL > CHECK > PASS
            res.Worst = res.Slices.OrderBy(s => res.HasBudget ? s.Margin : -Math.Max(Math.Abs(s.Lo), Math.Abs(s.Hi))).First();
            if (res.HasBudget)
                res.Verdict = res.Slices.Any(s => s.Verdict == "FAIL") ? "FAIL" : res.Slices.Any(s => s.Verdict == "CHECK") ? "CHECK" : "PASS";
            if (res.HasBudget && res.Worst.Verdict != res.Verdict)
                res.Worst = res.Slices.Where(s => s.Verdict == res.Verdict).OrderBy(s => s.Margin).First();

            // the systematic part a corrected TF can remove: e = a + b x (or the mean for a fixed input)
            if (varies)
            {
                var fit = Stats.Fit(xs, es);
                res.FitOffset = fit.Intercept; res.FitSlope = fit.Slope; res.ResidualStd = fit.ResidualStd;
            }
            else { res.FitOffset = res.Worst.D.Mean; res.FitSlope = 0; res.ResidualStd = res.Worst.D.Std; }

            if (rs.Params.Count > 0 || rs.Mode == TempMode.Random)
            {
                var set = new HashSet<ResultSet.Row>(res.Worst.Rows);
                res.Sens = SensitivityReport.Parameters(rs, Column(it), 0, r => set.Contains(r));
            }
            return res;
        }

        static void Judge(ErrorSlice s, ErrorItem it, double k, bool pessimistic)
        {
            s.D = Descriptive.Of(s.E);
            Stats.AndersonDarling(s.D, out _, out s.AdP);
            s.Normal = !double.IsNaN(s.AdP) && s.AdP >= MeasStats.NormalAlpha;
            s.TailsOk = s.Normal || TailsNoLongerThanNormal(s.D);
            s.SigmaUp = SigmaUpper(s.D.Std, s.D.N);
            s.Sigma = pessimistic && !double.IsNaN(s.SigmaUp) ? s.SigmaUp : s.D.Std;
            s.Lo = s.D.Mean - k * s.Sigma;
            s.Hi = s.D.Mean + k * s.Sigma;
            if (double.IsNaN(it.Budget) || !(it.Budget > 0)) return;
            s.Margin = it.Budget - Math.Max(Math.Abs(s.Lo), Math.Abs(s.Hi));
            s.Outside = s.E.Count(e => Math.Abs(e) > it.Budget);
            // units already outside: FAIL. Bell-shaped: the k-sigma bound decides. Shorter tails than normal: the bound
            // is safe, so inside = PASS, outside = CHECK (the real extremes may still fit). Longer tails: CHECK.
            s.Verdict = s.Outside > 0 ? "FAIL"
                      : s.Normal ? (s.Margin < 0 ? "FAIL" : "PASS")
                      : s.TailsOk ? (s.Margin < 0 ? "CHECK" : "PASS")
                      : "CHECK";
        }
    }
}
