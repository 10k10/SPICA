using System;
using System.Collections.Generic;
using System.Linq;

namespace Spica.Core
{
    /// <summary>Descriptive statistics of one sample.</summary>
    public sealed class Descriptive
    {
        public int N;
        public double Mean = double.NaN, Std = double.NaN, Min = double.NaN, Max = double.NaN, Median = double.NaN;
        public double Skew = double.NaN, ExKurt = double.NaN;
        public double[] Sorted = new double[0];

        public static Descriptive Of(IEnumerable<double> values)
        {
            var d = new Descriptive();
            var a = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToArray();
            Array.Sort(a);
            d.Sorted = a;
            d.N = a.Length;
            if (d.N == 0) return d;
            d.Min = a[0];
            d.Max = a[a.Length - 1];
            d.Mean = a.Average();
            d.Median = Stats.Quantile(a, 0.5);
            if (d.N > 1)
            {
                double m2 = 0, m3 = 0, m4 = 0;
                foreach (var v in a) { double e = v - d.Mean, e2 = e * e; m2 += e2; m3 += e2 * e; m4 += e2 * e2; }
                d.Std = Math.Sqrt(m2 / (d.N - 1));
                double pm2 = m2 / d.N;
                if (pm2 > 0)
                {
                    d.Skew = (m3 / d.N) / Math.Pow(pm2, 1.5);
                    d.ExKurt = (m4 / d.N) / (pm2 * pm2) - 3;
                }
            }
            else d.Std = 0;
            return d;
        }

        public double Quantile(double p) => Stats.Quantile(Sorted, p);
    }

    /// <summary>Result of checking one sample against spec limits.</summary>
    public sealed class Capability
    {
        public int N, Pass;
        public double Yield = double.NaN, YieldLo = double.NaN, YieldHi = double.NaN;   // observed, with 95 % CI
        public double Cp = double.NaN, Cpk = double.NaN;
        public double SigmaToLimit = double.NaN;      // distance from the mean to the nearest limit, in sigmas (= 3 Cpk)
        public double PredictedYield = double.NaN;    // from the normal fit
        public double PredictedPpm = double.NaN;
    }

    public sealed class LinearFit
    {
        public int N;
        public double Slope = double.NaN, Intercept = double.NaN, SlopeSe = double.NaN, InterceptSe = double.NaN;
        public double R2 = double.NaN, ResidualStd = double.NaN;
        public double OriginSlope = double.NaN, OriginSlopeSe = double.NaN, OriginResidualStd = double.NaN;
        public double MeanX, SxX;
        public double Predict(double x) => Slope * x + Intercept;
    }

    public static class Stats
    {
        // ------------------------------------------------------------------ quantiles

        /// <summary>Linear-interpolation quantile (type 7) of an ascending array.</summary>
        public static double Quantile(double[] sorted, double p)
        {
            int n = sorted.Length;
            if (n == 0) return double.NaN;
            if (n == 1) return sorted[0];
            double h = (n - 1) * Math.Max(0, Math.Min(1, p));
            int lo = (int)Math.Floor(h);
            int hi = Math.Min(n - 1, lo + 1);
            return sorted[lo] + (h - lo) * (sorted[hi] - sorted[lo]);
        }

        // ------------------------------------------------------------------ normal distribution

        /// <summary>Complementary error function, fractional error below 1.2e-7 everywhere (tails included).</summary>
        public static double Erfc(double x)
        {
            double z = Math.Abs(x);
            double t = 1.0 / (1.0 + 0.5 * z);
            double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 +
                       t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 +
                       t * (-0.82215223 + t * 0.17087277)))))))));
            return x >= 0 ? r : 2.0 - r;
        }

        public static double NormalCdf(double z) => 0.5 * Erfc(-z / Math.Sqrt(2));

        /// <summary>Inverse standard normal CDF (Acklam, refined with one Halley step).</summary>
        public static double NormalInv(double p)
        {
            if (p <= 0) return double.NegativeInfinity;
            if (p >= 1) return double.PositiveInfinity;
            double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
            double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
            double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
            double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
            const double plow = 0.02425, phigh = 1 - plow;
            double x;
            if (p < plow)
            {
                double q = Math.Sqrt(-2 * Math.Log(p));
                x = (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
            }
            else if (p <= phigh)
            {
                double q = p - 0.5, r = q * q;
                x = (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
            }
            else
            {
                double q = Math.Sqrt(-2 * Math.Log(1 - p));
                x = -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
            }
            double e = NormalCdf(x) - p;
            double u = e * Math.Sqrt(2 * Math.PI) * Math.Exp(x * x / 2);
            return x - u / (1 + x * u / 2);
        }

        public static double NormalPdf(double z) => Math.Exp(-0.5 * z * z) / Math.Sqrt(2 * Math.PI);

        // ------------------------------------------------------------------ normality

        /// <summary>Anderson-Darling test for normality (mean and sigma estimated). Returns A*² and its p-value.</summary>
        public static void AndersonDarling(Descriptive d, out double a2, out double p)
        {
            a2 = double.NaN; p = double.NaN;
            int n = d.N;
            if (n < 8 || !(d.Std > 0)) return;
            var x = d.Sorted;
            double s = 0;
            for (int i = 0; i < n; i++)
            {
                double fi = Clamp01(NormalCdf((x[i] - d.Mean) / d.Std));
                double fj = Clamp01(NormalCdf((x[n - 1 - i] - d.Mean) / d.Std));
                s += (2 * i + 1) * (Math.Log(fi) + Math.Log(1 - fj));
            }
            double A = -n - s / n;
            A *= 1 + 0.75 / n + 2.25 / ((double)n * n);
            a2 = A;
            if (A >= 0.6) p = Math.Exp(1.2937 - 5.709 * A + 0.0186 * A * A);
            else if (A >= 0.34) p = Math.Exp(0.9177 - 4.279 * A - 1.38 * A * A);
            else if (A >= 0.2) p = 1 - Math.Exp(-8.318 + 42.796 * A - 59.938 * A * A);
            else p = 1 - Math.Exp(-13.436 + 101.14 * A - 223.73 * A * A);
            p = Math.Max(0, Math.Min(1, p));
        }

        static double Clamp01(double v) => Math.Min(1 - 1e-15, Math.Max(1e-300, v));

        // ------------------------------------------------------------------ yield

        /// <summary>Exact (Clopper-Pearson) confidence interval of a binomial proportion.</summary>
        public static void ClopperPearson(int k, int n, double conf, out double lo, out double hi)
        {
            lo = double.NaN; hi = double.NaN;
            if (n <= 0) return;
            double alpha = 1 - conf;
            lo = k == 0 ? 0 : BetaInv(alpha / 2, k, n - k + 1);
            hi = k == n ? 1 : BetaInv(1 - alpha / 2, k + 1, n - k);
        }

        public static Capability Capability(Descriptive d, SpecLimit spec)
        {
            var c = new Capability { N = d.N };
            if (spec == null || !spec.IsSet || d.N == 0) return c;
            c.Pass = d.Sorted.Count(spec.Passes);
            c.Yield = (double)c.Pass / d.N;
            ClopperPearson(c.Pass, d.N, 0.95, out c.YieldLo, out c.YieldHi);
            if (d.Std > 0)
            {
                bool lo = !double.IsNaN(spec.Lsl), hi = !double.IsNaN(spec.Usl);
                double zl = lo ? (d.Mean - spec.Lsl) / d.Std : double.PositiveInfinity;
                double zu = hi ? (spec.Usl - d.Mean) / d.Std : double.PositiveInfinity;
                if (lo && hi) c.Cp = (spec.Usl - spec.Lsl) / (6 * d.Std);
                c.SigmaToLimit = Math.Min(zl, zu);
                c.Cpk = c.SigmaToLimit / 3;
                double fail = (lo ? NormalCdf(-zl) : 0) + (hi ? NormalCdf(-zu) : 0);
                c.PredictedYield = 1 - fail;
                c.PredictedPpm = fail * 1e6;
            }
            return c;
        }

        /// <summary>Yield "sigma level" quoted the usual way: the z of the observed failure fraction.</summary>
        public static double YieldToSigma(double yield) => yield <= 0 ? double.NegativeInfinity : yield >= 1 ? double.PositiveInfinity : NormalInv(yield);

        // ------------------------------------------------------------------ regression

        public static LinearFit Fit(IList<double> x, IList<double> y)
        {
            var f = new LinearFit();
            int n = Math.Min(x.Count, y.Count);
            double sx = 0, sy = 0;
            int m = 0;
            for (int i = 0; i < n; i++) if (Ok(x[i]) && Ok(y[i])) { sx += x[i]; sy += y[i]; m++; }
            f.N = m;
            if (m < 3) return f;
            double mx = sx / m, my = sy / m, sxx = 0, sxy = 0, syy = 0, x2 = 0, xy = 0;
            for (int i = 0; i < n; i++)
            {
                if (!Ok(x[i]) || !Ok(y[i])) continue;
                double dx = x[i] - mx, dy = y[i] - my;
                sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
                x2 += x[i] * x[i]; xy += x[i] * y[i];
            }
            f.MeanX = mx; f.SxX = sxx;
            if (sxx > 0)
            {
                f.Slope = sxy / sxx;
                f.Intercept = my - f.Slope * mx;
                double sse = Math.Max(0, syy - f.Slope * sxy);
                f.ResidualStd = Math.Sqrt(sse / (m - 2));
                f.SlopeSe = f.ResidualStd / Math.Sqrt(sxx);
                f.InterceptSe = f.ResidualStd * Math.Sqrt(1.0 / m + mx * mx / sxx);
                f.R2 = syy > 0 ? 1 - sse / syy : 1;
            }
            if (x2 > 0)
            {
                f.OriginSlope = xy / x2;
                double sse0 = 0;
                for (int i = 0; i < n; i++)
                    if (Ok(x[i]) && Ok(y[i])) { double r = y[i] - f.OriginSlope * x[i]; sse0 += r * r; }
                f.OriginResidualStd = Math.Sqrt(sse0 / (m - 1));
                f.OriginSlopeSe = f.OriginResidualStd / Math.Sqrt(x2);
            }
            return f;
        }

        static bool Ok(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        // ------------------------------------------------------------------ histogram

        /// <summary>Bin edges by the Freedman-Diaconis rule, clamped to 8..80 bins.</summary>
        public static double[] HistogramEdges(Descriptive d)
        {
            if (d.N == 0) return new double[0];
            double lo = d.Min, hi = d.Max;
            if (!(hi > lo)) { lo -= 0.5; hi += 0.5; }
            double iqr = d.Quantile(0.75) - d.Quantile(0.25);
            int bins = iqr > 0 ? (int)Math.Ceiling((hi - lo) / (2 * iqr / Math.Pow(d.N, 1.0 / 3))) : (int)Math.Ceiling(Math.Sqrt(d.N));
            bins = Math.Max(8, Math.Min(80, bins));
            var e = new double[bins + 1];
            for (int i = 0; i <= bins; i++) e[i] = lo + (hi - lo) * i / bins;
            return e;
        }

        public static int[] Histogram(double[] sorted, double[] edges)
        {
            int bins = Math.Max(0, edges.Length - 1);
            var counts = new int[bins];
            if (bins == 0) return counts;
            double lo = edges[0], w = (edges[bins] - lo) / bins;
            foreach (var v in sorted)
            {
                int b = (int)((v - lo) / w);
                if (b < 0) b = 0;
                if (b >= bins) b = bins - 1;
                counts[b]++;
            }
            return counts;
        }

        // ------------------------------------------------------------------ incomplete beta

        static double LogGamma(double x)
        {
            double[] c = { 76.18009172947146, -86.50532032941677, 24.01409824083091, -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5 };
            double y = x, tmp = x + 5.5;
            tmp -= (x + 0.5) * Math.Log(tmp);
            double ser = 1.000000000190015;
            for (int j = 0; j < 6; j++) ser += c[j] / ++y;
            return -tmp + Math.Log(2.5066282746310005 * ser / x);
        }

        static double BetaCf(double a, double b, double x)
        {
            const int MaxIt = 400;
            const double Eps = 3e-14, Fpmin = 1e-300;
            double qab = a + b, qap = a + 1, qam = a - 1, c = 1, d = 1 - qab * x / qap;
            if (Math.Abs(d) < Fpmin) d = Fpmin;
            d = 1 / d;
            double h = d;
            for (int m = 1; m <= MaxIt; m++)
            {
                int m2 = 2 * m;
                double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
                d = 1 + aa * d; if (Math.Abs(d) < Fpmin) d = Fpmin;
                c = 1 + aa / c; if (Math.Abs(c) < Fpmin) c = Fpmin;
                d = 1 / d; h *= d * c;
                aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
                d = 1 + aa * d; if (Math.Abs(d) < Fpmin) d = Fpmin;
                c = 1 + aa / c; if (Math.Abs(c) < Fpmin) c = Fpmin;
                d = 1 / d;
                double del = d * c;
                h *= del;
                if (Math.Abs(del - 1) < Eps) break;
            }
            return h;
        }

        /// <summary>Regularized incomplete beta function I_x(a, b).</summary>
        public static double BetaI(double a, double b, double x)
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            double bt = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x));
            return x < (a + 1) / (a + b + 2) ? bt * BetaCf(a, b, x) / a : 1 - bt * BetaCf(b, a, 1 - x) / b;
        }

        static double BetaInv(double p, double a, double b)
        {
            double lo = 0, hi = 1;
            for (int i = 0; i < 200; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (BetaI(a, b, mid) < p) lo = mid; else hi = mid;
                if (hi - lo < 1e-15) break;
            }
            return 0.5 * (lo + hi);
        }
    }
}
