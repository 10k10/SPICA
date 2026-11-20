using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Spica.Core.Graph
{
    /// <summary>What the blocks need from the program that runs them.</summary>
    public interface IStudyHost
    {
        /// <summary>The schematic a Schematic block without a file of its own stands for (the one open), or "".</summary>
        string DefaultSchematic { get; }
        /// <summary>The netlist of a schematic; throws with the reason.</summary>
        Netlist NetlistFor(string asc);
        /// <summary>The folder of a study's campaigns (&lt;results&gt;\studies\&lt;study&gt;).</summary>
        string StudyFolder(Study study);
        /// <summary>
        /// Queues one campaign (the host adds LTspice and how to run it); <paramref name="done"/> gets the campaign folder
        /// or null and why not, on the thread that runs the study.
        /// </summary>
        void Submit(CampaignRequest request, Action<string, string> done);
    }

    /// <summary>The executors of the blocks, and their registration on a runner.</summary>
    public static class StudyExecutors
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static StudyRunner Register(StudyRunner r, IStudyHost host)
        {
            r.Register(BlockKind.Schematic, new Exec(ctx => Schematic(ctx, host)));
            r.Register(BlockKind.Temperature, new Exec(ctx => ctx.Out("temperature", new List<TempSpec> { ReadTemperature(ctx.Block) })));
            r.Register(BlockKind.Nominal, new Exec(Nominal));
            r.Register(BlockKind.MonteCarlo, new Exec(MonteCarlo));
            r.Register(BlockKind.WorstCase, new Exec(WorstCaseBlock));
            r.Register(BlockKind.Simulate, new Exec(ctx => Simulate(ctx, host)));
            return r;
        }

        sealed class Exec : IBlockExecutor
        {
            readonly Action<BlockContext> f;
            public Exec(Action<BlockContext> f) { this.f = f; }
            public void Run(BlockContext ctx) => f(ctx);
        }

        /// <summary>A number as LTspice writes it (1k, 2.2u, 1e-3); NaN when it is not one.</summary>
        public static double Num(string s, double def = double.NaN)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return def;
            return Expr.TryParse(s, out var e, out _, true) ? e.Eval(new Dictionary<string, double>()) : def;
        }

        /// <summary>"25", "-55; 25; 85" or empty (the schematic's own); throws on what is not a temperature.</summary>
        public static List<double> Temps(string s)
        {
            var list = new List<double>();
            foreach (var part in (s ?? "").Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double t = Num(part);
                if (double.IsNaN(t) || t < -273 || t > 1000) throw new Exception($"'{part}' is not a temperature in °C (e.g. -55; 25; 85).");
                if (!list.Contains(t)) list.Add(t);
            }
            return list;
        }

        /// <summary>A Temperature block's temperatures, by its mode; throws with what is wrong in its settings.</summary>
        public static TempSpec ReadTemperature(Block b)
        {
            string mode = b.Get("Mode", BlockKind.TempList);
            double Field(string key, string what)
            {
                double v = Num(b.Get(key));
                if (double.IsNaN(v)) throw new Exception($"Enter the {what} ({key}).");
                return v;
            }
            string F(double v) => v.ToString("0.###", Inv);
            if (mode.Equals(BlockKind.TempList, StringComparison.OrdinalIgnoreCase))
            {
                var pts = Temps(b.Get("Points"));
                if (pts.Count == 0) throw new Exception("Enter one temperature or more (Points), e.g. -55; 25; 85.");
                return new TempSpec { Points = pts, Text = pts.Count == 1 ? $"{F(pts[0])} °C" : string.Join("; ", pts.Select(F)) + " °C" };
            }
            bool random = mode.Equals(BlockKind.TempRandom, StringComparison.OrdinalIgnoreCase);
            if (random && b.Get("Distribution", BlockKind.Uniform).Equals(BlockKind.Normal, StringComparison.OrdinalIgnoreCase))
            {
                double mean = Field("Nominal", "mean temperature"), sd = Field("Sigma", "deviation");
                if (mean < -273 || mean > 1000) throw new Exception("The mean temperature must be within -273 .. 1000 °C.");
                if (!(sd > 0) || sd > 500) throw new Exception("The deviation (Sigma) must be above 0 °C (and at most 500).");
                return new TempSpec { Random = new TempDraw { Normal = true, A = mean, B = sd }, Text = $"random {F(mean)} ± {F(sd)} °C" };
            }
            double lo = Field("Min", "lowest temperature"), hi = Field("Max", "highest temperature");
            if (lo < -273 || hi > 1000) throw new Exception("The temperatures must be within -273 .. 1000 °C.");
            if (!(hi > lo)) throw new Exception("The highest temperature (Max) must be above the lowest (Min).");
            if (mode.Equals(BlockKind.TempCorners, StringComparison.OrdinalIgnoreCase))
            {
                // the edges of the range, and the nominal between them when one is given
                var pts = new List<double> { lo };
                double nom = Num(b.Get("Nominal"));
                if (!double.IsNaN(nom))
                {
                    if (!(nom > lo && nom < hi)) throw new Exception("The nominal temperature must be between Min and Max (or left empty).");
                    pts.Add(nom);
                }
                pts.Add(hi);
                return new TempSpec { Points = pts, Text = $"corners {string.Join(" / ", pts.Select(F))} °C" };
            }
            if (random) return new TempSpec { Random = new TempDraw { A = lo, B = hi }, Text = $"random {F(lo)} .. {F(hi)} °C" };
            throw new Exception($"Unknown mode '{mode}': List, Corners or Random.");
        }

        /// <summary>The temperature wired into an analysis (null: none, the schematic's own).</summary>
        static TempSpec TempIn(BlockContext ctx) => ctx.All<TempSpec>("temperature").FirstOrDefault();

        /// <summary>A condition at the temperature wired in: its points, or one drawn per unit (Monte Carlo only).</summary>
        static Condition At(Condition c, TempSpec t, bool drawn)
        {
            if (t == null) return c;
            if (t.Random != null && !drawn)
                throw new Exception($"A temperature drawn at random goes with Monte Carlo units only: {c.Name} has none to draw it for. Use a list or the corners.");
            c.Temps = new List<double>(t.Points);
            c.RandomTemp = t.Random;
            c.Name += " at " + t.Text;
            return c;
        }

        // ------------------------------------------------------------------ circuit

        static void Schematic(BlockContext ctx, IStudyHost host)
        {
            string asc = ctx.Block.Get("File");
            if (asc.Length == 0) asc = host.DefaultSchematic ?? "";
            if (asc.Length == 0 || !File.Exists(asc)) throw new Exception(asc.Length == 0 ? "Choose the schematic (File)." : "The schematic is not there: " + asc);
            ctx.Out("circuit", new List<Circuit> { new Circuit { Name = Path.GetFileNameWithoutExtension(asc), Asc = asc, Netlist = host.NetlistFor(asc) } });
        }

        // ------------------------------------------------------------------ analyses

        static void Nominal(BlockContext ctx) =>
            ctx.Out("conditions", new List<Condition> { At(new Condition { Name = "Nominal", Kind = RunKind.Nominal }, TempIn(ctx), false) });

        static void MonteCarlo(BlockContext ctx)
        {
            var b = ctx.Block;
            double runs = Num(b.Get("Runs"));
            if (!(runs >= 1) || runs > 10_000_000 || runs != Math.Floor(runs)) throw new Exception("Runs must be a whole number from 1 to 10,000,000.");
            double seed = Num(b.Get("Seed"), 0);
            if (!(seed >= 0) || seed > uint.MaxValue || seed != Math.Floor(seed)) throw new Exception("The seed must be a whole number, 0 or more (0 = a new one each time).");
            ctx.Out("conditions", new List<Condition>
            {
                At(new Condition { Name = $"Monte Carlo {runs:0}", Kind = RunKind.MonteCarlo, Runs = (int)runs, Seed = (uint)seed, Paired = b.Get("Paired") == "1" }, TempIn(ctx), true),
            });
        }

        static void WorstCaseBlock(BlockContext ctx)
        {
            double k = Num(ctx.Block.Get("Sigma"), 3);
            if (!(k > 0) || k > 10) throw new Exception("Sigma must be above 0 and at most 10.");
            ctx.Out("conditions", new List<Condition> { At(new Condition { Name = "Worst Case", Kind = RunKind.WorstCase, Sigma = k }, TempIn(ctx), false) });
        }

        // ------------------------------------------------------------------ simulation

        static string Hash(string s)
        {
            using (var sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s)).Take(8).Select(x => x.ToString("x2")));
        }

        /// <summary>A short folder name: letters, digits and _ only.</summary>
        public static string Short(string s, int max)
        {
            s = Regex.Replace(Regex.Replace(s ?? "", @"[^A-Za-z0-9]+", "_"), "_+", "_").Trim('_');
            if (s.Length == 0) s = "x";
            return s.Length > max ? s.Substring(0, max).TrimEnd('_') : s;
        }

        /// <summary>Room left in a campaign folder's path for what LTspice writes inside it (chunks\chunk00001_r1-1000_T-55.raw).</summary>
        public const int PathReserve = 64;

        static void Simulate(BlockContext ctx, IStudyHost host)
        {
            var circuits = ctx.All<Circuit>("circuit");
            var conds = ctx.All<Condition>("conditions");
            if (circuits.Count == 0) throw new Exception("Nothing to simulate: no circuit came in.");
            if (conds.Count == 0) throw new Exception("No analysis came in.");
            var save = ctx.Block.Get("Save").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string root = Path.Combine(host.StudyFolder(ctx.Study), "s" + ctx.Block.Id);
            var sets = new List<RunSet>();
            var errors = new List<string>();
            int pending = 0;

            foreach (var c in circuits)
                foreach (var cond in conds)
                {
                    var set = new RunSet { Circuit = c, Condition = cond };
                    sets.Add(set);
                    // folders stay short: LTspice cannot open files past 260 characters of path
                    string dir = Path.Combine(root, Short(cond.Name.Replace("°C", "C"), 16) + "_" + Hash(c.Asc + "\n" + cond.Key).Substring(0, 4));
                    if (Path.GetFullPath(dir).Length + 16 + PathReserve > 259)
                        throw new Exception($"The study's folders would be too deep for LTspice (more than 260 characters of path): {dir}. Choose a shorter results folder in Tools > Options.");
                    string key = Hash(string.Join("\n", c.Netlist.Lines) + "\n" + cond.Key + "\n" + string.Join(" ", save));
                    // the cache: the newest finished campaign made from the same netlist and condition
                    string cached = Directory.Exists(dir)
                        ? Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, "results.csv")) && File.Exists(Path.Combine(d, "study.key")) &&
                                                                  File.ReadAllText(Path.Combine(d, "study.key")).Trim() == key &&
                                                                  CampaignState.Load(d)?.Status == "Finished")
                                   .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).FirstOrDefault()
                        : null;
                    if (cached != null) { set.Dir = cached; set.Cached = true; continue; }

                    var req = new CampaignRequest
                    {
                        Name = $"{ctx.Study.Name}: {cond.Name}",
                        AscPath = c.Asc,
                        OutputDir = NewFolder(dir),
                        Netlist = c.Netlist,
                        Kind = cond.Kind, Runs = cond.Runs, Seed = cond.Seed, Paired = cond.Paired, Sigma = cond.Sigma,
                        Temps = cond.Temps.Count > 0 ? new List<double>(cond.Temps) : null,
                        RandomTemp = cond.RandomTemp,
                        Save = save.Count > 0 ? save : null,
                    };
                    pending++;
                    var thisSet = set;
                    host.Submit(req, (folder, why) =>
                    {
                        if (folder != null)
                        {
                            thisSet.Dir = folder;
                            try { File.WriteAllText(Path.Combine(folder, "study.key"), key); } catch { }
                        }
                        else { thisSet.Error = why ?? "did not finish"; errors.Add($"{cond.Name}: {thisSet.Error}"); }
                        if (--pending == 0) Finish();
                    });
                }

            void Finish()
            {
                ctx.Out("data", sets);
                ctx.Cached = sets.All(s => s.Cached);
                if (sets.All(s => s.Dir == null)) { ctx.Done(errors.Count > 0 ? string.Join("; ", errors.Take(3)) : "no campaign finished"); return; }
                foreach (var e in errors) ctx.Log("Simulate: " + e);
                ctx.Done(null);
            }

            ctx.Log($"Simulate: {sets.Count} campaign{(sets.Count == 1 ? "" : "s")}: {sets.Count(s => s.Cached)} from earlier results, {pending} queued.");
            if (pending == 0) { Finish(); return; }
            ctx.Waiting = true;
        }

        /// <summary>A new campaign folder under <paramref name="dir"/>, named by the time.</summary>
        static string NewFolder(string dir)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv);
            string d = Path.Combine(dir, stamp);
            for (int i = 2; Directory.Exists(d); i++) d = Path.Combine(dir, stamp + "_" + i);
            return d;
        }
    }
}
