using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Spica.Core
{
    /// <summary>
    /// Which inputs drive a measurement.
    ///
    /// Parameters (paired campaigns): a multiple linear regression of the measurement on every random
    /// parameter (and on temperature for a random-temperature campaign), on standardized values. The
    /// standardized coefficient (SRC) of an input is the shift of the measurement, in its own sigmas, for
    /// one sigma of that input. The draws are independent by construction, so SRC^2 is the share of the
    /// variance that input explains and the shares add up to R^2; what R^2 leaves out is nonlinearity,
    /// interactions, and random values LTspice draws inside .subckt definitions (invisible to SPICA).
    ///
    /// Measurements: the Pearson correlation with every other measurement (no causality implied).
    /// </summary>
    public sealed class SensitivityReport
    {
        public sealed class Item
        {
            public string Name;
            public double R = double.NaN;        // Pearson correlation with the measurement
            public double Src = double.NaN;      // standardized regression coefficient
            public double Share = double.NaN;    // fraction of the variance: SRC^2 (parameters) or r^2 (measurements)
            public double Slope = double.NaN;    // measurement units per input unit
            public double AtLimit = double.NaN;  // shift of the measurement with the input at its tolerance limit
            public string LimitText = "";
        }

        public string Meas, Error;
        public bool OnParameters;
        public int N;
        public double R2 = double.NaN, MeanY = double.NaN, StdY = double.NaN;
        public readonly List<Item> Items = new List<Item>();   // most influential first

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Regression on the random parameters (and TEMP when it varies per run).</summary>
        public static SensitivityReport Parameters(ResultSet rs, string meas, int group)
        {
            var rep = new SensitivityReport { Meas = meas, OnParameters = true };
            var inputs = new List<string>(rs.Params);
            if (rs.Mode == TempMode.Random) inputs.Add(ResultSet.TempColumn);
            if (inputs.Count == 0)
            {
                rep.Error = "No random parameters to analyse: SPICA sees the draws only with paired samples (Setup tab).";
                return rep;
            }

            // complete rows only
            var xs = new List<double[]>(); var ys = new List<double>();
            foreach (var r in rs.InGroup(group))
            {
                double y = rs.Get(r, meas);
                if (double.IsNaN(y)) continue;
                var x = new double[inputs.Count];
                bool ok = true;
                for (int j = 0; j < x.Length && ok; j++) { x[j] = rs.Get(r, inputs[j]); ok = !double.IsNaN(x[j]); }
                if (ok) { xs.Add(x); ys.Add(y); }
            }
            int n = ys.Count;
            rep.N = n;
            var dy = Descriptive.Of(ys);
            rep.MeanY = dy.Mean; rep.StdY = dy.Std;
            if (n < 5) { rep.Error = "Not enough runs with data."; return rep; }
            if (!(dy.Std > 0)) { rep.Error = $"{meas} does not vary, so nothing drives it."; return rep; }

            // standardize; inputs that do not vary drop out of the regression
            int p = inputs.Count;
            var mean = new double[p]; var sd = new double[p];
            for (int j = 0; j < p; j++)
            {
                var d = Descriptive.Of(xs.Select(x => x[j]));
                mean[j] = d.Mean; sd[j] = d.Std;
            }
            var use = Enumerable.Range(0, p).Where(j => sd[j] > 0).ToList();
            var rxy = new double[p];
            for (int j = 0; j < p; j++)
            {
                if (!(sd[j] > 0)) continue;
                double s = 0;
                for (int i = 0; i < n; i++) s += (xs[i][j] - mean[j]) / sd[j] * (ys[i] - dy.Mean) / dy.Std;
                rxy[j] = s / (n - 1);
            }

            double[] beta = new double[p];
            if (use.Count > 0 && n > use.Count + 2)
            {
                int k = use.Count;
                var a = new double[k, k + 1];
                for (int u = 0; u < k; u++)
                {
                    for (int v = u; v < k; v++)
                    {
                        int ju = use[u], jv = use[v];
                        double s = 0;
                        for (int i = 0; i < n; i++) s += (xs[i][ju] - mean[ju]) / sd[ju] * (xs[i][jv] - mean[jv]) / sd[jv];
                        a[u, v] = a[v, u] = s / (n - 1);
                    }
                    a[u, k] = rxy[use[u]];
                }
                var b = Solve(a, k);
                if (b != null) for (int u = 0; u < k; u++) beta[use[u]] = b[u];
                else use.ForEach(j => beta[j] = rxy[j]);      // singular: fall back to the correlations
            }
            else
            {
                rep.Error = $"Only {n} runs for {use.Count} varying inputs: showing correlations; the regression needs more runs than inputs.";
                use.ForEach(j => beta[j] = rxy[j]);
            }
            rep.R2 = use.Sum(j => beta[j] * rxy[j]);

            foreach (int j in Enumerable.Range(0, p))
            {
                var it = new Item { Name = inputs[j], R = sd[j] > 0 ? rxy[j] : double.NaN, Src = sd[j] > 0 ? beta[j] : double.NaN };
                it.Share = sd[j] > 0 ? beta[j] * beta[j] : double.NaN;
                it.Slope = sd[j] > 0 ? beta[j] * dy.Std / sd[j] : double.NaN;
                if (inputs[j] == ResultSet.TempColumn)
                {
                    var d = Descriptive.Of(xs.Select(x => x[j]));
                    it.AtLimit = it.Slope * (d.Max - d.Min) / 2;
                    it.LimitText = "half the temperature span";
                }
                else if (ResultSet.IsGaussianParam(inputs[j])) { it.AtLimit = it.Slope * 3; it.LimitText = "3 sigma"; }
                else { it.AtLimit = it.Slope; it.LimitText = "tolerance edge"; }
                rep.Items.Add(it);
            }
            rep.Items.Sort((u, v) => (double.IsNaN(v.Share) ? -1 : v.Share).CompareTo(double.IsNaN(u.Share) ? -1 : u.Share));
            return rep;
        }

        /// <summary>Correlation of a measurement with every other measurement.</summary>
        public static SensitivityReport Measurements(ResultSet rs, string meas, int group)
        {
            var rep = new SensitivityReport { Meas = meas };
            foreach (var other in rs.Meas)
            {
                if (other.Equals(meas, StringComparison.OrdinalIgnoreCase)) continue;
                rs.Pairs(other, meas, group, out var x, out var y, out _);
                var dx = Descriptive.Of(x); var dy = Descriptive.Of(y);
                var it = new Item { Name = other };
                if (x.Length > 2 && dx.Std > 0 && dy.Std > 0)
                {
                    double s = 0;
                    for (int i = 0; i < x.Length; i++) s += (x[i] - dx.Mean) * (y[i] - dy.Mean);
                    it.R = s / ((x.Length - 1) * dx.Std * dy.Std);
                    it.Share = it.R * it.R;
                    it.Src = it.R;
                    it.Slope = it.R * dy.Std / dx.Std;
                }
                rep.Items.Add(it);
                rep.N = Math.Max(rep.N, x.Length);
            }
            var d = Descriptive.Of(rs.Values(meas, group));
            rep.MeanY = d.Mean; rep.StdY = d.Std;
            if (rep.Items.Count == 0) rep.Error = "There is only one measurement.";
            else if (!(d.Std > 0)) rep.Error = $"{meas} does not vary.";
            rep.Items.Sort((u, v) => (double.IsNaN(v.Share) ? -1 : v.Share).CompareTo(double.IsNaN(u.Share) ? -1 : u.Share));
            return rep;
        }

        /// <summary>Gaussian elimination with partial pivoting on an augmented k x (k+1) matrix.</summary>
        static double[] Solve(double[,] a, int k)
        {
            for (int c = 0; c < k; c++)
            {
                int piv = c;
                for (int r = c + 1; r < k; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
                if (Math.Abs(a[piv, c]) < 1e-12) return null;
                if (piv != c) for (int j = c; j <= k; j++) { var t = a[c, j]; a[c, j] = a[piv, j]; a[piv, j] = t; }
                for (int r = c + 1; r < k; r++)
                {
                    double f = a[r, c] / a[c, c];
                    for (int j = c; j <= k; j++) a[r, j] -= f * a[c, j];
                }
            }
            var x = new double[k];
            for (int r = k - 1; r >= 0; r--)
            {
                double s = a[r, k];
                for (int j = r + 1; j < k; j++) s -= a[r, j] * x[j];
                x[r] = s / a[r, r];
            }
            return x;
        }

        public string Narrative(int top = 8)
        {
            var sb = new StringBuilder();
            string F(double v) => MeasStats.F(v);
            string Pct(double v) => double.IsNaN(v) ? "-" : (100 * v).ToString("0.0", Inv) + " %";
            if (OnParameters)
            {
                sb.AppendLine($"{Meas}: n = {N}, sigma = {F(StdY)}");
                if (Error != null) { sb.AppendLine(Error); if (Items.Count == 0) return sb.ToString(); }
                sb.AppendLine($"A linear model on {Items.Count(i => !double.IsNaN(i.Share))} inputs explains R2 = {Pct(R2)}");
                sb.AppendLine("of the variance.");
                sb.AppendLine();
                sb.AppendLine("Share of the variance:");
                foreach (var it in Items.Where(i => !double.IsNaN(i.Share)).Take(top))
                    sb.AppendLine($"  {Pct(it.Share),7}  {it.Name}");
                int rest = Items.Count(i => !double.IsNaN(i.Share)) - top;
                if (rest > 0) sb.AppendLine($"  ... {rest} more in the table");
                sb.AppendLine();
                if (R2 > 0.95)
                    sb.AppendLine($"Close to linear: the remaining {Pct(1 - R2)} is\nnonlinearity, interactions and noise.");
                else
                    sb.AppendLine($"{Pct(1 - R2)} is not explained linearly: nonlinear or\ninteracting inputs, or random values LTspice draws\n" +
                                  "inside .subckt definitions (SPICA cannot see those).\nDouble-click an input to see its scatter plot.");
                sb.AppendLine();
                sb.AppendLine($"The table gives the shift of {Meas} with each input\nat its limit. Uniform inputs run -1 .. 1 across their\ntolerance; gauss inputs are in sigmas.");
            }
            else
            {
                sb.AppendLine($"{Meas}: correlation with the other measurements, n = {N}");
                if (Error != null) { sb.AppendLine(Error); return sb.ToString(); }
                foreach (var it in Items.Take(top))
                    sb.AppendLine($"  r = {(double.IsNaN(it.R) ? "   -  " : it.R.ToString("+0.000;-0.000", Inv))}   {it.Name}");
                sb.AppendLine();
                sb.AppendLine("|r| near 1: the two move together (shared cause, or one sets the other).");
                sb.AppendLine("Correlation is not causation: use paired samples for parameter sensitivity.");
            }
            return sb.ToString();
        }
    }
}
