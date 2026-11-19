using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Spica.Core
{
    /// <summary>
    /// Which tolerance makes a result lopsided (one tail reaching further than the other).
    ///
    /// Model, on the standardized draws z_j of the paired campaign: y = a + sum_j g_j(z_j), each g_j a broken line
    /// (see <see cref="Basis"/>), so each tolerance may also bend the result. The draws are independent, so the
    /// third central moment of y (what makes it lopsided) is the sum of the third central moments of the g_j:
    /// that splits the asymmetry seen among the tolerances. An effect is lopsided because the result bends with
    /// that tolerance, or because the tolerance's own draws are lopsided.
    /// The circuit's input (excitation) is fitted with a cubic and left out: its effect is not a tolerance.
    /// What the split leaves out is interactions between tolerances, and noise.
    ///
    /// How sure: the skewness against its standard error, the share of the asymmetry the model accounts for,
    /// and how often the top cause stays on top when the runs are resampled (bootstrap).
    /// </summary>
    public sealed class Asymmetry
    {
        public sealed class Cause
        {
            public string Name;
            public double Share;          // of the asymmetry seen (negative: works against it)
            public bool OwnShape;         // its own draws are lopsided, rather than the result bending with it
            public double Mean, Sd;       // z = (x - Mean) / Sd
            public Func<double, double> Effect;   // its fitted effect on the result, as a function of z
        }

        public string Error;
        public int N;
        public double Skew = double.NaN, SkewSe = double.NaN;
        public int Side;                  // +1: the high side reaches further; -1: the low side
        public bool Significant;          // more than chance alone would give with this many runs
        public double Explained = double.NaN;
        public readonly List<Cause> Causes = new List<Cause>();
        public int Stable = -1, Resamples;
        public string Confidence = "low";
        public readonly HashSet<ResultSet.Row> Tail = new HashSet<ResultSet.Row>();   // the units in the long tail

        // the top cause, for a chart: its value and the result with every other effect removed
        public double[] PlotX = new double[0], PlotY = new double[0];
        public bool[] PlotTail = new bool[0];
        public double PlotA;              // the fitted curve: PlotA + Top.Effect(z)

        // no single tolerance explains it: two acting together (an interaction), when one pair does
        public string PairA, PairB;
        public double PairShare = double.NaN;
        public bool HasPair => PairA != null;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public Cause Top => Causes.Count > 0 ? Causes[0] : null;
        public string SideText => Side > 0 ? "high" : "low";

        /// <summary>Skewed enough to look for a cause.</summary>
        public static bool IsLopsided(Descriptive d) => d != null && d.N >= 20 && Math.Abs(d.Skew) >= 0.3;

        /// <summary>Standard error of the sample skewness of n normal values.</summary>
        public static double SkewError(int n) => n > 3 ? Math.Sqrt(6.0 * (n - 2) / ((n + 1.0) * (n + 3))) : double.NaN;

        public static Asymmetry Of(ResultSet rs, string column, IEnumerable<ResultSet.Row> rows, int resamples = 30)
        {
            var a = new Asymmetry();
            var names = new List<string>(rs.Params);
            if (rs.Mode == TempMode.Random) names.Add(ResultSet.TempColumn);
            var input = new HashSet<string>(Excitation.Columns(rs, out _, out _), StringComparer.OrdinalIgnoreCase);

            var rr = new List<ResultSet.Row>(); var ys = new List<double>(); var xs = new List<double[]>();
            foreach (var r in rows)
            {
                double y = rs.Get(r, column);
                if (double.IsNaN(y) || double.IsInfinity(y)) continue;
                var x = new double[names.Count];
                bool ok = true;
                for (int j = 0; j < x.Length && ok; j++) { x[j] = rs.Get(r, names[j]); ok = !double.IsNaN(x[j]); }
                if (ok) { rr.Add(r); ys.Add(y); xs.Add(x); }
            }
            int n = a.N = ys.Count;
            if (names.Count == 0) { a.Error = "No tolerances recorded (paired samples are needed to find a cause)."; return a; }
            if (n < 20) { a.Error = "Too few runs to find a cause."; return a; }

            // standardized draws; tolerances that do not vary drop out
            var mean = new double[names.Count]; var sd = new double[names.Count];
            var used = new List<int>();
            for (int j = 0; j < names.Count; j++)
            {
                var d = Descriptive.Of(xs.Select(x => x[j]));
                mean[j] = d.Mean; sd[j] = d.Std;
                if (d.Std > 0) used.Add(j);
            }
            var z = used.Select(j => xs.Select(x => (x[j] - mean[j]) / sd[j]).ToArray()).ToList();
            var isInput = used.Select(j => input.Contains(names[j])).ToList();
            var basis = Basis(z, isInput, n);
            int[] all = Enumerable.Range(0, n).ToArray();

            var fit = Fit(basis, ys, all);
            if (fit == null) { a.Error = $"Only {n} runs for {used.Count} tolerances: too few to split the asymmetry."; return a; }

            // what is left once the input's own effect is removed: the result the tolerances spread
            var yr = Rest(fit, basis, ys, isInput, all);
            var dr = Descriptive.Of(yr);
            a.Skew = dr.Skew; a.SkewSe = SkewError(n);
            a.Side = dr.Skew >= 0 ? 1 : -1;
            a.Significant = Math.Abs(dr.Skew) >= 0.3 && Math.Abs(dr.Skew) > 2 * a.SkewSe;

            var k3 = Moments(fit, basis, ys, isInput, all, out double m3);
            if (!(Math.Abs(m3) > 0)) { a.Error = "The result is not lopsided."; return a; }
            for (int u = 0; u < used.Count; u++)
            {
                if (isInput[u]) continue;
                // the straight line closest to the tolerance's effect: lopsided on its own = its draws are lopsided
                var g = all.Select(i => Term(fit, basis, u, i)).ToArray();
                double gm = g.Average(), zz = 0, gz = 0;
                for (int i = 0; i < n; i++) { zz += z[u][i] * z[u][i]; gz += (g[i] - gm) * z[u][i]; }
                double slope = zz > 0 ? gz / zz : 0, lin = Third(z[u].Select(v => slope * v).ToArray());
                int uu = u;
                a.Causes.Add(new Cause
                {
                    Name = names[used[u]], Share = k3[u] / m3, OwnShape = Math.Abs(lin) > 0.5 * Math.Abs(k3[u]),
                    Mean = mean[used[u]], Sd = sd[used[u]],
                    Effect = zv => basis[uu].Select((b, q) => fit.Coef[uu][q] * b.F(zv)).Sum(),
                });
            }
            a.Explained = a.Causes.Sum(c => c.Share);
            a.Causes.Sort((p, q) => q.Share.CompareTo(p.Share));
            int top = a.Top == null ? -1 : used.FindIndex(j => names[j] == a.Top.Name);

            // the tolerances one by one leave most of it: look for two that act together, among the six that
            // spread the result most (each quadrant of the pair gets its own product term)
            if (a.Significant && a.Explained < 0.6)
            {
                var big = Enumerable.Range(0, used.Count).Where(u => !isInput[u])
                    .OrderByDescending(u => { var g = all.Select(i => Term(fit, basis, u, i)).ToArray(); double gm = g.Average(); return g.Sum(v => (v - gm) * (v - gm)); })
                    .Take(6).ToList();
                double best = 0; int ba = -1, bb = -1;
                for (int i1 = 0; i1 < big.Count; i1++)
                    for (int i2 = i1 + 1; i2 < big.Count; i2++)
                    {
                        int u = big[i1], v = big[i2];
                        double gain = PairGain(basis, z[u], z[v], ys, isInput, all, m3);
                        if (gain > best) { best = gain; ba = u; bb = v; }
                    }
                if (best >= 0.3)
                {
                    a.PairA = names[used[ba]]; a.PairB = names[used[bb]]; a.PairShare = best;
                    a.PlotX = xs.Select(x => x[used[ba]]).ToArray();
                    a.PlotY = xs.Select(x => x[used[bb]]).ToArray();
                }
            }

            // the long tail: beyond 2 sigma on the long side (at least the furthest 2.5 %)
            double cut = dr.Mean + a.Side * 2 * dr.Std;
            foreach (int i in all.OrderByDescending(i => a.Side * yr[i]))
                if (a.Side * (yr[i] - cut) > 0 || a.Tail.Count < Math.Max(3, n / 40)) a.Tail.Add(rr[i]); else break;

            if (top >= 0 && !a.HasPair)
            {
                // the result against its top cause, every other effect removed: the bend shows on its own
                a.PlotX = xs.Select(x => x[used[top]]).ToArray();
                a.PlotY = all.Select(i => ys[i] - Predicted(fit, basis, i, skip: top)).ToArray();
                a.PlotA = fit.A;
                a.PlotTail = rr.Select(r => a.Tail.Contains(r)).ToArray();

                // stability: the same split on resampled runs
                long cost = (long)n * fit.Cols * fit.Cols;
                int b = (int)Math.Min(resamples, 3e8 / Math.Max(1, cost));
                if (b >= 10)
                {
                    var rnd = new Random(1);
                    int same = 0;
                    for (int t = 0; t < b; t++)
                    {
                        var idx = new int[n];
                        for (int i = 0; i < n; i++) idx[i] = rnd.Next(n);
                        var f = Fit(basis, ys, idx);
                        if (f == null) continue;
                        var kb = Moments(f, basis, ys, isInput, idx, out double mb);
                        if (!(Math.Abs(mb) > 0)) continue;
                        int best = -1;
                        for (int u = 0; u < used.Count; u++)
                            if (!isInput[u] && (best < 0 || kb[u] / mb > kb[best] / mb)) best = u;
                        a.Resamples++;
                        if (best == top) same++;
                    }
                    if (a.Resamples > 0) a.Stable = same;
                }
            }

            if (a.HasPair) a.PlotTail = rr.Select(r => a.Tail.Contains(r)).ToArray();
            double topShare = a.Top?.Share ?? 0, stable = a.Resamples > 0 ? (double)a.Stable / a.Resamples : double.NaN;
            bool steady = double.IsNaN(stable) || stable >= 0.8, fair = double.IsNaN(stable) || stable >= 0.6;
            a.Confidence = a.HasPair ? (a.PairShare >= 0.6 ? "medium" : "low")
                         : !a.Significant ? "low"
                         : a.Explained >= 0.7 && a.Explained <= 1.4 && topShare >= 0.5 && steady ? "high"
                         : a.Explained >= 0.4 && topShare >= 0.3 && fair ? "medium" : "low";
            return a;
        }

        /// <summary>"R5 (72 %, the result bends with it), then U1.Vos (15 %)", or the pair acting together.</summary>
        public string CauseText(int max = 2)
        {
            if (HasPair) return $"{Short(PairA)} and {Short(PairB)} together ({(100 * PairShare).ToString("0", Inv)} %: neither alone, the tail appears where both are at one end)";
            var c = Causes.Where(x => x.Share >= 0.1).Take(max).ToList();
            if (c.Count == 0) return null;
            string Why(Cause x) => x.OwnShape ? "its own draws are lopsided" : "the result bends with it";
            return string.Join(", then ", c.Select((x, i) => $"{Short(x.Name)} ({(100 * x.Share).ToString("0", Inv)} %" + (i == 0 ? ", " + Why(x) : "") + ")"));
        }

        /// <summary>Why the confidence is what it is, in a few words.</summary>
        public string ConfidenceText()
        {
            var why = new List<string>();
            if (!Significant) why.Add($"the skewness {Skew.ToString("+0.00;-0.00", Inv)} is within what chance gives with {N} runs (±{(2 * SkewSe).ToString("0.00", Inv)})");
            if (!double.IsNaN(Explained)) why.Add($"{(HasPair ? "one by one, " : "")}the tolerances account for {(HasPair ? "only " : "")}{(100 * Math.Max(0, Explained)).ToString("0", Inv)} % of the asymmetry");
            if (HasPair) why.Add("an interaction is read from fewer units than a single cause; check it with the chart");
            else if (Resamples > 0 && Top != null) why.Add($"{Short(Top.Name)} on top in {Stable} of {Resamples} resamples");
            return Confidence + " (" + string.Join("; ", why) + ")";
        }

        /// <summary>"R5 [uniform]" -> "R5".</summary>
        public static string Short(string name) { int i = name.LastIndexOf(" ["); return i > 0 ? name.Substring(0, i) : name; }

        // ------------------------------------------------------------------ the fit

        /// <summary>One function of a standardized draw; the effect of a tolerance is a sum of them.</summary>
        sealed class Fn
        {
            public Func<double, double> F;
            public double[] Col;          // over every run
        }

        /// <summary>
        /// The functions each draw may act through. A tolerance: a straight line plus hinges max(0, z - knot) at
        /// its quantiles, a broken line that follows a bend, a one-sided clipping or a saturation (with fewer runs,
        /// fewer hinges, or a parabola). The input: a cubic. A draw with fewer than five distinct values: a line.
        /// </summary>
        static List<List<Fn>> Basis(List<double[]> z, List<bool> isInput, int n)
        {
            int p = z.Count;
            var bends = new HashSet<int>(Enumerable.Range(0, p).Where(j => z[j].Distinct().Take(5).Count() >= 5));
            int tol = bends.Count(j => !isInput[j]), inp = bends.Count(j => isInput[j]);
            int hinges = new[] { 4, 2, 1, 0 }.First(h => h == 0 || 1 + p + 2 * inp + tol * h + 10 <= n);
            Fn Of(Func<double, double> f, double[] v) => new Fn { F = f, Col = v.Select(f).ToArray() };
            var basis = new List<List<Fn>>();
            for (int j = 0; j < p; j++)
            {
                var v = z[j];
                var fs = new List<Fn> { Of(x => x, v) };
                if (bends.Contains(j))
                {
                    if (isInput[j]) { fs.Add(Of(x => x * x, v)); fs.Add(Of(x => x * x * x, v)); }
                    else if (hinges == 1) fs.Add(Of(x => x * x, v));
                    else if (hinges > 1)
                    {
                        var sorted = v.OrderBy(x => x).ToArray();
                        for (int h = 1; h <= hinges; h++)
                        {
                            double k = Stats.Quantile(sorted, h / (hinges + 1.0));
                            fs.Add(Of(x => Math.Max(0, x - k), v));
                        }
                    }
                }
                basis.Add(fs);
            }
            return basis;
        }

        sealed class Model
        {
            public double A;
            public double[][] Coef;
            public int Cols;
        }

        static Model Fit(List<List<Fn>> basis, List<double> ys, int[] idx)
        {
            int n = idx.Length;
            var cols = new List<double[]> { Enumerable.Repeat(1.0, n).ToArray() };
            foreach (var fs in basis) foreach (var f in fs) cols.Add(idx.Select(i => f.Col[i]).ToArray());
            if (n < cols.Count + 10) return null;
            var b = SensitivityReport.LeastSquares(cols, idx.Select(i => ys[i]).ToArray(), n);
            if (b == null || b.Any(v => double.IsNaN(v) || double.IsInfinity(v))) return null;
            var m = new Model { A = b[0], Coef = new double[basis.Count][], Cols = cols.Count };
            int k = 1;
            for (int j = 0; j < basis.Count; j++)
            {
                m.Coef[j] = new double[basis[j].Count];
                for (int q = 0; q < basis[j].Count; q++) m.Coef[j][q] = b[k++];
            }
            return m;
        }

        static double Term(Model m, List<List<Fn>> basis, int j, int i)
        {
            double s = 0;
            for (int q = 0; q < basis[j].Count; q++) s += m.Coef[j][q] * basis[j][q].Col[i];
            return s;
        }

        static double Predicted(Model m, List<List<Fn>> basis, int i, int skip)
        {
            double s = 0;
            for (int j = 0; j < basis.Count; j++) if (j != skip) s += Term(m, basis, j, i);
            return s;
        }

        /// <summary>The result with the input's fitted effect removed.</summary>
        static double[] Rest(Model m, List<List<Fn>> basis, List<double> ys, List<bool> isInput, int[] idx) =>
            idx.Select(i => ys[i] - Enumerable.Range(0, basis.Count).Where(j => isInput[j]).Sum(j => Term(m, basis, j, i))).ToArray();

        /// <summary>Third central moment of each tolerance's effect, and of the result the tolerances spread (m3).</summary>
        static double[] Moments(Model m, List<List<Fn>> basis, List<double> ys, List<bool> isInput, int[] idx, out double m3)
        {
            var k3 = new double[basis.Count];
            for (int j = 0; j < basis.Count; j++)
                if (!isInput[j]) k3[j] = Third(idx.Select(i => Term(m, basis, j, i)).ToArray());
            m3 = Third(Rest(m, basis, ys, isInput, idx));
            return k3;
        }

        /// <summary>
        /// Share of the asymmetry two tolerances add when they may act together: the model with a product term per
        /// quadrant of the pair, against the same model without them.
        /// </summary>
        static double PairGain(List<List<Fn>> basis, double[] zu, double[] zv, List<double> ys, List<bool> isInput, int[] idx, double m3)
        {
            var extra = new List<Fn>();
            foreach (var su in new[] { 1, -1 })
                foreach (var sv in new[] { 1, -1 })
                    extra.Add(new Fn { Col = zu.Select((x, i) => Math.Max(0, su * x) * Math.Max(0, sv * zv[i])).ToArray() });
            var wide = basis.Select((fs, j) => fs).ToList();
            wide.Add(extra);
            var f = Fit(wide, ys, idx);
            if (f == null) return 0;
            int last = wide.Count - 1;
            var tol = Enumerable.Range(0, basis.Count).Where(j => !isInput[j]).ToList();
            var with = idx.Select(i => tol.Sum(j => Term(f, wide, j, i)) + Term(f, wide, last, i)).ToArray();
            var without = idx.Select(i => tol.Sum(j => Term(f, wide, j, i))).ToArray();
            return (Third(with) - Third(without)) / m3;
        }

        static double Third(double[] a)
        {
            if (a.Length == 0) return 0;
            double mu = a.Average(), s = 0;
            foreach (var x in a) { double e = x - mu; s += e * e * e; }
            return s / a.Length;
        }
    }
}
