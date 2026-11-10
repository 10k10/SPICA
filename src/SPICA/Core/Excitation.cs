using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Spica.Core
{
    /// <summary>
    /// The circuit's input (excitation): random parameters that sweep the operating range (e.g. the input voltage)
    /// rather than tolerances. They obviously drive the outputs, so the sensitivity sets them apart and shares what is
    /// left among the tolerances.
    /// </summary>
    public static class Excitation
    {
        /// <summary>"Vin [uniform]" -> "Vin": parameters are stored by name in the setup, without their kind.</summary>
        public static string Label(string column)
        {
            int b = column.LastIndexOf(" [", StringComparison.Ordinal);
            return b > 0 ? column.Substring(0, b) : column;
        }

        /// <summary>
        /// The input columns of a campaign: as chosen in the setup, or found by SPICA (null setup list): a random parameter
        /// that is the reference x of an error or of the transfer function, or moves with such a measurement (|r| > 0.95).
        /// </summary>
        public static List<string> Columns(ResultSet rs, out bool auto, out string why)
        {
            auto = rs.Setup.Inputs == null;
            why = "";
            var list = new List<string>();
            if (!auto)
            {
                foreach (var p in rs.Params)
                    if (rs.Setup.Inputs.Contains(Label(p), StringComparer.OrdinalIgnoreCase)) list.Add(p);
                why = "chosen";
                return list;
            }
            var refs = rs.Setup.Errors.Select(e => e.X).Concat(new[] { rs.Setup.Tf.X })
                         .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var reasons = new List<string>();
            foreach (var x in refs)
            {
                if (rs.IsParam(x)) { if (!list.Contains(x)) { list.Add(x); reasons.Add($"{Label(x)} is the reference of the error budget"); } continue; }
                if (rs.Col(x) < 0) continue;
                foreach (var p in rs.Params)
                {
                    if (list.Contains(p)) continue;
                    double r = Correlation(rs, p, x);
                    if (Math.Abs(r) > 0.95) { list.Add(p); reasons.Add($"{Label(p)} sets {x} (r = {r.ToString("0.000", CultureInfo.InvariantCulture)})"); }
                }
            }
            why = string.Join("; ", reasons);
            return list;
        }

        public static double Correlation(ResultSet rs, string a, string b)
        {
            double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0; int n = 0;
            foreach (var r in rs.Rows)
            {
                double x = rs.Get(r, a), y = rs.Get(r, b);
                if (double.IsNaN(x) || double.IsNaN(y)) continue;
                sa += x; sb += y; saa += x * x; sbb += y * y; sab += x * y; n++;
            }
            if (n < 3) return double.NaN;
            double cov = sab - sa * sb / n, va = saa - sa * sa / n, vb = sbb - sb * sb / n;
            return va > 0 && vb > 0 ? cov / Math.Sqrt(va * vb) : double.NaN;
        }

        /// <summary>The input bands of the first input for the sensitivity: equal-count, "Vin 0..2", with their row filter.</summary>
        public static List<KeyValuePair<string, Func<ResultSet.Row, bool>>> Bands(ResultSet rs, string input, int count)
        {
            var res = new List<KeyValuePair<string, Func<ResultSet.Row, bool>>>();
            var v = rs.Rows.Select(r => rs.Get(r, input)).Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
            if (v.Count < count * 20) return res;
            for (int b = 0; b < count; b++)
            {
                double lo = v[b * v.Count / count], hi = v[Math.Min(v.Count - 1, (b + 1) * v.Count / count - 1)];
                bool last = b == count - 1;
                double l = lo, h = hi;
                res.Add(new KeyValuePair<string, Func<ResultSet.Row, bool>>(
                    $"{Label(input)} {ErrorBudget.Eng(lo)} .. {ErrorBudget.Eng(hi)}",
                    r => { double x = rs.Get(r, input); return x >= l && (last ? x <= h : x <= h); }));
            }
            return res;
        }
    }

    public sealed partial class SensitivityReport
    {
        /// <summary>The input(s) set apart: their names, the share of the variance they explain together, and why.</summary>
        public readonly List<string> InputNames = new List<string>();
        public double InputShare = double.NaN;
        /// <summary>Sigma of what the input leaves, in the measurement's units.</summary>
        public double RestStd = double.NaN;

        /// <summary>A share as text, with the digits a share near 0 or 100 % needs ("99.99997 %", "0.00003 %").</summary>
        public static string ShareText(double v)
        {
            if (double.IsNaN(v)) return "-";
            var inv = CultureInfo.InvariantCulture;
            double pct = 100 * v, rest = 100 - pct;
            if (pct >= 99.95 && rest > 0) { int d = Math.Min(8, Math.Max(1, (int)Math.Ceiling(-Math.Log10(rest)) + 1)); return pct.ToString("0." + new string('#', d), inv) + " %"; }
            if (pct > 0 && pct < 0.05) { int d = Math.Min(8, Math.Max(1, (int)Math.Ceiling(-Math.Log10(pct)) + 1)); return pct.ToString("0." + new string('#', d), inv) + " %"; }
            return pct.ToString("0.0", inv) + " %";
        }
        public string InputWhy = "";
        public bool InputAuto;
        public bool Conditioned => InputNames.Count > 0 && !double.IsNaN(InputShare);
        /// <summary>A parameter explaining most of the variance that is not marked as the input: maybe it is one.</summary>
        public string Dominant;

        /// <summary>
        /// The regression with the input(s) set apart: y = f(input) + sum over tolerances of (a + b * input) * tolerance.
        /// f is a cubic in each input, so a nonlinear transfer is removed; the products let a tolerance whose effect grows
        /// with the input (a gain error) show its full size even when the input sweeps around zero. A tolerance's share is
        /// the variance of its own terms; ShareRest is that share of what the input leaves.
        /// </summary>
        static void Conditioned_(SensitivityReport rep, List<string> inputs, List<double[]> xs, List<double> ys, List<int> stim)
        {
            int n = ys.Count, p = inputs.Count;
            var dy = Descriptive.Of(ys);
            double[] Z(Func<int, double> f)
            {
                var v = new double[n];
                for (int i = 0; i < n; i++) v[i] = f(i);
                double m = v.Average(), sd = Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / Math.Max(1, n - 1));
                if (!(sd > 1e-15)) return null;
                for (int i = 0; i < n; i++) v[i] = (v[i] - m) / sd;
                return v;
            }
            var z = new double[p][];
            var sdIn = new double[p];
            for (int j = 0; j < p; j++)
            {
                int jj = j;
                var d = Descriptive.Of(xs.Select(x => x[jj]));
                sdIn[j] = d.Std;
                z[j] = Z(i => xs[i][jj]);
            }
            double Var(double[] v) { double m = v.Average(); return v.Sum(x => (x - m) * (x - m)) / Math.Max(1, n - 1); }
            var tol = Enumerable.Range(0, p).Where(j => !stim.Contains(j) && z[j] != null).ToList();
            var st = stim.Where(j => z[j] != null).ToList();
            if (st.Count == 0) return;

            // 1) the input's own effect: least squares of y on a cubic in each input (in y's units, so the small
            //    remainder is computed row by row and keeps its precision even when the input explains 99.9999 %)
            var basis = new List<double[]>(); var basisOf = new List<int>();
            foreach (int s in st)
                for (int pow = 1; pow <= 3; pow++)
                {
                    int ss = s, pp = pow;
                    var c = Z(i => Math.Pow(z[ss][i], pp));
                    if (c != null) { basis.Add(c); basisOf.Add(s); }
                }
            var yc = ys.Select(y => y - dy.Mean).ToArray();
            var b1 = LeastSquares(basis, yc, n);
            if (b1 == null) { rep.Error = "The input's effect could not be fitted."; return; }
            var res = new double[n];
            for (int i = 0; i < n; i++)
            {
                double f = 0;
                for (int c = 0; c < basis.Count; c++) f += b1[c] * basis[c][i];
                res[i] = yc[i] - f;
            }
            double vy = dy.Std * dy.Std, vres = Var(res);
            rep.InputShare = Math.Max(0, Math.Min(1, 1 - vres / vy));
            rep.RestStd = Math.Sqrt(vres);
            if (vres <= 1e-14 * vy)
            {
                // the measurement is the input (or a fixed function of it): what is left is rounding
                rep.InputShare = 1;
                rep.Error = $"{rep.Meas} is set by the input alone: no tolerance moves it (what is left, sigma {ErrorBudget.Eng(rep.RestStd)}, is numerical noise).";
                rep.Items.Clear();
                foreach (int s in st) rep.Items.Add(new Item { Name = inputs[s], IsInput = true, Share = 1, LimitText = "input (excitation)" });
                return;
            }

            // 2) the tolerances on what is left: each tolerance and each tolerance x input
            var cols = new List<double[]>(); var owner = new List<int>(); var inter = new List<int>(); var interSd = new List<double>();
            foreach (int t in tol) { cols.Add(z[t]); owner.Add(t); inter.Add(-1); interSd.Add(1); }
            bool products = n > cols.Count * (1 + st.Count) + 20 && Math.Pow(cols.Count * (1 + st.Count), 2) * n < 6e8;
            if (products)
                foreach (int t in tol)
                    foreach (int s in st)
                    {
                        var raw = new double[n];
                        for (int i = 0; i < n; i++) raw[i] = z[t][i] * z[s][i];
                        double m = raw.Average(), sd = Math.Sqrt(raw.Sum(x => (x - m) * (x - m)) / Math.Max(1, n - 1));
                        if (!(sd > 1e-15)) continue;
                        for (int i = 0; i < n; i++) raw[i] = (raw[i] - m) / sd;
                        cols.Add(raw); owner.Add(t); inter.Add(s); interSd.Add(sd);
                    }
            int k = cols.Count;
            if (!(vres > 0) || k == 0 || n <= k + 2)
            {
                rep.Error = k == 0 ? null : $"Only {n} runs for {k} terms: too few to share what the input leaves.";
                if (k > 0) { rep.InputShare = double.NaN; return; }   // the plain regression instead
            }
            var b2 = k > 0 ? LeastSquares(cols, res, n) : new double[0];
            if (b2 == null) { rep.Error = "The tolerances could not be separated (singular regression)."; rep.InputShare = double.NaN; return; }
            double explained = 0;
            {
                var fit = new double[n];
                for (int u = 0; u < k; u++) for (int i = 0; i < n; i++) fit[i] += b2[u] * cols[u][i];
                explained = vres > 0 ? Var(fit) / vres : 0;
            }
            rep.R2 = rep.InputShare + (1 - rep.InputShare) * explained;

            rep.Items.Clear();
            foreach (int s in st)
            {
                var part = new double[n];
                for (int c = 0; c < basis.Count; c++) if (basisOf[c] == s) for (int i = 0; i < n; i++) part[i] += b1[c] * basis[c][i];
                int lin = basisOf.IndexOf(s);
                var it = new Item { Name = inputs[s], IsInput = true, Share = Math.Min(1, Var(part) / vy), LimitText = "input (excitation)" };
                it.Src = lin >= 0 ? b1[lin] / dy.Std : double.NaN;      // linear term: sigmas of y per sigma of the input
                it.Slope = it.Src * dy.Std / sdIn[s];
                rep.Items.Add(it);
            }
            foreach (int t in tol)
            {
                int main = owner.IndexOf(t);
                var part = new double[n];
                for (int u = 0; u < k; u++) if (owner[u] == t) for (int i = 0; i < n; i++) part[i] += b2[u] * cols[u][i];
                var it = new Item { Name = inputs[t] };
                it.ShareRest = vres > 0 ? Var(part) / vres : double.NaN;
                it.Share = (1 - rep.InputShare) * it.ShareRest;
                it.Src = main >= 0 ? b2[main] / dy.Std : double.NaN;    // at the mid input, in sigmas of y per sigma
                double r = 0, sx = 0, sy = 0, sxx = 0, syy = 0;
                for (int i = 0; i < n; i++) { sx += z[t][i]; sy += res[i]; sxx += z[t][i] * z[t][i]; syy += res[i] * res[i]; r += z[t][i] * res[i]; }
                double cv = r - sx * sy / n, vx = sxx - sx * sx / n, vr = syy - sy * sy / n;
                it.R = vx > 0 && vr > 0 ? cv / Math.Sqrt(vx * vr) : double.NaN;   // with what the input leaves
                // its effect at the input where it is largest (units of y per sigma of the tolerance)
                double g0 = main >= 0 ? b2[main] : 0, best = g0;
                for (int i = 0; i < n; i++)
                {
                    double gi = g0;
                    for (int u = 0; u < k; u++) if (owner[u] == t && inter[u] >= 0) gi += b2[u] * z[inter[u]][i] / interSd[u];
                    if (Math.Abs(gi) > Math.Abs(best)) best = gi;
                }
                it.Slope = g0 / sdIn[t];
                double worstSlope = best / sdIn[t];
                bool grows = Math.Abs(best) > 1.2 * Math.Abs(g0);
                if (inputs[t] == ResultSet.TempColumn)
                {
                    var d = Descriptive.Of(xs.Select(x => x[t]));
                    it.AtLimit = worstSlope * (d.Max - d.Min) / 2; it.LimitText = "half the temperature span";
                }
                else if (ResultSet.IsGaussianParam(inputs[t])) { it.AtLimit = worstSlope * 3; it.LimitText = "3 sigma"; }
                else { it.AtLimit = worstSlope; it.LimitText = "tolerance edge"; }
                if (grows) it.LimitText += ", at the worst input";
                rep.Items.Add(it);
            }
            // the tolerances' own variances add up to what the model explains only on average (sampling): scale them to it
            double sum = rep.Items.Where(i => !i.IsInput && !double.IsNaN(i.ShareRest)).Sum(i => i.ShareRest);
            if (sum > 0 && explained > 0)
                foreach (var it in rep.Items.Where(i => !i.IsInput && !double.IsNaN(i.ShareRest)))
                {
                    it.ShareRest *= explained / sum;
                    it.Share = (1 - rep.InputShare) * it.ShareRest;
                }
            foreach (int j in Enumerable.Range(0, p).Where(j => z[j] == null))
                rep.Items.Add(new Item { Name = inputs[j], IsInput = stim.Contains(j) });
            rep.Items.Sort((u, v) =>
                u.IsInput != v.IsInput ? (u.IsInput ? -1 : 1)
                : (double.IsNaN(v.Share) ? -1 : v.Share).CompareTo(double.IsNaN(u.Share) ? -1 : u.Share));
        }

        /// <summary>Coefficients of y on centered columns (normal equations), or null when singular.</summary>
        static double[] LeastSquares(List<double[]> cols, double[] y, int n)
        {
            int k = cols.Count;
            if (k == 0) return new double[0];
            var a = new double[k, k + 1];
            for (int u = 0; u < k; u++)
            {
                for (int v = u; v < k; v++)
                {
                    double s = 0; var cu = cols[u]; var cv = cols[v];
                    for (int i = 0; i < n; i++) s += cu[i] * cv[i];
                    a[u, v] = a[v, u] = s;
                }
                double sy = 0;
                for (int i = 0; i < n; i++) sy += cols[u][i] * y[i];
                a[u, k] = sy;
            }
            return Solve(a, k);
        }
    }
}
