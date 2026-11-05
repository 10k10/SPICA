using System;
using System.Collections.Generic;
using Spica.Core;

namespace Spica.UI
{
    /// <summary>
    /// Hover texts for the result tables: what a column means for the design (header), and what one value
    /// means for one measurement (cell). Written for the circuit designer, not for a statistician.
    /// </summary>
    public static class Glossary
    {
        static readonly Dictionary<string, string> Columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Measurement"] = "The .meas result. Hover any cell for what its value means for this measurement.",
            ["n"] = "Runs (units) with a value.\n" +
                    "Precision grows with the square root of n: sigma is known to about\n" +
                    "+/-10 % with 200 runs, +/-5 % with 770, +/-3 % with 2100 (95 % confidence).",
            ["Mean"] = "Average over all simulated units.\n" +
                       "If it differs from the nominal (no-tolerance) value, the circuit is nonlinear\n" +
                       "in its tolerances, or the tolerances are not centred.",
            ["Std dev"] = "Sigma: the typical unit-to-unit spread caused by the tolerances.\n" +
                          "For a normal (bell-shaped) result: 68 % of units within mean +/- 1 sigma,\n" +
                          "99.73 % within +/- 3 sigma. Check 'Normal p' before relying on that.",
            ["Min"] = "Lowest value seen in these runs. NOT a worst case: more runs find lower values.",
            ["Max"] = "Highest value seen in these runs. NOT a worst case: more runs find higher values.",
            ["Median"] = "Half of the units are below, half above.\n" +
                         "Far from the mean = the distribution is lopsided (see Skew).",
            ["P0.135%"] = "Value that only 0.135 % of units fall below.\n" +
                          "Same coverage as mean - 3 sigma, but read from the data, with no normal assumption:\n" +
                          "use it as your lower '3 sigma' value when the result is not normal.\n" +
                          "Needs about 740+ runs; with fewer it is simply the minimum.",
            ["P99.865%"] = "Value that only 0.135 % of units exceed.\n" +
                           "Same coverage as mean + 3 sigma, read from the data with no normal assumption:\n" +
                           "use it as your upper '3 sigma' value when the result is not normal.\n" +
                           "Needs about 740+ runs; with fewer it is simply the maximum.",
            ["Skew"] = "Asymmetry. 0 = symmetric.\n" +
                       "> 0: the high side has the longer tail;  < 0: the low side does.\n" +
                       "Above about 0.5 (either sign), mean +/- 3 sigma misjudges the long side:\n" +
                       "check that spec with the percentile. Typical cause: a nonlinear transfer\n" +
                       "(1/x, x^2, exponential, a ratio of toleranced parts).",
            ["Ex.kurt"] = "Tail weight compared with a normal curve. 0 = normal.\n" +
                          "Near -1.2: flat and bounded, like one dominant flat() tolerance;\n" +
                          "  +/- 3 sigma then over-estimates the extremes (safe side).\n" +
                          "> 0: heavier tails, more outliers than a normal curve predicts;\n" +
                          "  +/- 3 sigma, Cpk and ppm are then OPTIMISTIC.",
            ["Normal p"] = "Can the result be treated as a normal (bell) curve?\n" +
                           "p >= 0.05 (green): yes, so mean +/- k sigma, Cpk and predicted ppm are usable.\n" +
                           "p < 0.05 (red): no, so use the percentiles and the observed yield instead.\n" +
                           "With thousands of runs even harmless departures give a low p: then judge\n" +
                           "by Skew and Ex.kurt. (Anderson-Darling test.)",
            ["LSL"] = "Lower spec limit: your requirement. Set it in 'Spec limits' below.",
            ["USL"] = "Upper spec limit: your requirement. Set it in 'Spec limits' below.",
            ["Yield"] = "Share of the simulated units inside the spec limits, counted directly.",
            ["Yield 95% CI"] = "Range that holds the design's true yield with 95 % confidence, given this many runs.\n" +
                               "No failures in n runs only proves a yield above about 1 - 3/n\n" +
                               "(300 runs: > 99 %; 3000 runs: > 99.9 %).",
            ["Cpk"] = "Distance from the mean to the NEAREST spec limit, in units of 3 sigma.\n" +
                      "1.00 = limit at 3 sigma (about 1350 ppm out on that side)\n" +
                      "1.33 = 4 sigma (about 32 ppm)   1.67 = 5 sigma   2.00 = 6 sigma\n" +
                      "Green >= 1.33, olive >= 1, red < 1. Trust it only when 'Normal p' is green.",
            ["Pred. ppm"] = "Parts per million expected outside spec, extrapolated from the normal curve.\n" +
                            "It estimates failure rates far below what the runs can show directly.\n" +
                            "Shown only when the result is normal.",

            // temperature
            ["Temp °C"] = "Temperature corner.",
            ["Band °C"] = "Temperature band (random-temperature campaigns are grouped into bands).",
            ["Mean drift"] = "Average change of each unit from the reference temperature (paired samples:\n" +
                             "the same unit compared with itself).",
            ["Drift sigma"] = "How much the drift differs from unit to unit.",
            ["Max |drift|"] = "Largest drift of any single unit seen in these runs.",
            ["Tempco ppm/°C"] = "Average temperature coefficient: drift relative to the reference value,\n" +
                                "in parts per million per °C.",
            ["Tempco sigma"] = "Unit-to-unit spread of the temperature coefficient.",

            // sensitivity
            ["#"] = "Rank: the input with the largest share of the variance first.",
            ["Input"] = "A random parameter (an mc()/flat()/gauss() in the schematic), TEMP,\n" +
                        "or another measurement.",
            ["Share of variance"] = "How much of the measurement's spread this input causes.\n" +
                                    "The shares add up to R2. Tightening the top inputs reduces the spread most;\n" +
                                    "inputs with a tiny share can use cheaper parts.",
            ["Signed effect (SRC)"] = "Shift of the measurement, in its own sigmas, for one sigma of this input.\n" +
                                      "The sign is the direction: + means the measurement rises with the input.\n" +
                                      "This is the direction a worst-case corner pushes the input.",
            ["Correlation r"] = "-1 .. 1: how closely the measurement follows this input alone.",
            ["Slope (per unit)"] = "Change of the measurement per unit of the input (a local derivative).",
            ["Shift at limit"] = "Change of the measurement when this input sits at its tolerance limit and\n" +
                                 "everything else is nominal: its contribution to a worst-case stack-up.",
            ["Limit"] = "The tolerance limit used for 'Shift at limit'.",
        };

        public static string Column(string header) => header != null && Columns.TryGetValue(header, out var t) ? t : null;

        static string F(double v) => MeasStats.F(v);

        /// <summary>Statistics tab: what one value means for this measurement. Columns as in the Statistics table.</summary>
        public static string StatCell(MeasStats s, int col)
        {
            var d = s.D;
            bool spec = s.Spec != null && s.Spec.IsSet;
            if (d.N < 2) return "Not enough runs with data.";
            switch (col)
            {
                case 1:
                    return $"{d.N} runs. Mean known to +/-{F(1.96 * d.Std / Math.Sqrt(d.N))}, sigma to +/-{100 * 1.96 / Math.Sqrt(2.0 * (d.N - 1)):0.#} % (95 %).";
                case 2:
                    return $"Average {s.Meas} = {F(d.Mean)} (known to +/-{F(1.96 * d.Std / Math.Sqrt(d.N))} with {d.N} runs).";
                case 3:
                    return $"Sigma = {F(d.Std)}" + (d.Mean != 0 ? $" ({100 * d.Std / Math.Abs(d.Mean):0.###} % of the mean)" : "") + ".\n" +
                           (s.NormalOk ? $"Normal result: 99.73 % of units within {F(d.Mean - 3 * d.Std)} .. {F(d.Mean + 3 * d.Std)}."
                                       : $"Not normal: read the 3-sigma-equivalent range from the percentiles:\n{F(s.P00135)} .. {F(s.P99865)}.");
                case 4:
                case 5:
                    return $"Range seen in {d.N} runs: {F(d.Min)} .. {F(d.Max)}.\nMore runs will widen it; it is not a worst-case bound.";
                case 6:
                    return d.Std > 0 && Math.Abs(d.Median - d.Mean) > 0.1 * d.Std
                        ? $"Median {F(d.Median)} is {Math.Abs(d.Median - d.Mean) / d.Std:0.00} sigma {(d.Median < d.Mean ? "below" : "above")} the mean: the result is lopsided."
                        : $"Median {F(d.Median)} is close to the mean: the middle of the distribution is symmetric.";
                case 7:
                case 8:
                    return $"99.73 % of units between {F(s.P00135)} and {F(s.P99865)} (read from the data)." +
                           (s.NormalOk ? $"\nNormal model gives {F(d.Mean - 3 * d.Std)} .. {F(d.Mean + 3 * d.Std)}." : "") +
                           (d.N < 740 ? $"\nWith only {d.N} runs these are near the min/max: run 740+ to resolve them." : "");
                case 9:
                    if (double.IsNaN(d.Skew)) return null;
                    if (Math.Abs(d.Skew) < 0.2) return $"Skew {d.Skew:0.00}: symmetric. Both sides of mean +/- 3 sigma are comparable.";
                    string side = d.Skew > 0 ? "HIGH" : "LOW";
                    return $"Skew {d.Skew:0.00}: the {side} side has the longer tail" + (Math.Abs(d.Skew) > 0.5 ? " (clearly)." : " (mildly).") + "\n" +
                           $"Units reach further {(d.Skew > 0 ? "above" : "below")} the mean than mean +/- 3 sigma suggests on that side.\n" +
                           $"Check the {(d.Skew > 0 ? "upper" : "lower")} spec with P{(d.Skew > 0 ? "99.865" : "0.135")}% = {F(d.Skew > 0 ? s.P99865 : s.P00135)}.";
                case 10:
                    if (double.IsNaN(d.ExKurt)) return null;
                    if (d.ExKurt < -0.8) return $"Ex.kurt {d.ExKurt:0.00}: flat and bounded (like a dominant flat() tolerance).\nmean +/- 3 sigma over-estimates the real extremes: the sigma figures are on the safe side.";
                    if (d.ExKurt > 1) return $"Ex.kurt {d.ExKurt:0.00}: heavy tails. More units far from the mean than a normal curve predicts:\nmean +/- 3 sigma, Cpk and ppm are OPTIMISTIC here. Use the percentiles and the observed yield.";
                    return $"Ex.kurt {d.ExKurt:0.00}: tails close to a normal curve.";
                case 11:
                    if (double.IsNaN(s.AdP)) return "Normality not tested (needs 8+ values with some spread).";
                    return s.NormalOk
                        ? $"p = {MeasStats.P(s.AdP)}: consistent with a normal curve.\nmean +/- k sigma, Cpk and the predicted ppm can be used for {s.Meas}."
                        : $"p = {MeasStats.P(s.AdP)}: NOT a normal curve.\nDo not extrapolate with sigma, Cpk or ppm; use the percentiles and the observed yield." +
                          (d.N > 2000 ? $"\n(With {d.N} runs the test flags even small departures: if |Skew| < 0.3 and |Ex.kurt| < 0.5 the sigma figures are still a fair guide.)" : "");
                case 12:
                case 13:
                    return spec ? $"Spec: {(double.IsNaN(s.Spec.Lsl) ? "-" : F(s.Spec.Lsl))} .. {(double.IsNaN(s.Spec.Usl) ? "-" : F(s.Spec.Usl))}. Edit it in 'Spec limits' below." : "No spec set. Set the requirement in 'Spec limits' below to get yield and Cpk.";
                case 14:
                case 15:
                    if (!spec || s.Cap.N == 0) return null;
                    var c = s.Cap;
                    return $"{c.Pass} of {c.N} units inside spec ({MeasStats.Pct(c.Yield)}).\n" +
                           $"True yield is between {MeasStats.Pct(c.YieldLo)} and {MeasStats.Pct(c.YieldHi)} (95 % confidence)." +
                           (c.Pass == c.N ? $"\nNo failure seen; to prove a higher yield, run more (failure rate < 3/n)." : "");
                case 16:
                    if (double.IsNaN(s.Cap.Cpk)) return null;
                    string verdict = s.Cap.Cpk >= 1.33 ? "comfortable margin" : s.Cap.Cpk >= 1 ? "marginal: the nearest limit is within 4 sigma" : "insufficient: the nearest limit is closer than 3 sigma";
                    return $"Cpk {s.Cap.Cpk:0.00}: nearest limit {s.Cap.SigmaToLimit:0.0} sigma from the mean, {verdict}." +
                           (s.NormalOk ? "" : "\nThe result is not normal, so treat this Cpk as indicative only.");
                case 17:
                    if (!spec) return null;
                    return s.NormalOk
                        ? $"Normal model: about {MeasStats.Ppm(s.Cap.PredictedPpm)} of units outside spec (extrapolated beyond the runs)."
                        : "Withheld: the result is not normal, so a normal-curve extrapolation would be misleading.";
            }
            return null;
        }
    }
}
