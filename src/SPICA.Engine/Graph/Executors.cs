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
    /// <summary>What the blocks need from the program that runs them (the window, or the command line).</summary>
    public interface IStudyHost
    {
        SpicaProject Project { get; }
        /// <summary>The netlist of a schematic (the one open, or another candidate's); throws with the reason.</summary>
        Netlist NetlistFor(string asc);
        IList<QualLibrary> Libraries { get; }
        /// <summary>The folder of a study's campaigns and report (&lt;output&gt;\studies\&lt;study&gt;).</summary>
        string StudyFolder(Study study);
        /// <summary>
        /// Queues one campaign; <paramref name="done"/> gets the campaign folder (with results.csv) or null and why not,
        /// on the thread that runs the study.
        /// </summary>
        void Submit(string name, CampaignRequest request, Action<string, string> done);
        /// <summary>A report or other file a block wrote (to open or list).</summary>
        void Wrote(string what, string path);
    }

    /// <summary>The executors of the blocks that run in this version, and the registration of them on a runner.</summary>
    public static class StudyExecutors
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static StudyRunner Register(StudyRunner r, IStudyHost host)
        {
            r.Register(BlockKind.SupplyWaveform, new Exec(ctx => SupplyWaveform(ctx, host)));
            r.Register(BlockKind.Fault, new Exec(Fault));
            r.Register(BlockKind.Schematic, new Exec(ctx => Schematic(ctx, host)));
            r.Register(BlockKind.Candidates, new Exec(Candidates));
            r.Register(BlockKind.Measure, new Exec(Measure));
            r.Register(BlockKind.Nominal, new Exec(Nominal));
            r.Register(BlockKind.MonteCarlo, new Exec(MonteCarlo));
            r.Register(BlockKind.Corners, new Exec(Corners));
            r.Register(BlockKind.Combine, new Exec(Combine));
            r.Register(BlockKind.Simulate, new Exec(ctx => Simulate(ctx, host)));
            r.Register(BlockKind.Limits, new Exec(ctx => Limits(ctx, host)));
            r.Register(BlockKind.ErrorBudget, new Exec(ctx => ErrorBudgetBlock(ctx, host)));
            r.Register(BlockKind.Spec, new Exec(Spec));
            r.Register(BlockKind.Plots, new Exec(ctx => { }));
            r.Register(BlockKind.Compare, new Exec(ctx => { }));
            r.Register(BlockKind.RequirementsMatrix, new Exec(ctx => { }));
            r.Register(BlockKind.Report, new Exec(ctx => Report(ctx, host)));
            r.Register(BlockKind.Export, new Exec(ctx => Export(ctx, host)));
            return r;
        }

        sealed class Exec : IBlockExecutor
        {
            readonly Action<BlockContext> f;
            public Exec(Action<BlockContext> f) { this.f = f; }
            public void Run(BlockContext ctx) => f(ctx);
        }

        static List<string> Split(string s) => (s ?? "").Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        static double Num(string s, double def = double.NaN)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return def;
            return Expr.TryParse(s, out var e, out _, true) ? e.Eval(new Dictionary<string, double>()) : def;
        }

        // ------------------------------------------------------------------ stimuli

        static void SupplyWaveform(BlockContext ctx, IStudyHost host)
        {
            var b = ctx.Block;
            var lib = host.Libraries.FirstOrDefault(l => l.Key.Equals(b.Get("Library"), StringComparison.OrdinalIgnoreCase))
                      ?? throw new Exception($"No library '{b.Get("Library")}' in the qualification folder.");
            string cat = b.Get("Category");
            if (!lib.CategoryOrder.Contains(cat, StringComparer.OrdinalIgnoreCase)) throw new Exception($"{lib.Standard} has no category '{cat}'.");
            var tests = Split(b.Get("Tests"));
            if (tests.Count == 0) throw new Exception("No test chosen (Tests).");
            if (b.Get("Net").Length == 0) throw new Exception("Choose the net the waveform goes into (Net).");
            var cases = new List<StimulusCase>();
            foreach (var name in tests)
            {
                var t = lib.TestsFor(cat).FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new Exception($"{lib.Standard}, category {cat}, has no test '{name}'.");
                cases.Add(new StimulusCase { Name = t.Name, Library = lib, Test = t, Category = cat, Net = b.Get("Net"), Rser = Math.Max(0, Num(b.Get("Rser"), 0)) });
            }
            ctx.Out("stimulus", cases);
        }

        static void Fault(BlockContext ctx)
        {
            var b = ctx.Block;
            if (b.Get("From").Length == 0) throw new Exception("Choose the net the fault starts from (From).");
            var c = new StimulusCase
            {
                IsFault = true, From = b.Get("From"), To = b.Get("To", "0").Length == 0 ? "0" : b.Get("To", "0"),
                R = Num(b.Get("R"), 0.01), Start = Num(b.Get("Start"), 0), Duration = Num(b.Get("Duration"), 0),
            };
            if (!(c.R > 0)) throw new Exception("The fault resistance (R) must be above 0.");
            c.Name = $"fault {c.From}-{c.To}";
            ctx.Out("stimulus", new List<StimulusCase> { c });
        }

        // ------------------------------------------------------------------ circuit

        static void Schematic(BlockContext ctx, IStudyHost host)
        {
            var b = ctx.Block;
            string asc = b.Get("File");
            if (asc.Length == 0) asc = host.Project.Schematic ?? "";
            if (asc.Length == 0 || !File.Exists(asc)) throw new Exception(asc.Length == 0 ? "Choose the schematic (File)." : "The schematic is not there: " + asc);
            var nl = host.NetlistFor(asc);
            string name = b.Get("Name");
            var stimuli = ctx.All<StimulusCase>("stimulus");
            var variants = new List<CircuitVariant>();
            if (stimuli.Count == 0) variants.Add(new CircuitVariant { Candidate = name, Asc = asc, Netlist = nl });
            foreach (var s in stimuli) variants.Add(new CircuitVariant { Candidate = name, Asc = asc, Netlist = nl, Stimulus = s });
            ctx.Out("circuit", variants);
        }

        /// <summary>Several candidates on one wire; "OUT = A: V(out), B: V(vo)" lines map the logical signals per candidate.</summary>
        static void Candidates(BlockContext ctx)
        {
            var all = ctx.All<CircuitVariant>("candidates");
            var names = all.Select(v => v.Candidate).Distinct().ToList();
            if (names.Count < 2) ctx.Log("Candidates: only one candidate is wired.");
            if (names.Any(n => n.Length == 0)) throw new Exception("Give every candidate schematic a name (Name), to tell them apart.");
            if (names.Count != names.Distinct(StringComparer.OrdinalIgnoreCase).Count()) throw new Exception("Two candidates have the same name.");
            foreach (var line in ctx.Block.GetAll("Signal").Where(x => x.Trim().Length > 0))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) throw new Exception($"Signal '{line}': write it as OUT = A: V(out), B: V(vo).");
                string logical = line.Substring(0, eq).Trim();
                foreach (var part in line.Substring(eq + 1).Split(','))
                {
                    int colon = part.IndexOf(':');
                    if (colon <= 0) continue;
                    string cand = part.Substring(0, colon).Trim(), actual = part.Substring(colon + 1).Trim();
                    foreach (var v in all.Where(x => x.Candidate.Equals(cand, StringComparison.OrdinalIgnoreCase))) v.Signals[logical] = actual;
                }
            }
            ctx.Out("circuit", all);
        }

        /// <summary>More signals to keep in the .raw (to look at later) and more .meas lines, on every circuit that passes.</summary>
        static void Measure(BlockContext ctx)
        {
            var save = ctx.Block.Get("Save").Split(new[] { ' ', ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var meas = Split(ctx.Block.Get("Meas")).Select(m => m.StartsWith(".") ? m : ".meas " + m).ToList();
            var outv = ctx.All<CircuitVariant>("circuit").Select(v =>
            {
                var c = v.Clone();
                c.Save.AddRange(save.Where(s => !c.Save.Contains(s, StringComparer.OrdinalIgnoreCase)));
                c.Meas.AddRange(meas);
                return c;
            }).ToList();
            ctx.Out("circuit", outv);
        }

        // ------------------------------------------------------------------ variation

        static double? Temp(string s)
        {
            double t = Num(s);
            return double.IsNaN(t) ? (double?)null : t;
        }

        static void Nominal(BlockContext ctx)
        {
            var t = Temp(ctx.Block.Get("Temperature"));
            ctx.Out("conditions", new List<Condition> { new Condition { Name = t.HasValue ? $"nominal {t.Value.ToString("0.###", Inv)} °C" : "nominal", Temp = t } });
        }

        static void MonteCarlo(BlockContext ctx)
        {
            var b = ctx.Block;
            int runs = (int)Num(b.Get("Runs"), 1000);
            if (runs < 1) throw new Exception("Runs must be 1 or more.");
            var c = new Condition { Nominal = false, Runs = runs, Paired = b.Get("Paired") == "1", Seed = (uint)Math.Max(0, Num(b.Get("Seed"), 0)) };
            string temp = b.Get("Temperature").Trim();
            var m = Regex.Match(temp, @"^random\s+(\S+)\s*\.\.\s*(\S+)$", RegexOptions.IgnoreCase);
            if (m.Success) { c.TempMin = Num(m.Groups[1].Value); c.TempMax = Num(m.Groups[2].Value); }
            else c.Temp = Temp(temp);
            c.Name = $"MC {runs}" + (c.Temp.HasValue ? $" {c.Temp.Value.ToString("0.###", Inv)} °C" : m.Success ? $" {temp}" : "");
            ctx.Out("conditions", new List<Condition> { c });
        }

        static void Corners(BlockContext ctx)
        {
            var b = ctx.Block;
            if (b.Get("Edges").Trim().Length > 0) ctx.Log("Corners: tolerance edges do not run yet in this version; only the temperatures are used.");
            var temps = b.Get("Temperatures").Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Num(x)).Where(x => !double.IsNaN(x)).Distinct().ToList();
            if (temps.Count == 0) throw new Exception("Enter the temperatures (e.g. -55; 25; 85).");
            ctx.Out("conditions", temps.Select(t => new Condition { Name = t.ToString("0.###", Inv) + " °C", Temp = t }).ToList());
        }

        static void Combine(BlockContext ctx)
        {
            var sets = ctx.In("conditions").OfType<List<Condition>>().ToList();
            bool union = ctx.Block.Get("Mode").Equals("union", StringComparison.OrdinalIgnoreCase);
            List<Condition> res;
            if (union) res = sets.SelectMany(x => x).ToList();
            else
            {
                // every condition of one set with every one of the next: runs from the varying one, the temperature from whichever has it
                res = new List<Condition> { null };
                foreach (var set in sets)
                    res = res.SelectMany(a => set.Select(c =>
                    {
                        if (a == null) return c.Clone();
                        var m = a.Clone();
                        if (!c.Nominal) { m.Nominal = false; m.Runs = c.Runs; m.Paired = c.Paired; m.Seed = c.Seed; }
                        if (c.Temp.HasValue) m.Temp = c.Temp;
                        if (!double.IsNaN(c.TempMin)) { m.TempMin = c.TempMin; m.TempMax = c.TempMax; }
                        m.Name = a.Name + " × " + c.Name;
                        return m;
                    })).ToList();
                res = res.Where(x => x != null).ToList();
            }
            var seen = new HashSet<string>();
            ctx.Out("conditions", res.Where(c => seen.Add(c.Key)).ToList());
        }

        // ------------------------------------------------------------------ simulation

        /// <summary>The criteria and signals the blocks fed by this Simulate need, so that the netlists measure and save them.</summary>
        static void Needs(BlockContext ctx, IStudyHost host, out List<QualCriterion> criteria, out List<string> plot)
        {
            criteria = new List<QualCriterion>(); plot = new List<string>();
            foreach (var d in ctx.Study.Downstream(ctx.Block))
            {
                if (d.Kind == BlockKind.Limits)
                    foreach (var n in Split(d.Get("Criteria")))
                    {
                        var c = host.Project.QualCriteria.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                                ?? throw new Exception($"Limits names the criterion '{n}', which the setup does not have (Qualification > Pass / Fail).");
                        if (!criteria.Contains(c)) criteria.Add(c);
                    }
                if (d.Kind == BlockKind.Plots) plot.AddRange(d.Get("Signals").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
            }
        }

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

        /// <summary>The .tran stop time of a netlist (the schematic's own analysis), NaN when it has none.</summary>
        public static double TranStop(IEnumerable<string> lines)
        {
            foreach (var l in lines)
            {
                var t = l.Trim();
                if (!t.StartsWith(".tran", StringComparison.OrdinalIgnoreCase)) continue;
                var nums = Netlist.Tokenize(t).Skip(1).Select(x => Num(x)).TakeWhile(x => !double.IsNaN(x)).ToList();
                return nums.Count == 1 ? nums[0] : nums.Count >= 2 ? nums[1] : double.NaN;
            }
            return double.NaN;
        }

        /// <summary>The netlist of a variant without a supply waveform: the schematic as drawn, a fault switch if any, and the criteria's .meas.</summary>
        static QualCase BuildPlain(CircuitVariant v, List<QualCriterion> criteria)
        {
            var nl = v.Netlist;
            var lines = new List<string>();
            foreach (var line in nl.Lines)
            {
                var low = line.Trim().ToLowerInvariant();
                if (low == ".end" || low.StartsWith(".end ")) break;
                lines.Add(line);
            }
            double stop = TranStop(lines);
            var c = new QualCase { Lines = lines, Category = "", Test = new QualTest { Name = v.CaseName } };
            if (v.Stimulus != null && v.Stimulus.IsFault)
            {
                var f = v.Stimulus;
                if (double.IsNaN(stop)) { c.Error = "The schematic has no .tran: a fault needs a transient analysis."; return c; }
                double t1 = f.Start, t2 = f.Duration > 0 ? f.Start + f.Duration : double.NaN;
                string G(double x) => x.ToString("0.#########", Inv);
                string pwl = $"PWL(0 0 {G(t1)} 0 {G(t1 + 1e-9)} 1" + (double.IsNaN(t2) ? "" : $" {G(t2)} 1 {G(t2 + 1e-9)} 0") + ")";
                lines.Add($"* ---- SPICA study: fault {v.MapNet(f.From)} -> {v.MapNet(f.To)}, {G(f.R)} ohm from {G(t1)} s ----");
                lines.Add($"Sspica_fault {v.MapNet(f.From)} {v.MapNet(f.To)} spica_fctl 0 SPICA_FAULT");
                lines.Add($"Vspica_fctl spica_fctl 0 {pwl}");
                lines.Add($".model SPICA_FAULT SW(Ron={G(f.R)} Roff=1G Vt=0.5 Vh=-0.2)");
                c.Events = new QualEvents { Start = t1, End = double.IsNaN(t2) ? stop : t2, Stop = stop };
            }
            else c.Events = new QualEvents { Start = 0, End = 0, Stop = double.IsNaN(stop) ? 0 : stop };
            var seen = new HashSet<string>(nl.MeasNames, StringComparer.OrdinalIgnoreCase);
            foreach (var cr in criteria.Where(x => x.AppliesTo(v.CaseName)))
            {
                if (double.IsNaN(stop)) { c.Error = $"Criterion {cr.Name} needs a .tran in the schematic."; return c; }
                if (!cr.Window(c.Events, out double a, out double z)) continue;
                c.Criteria.Add(cr);
                string G(double x) => x.ToString("0.#########", Inv);
                if (!double.IsNaN(cr.Lo) && seen.Add(cr.MinMeas)) lines.Add($".meas tran {cr.MinMeas} MIN {cr.Signal} FROM={G(a)} TO={G(z)}");
                if (!double.IsNaN(cr.Hi) && seen.Add(cr.MaxMeas)) lines.Add($".meas tran {cr.MaxMeas} MAX {cr.Signal} FROM={G(a)} TO={G(z)}");
            }
            lines.Add(".end");
            return c;
        }

        static void Simulate(BlockContext ctx, IStudyHost host)
        {
            var variants = ctx.All<CircuitVariant>("circuit");
            var conds = ctx.All<Condition>("conditions");
            if (variants.Count == 0) throw new Exception("Nothing to simulate: no circuit came in.");
            if (conds.Count == 0) throw new Exception("No condition came in.");
            Needs(ctx, host, out var criteria, out var plot);
            string root = Path.Combine(host.StudyFolder(ctx.Study), "s" + ctx.Block.Id);
            var p = host.Project;
            var sets = new List<RunSet>();
            int pending = 0;
            var errors = new List<string>();

            foreach (var v in variants)
            {
                // the criteria in this candidate's own signal names
                var mine = criteria.Select(c => { var m = c.Clone(); m.Signal = v.Map(c.Signal); return m; }).ToList();
                QualCase qc;
                if (v.Stimulus != null && !v.Stimulus.IsFault)
                {
                    var ch = new QualStdChoice { Category = v.Stimulus.Category, Net = v.MapNet(v.Stimulus.Net), Rser = v.Stimulus.Rser };
                    qc = QualBuild.Make(v.Netlist, v.Stimulus.Library, v.Stimulus.Test, v.Stimulus.Category, ch, mine);
                }
                else qc = BuildPlain(v, mine);
                if (qc.Error != null) throw new Exception($"{v.Label}: {qc.Error}");
                if (v.Meas.Count > 0)
                {
                    int end = qc.Lines.FindLastIndex(l => l.Trim().Equals(".end", StringComparison.OrdinalIgnoreCase));
                    qc.Lines.InsertRange(end < 0 ? qc.Lines.Count : end, v.Meas.Select(v.Map));
                }

                // folders stay short: LTspice cannot open files past 260 characters of path
                string caseDir = Path.Combine(root, (v.Candidate.Length > 0 ? Short(v.Candidate, 8) + "_" : "") + Short(v.CaseName, 18) + "_" + Hash(v.Label).Substring(0, 4));
                foreach (var cond in conds)
                {
                    var set = new RunSet { Variant = v, Condition = cond, Case = qc, Criteria = qc.Criteria };
                    sets.Add(set);
                    string dir = Path.Combine(caseDir, Short(cond.Name.Replace("°C", "C"), 10) + "_" + Hash(cond.Key).Substring(0, 4));
                    if (Path.GetFullPath(dir).Length + QualCase.PathReserve > 259)
                        throw new Exception($"The study's folders would be too deep for LTspice (more than 260 characters of path): {dir}. Choose a shorter output folder on the Setup tab.");
                    string key = Hash(string.Join("\n", qc.Lines) + "\n" + cond.Key + "\n" + string.Join(" ", plot) + " " + string.Join(" ", v.Save) + "\n" + v.Asc);
                    // the cache: the newest finished campaign made from the same netlist and condition
                    string cached = Directory.Exists(dir)
                        ? Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, "results.csv")) && File.Exists(Path.Combine(d, "study.key")) &&
                                                                  File.ReadAllText(Path.Combine(d, "study.key")).Trim() == key)
                                   .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).FirstOrDefault()
                        : null;
                    if (cached != null) { set.Dir = cached; set.Cached = true; continue; }

                    var setup = p.Clone();
                    setup.Studies.Clear();
                    setup.Runs = cond.Nominal ? 1 : cond.Runs;
                    setup.Paired = cond.Paired;
                    setup.Seed = cond.Seed;
                    setup.WorstCase = WcMethod.Off;
                    setup.Stop.Enabled = false;
                    setup.Report.Auto = false;
                    if (cond.Temp.HasValue) { setup.Temp = TempMode.Corners; setup.Corners = new List<double> { cond.Temp.Value }; }
                    else if (!double.IsNaN(cond.TempMin)) { setup.Temp = TempMode.Random; setup.TempMin = cond.TempMin; setup.TempMax = cond.TempMax; }
                    else setup.Temp = TempMode.Default;
                    foreach (var cr in qc.Criteria)
                    {
                        if (!double.IsNaN(cr.Lo)) setup.Specs[cr.MinMeas] = new SpecLimit { Lsl = cr.Lo, Usl = double.NaN };
                        if (!double.IsNaN(cr.Hi)) setup.Specs[cr.MaxMeas] = new SpecLimit { Lsl = double.NaN, Usl = cr.Hi };
                    }
                    // the waveforms are drawn from the .raw files: keep them, with the stimulus, the judged and the plotted signals
                    bool waves = v.Stimulus != null || qc.Criteria.Count > 0 || plot.Count > 0 || v.Save.Count > 0;
                    if (waves) setup.KeepRaw = true;
                    var sigs = new List<string>();
                    if (v.Stimulus != null && !v.Stimulus.IsFault) sigs.Add($"V({v.MapNet(v.Stimulus.Net)})");
                    if (v.Stimulus != null && v.Stimulus.IsFault) sigs.Add($"V({v.MapNet(v.Stimulus.From)})");
                    sigs.AddRange(qc.Criteria.Select(x => x.Signal));
                    sigs.AddRange(plot.Select(v.Map));
                    sigs.AddRange(v.Save.Select(v.Map));
                    foreach (var s in sigs) if (!setup.Signals.Contains(s, StringComparer.OrdinalIgnoreCase)) setup.Signals.Add(s);
                    // nothing asked for at all (a circuit simulated as drawn): keep every signal, as LTspice does, to look at
                    if (sigs.Count == 0 && setup.Signals.Count == 0) { setup.SaveSelected = false; setup.KeepRaw = true; }

                    var req = new CampaignRequest
                    {
                        AscPath = v.Asc,
                        OutputRoot = dir,
                        Netlist = v.Netlist.WithLines(qc.Lines),
                        Project = setup,
                        NominalOnly = cond.Nominal,
                    };
                    pending++;
                    var thisSet = set;
                    host.Submit($"{ctx.Study.Name}: {v.Label} ({cond.Name})", req, (folder, why) =>
                    {
                        if (folder != null)
                        {
                            thisSet.Dir = folder;
                            try { File.WriteAllText(Path.Combine(folder, "study.key"), key); } catch { }
                        }
                        else { thisSet.Error = why ?? "did not finish"; errors.Add($"{v.Label} ({cond.Name}): {thisSet.Error}"); }
                        if (--pending == 0) Finish();
                    });
                }
            }

            void Finish()
            {
                ctx.Out("data", sets);
                ctx.Cached = sets.All(s => s.Cached);
                if (sets.All(s => s.Dir == null)) { ctx.Done(errors.Count > 0 ? string.Join("; ", errors.Take(3)) : "no campaign finished"); return; }
                foreach (var e in errors) ctx.Log("Simulate: " + e);
                ctx.Done(null);
            }

            ctx.Log($"Simulate: {variants.Count} case{(variants.Count == 1 ? "" : "s")} × {conds.Count} condition{(conds.Count == 1 ? "" : "s")}: " +
                    $"{sets.Count(s => s.Cached)} from the cache, {pending} queued.");
            if (pending == 0) { Finish(); return; }
            ctx.Waiting = true;
        }

        // ------------------------------------------------------------------ evaluation

        static void Limits(BlockContext ctx, IStudyHost host)
        {
            var names = Split(ctx.Block.Get("Criteria"));
            if (names.Count == 0) throw new Exception("Choose the criteria to judge (Criteria).");
            var recs = new List<VerdictRecord>();
            foreach (var set in ctx.All<RunSet>("data").Where(s => s.Dir != null))
            {
                var crit = set.Criteria.Where(c => names.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
                var rs = set.Results;
                foreach (var v in QualVerdict.Of(rs, set.Variant.Label, crit))
                {
                    var c = crit.First(x => x.Name == v.Criterion);
                    recs.Add(new VerdictRecord
                    {
                        Source = ctx.Block.Spec.Title, Candidate = set.Variant.Candidate, Case = set.Variant.CaseName, Condition = set.Condition.Name,
                        Criterion = v.Criterion, Requirement = c.Requirement, Rule = v.Rule, Window = v.Window, Verdict = v.Verdict, Worst = v.WorstText(),
                        Margin = v.Margin, Runs = v.Runs, Failing = v.Failing, Run = set,
                    });
                }
                // a criterion that does not apply to this case (its test filter or window) is left out
            }
            ctx.Out("verdicts", recs);
        }

        static void ErrorBudgetBlock(BlockContext ctx, IStudyHost host)
        {
            var b = ctx.Block;
            var names = Split(b.Get("Errors"));
            var items = names.Count == 0 ? host.Project.Errors : names.Select(n => host.Project.Errors.FirstOrDefault(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                                                                     ?? throw new Exception($"The setup has no error '{n}' (Error Budget page).")).ToList();
            if (items.Count == 0) throw new Exception("The setup defines no error (Error Budget page).");
            double k = Num(b.Get("Sigma"), host.Project.DesignSigma);
            bool pess = b.Get("Pessimistic", "1") == "1";
            var recs = new List<VerdictRecord>();
            foreach (var set in ctx.All<RunSet>("data").Where(s => s.Dir != null))
            {
                var rs = set.Results;
                ErrorBudget.Register(rs, items);
                foreach (var it in items)
                {
                    var r = ErrorBudget.Compute(rs, it, 0, k, pess);
                    recs.Add(new VerdictRecord
                    {
                        Source = b.Spec.Title, Candidate = set.Variant.Candidate, Case = set.Variant.CaseName, Condition = set.Condition.Name,
                        Criterion = it.Name, Rule = $"mean ± {k.ToString("0.#", Inv)} sigma inside the budget",
                        Verdict = r.Error != null ? "error" : r.Verdict, Worst = r.Error ?? r.VerdictText(), Margin = r.Margin, Runs = r.Rows.Length,
                        Failing = r.Outside, Run = set,
                    });
                }
            }
            ctx.Out("verdicts", recs);
        }

        static void Spec(BlockContext ctx)
        {
            var b = ctx.Block;
            var want = Split(b.Get("Measurements").Replace(" ", "|"));
            bool all = want.Count == 0 || want.Any(x => x.Equals("all", StringComparison.OrdinalIgnoreCase));
            var recs = new List<VerdictRecord>();
            foreach (var set in ctx.All<RunSet>("data").Where(s => s.Dir != null))
            {
                var rs = set.Results;
                foreach (var m in rs.Meas.Where(x => all || want.Contains(x, StringComparer.OrdinalIgnoreCase)))
                {
                    var sp = rs.Setup.Spec(m);
                    if (sp == null || !sp.IsSet) continue;
                    var vals = rs.Rows.Select(r => rs.Get(r, m)).Where(x => !double.IsNaN(x)).ToList();
                    if (vals.Count == 0) { recs.Add(new VerdictRecord { Source = b.Spec.Title, Candidate = set.Variant.Candidate, Case = set.Variant.CaseName, Condition = set.Condition.Name, Criterion = m, Verdict = "no data", Run = set }); continue; }
                    int bad = vals.Count(x => (!double.IsNaN(sp.Lsl) && x < sp.Lsl) || (!double.IsNaN(sp.Usl) && x > sp.Usl));
                    double margin = Math.Min(double.IsNaN(sp.Lsl) ? double.PositiveInfinity : vals.Min() - sp.Lsl, double.IsNaN(sp.Usl) ? double.PositiveInfinity : sp.Usl - vals.Max());
                    recs.Add(new VerdictRecord
                    {
                        Source = b.Spec.Title, Candidate = set.Variant.Candidate, Case = set.Variant.CaseName, Condition = set.Condition.Name, Criterion = m,
                        Rule = (double.IsNaN(sp.Lsl) ? "" : $"≥ {ErrorBudget.Eng(sp.Lsl)}") + (double.IsNaN(sp.Lsl) || double.IsNaN(sp.Usl) ? "" : " and ") + (double.IsNaN(sp.Usl) ? "" : $"≤ {ErrorBudget.Eng(sp.Usl)}"),
                        Verdict = bad == 0 ? "PASS" : "FAIL", Worst = $"{ErrorBudget.Eng(vals.Min())} .. {ErrorBudget.Eng(vals.Max())}", Margin = margin,
                        Runs = vals.Count, Failing = bad, Run = set,
                    });
                }
            }
            ctx.Out("verdicts", recs);
        }

        // ------------------------------------------------------------------ output

        static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        /// <summary>The verdicts as grids (one per evaluation: case × condition) and in full, in an HTML page beside the campaigns.</summary>
        static void Report(BlockContext ctx, IStudyHost host)
        {
            var b = ctx.Block;
            var recs = ctx.All<VerdictRecord>("verdicts");
            string title = b.Get("Title").Length > 0 ? b.Get("Title") : ctx.Study.Name;
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(Esc(title)).Append("</title><style>")
              .Append("html{background:#e4e5e9}body{font:14px/1.45 Segoe UI,Arial,sans-serif;color:#1d1d1f;margin:0}")
              .Append(".page{max-width:980px;margin:24px auto;background:#fff;border:1px solid #c9cbd1;box-shadow:0 2px 8px rgba(0,0,0,.08);padding:36px 48px}")
              .Append("h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px;border-bottom:1px solid #ddd;padding-bottom:4px}.muted{color:#666}")
              .Append("table{border-collapse:collapse;width:100%;margin:8px 0 16px;font-size:13px}th,td{border:1px solid #d0d0d0;padding:4px 8px;text-align:left;vertical-align:top}")
              .Append("th{background:#f2f3f5}td.c{text-align:center;white-space:nowrap}.PASS{color:#0a6b0a;font-weight:600}.FAIL{color:#b00000;font-weight:600}.CHECK{color:#8a5a00;font-weight:600}")
              .Append("@media print{html{background:#fff}.page{border:0;box-shadow:none;margin:0;max-width:none}}</style></head><body><div class=\"page\">");
            sb.Append("<h1>").Append(Esc(title)).Append("</h1><div class=\"muted\">Study ").Append(Esc(ctx.Study.Name)).Append(", ")
              .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv)).Append(". SPICA - Simulation Platform for Investigation, Compliance and Analysis.</div>");
            if (ctx.Study.Note.Length > 0) sb.Append("<p>").Append(Esc(ctx.Study.Note).Replace("\n", "<br>")).Append("</p>");
            int pass = recs.Count(r => r.Verdict == "PASS"), fail = recs.Count(r => r.Verdict == "FAIL");
            sb.Append("<p><b>").Append(recs.Count).Append(" verdicts:</b> <span class=\"PASS\">").Append(pass).Append(" pass</span>, <span class=\"FAIL\">")
              .Append(fail).Append(" fail</span>").Append(recs.Count - pass - fail > 0 ? $", {recs.Count - pass - fail} other" : "").Append(".</p>");
            foreach (var g in recs.GroupBy(r => r.Source + " · " + r.Criterion))
            {
                var first = g.First();
                sb.Append("<h2>").Append(Esc(first.Criterion)).Append(" <span class=\"muted\" style=\"font-weight:normal\">(").Append(Esc(first.Source)).Append(")</span></h2>");
                sb.Append("<div class=\"muted\">").Append(Esc(first.Rule)).Append(first.Window.Length > 0 ? ", " + Esc(first.Window) : "")
                  .Append(first.Requirement.Length > 0 ? " · requirement " + Esc(first.Requirement) : "").Append("</div>");
                var rows = g.Select(r => r.Row).Distinct().ToList();
                var cols = g.Select(r => r.Condition).Distinct().ToList();
                sb.Append("<table><tr><th></th>");
                foreach (var c in cols) sb.Append("<th>").Append(Esc(c)).Append("</th>");
                sb.Append("</tr>");
                foreach (var row in rows)
                {
                    sb.Append("<tr><td>").Append(Esc(row)).Append("</td>");
                    foreach (var c in cols)
                    {
                        var r = g.FirstOrDefault(x => x.Row == row && x.Condition == c);
                        if (r == null) { sb.Append("<td class=\"c muted\">-</td>"); continue; }
                        sb.Append("<td class=\"c\"><span class=\"").Append(Esc(r.Verdict)).Append("\">").Append(Esc(r.Verdict)).Append("</span><br><span class=\"muted\">")
                          .Append(Esc(r.Worst)).Append(double.IsNaN(r.Margin) ? "" : ", margin " + Esc(ErrorBudget.Eng(r.Margin)))
                          .Append(r.Runs > 1 ? $", {r.Failing} of {r.Runs} failing" : "").Append("</span></td>");
                    }
                    sb.Append("</tr>");
                }
                sb.Append("</table>");
            }
            var data = ctx.All<RunSet>("data");
            var used = recs.Select(r => r.Run).Concat(data).Where(r => r?.Dir != null).Distinct().ToList();
            if (used.Count > 0)
            {
                sb.Append("<h2>Campaigns</h2><table><tr><th>Case</th><th>Condition</th><th>Folder</th></tr>");
                foreach (var r in used) sb.Append("<tr><td>").Append(Esc(r.Variant.Label)).Append("</td><td>").Append(Esc(r.Condition.Name)).Append("</td><td>").Append(Esc(r.Dir)).Append(r.Cached ? " (from an earlier run)" : "").Append("</td></tr>");
                sb.Append("</table>");
            }
            sb.Append("</div></body></html>");
            string dir = host.StudyFolder(ctx.Study);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, Short(title, 40) + "_report.html");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            host.Wrote("report", path);
        }

        static void Export(BlockContext ctx, IStudyHost host)
        {
            var recs = ctx.All<VerdictRecord>("verdicts");
            string file = ctx.Block.Get("File");
            if (file.Length == 0) file = Path.Combine(host.StudyFolder(ctx.Study), Short(ctx.Study.Name, 40) + "_verdicts.csv");
            string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            var sb = new StringBuilder("evaluation,candidate,case,condition,criterion,requirement,rule,verdict,worst,margin,runs,failing,folder\r\n");
            foreach (var r in recs)
                sb.Append(string.Join(",", Q(r.Source), Q(r.Candidate), Q(r.Case), Q(r.Condition), Q(r.Criterion), Q(r.Requirement), Q(r.Rule), Q(r.Verdict), Q(r.Worst),
                                      double.IsNaN(r.Margin) ? "" : r.Margin.ToString("R", Inv), r.Runs.ToString(Inv), r.Failing.ToString(Inv), Q(r.Run?.Dir))).Append("\r\n");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            host.Wrote("verdicts", file);
        }
    }
}
