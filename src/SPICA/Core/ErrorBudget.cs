using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Spica.Core
{
    public enum BudgetUnit { Abs, PctReading, PctFs }

    /// <summary>How the ideal output is given when an output is compared with a reference.</summary>
    public enum IdealKind { Linear, Expression }

    /// <summary>
    /// One error the design must keep inside its boundaries, in one of two styles:
    ///  - a measurement that already is the error (computed by a .meas in LTspice): Tf empty;
    ///  - an output Y compared run by run with the ideal value of a reference X (a measurement or a number):
    ///    error = Y - ideal(X), with ideal = Gain * x + Offset or any expression of x (and T),
    ///    optionally referred to the input by dividing by the ideal gain d ideal/dx.
    /// The boundaries may be symmetric (+/- one value) or not, and either side may be left open.
    /// </summary>
    public sealed class ErrorItem
    {
        public string Name = "";
        public string Y = "";              // measurement: the error itself, or the output
        public string X = "";              // reference / input: measurement name or a number; empty = none
        public string Tf = "";             // ideal output as an expression of x (and T); empty = Y is already the error
        public IdealKind Ideal = IdealKind.Linear;
        public double Gain = 1, Offset = 0;   // the linear ideal, kept in Tf as "Gain*x + Offset"
        public bool ReferToInput = true;
        public double Lower = double.NaN, Upper = double.NaN;   // boundaries, in the units below; NaN = open
        public bool Symmetric = true;
        public BudgetUnit Unit = BudgetUnit.Abs;
        public double FullScale = double.NaN;

        public bool UsesTf => !string.IsNullOrWhiteSpace(Tf);
        public bool HasBounds => !double.IsNaN(Lower) || !double.IsNaN(Upper);

        /// <summary>Symmetric budget +/- value (setups before 0.4 stored only this).</summary>
        public double Budget
        {
            get => Symmetric ? Upper : double.NaN;
            set { Symmetric = true; Lower = -value; Upper = value; }
        }

        /// <summary>Half the allowed range (or the distance of a single boundary from 0): what margins are a share of.</summary>
        public double Span =>
            !double.IsNaN(Lower) && !double.IsNaN(Upper) ? (Upper - Lower) / 2 : Math.Abs(double.IsNaN(Upper) ? Lower : Upper);

        public bool Inside(double v) => (double.IsNaN(Lower) || v >= Lower) && (double.IsNaN(Upper) || v <= Upper);

        public ErrorItem Clone() => (ErrorItem)MemberwiseClone();

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>"g*x + o" for the linear ideal.</summary>
        public static string LinearTf(double g, double o)
        {
            string s = g.ToString("G10", Inv) + "*x";
            if (o > 0) s += " + " + o.ToString("G10", Inv);
            else if (o < 0) s += " - " + (-o).ToString("G10", Inv);
            return s;
        }

        /// <summary>Is the ideal expression a straight line in x that does not depend on T? Then its gain and offset.</summary>
        public static bool TryLinear(string tf, out double g, out double o)
        {
            g = double.NaN; o = double.NaN;
            if (string.IsNullOrWhiteSpace(tf) || !Expr.TryParse(tf, out var e, out _)) return false;
            o = e.Eval(0, 25);
            g = e.Eval(1, 25) - o;
            if (double.IsNaN(g) || double.IsNaN(o)) return false;
            foreach (var x in new[] { -3.7, 0.37, 2.9, 1e3 })
                foreach (var t in new[] { -40.0, 25, 125 })
                {
                    double v = e.Eval(x, t), want = g * x + o;
                    if (double.IsNaN(v) || Math.Abs(v - want) > 1e-9 * Math.Max(1, Math.Abs(want))) return false;
                }
            return true;
        }

        public string Definition()
        {
            if (!UsesTf) return Y + " (the error, from LTspice)";
            string x = X.Length > 0 ? X : "x";
            string ideal = Ideal == IdealKind.Linear ? LinearTf(Gain, Offset).Replace("x", x) : $"TF({x}), TF = {Tf}";
            return ReferToInput ? $"({Y} - ({ideal})) / gain" : $"{Y} - ({ideal})";
        }

        public string UnitText() => Unit == BudgetUnit.PctReading ? "% of reading" : Unit == BudgetUnit.PctFs ? "% of full scale" : (UsesTf && ReferToInput ? "input units" : "units of " + Y);
        public string Suffix() => Unit == BudgetUnit.Abs ? "" : " %";

        /// <summary>"+/-15m", "-10m .. +20m", ">= -10m".</summary>
        public string BoundsText()
        {
            string F(double v) => ErrorBudget.Fmt(v, this);
            if (!HasBounds) return "";
            if (Symmetric && !double.IsNaN(Upper)) return "±" + F(Upper);
            if (double.IsNaN(Lower)) return "≤ " + F(Upper);
            if (double.IsNaN(Upper)) return "≥ " + F(Lower);
            return F(Lower) + " .. " + F(Upper);
        }

        /// <summary>The boundaries as entered, with what the % is taken of: "±0.5000 % of full scale (FS = 10.00)".</summary>
        public string RequirementText()
        {
            if (!HasBounds) return "none";
            string b = BoundsText();
            if (Unit == BudgetUnit.PctFs) return $"{b} of full scale (FS = {ErrorBudget.Eng(FullScale)})";
            if (Unit == BudgetUnit.PctReading) return $"{b} of reading ({(UsesTf && !ReferToInput ? "the ideal output" : X)})";
            return b;
        }

        /// <summary>An error value in absolute units (NaN for % of reading: that depends on each run's reading).</summary>
        public double ToAbs(double v) => Unit == BudgetUnit.Abs ? v : Unit == BudgetUnit.PctFs ? v * FullScale / 100 : double.NaN;

        /// <summary>The quantity the absolute error is in: the input (referred to it) or the output.</summary>
        public string AbsUnits() => UsesTf && ReferToInput ? $"input units ({X})" : $"units of {Y}";
    }

    /// <summary>
    /// The error of the units in one condition: one temperature and one input value. Units in the same condition
    /// differ only by their tolerances, so this is the distribution the k-sigma rule applies to. With an input swept
    /// in levels, each level is a condition of its own; with an input that varies continuously, the condition is a
    /// point of the fitted envelope (Modelled: mean and sigma from the fit, Rows = the runs nearest to it).
    /// </summary>
    public sealed class ErrorSlice
    {
        public string Label = "all runs";
        public int Corner = -1;
        public double X = double.NaN;                                 // the input value of the condition (NaN = input fixed)
        public bool Modelled;
        public double[] E = new double[0];
        public List<ResultSet.Row> Rows = new List<ResultSet.Row>();
        public Descriptive D;                                         // the runs of the condition (seen min / max)
        public double Mean = double.NaN, Std = double.NaN;            // the estimate the k-sigma rule uses
        public Descriptive Shape;                                     // what the shape test saw (standardized residuals when modelled)
        public double AdP = double.NaN;
        public bool Normal, TailsOk;
        public double SigmaUp = double.NaN, Sigma = double.NaN;
        public double Lo = double.NaN, Hi = double.NaN;               // mean -/+ k sigma
        public double MarginLo = double.NaN, MarginHi = double.NaN;   // room left on each side at k sigma (NaN = open side)
        public double Margin = double.NaN;                            // the smaller of the two
        public double SigmasLo = double.NaN, SigmasHi = double.NaN;   // boundary distance from the mean, in sigma
        public int Outside;
        public string Verdict = "-";
    }

    /// <summary>Mean and mean +/- k sigma across the input at one temperature: a curve that widens where the error spreads more.</summary>
    public sealed class ErrorEnvelope
    {
        public int Corner = -1;
        public bool Levels;                                           // through the levels swept, not a fit
        public double[] X = new double[0], Mean = new double[0], Lo = new double[0], Hi = new double[0];
    }

    /// <summary>The verdict of one error item against its boundaries at k sigma: the worst of its slices.</summary>
    public sealed class ErrorResult
    {
        public ErrorItem Item;
        public string Error;
        public double K;
        public bool Pessimistic;
        public double[] E = new double[0], X = new double[0];        // every run (charts)
        public ResultSet.Row[] Rows = new ResultSet.Row[0];
        public readonly List<ErrorSlice> Slices = new List<ErrorSlice>();
        public readonly List<ErrorEnvelope> Envelopes = new List<ErrorEnvelope>();
        public ErrorSlice Worst;                                      // the point with the least margin: what the figures show
        public ErrorSlice Deciding;                                   // the point that sets the verdict (a CHECK may be elsewhere)
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
        public int Outside;                                           // simulated units outside the boundaries

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        string F(double v) => ErrorBudget.Fmt(v, Item);
        string K0 => K.ToString("0.#", Inv);
        static string Eng(double v) => ErrorBudget.Eng(v);
        static double Nz(double v) => double.IsNaN(v) ? double.PositiveInfinity : v;

        public bool HasBudget => Item.HasBounds;
        /// <summary>Margin as a share of half the allowed range.</summary>
        public double MarginPct => HasBudget && Item.Span > 0 ? Margin / Item.Span * 100 : double.NaN;
        public double SlicePct(ErrorSlice s) => HasBudget && Item.Span > 0 ? s.Margin / Item.Span * 100 : double.NaN;
        public bool InputVaries => X.Length > 2 && X.Where(v => !double.IsNaN(v)).DefaultIfEmpty().Max() > X.Where(v => !double.IsNaN(v)).DefaultIfEmpty().Min();

        /// <summary>Runs with no error outside the budget needed to show a coverage by samples alone (95 % confidence).</summary>
        public static double RunsToShow(double coverage) => Math.Log(0.05) / Math.Log(coverage);

        /// <summary>One line: the verdict in words.</summary>
        public string VerdictText()
        {
            if (Error != null) return Error;
            var w = Worst;
            return Verdict == "PASS" ? $"mean ± {K0} sigma is inside the boundaries" + (Slices.Count > 1 ? " everywhere" : "") + (w.Normal ? "." : " (the tails are shorter than normal: safe).")
                 : Verdict == "FAIL" ? (Outside > 0 ? $"{Outside} of {N} simulated units are already outside the boundaries." : $"mean ± {K0} sigma crosses the {(w.MarginLo < w.MarginHi || double.IsNaN(w.MarginHi) ? "lower" : "upper")} boundary at {w.Label}.")
                 : Verdict == "CHECK" ? (Deciding != null && Deciding != w
                     ? $"mean ± {K0} sigma is inside the boundaries everywhere, but at {Deciding.Label} the error is not bell-shaped, so the {K0}-sigma figure cannot decide there " +
                       $"(boundary {Math.Min(Nz(Deciding.SigmasLo), Nz(Deciding.SigmasHi)).ToString("0.0", Inv)} sigma from its mean)."
                     : $"the error is not bell-shaped at {w.Label}, so the {K0}-sigma figure cannot decide.")
                 : "no boundaries set: enter them to get a verdict.";
        }

        /// <summary>Short notes under the figures: why CHECK, what a correction would leave, unexplained spread.</summary>
        public List<string> Notes()
        {
            var n = new List<string>();
            if (Error != null || Worst == null) return n;
            var w = Deciding ?? Worst;
            if (!w.Normal)
            {
                if (w.TailsOk)
                    n.Add($"Not exactly bell-shaped (p = {MeasStats.P(w.AdP)}), but the tails are no longer than a normal's (flat tolerances adding up): mean ± {K0} sigma over-states the real extremes." +
                          (Verdict == "CHECK" ? " Over the boundary on paper only: run a worst case (Setup > Analysis) for the hard bound." : ""));
                else
                {
                    var d = w.Shape ?? w.D;
                    string shape = d.ExKurt > 0.5 ? "heavy-tailed (longer tails than sigma says)" : Math.Abs(d.Skew) > 0.3 ? "lopsided (one side reaches further)" : "tails longer than a normal's";
                    n.Add($"Not bell-shaped (p = {MeasStats.P(w.AdP)}): {shape}. Use a worst case, or more runs: with no unit outside, {RunsToShow(0.9973):N0} runs show 3 sigma, {RunsToShow(0.99994):N0} show 4 sigma.");
                }
            }
            if (!double.IsNaN(FitOffset) && InputVaries)
                n.Add($"The mean error follows e = {Eng(FitOffset)} {(FitSlope < 0 ? "-" : "+")} {Eng(Math.Abs(FitSlope))}·x (offset and gain error): a TF corrected for it leaves ±{K0} sigma = ±{F(K * ResidualStd)}" +
                      (HasBudget ? (Item.Inside(-K * ResidualStd) && Item.Inside(K * ResidualStd) ? ", inside the boundaries." : ", still outside the boundaries.") : "."));
            else if (!double.IsNaN(FitOffset) && Math.Abs(w.Mean) > 0.5 * w.Sigma)
                n.Add($"Removing the mean (a calibration) leaves ±{K0} sigma = ±{F(K * w.Sigma)}" +
                      (HasBudget ? (Item.Inside(-K * w.Sigma) && Item.Inside(K * w.Sigma) ? ", inside the boundaries." : ", still outside the boundaries.") : "."));
            if (Sens != null && Sens.Conditioned && Sens.InputShare > 0.5)
                n.Add($"The input ({string.Join(", ", Sens.InputNames)}) explains {(100 * Sens.InputShare).ToString("0", Inv)} % of the error's spread: the ideal TF does not match the circuit (a systematic gain or offset error, or a nonlinearity), not a tolerance.");
            if (!InputVaries && string.IsNullOrWhiteSpace(Item.X) && Sens != null && Sens.Conditioned && Sens.InputShare > 0.2)
                n.Add($"The input ({string.Join(", ", Sens.InputNames)}) explains {(100 * Sens.InputShare).ToString("0", Inv)} % of this error, but the error has no input set: units at every input are pooled, " +
                      "which widens sigma and can make the shape look not bell-shaped. Set the input in the error's definition to judge it across the input.");
            if (Sens != null && Sens.Error == null && Sens.Items.Count > 0 && Sens.R2 < 0.9)
                n.Add($"{(100 * (1 - Sens.R2)).ToString("0", Inv)} % of the spread is not explained by the tolerances: nonlinear effects, or random values SPICA does not draw (LTspice's mc() without paired samples, encrypted models).");
            return n;
        }

        /// <summary>Plain-text version (report, console).</summary>
        public string Summary()
        {
            if (Error != null) return Item.Name + ": " + Error;
            var w = Worst;
            var sb = new StringBuilder();
            sb.AppendLine($"{Item.Name}:  {Verdict}{(Verdict == "-" ? "" : " at " + K0 + " sigma")}: {VerdictText()}");
            sb.AppendLine($"  error = {Item.Definition()}, in {Item.UnitText()}");
            if (HasBudget) sb.AppendLine($"  boundaries {Item.BoundsText()}");
            sb.AppendLine();
            sb.AppendLine("Worst point" + (Slices.Count > 1 ? $": {w.Label} ({w.D.N} units)" : ""));
            sb.AppendLine($"  mean          {F(w.Mean)}");
            sb.AppendLine($"  sigma         {F(w.Std)}   (95 % upper bound {F(w.SigmaUp)}{(Pessimistic ? ": used" : "")})");
            sb.AppendLine($"  mean ± {K0}s    {F(w.Lo)} .. {F(w.Hi)}");
            sb.AppendLine($"  seen          {F(w.D.Min)} .. {F(w.D.Max)}");
            sb.AppendLine($"  bell-shaped   {(w.Normal ? "yes" : w.TailsOk ? "no, shorter tails (safe)" : "no")}  (p = {MeasStats.P(w.AdP)})");
            if (HasBudget)
            {
                if (!double.IsNaN(w.MarginLo)) sb.AppendLine($"  low side      {F(w.MarginLo)} of room, boundary {w.SigmasLo.ToString("0.0", Inv)} sigma from the mean (needed {K0})");
                if (!double.IsNaN(w.MarginHi)) sb.AppendLine($"  high side     {F(w.MarginHi)} of room, boundary {w.SigmasHi.ToString("0.0", Inv)} sigma from the mean (needed {K0})");
            }
            if (Slices.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine("Every condition");
                int lw = Math.Max(10, Slices.Max(s => s.Label.Length));
                sb.AppendLine($"  {"condition".PadRight(lw)} {"n",5} {"mean",10} {"sigma",10} {"mean ±" + K0 + "s",22} {"margin",7}  verdict");
                foreach (var s in Slices)
                    sb.AppendLine($"  {s.Label.PadRight(lw)} {s.D.N,5} {F(s.Mean),10} {F(s.Sigma),10} {F(s.Lo) + " .. " + F(s.Hi),22} " +
                                  $"{(HasBudget ? SlicePct(s).ToString("0", Inv) + " %" : ""),7}  {s.Verdict}");
            }
            var notes = Notes();
            if (notes.Count > 0) { sb.AppendLine(); foreach (var t in notes) sb.AppendLine("  - " + t); }
            if (Sens != null && Sens.Error == null && Sens.Items.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"What drives the spread{(Slices.Count > 1 ? " in the worst condition" : "")} (share of the variance" +
                              (Sens.Conditioned ? $" the input {string.Join(", ", Sens.InputNames)} leaves; it explains {SensitivityReport.ShareText(Sens.InputShare)})" : ")"));
                foreach (var it in Sens.Items.Where(i => !i.IsInput && !double.IsNaN(i.Share)).Take(6))
                    sb.AppendLine($"  {(100 * it.Bar).ToString("0.0", Inv),5} %  {it.Name}");
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
            if (it.UsesTf && !Expr.TryParse(it.Tf, out _, out var err)) return "Ideal TF: " + err + "  (use x for the reference, T for temperature, e.g. 2*x)";
            bool needX = it.UsesTf || it.Unit == BudgetUnit.PctReading;
            if (needX && !TryConstant(rs, it.X, out _) && rs.Col(it.X) < 0 && !rs.IsParam(it.X))
                return it.UsesTf ? "Choose the reference: a measurement (e.g. vin) or a fixed number (e.g. 1)."
                                 : "For a % of reading boundary, choose the reading: the measurement the % is taken of.";
            if (!needX && !string.IsNullOrWhiteSpace(it.X) && !TryConstant(rs, it.X, out _) && rs.Col(it.X) < 0 && !rs.IsParam(it.X))
                return $"Input: '{it.X}' is not a measurement or parameter of this campaign.";
            if (it.Unit == BudgetUnit.PctFs && !(it.FullScale > 0)) return "Enter the full scale for a % of full scale boundary.";
            if (!double.IsNaN(it.Lower) && !double.IsNaN(it.Upper) && it.Lower >= it.Upper) return "The lower boundary must be below the upper one.";
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
            res.E = es.ToArray(); res.X = xs.ToArray(); res.Rows = rows.ToArray(); res.N = es.Count;
            if (es.Count < 3) { res.Error = "fewer than 3 runs with data."; return res; }

            // conditions: each temperature corner on its own (when looking at all of them); across the input, each level
            // swept, or points of an envelope fitted to the runs when the input varies continuously
            var corners = rs.Mode == TempMode.Corners && group == 0 ? Enumerable.Range(0, rs.Corners.Count).ToList() : new List<int> { -1 };
            bool varies = res.InputVaries && xs.All(v => !double.IsNaN(v));
            foreach (int c in corners)
            {
                var idx = Enumerable.Range(0, rows.Count).Where(i => c < 0 || rows[i].Corner == c).ToList();
                if (idx.Count < 3) continue;
                string tl = c >= 0 ? ResultSet.TempLabel(rs.Corners[c]) : (rs.Mode == TempMode.Corners && group > 0 ? rs.GroupNames()[group] : "");
                string Label(double xv) => string.Join(", ", new[] { tl, double.IsNaN(xv) ? "" : $"{it.X} = {Eng(xv)}" }.Where(t => t.Length > 0).DefaultIfEmpty("all runs"));
                ErrorSlice Of(List<int> part, double xv) =>
                    new ErrorSlice { Corner = c, X = xv, E = part.Select(i => es[i]).ToArray(), Rows = part.Select(i => rows[i]).ToList(), Label = Label(xv) };

                var levels = varies ? Levels(idx, xs) : null;
                if (!varies || levels == null && idx.Count < 40)
                {
                    var s = Of(idx, double.NaN);
                    Judge(s, it, k, pessimistic);
                    res.Slices.Add(s);
                }
                else if (levels != null)
                {
                    var env = new ErrorEnvelope { Corner = c, Levels = true };
                    // one shape test per temperature on the runs of every level, each centred and scaled by its own
                    // mean and sigma: a test per level would flag some level by chance alone (5 % each)
                    var made = levels.Select(lv => Of(lv, lv.Average(i => xs[i]))).ToList();
                    var zs = new List<double>();
                    foreach (var s in made)
                    {
                        var d = Descriptive.Of(s.E);
                        if (d.Std > 0) zs.AddRange(s.E.Select(e => (e - d.Mean) / d.Std));
                    }
                    var zd = Descriptive.Of(zs);
                    Stats.AndersonDarling(zd, out _, out double adp);
                    bool normal = !double.IsNaN(adp) && adp >= MeasStats.NormalAlpha;
                    foreach (var s in made)
                    {
                        s.Shape = zd; s.AdP = adp; s.Normal = normal; s.TailsOk = normal || TailsNoLongerThanNormal(zd);
                        Judge(s, it, k, pessimistic);
                        res.Slices.Add(s);
                    }
                    var mine = res.Slices.Where(x => x.Corner == c && !double.IsNaN(x.X)).OrderBy(x => x.X).ToList();
                    env.X = mine.Select(x => x.X).ToArray(); env.Mean = mine.Select(x => x.Mean).ToArray();
                    env.Lo = mine.Select(x => x.Lo).ToArray(); env.Hi = mine.Select(x => x.Hi).ToArray();
                    res.Envelopes.Add(env);
                }
                else Envelope(res, it, c, idx, xs, es, rows, k, pessimistic, Of);
            }
            res.Outside = it.HasBounds ? es.Count(e => !it.Inside(e)) : 0;
            if (res.Slices.Count == 0) { res.Error = "not enough runs in any condition."; return res; }

            // the worst slice: the smallest margin (or the widest bound without a budget); verdict: FAIL > CHECK > PASS
            res.Worst = res.Slices.OrderBy(s => res.HasBudget ? s.Margin : -Math.Max(Math.Abs(s.Lo), Math.Abs(s.Hi))).First();
            if (res.HasBudget)
                res.Verdict = res.Slices.Any(s => s.Verdict == "FAIL") ? "FAIL" : res.Slices.Any(s => s.Verdict == "CHECK") ? "CHECK" : "PASS";
            // the figures stay on the tightest point; the point behind the verdict is kept apart (a CHECK elsewhere)
            res.Deciding = res.HasBudget && res.Worst.Verdict != res.Verdict
                ? res.Slices.Where(s => s.Verdict == res.Verdict).OrderBy(s => s.Margin).First() : res.Worst;

            // the systematic part a corrected TF can remove: e = a + b x (or the mean for a fixed input)
            if (varies)
            {
                var fit = Stats.Fit(xs, es);
                res.FitOffset = fit.Intercept; res.FitSlope = fit.Slope; res.ResidualStd = fit.ResidualStd;
            }
            else { res.FitOffset = res.Worst.Mean; res.FitSlope = 0; res.ResidualStd = res.Worst.Std; }

            if (rs.Params.Count > 0 || rs.Mode == TempMode.Random)
            {
                var set = new HashSet<ResultSet.Row>(res.Worst.Rows);
                res.Sens = SensitivityReport.Parameters(rs, Column(it), 0, r => set.Contains(r));
            }
            return res;
        }

        /// <summary>
        /// The levels of an input swept in steps: up to 30 distinct values with at least 8 runs each (values closer than
        /// a millionth of the range are the same level). Null when the input varies continuously.
        /// </summary>
        static List<List<int>> Levels(List<int> idx, List<double> xs)
        {
            var ord = idx.OrderBy(i => xs[i]).ToList();
            double tol = 1e-6 * Math.Max(Math.Abs(xs[ord[ord.Count - 1]] - xs[ord[0]]), 1e-300);
            var levels = new List<List<int>>();
            foreach (int i in ord)
            {
                if (levels.Count == 0 || xs[i] - xs[levels[levels.Count - 1][0]] > tol) levels.Add(new List<int>());
                levels[levels.Count - 1].Add(i);
                if (levels.Count > 30) return null;
            }
            return levels.Count >= 2 && levels.All(l => l.Count >= 8) ? levels : null;
        }

        /// <summary>
        /// An input that varies continuously: the mean error as a polynomial of the input (up to cubic) and its variance
        /// as a quadratic (exact when the tolerances act as offsets and gains: var = a + b x + c x^2), so mean +/- k sigma
        /// is a smooth envelope that widens where the error spreads more. The shape test uses the residuals divided by
        /// the local sigma. Conditions: 11 points across the range, each judged on the envelope.
        /// </summary>
        static void Envelope(ErrorResult res, ErrorItem it, int c, List<int> idx, List<double> xs, List<double> es, List<ResultSet.Row> rows,
                             double k, bool pessimistic, Func<List<int>, double, ErrorSlice> of)
        {
            int n = idx.Count;
            double x0 = idx.Min(i => xs[i]), x1 = idx.Max(i => xs[i]), xm = (x0 + x1) / 2, xh = (x1 - x0) / 2;
            var t = idx.Select(i => (xs[i] - xm) / xh).ToArray();
            var y = idx.Select(i => es[i]).ToArray();
            int dm = n >= 80 ? 3 : n >= 30 ? 2 : 1, dv = n >= 60 ? 2 : n >= 20 ? 1 : 0;
            double[] Poly(double[] tt, double[] yy, int deg)
            {
                var cols = Enumerable.Range(0, deg + 1).Select(p => tt.Select(v => Math.Pow(v, p)).ToArray()).ToList();
                return SensitivityReport.LeastSquares(cols, yy, tt.Length) ?? new[] { yy.Average() }.Concat(Enumerable.Repeat(0.0, deg)).ToArray();
            }
            double Val(double[] b, double tv) { double v = 0, p = 1; foreach (var q in b) { v += q * p; p *= tv; } return v; }
            var bm = Poly(t, y, dm);
            var r = y.Select((v, i) => v - Val(bm, t[i])).ToArray();
            var bv = Poly(t, r.Select(v => v * v).ToArray(), dv);
            double vAvg = r.Average(v => v * v);
            var grid = Enumerable.Range(0, 81).Select(j => -1 + 2.0 * j / 80).ToArray();
            double vTop = Math.Max(grid.Max(tv => Val(bv, tv)), vAvg);
            double Var(double tv) => Math.Max(Val(bv, tv), 1e-4 * vTop);   // a fitted variance never goes to zero or below
            // scaled so the residuals over their local sigma have unit spread
            var z = r.Select((v, i) => v / Math.Sqrt(Var(t[i]))).ToArray();
            var zd = Descriptive.Of(z);
            double scale = zd.Std > 0 ? zd.Std : 1;
            zd = Descriptive.Of(z.Select(v => v / scale));
            Stats.AndersonDarling(zd, out _, out double adp);
            bool normal = !double.IsNaN(adp) && adp >= MeasStats.NormalAlpha, tailsOk = normal || TailsNoLongerThanNormal(zd);
            double Sig(double tv) => scale * Math.Sqrt(Var(tv));
            int nEff = Math.Max(3, n - (dm + 1));

            var env = new ErrorEnvelope { Corner = c, X = grid.Select(tv => xm + xh * tv).ToArray(), Mean = grid.Select(tv => Val(bm, tv)).ToArray() };
            double SigK(double tv) { double sd = Sig(tv), up = SigmaUpper(sd, nEff); return k * (pessimistic && !double.IsNaN(up) ? up : sd); }
            env.Lo = grid.Select((tv, j) => env.Mean[j] - SigK(tv)).ToArray();
            env.Hi = grid.Select((tv, j) => env.Mean[j] + SigK(tv)).ToArray();
            res.Envelopes.Add(env);

            int win = Math.Max(20, n / 11);
            for (int j = 0; j <= 10; j++)
            {
                double tv = -1 + 0.2 * j, xv = xm + xh * tv;
                var near = Enumerable.Range(0, n).OrderBy(i => Math.Abs(t[i] - tv)).Take(win).Select(i => idx[i]).ToList();
                var s = of(near, xv);
                s.Modelled = true;
                s.Mean = Val(bm, tv); s.Std = Sig(tv);
                s.Shape = zd; s.AdP = adp; s.Normal = normal; s.TailsOk = tailsOk;
                Judge(s, it, k, pessimistic, nEff);
                res.Slices.Add(s);
            }
        }

        static void Judge(ErrorSlice s, ErrorItem it, double k, bool pessimistic, int nEff = 0)
        {
            s.D = Descriptive.Of(s.E);
            if (!s.Modelled)
            {
                if (s.Shape == null)
                {
                    Stats.AndersonDarling(s.D, out _, out s.AdP);
                    s.Normal = !double.IsNaN(s.AdP) && s.AdP >= MeasStats.NormalAlpha;
                    s.TailsOk = s.Normal || TailsNoLongerThanNormal(s.D);
                    s.Shape = s.D;
                }
                s.Mean = s.D.Mean; s.Std = s.D.Std;
                nEff = s.D.N;
            }
            s.SigmaUp = SigmaUpper(s.Std, nEff);
            s.Sigma = pessimistic && !double.IsNaN(s.SigmaUp) ? s.SigmaUp : s.Std;
            s.Lo = s.Mean - k * s.Sigma;
            s.Hi = s.Mean + k * s.Sigma;
            if (!it.HasBounds) return;
            // each side on its own: room left at k sigma, and how many sigma the boundary is from the mean
            if (!double.IsNaN(it.Lower))
            {
                s.MarginLo = s.Lo - it.Lower;
                s.SigmasLo = s.Sigma > 0 ? (s.Mean - it.Lower) / s.Sigma : double.NaN;
            }
            if (!double.IsNaN(it.Upper))
            {
                s.MarginHi = it.Upper - s.Hi;
                s.SigmasHi = s.Sigma > 0 ? (it.Upper - s.Mean) / s.Sigma : double.NaN;
            }
            s.Margin = double.IsNaN(s.MarginLo) ? s.MarginHi : double.IsNaN(s.MarginHi) ? s.MarginLo : Math.Min(s.MarginLo, s.MarginHi);
            s.Outside = s.E.Count(e => !it.Inside(e));
            // units already outside: FAIL. Bell-shaped: the k-sigma bound decides. Shorter tails than normal: the bound
            // is safe, so inside = PASS, outside = CHECK (the real extremes may still fit). Longer tails: CHECK.
            s.Verdict = s.Outside > 0 ? "FAIL"
                      : s.Normal ? (s.Margin < 0 ? "FAIL" : "PASS")
                      : s.TailsOk ? (s.Margin < 0 ? "CHECK" : "PASS")
                      : "CHECK";
        }
    }
}
