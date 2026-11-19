using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Spica.Core
{
    /// <summary>Statistics of one measurement in one group, with the sigma analysis.</summary>
    public sealed class MeasStats
    {
        public string Meas, Group;
        public Descriptive D;
        public double AdA2 = double.NaN, AdP = double.NaN;
        public SpecLimit Spec;
        public Capability Cap;
        public double P00135, P99865, P01, P99;      // the +/-3 sigma equivalent percentiles, and 1 % / 99 %

        public const double NormalAlpha = 0.05;
        public bool NormalOk => !double.IsNaN(AdP) && AdP >= NormalAlpha;

        public static MeasStats Of(string meas, string group, double[] values, SpecLimit spec)
        {
            var s = new MeasStats { Meas = meas, Group = group, D = Descriptive.Of(values), Spec = spec };
            Stats.AndersonDarling(s.D, out s.AdA2, out s.AdP);
            s.Cap = Stats.Capability(s.D, spec);
            s.P00135 = s.D.Quantile(0.00135); s.P99865 = s.D.Quantile(0.99865);
            s.P01 = s.D.Quantile(0.01); s.P99 = s.D.Quantile(0.99);
            return s;
        }

        /// <summary>
        /// The engineering view: does the design meet its requirement, what range will a production unit fall in,
        /// how much margin is left, and how far these numbers can be trusted with this many runs.
        /// The statistical detail behind it is <see cref="Narrative"/>.
        /// </summary>
        public string Summary()
        {
            var sb = new StringBuilder();
            var d = D;
            bool spec = Spec != null && Spec.IsSet;
            if (d.N < 2) return $"{Meas}: not enough runs with data.\n";
            if (!(d.Std > 0))
                return $"{Meas} = {F(d.Mean)} in every run: the tolerances do not move it.\n" +
                       (spec ? (Spec.Passes(d.Mean) ? "It meets the requirement.\n" : "It does NOT meet the requirement.\n") : "");

            // the range 99.73 % of units fall in ("3 sigma"), from the normal model when it holds, else from the data
            bool tailsResolved = d.N >= 740;
            double lo, hi; string how;
            if (NormalOk) { lo = d.Mean - 3 * d.Std; hi = d.Mean + 3 * d.Std; how = "normal model, mean +/- 3 sigma"; }
            else if (tailsResolved) { lo = P00135; hi = P99865; how = "read from the runs (the result is not bell-shaped)"; }
            else { lo = d.Mean - 3 * d.Std; hi = d.Mean + 3 * d.Std; how = $"mean +/- 3 sigma, ROUGH: the result is not bell-shaped and {d.N} runs cannot resolve the tails"; }

            // verdict
            string verdict;
            if (!spec) verdict = "no requirement set (enter the limits in 'Spec limits')";
            else
            {
                bool failsSeen = Cap.Pass < Cap.N;
                bool rangeInside = (double.IsNaN(Spec.Lsl) || lo >= Spec.Lsl) && (double.IsNaN(Spec.Usl) || hi <= Spec.Usl);
                if (failsSeen) verdict = $"FAILS: {Cap.N - Cap.Pass} of {Cap.N} units out of spec";
                else if (!rangeInside) verdict = "AT RISK: no failure in the runs, but the 99.73 % range crosses a limit";
                else if (NormalOk && Cap.Cpk < 1.33) verdict = "MEETS SPEC, small margin (nearest limit within 4 sigma)";
                else verdict = "MEETS SPEC with margin";
            }
            sb.AppendLine($"{Meas}  [{Group}]:  {verdict}");
            sb.AppendLine();

            sb.AppendLine("What a production unit will show");
            sb.AppendLine($"  typical       {F(d.Mean)}");
            sb.AppendLine($"  spread        +/-{F(3 * d.Std)} (3 sigma)" + (d.Mean != 0 ? $" = +/-{Pct(3 * d.Std / Math.Abs(d.Mean))} of typical" : ""));
            sb.AppendLine($"  99.73 % in    {F(lo)} .. {F(hi)}   ({how})");
            if (Math.Abs(d.Skew) > 0.5)
                sb.AppendLine($"  lopsided      the {(d.Skew > 0 ? "high" : "low")} side reaches further than the other");
            sb.AppendLine($"  seen          {F(d.Min)} .. {F(d.Max)} in {d.N} runs (more runs will widen this)");

            if (spec)
            {
                sb.AppendLine();
                sb.AppendLine("Against the requirement  " + $"{(double.IsNaN(Spec.Lsl) ? "-" : F(Spec.Lsl))} .. {(double.IsNaN(Spec.Usl) ? "-" : F(Spec.Usl))}");
                string Margin(double m) => m >= 0 ? $"{F(m)} to spare" : $"SHORT by {F(-m)}";
                if (!double.IsNaN(Spec.Lsl))
                    sb.AppendLine($"  low margin    {Margin(lo - Spec.Lsl)} (99.73 % range vs LSL)" + (d.Std > 0 ? $"  (LSL is {(d.Mean - Spec.Lsl) / d.Std:0.0} sigma below typical)" : ""));
                if (!double.IsNaN(Spec.Usl))
                    sb.AppendLine($"  high margin   {Margin(Spec.Usl - hi)} (99.73 % range vs USL)" + (d.Std > 0 ? $"  (USL is {(Spec.Usl - d.Mean) / d.Std:0.0} sigma above typical)" : ""));
                sb.AppendLine($"  yield         {Cap.Pass}/{Cap.N} passed; proven at least {Pct(Cap.YieldLo)} (95 % confidence)");
                if (NormalOk && !double.IsNaN(Cap.PredictedPpm))
                    sb.AppendLine($"  expected      {(Cap.PredictedPpm < 0.01 ? "" : "about ")}{Ppm(Cap.PredictedPpm)} out of spec in production (normal model, Cpk {Cap.Cpk:0.00})");
            }

            sb.AppendLine();
            sb.AppendLine("How far to trust this");
            sb.AppendLine($"  typical known to +/-{F(1.96 * d.Std / Math.Sqrt(d.N))}, spread to +/-{Pct(1.96 / Math.Sqrt(2.0 * (d.N - 1)))} (95 %)");
            string shape = double.IsNaN(AdP) ? "not tested" :
                           NormalOk ? "bell-shaped: sigma, Cpk and ppm figures are valid" :
                           d.ExKurt > 1 ? "heavy tails: sigma figures UNDER-state the extremes, rely on the runs" :
                           d.ExKurt < -0.8 ? "flat and bounded: sigma figures over-state the extremes (safe side)" :
                           Math.Abs(d.Skew) > 0.5 ? "lopsided: judge each limit by its own side of the range" :
                           d.N > 2000 ? "close to bell-shaped (the test is strict with this many runs)" : "not bell-shaped: rely on the runs, not on sigma";
            sb.AppendLine($"  shape         {shape}");
            var todo = new List<string>();
            if (!NormalOk && !tailsResolved) todo.Add($"run 740+ units to read the 99.73 % range from the data (now {d.N})");
            if (spec && Cap.Pass == Cap.N && Cap.YieldLo < 0.999)
                todo.Add($"to prove 99.9 % yield with no failure, run about {(int)Math.Ceiling(Math.Log(0.05) / Math.Log(0.999))} units");
            if (spec && (verdict.StartsWith("FAILS") || verdict.StartsWith("AT RISK") || verdict.StartsWith("MEETS SPEC, small")))
                todo.Add("the Sensitivity tab shows which tolerances cause the spread: tighten the top ones");
            foreach (var t in todo) sb.AppendLine("  next          " + t);
            return sb.ToString();
        }

        /// <summary>Plain-language sigma analysis: what can and cannot be concluded from this sample.</summary>
        public string Narrative()
        {
            var sb = new StringBuilder();
            var d = D;
            sb.AppendLine($"{Meas}  [{Group}]   n = {d.N}");
            if (d.N < 2) { sb.AppendLine("Not enough data."); return sb.ToString(); }
            if (!(d.Std > 0))
            {
                sb.AppendLine($"All {d.N} values are identical ({F(d.Mean)}): this measurement does not vary.");
                if (Spec != null && Spec.IsSet) sb.AppendLine(Spec.Passes(d.Mean) ? "It is inside the spec limits." : "It is OUTSIDE the spec limits.");
                return sb.ToString();
            }
            sb.AppendLine($"Mean {F(d.Mean)}, sigma {F(d.Std)} ({Pct(d.Std / Math.Abs(d.Mean))} of mean), range {F(d.Min)} .. {F(d.Max)}");
            double seMean = d.Std / Math.Sqrt(d.N);
            sb.AppendLine($"Mean known to +/-{F(1.96 * seMean)} (95 %); sigma known to +/-{Pct(1.96 / Math.Sqrt(2.0 * (d.N - 1)))} (95 %).");
            sb.AppendLine();

            if (double.IsNaN(AdP)) sb.AppendLine("Normality: not tested (needs 8+ values and some spread).");
            else
            {
                sb.AppendLine($"Normality (Anderson-Darling): A*2 = {AdA2:0.000}, p = {P(AdP)}  ->  " +
                              (NormalOk ? "consistent with a normal distribution." : "NOT normal at 5 %."));
                sb.AppendLine($"Shape: skewness {d.Skew:0.000}, excess kurtosis {d.ExKurt:0.000}" +
                              (Math.Abs(d.Skew) > 0.5 ? " (clearly skewed)" : "") + (d.ExKurt < -0.8 ? " (flat / bounded, e.g. uniform tolerances)" : d.ExKurt > 1 ? " (heavy tails)" : "") + ".");
                if (!NormalOk && d.N > 2000)
                    sb.AppendLine("  Note: with this many runs the test detects even small departures; judge the shape numbers too.");
            }
            sb.AppendLine();

            if (NormalOk)
            {
                sb.AppendLine($"Sigma analysis (normal model): mean +/- 3 sigma = {F(d.Mean - 3 * d.Std)} .. {F(d.Mean + 3 * d.Std)}");
                sb.AppendLine($"                               mean +/- 6 sigma = {F(d.Mean - 6 * d.Std)} .. {F(d.Mean + 6 * d.Std)}");
            }
            else
            {
                sb.AppendLine("Sigma figures are not reliable for a non-normal sample; use the percentiles:");
            }
            sb.AppendLine($"Empirical 0.135 % .. 99.865 % (the +/-3 sigma equivalent): {F(P00135)} .. {F(P99865)}" +
                          (d.N < 740 ? $"   (needs about 740+ runs to resolve; with {d.N} these are near min/max)" : ""));
            sb.AppendLine($"Empirical 1 % .. 99 %: {F(P01)} .. {F(P99)}");

            if (Spec != null && Spec.IsSet)
            {
                var c = Cap;
                sb.AppendLine();
                sb.AppendLine($"Spec: {(double.IsNaN(Spec.Lsl) ? "-" : F(Spec.Lsl))} .. {(double.IsNaN(Spec.Usl) ? "-" : F(Spec.Usl))}");
                sb.AppendLine($"Observed yield: {c.Pass}/{c.N} = {Pct(c.Yield)}  (95 % CI {Pct(c.YieldLo)} .. {Pct(c.YieldHi)}, exact binomial)");
                if (c.Pass == c.N)
                    sb.AppendLine($"  No failures seen: the true failure rate is below {Pct(1 - c.YieldLo)} with 95 % confidence.");
                if (!double.IsNaN(c.Cpk))
                {
                    sb.AppendLine($"Cpk = {c.Cpk:0.000}" + (double.IsNaN(c.Cp) ? "" : $", Cp = {c.Cp:0.000}") +
                                  $"  ->  nearest limit at {c.SigmaToLimit:0.00} sigma from the mean.");
                    if (NormalOk)
                        sb.AppendLine($"Predicted by the normal model: yield {PctFine(c.PredictedYield)}, {Ppm(c.PredictedPpm)} out of spec.");
                    else
                        sb.AppendLine("Normal-model prediction withheld (sample is not normal); rely on the observed yield and percentiles.");
                }
            }
            return sb.ToString();
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        /// <summary>6 significant digits; below 1m or from 100k up in engineering notation with SPICE suffixes (219.488u).</summary>
        public static string F(double v)
        {
            if (double.IsNaN(v)) return "-";
            if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
            double a = Math.Abs(v);
            if (a == 0 || (a >= 1e-3 && a < 1e5)) return v.ToString("G6", Inv);
            string[] pre = { "f", "p", "n", "u", "m", "", "k", "Meg", "G", "T" };
            int e3 = Math.Max(-5, Math.Min(4, (int)Math.Floor(Math.Log10(a) / 3)));
            double m = v / Math.Pow(1000, e3);
            if (Math.Abs(Math.Round(m, 3)) >= 1000 && e3 < 4) { e3++; m /= 1000; }   // 999.9996u -> 1m
            return m.ToString("G6", Inv) + pre[e3 + 5];
        }
        public static string Pct(double v) => double.IsNaN(v) ? "-" : (100 * v).ToString(v > 0.9999 && v < 1 ? "0.0000" : "0.00", Inv) + " %";
        public static string PctFine(double v) => double.IsNaN(v) ? "-" : (100 * v).ToString(v > 0.999999 ? "0.0000000" : "0.0000", Inv) + " %";
        public static string P(double p) => double.IsNaN(p) ? "-" : p < 0.0001 ? "< 0.0001" : p.ToString("0.0000", Inv);
        public static string Ppm(double ppm) => double.IsNaN(ppm) ? "-" : ppm < 0.01 ? "< 0.01 ppm" : ppm.ToString(ppm < 10 ? "0.00" : "0", Inv) + " ppm";
    }

    /// <summary>Compliance of a set of errors with an error budget.</summary>
    public sealed class BudgetCheck
    {
        public string Scenario;
        public Descriptive E;
        public double AdP = double.NaN;
        public int Inside, N;
        public double Lo = double.NaN, Hi = double.NaN;     // CI of the inside fraction
        public double Bound = double.NaN;                     // |mean| + k sigma
        public double SigmaMargin = double.NaN;               // (budget - |mean|) / sigma

        public static BudgetCheck Of(string scenario, double[] err, double budget, double k)
        {
            var b = new BudgetCheck { Scenario = scenario, E = Descriptive.Of(err) };
            Stats.AndersonDarling(b.E, out _, out b.AdP);
            b.N = b.E.N;
            if (b.N > 1)
            {
                b.Bound = Math.Abs(b.E.Mean) + k * b.E.Std;
                if (!double.IsNaN(budget))
                {
                    b.Inside = b.E.Sorted.Count(e => Math.Abs(e) <= budget);
                    Stats.ClopperPearson(b.Inside, b.N, 0.95, out b.Lo, out b.Hi);
                    if (b.E.Std > 0) b.SigmaMargin = (budget - Math.Abs(b.E.Mean)) / b.E.Std;
                }
            }
            return b;
        }
    }

    /// <summary>Transfer-function analysis of Y versus X.</summary>
    public sealed class TfReport
    {
        public string Error;
        public TfSetup Setup;
        public int N;
        public double[] X, Y, T;
        public LinearFit Fit;
        public Expr Theory;
        public double ThGain = double.NaN, ThOffset = double.NaN, ThLinR2 = double.NaN;
        public double GainErrPct = double.NaN, OffsetErr = double.NaN, RandomStd = double.NaN, RandomRelStdPct = double.NaN;
        public double[] ErrTheory;                          // per-sample error vs theory, in budget units
        public readonly List<BudgetCheck> Checks = new List<BudgetCheck>();
        public readonly List<double[]> Contrib = new List<double[]>();   // x, gain, offset, random, rss, linear (budget units)
        public string Unit => Setup.BudgetPercent ? "%" : "";

        public static TfReport Compute(ResultSet rs, TfSetup tf, int group)
        {
            var r = new TfReport { Setup = tf };
            if (string.IsNullOrEmpty(tf.X) || string.IsNullOrEmpty(tf.Y)) { r.Error = "Choose X and Y."; return r; }
            rs.Pairs(tf.X, tf.Y, group, out r.X, out r.Y, out r.T);
            r.N = r.X.Length;
            if (r.N < 3) { r.Error = "Fewer than 3 runs have both X and Y."; return r; }
            r.Fit = Stats.Fit(r.X, r.Y);

            double k = tf.K > 0 ? tf.K : 3;
            bool pct = tf.BudgetPercent;
            double budget = tf.Budget;

            if (!string.IsNullOrWhiteSpace(tf.Theory))
            {
                if (!Expr.TryParse(tf.Theory, out r.Theory, out var err)) { r.Error = "Theoretical TF: " + err; return r; }
                var yth = new double[r.N];
                for (int i = 0; i < r.N; i++) yth[i] = r.Theory.Eval(r.X[i], r.T[i]);
                if (yth.Any(v => double.IsNaN(v) || double.IsInfinity(v))) { r.Error = "The theoretical TF gives no value for some samples (uses T without temperature data?)."; return r; }

                // The theory's own linear form over the data range (exact when the theory is linear in x).
                var lin = Stats.Fit(r.X, yth);
                r.ThGain = lin.Slope; r.ThOffset = lin.Intercept; r.ThLinR2 = lin.R2;
                r.GainErrPct = (r.Fit.Slope / r.ThGain - 1) * 100;
                r.OffsetErr = r.Fit.Intercept - r.ThOffset;
                r.RandomStd = r.Fit.ResidualStd;

                r.ErrTheory = new double[r.N];
                for (int i = 0; i < r.N; i++) r.ErrTheory[i] = ErrorIn(r.Y[i], yth[i], pct);
                r.Checks.Add(BudgetCheck.Of("As designed (vs theoretical TF)", r.ErrTheory, budget, k));

                // Scatter around the best line, relative to it: in % budgets the unit-to-unit spread
                // usually scales with the signal (tolerances act on the gain), so it is taken as relative.
                var rel = new double[r.N];
                for (int i = 0; i < r.N; i++) rel[i] = (r.Y[i] - r.Fit.Predict(r.X[i])) / Math.Abs(r.Fit.Predict(r.X[i])) * 100;
                r.RandomRelStdPct = Descriptive.Of(rel).Std;

                // Error contributions at the ends and middle of the X range, in budget units.
                double xmin = r.X.Min(), xmax = r.X.Max();
                foreach (var x in new[] { xmin, 0.5 * (xmin + xmax), xmax })
                {
                    double ref_ = r.Theory.Eval(x, double.NaN);
                    if (double.IsNaN(ref_)) ref_ = r.ThGain * x + r.ThOffset;
                    double scale = pct ? 100 / Math.Abs(ref_) : 1;
                    double g = (r.Fit.Slope - r.ThGain) * x * scale;
                    double o = r.OffsetErr * scale;
                    double rnd = pct ? k * r.RandomRelStdPct : k * r.RandomStd;
                    r.Contrib.Add(new[] { x, g, o, rnd, Math.Sqrt(g * g + o * o + rnd * rnd), Math.Abs(g) + Math.Abs(o) + rnd });
                }
            }

            // What remains after trimming: gain only (through the origin), gain and offset (best line).
            var eGain = new double[r.N];
            var eBoth = new double[r.N];
            for (int i = 0; i < r.N; i++)
            {
                eGain[i] = ErrorIn(r.Y[i], r.Fit.OriginSlope * r.X[i], pct);
                eBoth[i] = ErrorIn(r.Y[i], r.Fit.Predict(r.X[i]), pct);
            }
            r.Checks.Add(BudgetCheck.Of("Gain trimmed to the average (y = a0*x)", eGain, budget, k));
            r.Checks.Add(BudgetCheck.Of("Gain and offset trimmed (best line y = a*x + b)", eBoth, budget, k));
            return r;
        }

        static double ErrorIn(double y, double reference, bool pct) => pct ? (y - reference) / Math.Abs(reference) * 100 : y - reference;

        public string Narrative()
        {
            var sb = new StringBuilder();
            if (Error != null) return Error;
            string F(double v) => MeasStats.F(v);
            var f = Fit;
            sb.AppendLine($"{Setup.Y} versus {Setup.X}, n = {N}");
            sb.AppendLine();
            sb.AppendLine("Best average linear TF (least squares):");
            sb.AppendLine($"  y = {F(f.Slope)} * x + {F(f.Intercept)}");
            sb.AppendLine($"  gain   {F(f.Slope)} +/- {F(1.96 * f.SlopeSe)} (95 %)");
            sb.AppendLine($"  offset {F(f.Intercept)} +/- {F(1.96 * f.InterceptSe)} (95 %)" +
                          (Math.Abs(f.Intercept) < 1.96 * f.InterceptSe ? "   -> not significantly different from 0" : "   -> significant"));
            sb.AppendLine($"  R2 = {f.R2:0.000000}, unit-to-unit scatter around the line: sigma = {F(f.ResidualStd)}");
            sb.AppendLine($"Through the origin: y = {F(f.OriginSlope)} * x  (+/- {F(1.96 * f.OriginSlopeSe)}), scatter sigma = {F(f.OriginResidualStd)}");

            if (Theory != null)
            {
                sb.AppendLine();
                sb.AppendLine($"Theoretical TF: y = {Theory.Text}" + (ThLinR2 < 0.999999 ? $"   (not linear in x over the data: R2 of its line {ThLinR2:0.0000})" : ""));
                sb.AppendLine($"  as a line: gain {F(ThGain)}, offset {F(ThOffset)}");
                sb.AppendLine("Error split (measured line vs theory):");
                sb.AppendLine($"  gain error   {GainErrPct:+0.0000;-0.0000} %  (systematic)");
                sb.AppendLine($"  offset error {F(OffsetErr)}  (systematic, units of Y)");
                sb.AppendLine($"  random       sigma {F(RandomStd)} units of Y, or {RandomRelStdPct:0.0000} % of the value  (unit-to-unit)");
                if (Contrib.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"Error budget contributions ({(Setup.BudgetPercent ? "% of theoretical value" : "units of Y")}, random at {Setup.K:0.#} sigma):");
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,12} {1,11} {2,11} {3,11} {4,11} {5,11}", "x", "gain", "offset", "random", "RSS", "linear sum"));
                    foreach (var c in Contrib)
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,12:G5} {1,11:G4} {2,11:G4} {3,11:G4} {4,11:G4} {5,11:G4}", c[0], c[1], c[2], c[3], c[4], c[5]));
                    int worst = Contrib.Select((c, i) => new { c, i }).OrderByDescending(a => a.c[4]).First().i;
                    var w = Contrib[worst];
                    string dom = Math.Abs(w[1]) >= Math.Abs(w[2]) && Math.Abs(w[1]) >= w[3] ? "gain" : Math.Abs(w[2]) >= w[3] ? "offset" : "random scatter";
                    sb.AppendLine($"  Largest contributor at the worst point (x = {F(w[0])}): {dom}.");
                }
            }

            if (Checks.Count > 0)
            {
                sb.AppendLine();
                bool hasBudget = !double.IsNaN(Setup.Budget);
                sb.AppendLine(hasBudget ? $"Error budget: +/-{F(Setup.Budget)} {Unit}   (bound = |mean| + {Setup.K:0.#} sigma)"
                                        : $"No error budget set; showing error spread (bound = |mean| + {Setup.K:0.#} sigma).");
                foreach (var c in Checks)
                {
                    sb.AppendLine($"  {c.Scenario}:");
                    sb.AppendLine($"    error mean {F(c.E.Mean)} {Unit}, sigma {F(c.E.Std)} {Unit}, worst {F(Math.Max(Math.Abs(c.E.Min), Math.Abs(c.E.Max)))} {Unit}, bound {F(c.Bound)} {Unit}");
                    if (hasBudget)
                    {
                        sb.AppendLine($"    inside budget: {c.Inside}/{c.N} = {MeasStats.Pct((double)c.Inside / Math.Max(1, c.N))} (95 % CI {MeasStats.Pct(c.Lo)} .. {MeasStats.Pct(c.Hi)})");
                        bool normal = !double.IsNaN(c.AdP) && c.AdP >= MeasStats.NormalAlpha;
                        sb.AppendLine($"    sigma margin {(double.IsNaN(c.SigmaMargin) ? "-" : c.SigmaMargin.ToString("0.00", CultureInfo.InvariantCulture))} sigma" +
                                      (normal ? "" : "   (errors not normal: margin is indicative only)") +
                                      (c.Bound <= Setup.Budget ? "   -> meets the budget at " + Setup.K.ToString("0.#", CultureInfo.InvariantCulture) + " sigma" : "   -> EXCEEDS the budget at " + Setup.K.ToString("0.#", CultureInfo.InvariantCulture) + " sigma"));
                    }
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>Temperature analysis: per-corner statistics and paired drift, or regression on a random temperature.</summary>
    public static class TempAnalysis
    {
        public sealed class CornerRow
        {
            public double Temp;
            public MeasStats S;
            public Descriptive Drift;            // paired: Y(T) - Y(Tref) per unit
            public Descriptive Tempco;           // paired: ppm/°C per unit
        }

        public static double ReferenceCorner(IList<double> corners) =>
            corners.OrderBy(t => Math.Abs(t - 25)).FirstOrDefault();

        public static List<CornerRow> Corners(ResultSet rs, string meas, double tref)
        {
            var list = new List<CornerRow>();
            int col = rs.Col(meas);
            if (col < 0) return list;
            var spec = rs.Setup.Spec(meas);
            int refIdx = rs.Corners.IndexOf(tref);
            var refByRun = new Dictionary<int, double>();
            if (refIdx >= 0)
                foreach (var r in rs.Rows.Where(r => r.Corner == refIdx && r.Status == RunStatus.Ok && !double.IsNaN(r.V[col])))
                    refByRun[r.Run] = r.V[col];
            for (int c = 0; c < rs.Corners.Count; c++)
            {
                var rows = rs.Rows.Where(r => r.Corner == c && r.Status == RunStatus.Ok && !double.IsNaN(r.V[col])).ToList();
                var cr = new CornerRow { Temp = rs.Corners[c], S = MeasStats.Of(meas, ResultSet.TempLabel(rs.Corners[c]), rows.Select(r => r.V[col]).ToArray(), spec) };
                if (rs.Paired && refIdx >= 0)
                {
                    var drift = new List<double>(); var tc = new List<double>();
                    double dt = rs.Corners[c] - tref;
                    foreach (var r in rows)
                        if (refByRun.TryGetValue(r.Run, out var y0))
                        {
                            drift.Add(r.V[col] - y0);
                            if (dt != 0 && y0 != 0) tc.Add((r.V[col] - y0) / (y0 * dt) * 1e6);
                        }
                    cr.Drift = Descriptive.Of(drift);
                    cr.Tempco = Descriptive.Of(tc);
                }
                list.Add(cr);
            }
            return list;
        }

        /// <summary>Units (runs) that pass every spec at every temperature corner.</summary>
        public static void PassAllTemps(ResultSet rs, out int pass, out int units)
        {
            pass = 0; units = 0;
            foreach (var g in rs.Rows.GroupBy(r => r.Run))
            {
                if (g.Count() < rs.Corners.Count) continue;   // unit not simulated at every corner
                units++;
                if (g.All(r => rs.RowPasses(r) == true)) pass++;
            }
        }

        public sealed class RandomReport
        {
            public string Meas;
            public LinearFit Fit;
            public double MeanY, StdY, StdT, TempcoPpm, TempcoPpmCi, SigmaTemp, SigmaResidual;
            public readonly List<Tuple<double, double, MeasStats>> Bands = new List<Tuple<double, double, MeasStats>>();
        }

        public static RandomReport Random(ResultSet rs, string meas, int bands = 6)
        {
            rs.Pairs(ResultSet.TempColumn, meas, 0, out var t, out var y, out _);
            var r = new RandomReport { Meas = meas, Fit = Stats.Fit(t, y) };
            if (t.Length < 3) return r;
            var dy = Descriptive.Of(y); var dt = Descriptive.Of(t);
            r.MeanY = dy.Mean; r.StdY = dy.Std; r.StdT = dt.Std;
            r.TempcoPpm = r.Fit.Slope / r.MeanY * 1e6;
            r.TempcoPpmCi = 1.96 * r.Fit.SlopeSe / Math.Abs(r.MeanY) * 1e6;
            r.SigmaTemp = Math.Abs(r.Fit.Slope) * r.StdT;
            r.SigmaResidual = r.Fit.ResidualStd;
            double lo = dt.Min, hi = dt.Max;
            var spec = rs.Setup.Spec(meas);
            for (int b = 0; b < bands; b++)
            {
                double a = lo + (hi - lo) * b / bands, z = lo + (hi - lo) * (b + 1) / bands;
                var vals = new List<double>();
                for (int i = 0; i < t.Length; i++) if (t[i] >= a && (t[i] < z || (b == bands - 1 && t[i] <= z))) vals.Add(y[i]);
                r.Bands.Add(Tuple.Create(a, z, MeasStats.Of(meas, $"{a:0.#}..{z:0.#} °C", vals.ToArray(), spec)));
            }
            return r;
        }
    }
}
