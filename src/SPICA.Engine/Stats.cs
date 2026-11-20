using System;
using System.Collections.Generic;
using System.Linq;

namespace Spica.Core
{
    /// <summary>Count, mean, standard deviation and range of one sample (the campaign summary).</summary>
    public sealed class Descriptive
    {
        public int N;
        public double Mean = double.NaN, Std = double.NaN, Min = double.NaN, Max = double.NaN;

        public static Descriptive Of(IEnumerable<double> values)
        {
            var d = new Descriptive();
            var a = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToArray();
            d.N = a.Length;
            if (d.N == 0) return d;
            d.Min = a.Min();
            d.Max = a.Max();
            d.Mean = a.Average();
            d.Std = d.N > 1 ? Math.Sqrt(a.Sum(v => (v - d.Mean) * (v - d.Mean)) / (d.N - 1)) : 0;
            return d;
        }
    }

    public static class Stats
    {
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

        /// <summary>Inverse standard normal CDF (Acklam, refined with one Halley step): SPICA's gaussian draws.</summary>
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
    }
}
