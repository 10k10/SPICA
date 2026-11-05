using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Spica.Core
{
    public enum TempMode { Default, Corners, Random }

    /// <summary>Lower / upper spec limit of one measurement (NaN = no limit).</summary>
    public sealed class SpecLimit
    {
        public double Lsl = double.NaN, Usl = double.NaN;
        public bool IsSet => !double.IsNaN(Lsl) || !double.IsNaN(Usl);
        public bool Passes(double v) =>
            !double.IsNaN(v) && (double.IsNaN(Lsl) || v >= Lsl) && (double.IsNaN(Usl) || v <= Usl);
    }

    /// <summary>Transfer-function analysis definition: Y versus X, optional theoretical TF and error budget.</summary>
    public sealed class TfSetup
    {
        public string X = "", Y = "";
        public string Theory = "";            // expression of x (and T), e.g. "1.93*x"; empty = none
        public double Budget = double.NaN;    // allowed error vs theory
        public bool BudgetPercent = true;     // true: % of the theoretical value, false: absolute units of Y
        public double K = 3;                  // k-sigma used for error bounds
    }

    /// <summary>
    /// A SPICA setup (.spica): everything needed to reproduce a campaign's setup and its analysis.
    /// The random seed is stored too, but 0 means "new seed for every campaign".
    /// </summary>
    public sealed class SpicaProject
    {
        public string FilePath;               // where this setup was loaded from / saved to (null = never saved)
        public string Schematic = "";         // absolute path of the .asc

        public int Runs = 1000;
        public uint Seed;                     // 0 = draw a new seed per campaign
        public bool Paired;                   // SPICA's own generator replaces mc()/gauss()/flat()
        public bool ClockReseed = true;
        public bool KeepRaw = true;
        public bool SaveSelected = true;
        public List<string> Signals = new List<string>();

        public TempMode Temp = TempMode.Default;
        public List<double> Corners = new List<double> { -40, 25, 90 };
        public double TempMin = -40, TempMax = 90;

        public readonly Dictionary<string, SpecLimit> Specs = new Dictionary<string, SpecLimit>(StringComparer.OrdinalIgnoreCase);
        public TfSetup Tf = new TfSetup();

        public WcMethod WorstCase = WcMethod.Off;   // Off = Monte Carlo
        public double WcSigma = 3;            // gaussian tolerances are pushed to +/- this many sigma
        public StopRule Stop = new StopRule();

        public double DesignSigma = 6;        // error budget: mean +/- this many sigma must stay inside the budget
        public bool PessimisticSigma = true;  // use the 95 % upper bound of sigma
        public List<ErrorItem> Errors = new List<ErrorItem>();

        public bool Dirty;

        public SpecLimit Spec(string meas) => meas != null && Specs.TryGetValue(meas, out var s) ? s : null;

        public void SetSpec(string meas, double lsl, double usl)
        {
            if (double.IsNaN(lsl) && double.IsNaN(usl)) Specs.Remove(meas);
            else Specs[meas] = new SpecLimit { Lsl = lsl, Usl = usl };
            Dirty = true;
        }

        public string TempDescription()
        {
            switch (Temp)
            {
                case TempMode.Corners: return "corners " + string.Join(", ", Corners.Select(Num)) + " °C";
                case TempMode.Random: return $"random flat {Num(TempMin)} .. {Num(TempMax)} °C";
                default: return "default (as in the schematic)";
            }
        }

        // ------------------------------------------------------------------ file format

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static string Num(double d) => double.IsNaN(d) ? "" : d.ToString("R", Inv);

        static double ParseNum(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return double.NaN;
            return double.TryParse(s.Trim(), NumberStyles.Float, Inv, out var v) ? v : double.NaN;
        }

        public static List<double> ParseList(string s)
        {
            var list = new List<double>();
            foreach (var part in (s ?? "").Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (double.TryParse(part, NumberStyles.Float, Inv, out var v)) list.Add(v);
            return list;
        }

        public static List<string> ParseNames(string s) =>
            (s ?? "").Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("; SPICA setup - Spice Parallel Instancer Circuit Analysis");
            sb.AppendLine("[Campaign]");
            sb.AppendLine("Schematic=" + RelativeTo(path, Schematic));
            sb.AppendLine("Runs=" + Runs.ToString(Inv));
            sb.AppendLine("Seed=" + Seed.ToString(Inv));
            sb.AppendLine("Paired=" + (Paired ? 1 : 0));
            sb.AppendLine("ClockReseed=" + (ClockReseed ? 1 : 0));
            sb.AppendLine("KeepRaw=" + (KeepRaw ? 1 : 0));
            sb.AppendLine();
            sb.AppendLine("[Temperature]");
            sb.AppendLine("Mode=" + Temp);
            sb.AppendLine("Corners=" + string.Join(", ", Corners.Select(Num)));
            sb.AppendLine("Min=" + Num(TempMin));
            sb.AppendLine("Max=" + Num(TempMax));
            sb.AppendLine();
            sb.AppendLine("[Save]");
            sb.AppendLine("Mode=" + (SaveSelected ? "Selected" : "All"));
            sb.AppendLine("Signals=" + string.Join(" ", Signals));
            foreach (var kv in Specs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!kv.Value.IsSet) continue;
                sb.AppendLine();
                sb.AppendLine($"[Spec {kv.Key}]");
                sb.AppendLine("LSL=" + Num(kv.Value.Lsl));
                sb.AppendLine("USL=" + Num(kv.Value.Usl));
            }
            sb.AppendLine();
            sb.AppendLine("[Transfer]");
            sb.AppendLine("X=" + Tf.X);
            sb.AppendLine("Y=" + Tf.Y);
            sb.AppendLine("Theory=" + Tf.Theory);
            sb.AppendLine("Budget=" + Num(Tf.Budget));
            sb.AppendLine("BudgetUnit=" + (Tf.BudgetPercent ? "%" : "abs"));
            sb.AppendLine("K=" + Num(Tf.K));
            sb.AppendLine();
            sb.AppendLine("[WorstCase]");
            sb.AppendLine("Method=" + WorstCase);
            sb.AppendLine("Sigma=" + Num(WcSigma));
            sb.AppendLine();
            sb.AppendLine("[Stop]");
            sb.AppendLine("Enabled=" + (Stop.Enabled ? 1 : 0));
            sb.AppendLine("Spread=" + Num(Stop.SpreadPct));
            sb.AppendLine("Yield=" + Num(Stop.YieldPct));
            sb.AppendLine("MinRuns=" + Stop.MinRuns.ToString(Inv));
            sb.AppendLine("Watch=" + string.Join(", ", Stop.Watch));
            sb.AppendLine();
            sb.AppendLine("[ErrorBudget]");
            sb.AppendLine("Sigma=" + Num(DesignSigma));
            sb.AppendLine("Pessimistic=" + (PessimisticSigma ? 1 : 0));
            foreach (var e in Errors)
            {
                sb.AppendLine();
                sb.AppendLine($"[Error {e.Name}]");
                sb.AppendLine("Y=" + e.Y);
                sb.AppendLine("X=" + e.X);
                sb.AppendLine("TF=" + e.Tf);
                sb.AppendLine("Ideal=" + e.Ideal);
                sb.AppendLine("ReferToInput=" + (e.ReferToInput ? 1 : 0));
                sb.AppendLine("Symmetric=" + (e.Symmetric ? 1 : 0));
                sb.AppendLine("Lower=" + Num(e.Lower));
                sb.AppendLine("Upper=" + Num(e.Upper));
                sb.AppendLine("Unit=" + e.Unit);
                sb.AppendLine("FullScale=" + Num(e.FullScale));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            FilePath = path;
            Dirty = false;
        }

        public static SpicaProject Load(string path)
        {
            var p = new SpicaProject { FilePath = path };
            string section = "";
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2).Trim(); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                bool B() => val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);

                if (section.Equals("Campaign", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "schematic": p.Schematic = Absolute(path, val); break;
                        case "runs": if (int.TryParse(val, NumberStyles.Integer, Inv, out var r)) p.Runs = r; break;
                        case "seed": if (uint.TryParse(val, NumberStyles.Integer, Inv, out var s)) p.Seed = s; break;
                        case "paired": p.Paired = B(); break;
                        case "clockreseed": p.ClockReseed = B(); break;
                        case "keepraw": p.KeepRaw = B(); break;
                    }
                }
                else if (section.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "mode": if (Enum.TryParse(val, true, out TempMode m)) p.Temp = m; break;
                        case "corners": var c = ParseList(val); if (c.Count > 0) p.Corners = c; break;
                        case "min": var mn = ParseNum(val); if (!double.IsNaN(mn)) p.TempMin = mn; break;
                        case "max": var mx = ParseNum(val); if (!double.IsNaN(mx)) p.TempMax = mx; break;
                    }
                }
                else if (section.Equals("Save", StringComparison.OrdinalIgnoreCase))
                {
                    if (key.Equals("Mode", StringComparison.OrdinalIgnoreCase)) p.SaveSelected = !val.Equals("All", StringComparison.OrdinalIgnoreCase);
                    else if (key.Equals("Signals", StringComparison.OrdinalIgnoreCase))
                        p.Signals = val.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                }
                else if (section.StartsWith("Spec ", StringComparison.OrdinalIgnoreCase))
                {
                    string meas = section.Substring(5).Trim();
                    if (!p.Specs.TryGetValue(meas, out var sp)) p.Specs[meas] = sp = new SpecLimit();
                    if (key.Equals("LSL", StringComparison.OrdinalIgnoreCase)) sp.Lsl = ParseNum(val);
                    else if (key.Equals("USL", StringComparison.OrdinalIgnoreCase)) sp.Usl = ParseNum(val);
                }
                else if (section.Equals("Transfer", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "x": p.Tf.X = val; break;
                        case "y": p.Tf.Y = val; break;
                        case "theory": p.Tf.Theory = val; break;
                        case "budget": p.Tf.Budget = ParseNum(val); break;
                        case "budgetunit": p.Tf.BudgetPercent = val != "abs"; break;
                        case "k": var k = ParseNum(val); if (k > 0) p.Tf.K = k; break;
                    }
                }
                else if (section.Equals("WorstCase", StringComparison.OrdinalIgnoreCase))
                {
                    if (key.Equals("Method", StringComparison.OrdinalIgnoreCase) && Enum.TryParse(val, true, out WcMethod wm)) p.WorstCase = wm;
                    else if (key.Equals("Sigma", StringComparison.OrdinalIgnoreCase)) { var ks = ParseNum(val); if (ks > 0) p.WcSigma = ks; }
                }
                else if (section.Equals("ErrorBudget", StringComparison.OrdinalIgnoreCase))
                {
                    if (key.Equals("Sigma", StringComparison.OrdinalIgnoreCase)) { var ks = ParseNum(val); if (ks > 0) p.DesignSigma = ks; }
                    else if (key.Equals("Pessimistic", StringComparison.OrdinalIgnoreCase)) p.PessimisticSigma = B();
                }
                else if (section.StartsWith("Error ", StringComparison.OrdinalIgnoreCase))
                {
                    string name = section.Substring(6).Trim();
                    var e = p.Errors.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (e == null) p.Errors.Add(e = new ErrorItem { Name = name });
                    switch (key.ToLowerInvariant())
                    {
                        case "y": e.Y = val; break;
                        case "x": e.X = val; break;
                        case "tf": e.Tf = val; break;
                        case "ideal": if (Enum.TryParse(val, true, out IdealKind ik)) e.Ideal = ik; break;
                        case "refertoinput": e.ReferToInput = B(); break;
                        case "budget": e.Budget = ParseNum(val); break;          // setups before 0.4: +/- one value
                        case "symmetric": e.Symmetric = B(); break;
                        case "lower": e.Lower = ParseNum(val); break;
                        case "upper": e.Upper = ParseNum(val); break;
                        case "unit": if (Enum.TryParse(val, true, out BudgetUnit bu)) e.Unit = bu; break;
                        case "fullscale": e.FullScale = ParseNum(val); break;
                    }
                }
                else if (section.Equals("Stop", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "enabled": p.Stop.Enabled = B(); break;
                        case "spread": var sp = ParseNum(val); if (sp > 0) p.Stop.SpreadPct = sp; break;
                        case "yield": var yp = ParseNum(val); if (yp > 0 && yp < 100) p.Stop.YieldPct = yp; break;
                        case "minruns": if (int.TryParse(val, NumberStyles.Integer, Inv, out var mr) && mr > 0) p.Stop.MinRuns = mr; break;
                        case "watch": p.Stop.Watch = ParseNames(val); break;
                    }
                }
            }
            // the gain and offset of a straight-line ideal (older setups only have the TF text)
            foreach (var e in p.Errors)
                if (e.UsesTf && e.Ideal == IdealKind.Linear)
                {
                    if (ErrorItem.TryLinear(e.Tf, out var g, out var o)) { e.Gain = g; e.Offset = o; }
                    else e.Ideal = IdealKind.Expression;
                }
            p.Dirty = false;
            return p;
        }

        public SpicaProject Clone()
        {
            var c = new SpicaProject
            {
                FilePath = FilePath, Schematic = Schematic, Runs = Runs, Seed = Seed, Paired = Paired,
                ClockReseed = ClockReseed, KeepRaw = KeepRaw, SaveSelected = SaveSelected,
                Signals = new List<string>(Signals), Temp = Temp, Corners = new List<double>(Corners),
                TempMin = TempMin, TempMax = TempMax, Dirty = Dirty,
                WorstCase = WorstCase, WcSigma = WcSigma, Stop = Stop.Clone(),
                DesignSigma = DesignSigma, PessimisticSigma = PessimisticSigma, Errors = Errors.Select(e => e.Clone()).ToList(),
                Tf = new TfSetup { X = Tf.X, Y = Tf.Y, Theory = Tf.Theory, Budget = Tf.Budget, BudgetPercent = Tf.BudgetPercent, K = Tf.K },
            };
            foreach (var kv in Specs) c.Specs[kv.Key] = new SpecLimit { Lsl = kv.Value.Lsl, Usl = kv.Value.Usl };
            return c;
        }

        static string RelativeTo(string file, string target)
        {
            if (string.IsNullOrEmpty(target)) return "";
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(file));
                string full = Path.GetFullPath(target);
                if (full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(dir.Length + 1);
                return full;
            }
            catch { return target; }
        }

        static string Absolute(string file, string target)
        {
            if (string.IsNullOrEmpty(target)) return "";
            try { return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file)), target)); }
            catch { return target; }
        }
    }
}
