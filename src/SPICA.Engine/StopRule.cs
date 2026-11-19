using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Spica.Core
{
    /// <summary>
    /// Early stop of a Monte Carlo campaign: the Runs field becomes the maximum, and the campaign ends as soon as
    /// the answer is known well enough. A normality test is deliberately NOT a stop rule (it passes almost anything
    /// at small n and rejects almost everything at large n). Each watched measurement must be settled:
    ///   with spec limits: the yield verdict is decided at 95 % confidence - proven at least the required yield,
    ///                     or proven below it;
    ///   without limits:   its spread (sigma) is known within +/- the given percentage (95 % confidence).
    /// With temperature corners every corner must be settled.
    /// </summary>
    public sealed class StopRule
    {
        public bool Enabled;
        public double SpreadPct = 5;          // sigma known within +/- this % (no spec)
        public double YieldPct = 99;          // required yield (with spec)
        public int MinRuns = 100;
        public List<string> Watch = new List<string>();   // empty = every measurement

        public StopRule Clone() => new StopRule { Enabled = Enabled, SpreadPct = SpreadPct, YieldPct = YieldPct, MinRuns = MinRuns, Watch = new List<string>(Watch) };

        public string Describe()
        {
            var inv = CultureInfo.InvariantCulture;
            return $"stop when each {(Watch.Count > 0 ? "of " + string.Join(", ", Watch) : "measurement")} is settled: " +
                   $"with limits, yield >= {YieldPct.ToString("0.###", inv)} % proven or disproven; without, sigma within +/-{SpreadPct.ToString("0.#", inv)} %; " +
                   $"at least {MinRuns} runs";
        }

        /// <summary>Required runs to prove a yield with no failure (95 % one-sided): ln(0.05) / ln(yield).</summary>
        public static int ZeroFailRuns(double yield) => yield >= 1 ? int.MaxValue : (int)Math.Ceiling(Math.Log(0.05) / Math.Log(yield));

        public StopCheck Evaluate(IList<RunResult> results, IList<string> meas, Func<string, SpecLimit> spec, int corners, Func<int, string> cornerName = null)
        {
            var inv = CultureInfo.InvariantCulture;
            var watched = Watch.Count == 0 ? meas.ToList() : meas.Where(m => Watch.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
            var check = new StopCheck();
            if (watched.Count == 0) { check.Status = "no watched measurement found"; return check; }
            double y = Math.Min(0.999999, Math.Max(0.5, YieldPct / 100));
            double target = Math.Max(0.1, SpreadPct) / 100;

            double worst = double.PositiveInfinity;
            int need = 0;
            string worstText = "";
            for (int c = 0; c < corners; c++)
            {
                var rows = results.Where(r => r.Corner == c && r.Status != RunStatus.Failed).ToList();
                int n = rows.Count;
                if (n < MinRuns)
                {
                    double pr = n / (double)Math.Max(1, MinRuns);
                    if (pr < worst) { worst = pr; worstText = $"minimum {MinRuns} runs ({n} done)"; }
                    need = Math.Max(need, MinRuns);
                    continue;
                }
                string where = corners > 1 ? $" ({(cornerName != null ? cornerName(c) : "corner " + (c + 1))})" : "";
                foreach (var m in watched)
                {
                    var vals = new List<double>();
                    int pass = 0, total = 0;
                    var sp = spec(m);
                    foreach (var r in rows)
                    {
                        double v = r.Status == RunStatus.Ok && r.Values.TryGetValue(m, out var s) && LogParser.TryNumber(s, out var d) ? d : double.NaN;
                        if (!double.IsNaN(v)) vals.Add(v);
                        if (sp != null && sp.IsSet) { total++; if (sp.Passes(v)) pass++; }
                    }
                    double progress; int needed; string text;
                    if (sp != null && sp.IsSet)
                    {
                        Stats.ClopperPearson(pass, total, 0.90, out double lo, out double hi);   // 95 % one-sided each way
                        int fails = total - pass;
                        if (lo >= y) { progress = 1; needed = total; text = $"{m}{where}: yield >= {Pct(y)} proven"; }
                        else if (hi < y) { progress = 1; needed = total; text = $"{m}{where}: yield below {Pct(y)} (proven: fails the requirement)"; }
                        else
                        {
                            double p = fails / (double)Math.Max(1, total), q = 1 - y;
                            needed = fails == 0 ? ZeroFailRuns(y)
                                   : Math.Abs(p - q) < 1e-12 ? int.MaxValue
                                   : (int)Math.Min(int.MaxValue, Math.Ceiling(2.706 * p * (1 - p) / ((p - q) * (p - q))));
                            needed = Math.Max(needed, total + 1);
                            progress = total / (double)needed;
                            text = $"{m}{where}: yield {Pct(pass / (double)Math.Max(1, total))} so far, not yet decided vs {Pct(y)}";
                        }
                    }
                    else
                    {
                        if (vals.Count < 3) { progress = 0; needed = MinRuns; text = $"{m}{where}: no data yet"; }
                        else
                        {
                            var d = Descriptive.Of(vals);
                            int k = vals.Count;
                            double kurt = double.IsNaN(d.ExKurt) ? 0 : d.ExKurt;
                            double rel = 1.96 * 0.5 * Math.Sqrt(Math.Max(0.5 / k, 2.0 / (k - 1) + kurt / k));
                            needed = (int)Math.Ceiling(k * (rel / target) * (rel / target));
                            progress = rel <= target ? 1 : target / rel * (target / rel);
                            text = $"{m}{where}: sigma known to +/-{(rel * 100).ToString("0.0", inv)} % (target {(target * 100).ToString("0.#", inv)} %)";
                        }
                    }
                    check.Lines.Add(text);
                    need = Math.Max(need, needed);
                    if (progress < worst || (progress >= 1 && worst == double.PositiveInfinity)) { worst = progress; worstText = text; }
                }
            }
            check.Done = worst >= 1;
            check.Progress = Math.Min(1, worst);
            check.RunsNeeded = need;
            check.Status = check.Done ? $"all {watched.Count} watched measurements settled" + (corners > 1 ? $" at {corners} temperatures" : "") :worstText + (need > 0 && need < int.MaxValue ? $"; ~{need:N0} runs needed" : "");
            return check;
        }

        static string Pct(double v) => (v * 100).ToString(v >= 0.999 ? "0.###" : "0.##", CultureInfo.InvariantCulture) + " %";
    }

    public sealed class StopCheck
    {
        public bool Done;
        public double Progress;       // 0..1, of the least settled measurement
        public int RunsNeeded;        // estimate per temperature
        public string Status = "";
        public readonly List<string> Lines = new List<string>();   // one per measurement and temperature
    }
}
