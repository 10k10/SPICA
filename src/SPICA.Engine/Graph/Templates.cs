using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Spica.Core.Graph
{
    /// <summary>Ready-made studies to start from instead of a blank canvas. They take the setup's schematic when it has one.</summary>
    public static class StudyTemplates
    {
        public sealed class Template
        {
            public string Name, Description;
            public Func<SpicaProject, Study> Make;
            public override string ToString() => Name;
        }

        public static readonly IReadOnlyList<Template> All = new List<Template>
        {
            new Template { Name = "Blank", Description = "An empty canvas.", Make = p => new Study { Name = "Study" } },
            new Template { Name = "Qualification (DO-160 power input)", Description = "Test waveforms into the supply net, nominal, pass/fail limits, report and plots.", Make = Qualification },
            new Template { Name = "Qualification at corners + worst case", Description = "The same tests at temperature / tolerance corners and at the worst case found for a measurement.", Make = QualificationCorners },
            new Template { Name = "Compare candidates", Description = "The same stimulus and limits on two candidate circuits, side by side.", Make = Compare },
            new Template { Name = "Tolerance (Monte Carlo + error budget)", Description = "Monte Carlo runs, the error budget and the spec limits, and the report.", Make = Tolerance },
            new Template { Name = "Input short", Description = "A short from the input to ground, the parts' stresses against their absolute ratings.", Make = InputShort },
            new Template { Name = "Derating", Description = "Nominal and corners, every part's stress against the internal derating rules.", Make = Derating },
        };

        public static Template Find(string name) => All.FirstOrDefault(t => t.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase));

        static Block Schematic(Study s, SpicaProject p, string name = null)
        {
            var b = s.Add(BlockKind.Schematic).Set("File", p?.Schematic ?? "");
            b.Set("Name", name ?? (string.IsNullOrEmpty(p?.Schematic) ? "" : Path.GetFileNameWithoutExtension(p.Schematic)));
            return b;
        }

        static void Wire(Study s, Block a, Block b, string toPort = "")
        {
            var why = s.Connect(a, b, "", toPort);
            if (why != null) throw new InvalidOperationException($"Template wiring {a.Kind} -> {b.Kind}: {why}");
        }

        static Block Supply(Study s) =>
            s.Add(BlockKind.SupplyWaveform).Set("Library", "do160g_s16_28vdc").Set("Category", "A").Set("Net", "in");

        static Study Qualification(SpicaProject p)
        {
            var s = new Study { Name = "Qualification" };
            var sw = Supply(s);
            var sch = Schematic(s, p);
            var nom = s.Add(BlockKind.Nominal);
            var sim = s.Add(BlockKind.Simulate);
            var lim = s.Add(BlockKind.Limits).Set("Criteria", string.Join(" | ", p?.QualCriteria.Select(c => c.Name) ?? new string[0]));
            var rep = s.Add(BlockKind.Report).Set("Title", "Qualification");
            var plots = s.Add(BlockKind.Plots);
            Wire(s, sw, sch); Wire(s, sch, sim); Wire(s, nom, sim); Wire(s, sim, lim); Wire(s, lim, rep, "verdicts"); Wire(s, sim, plots);
            s.AutoLayout();
            return s;
        }

        static Study QualificationCorners(SpicaProject p)
        {
            var s = new Study { Name = "Qualification at corners" };
            var sw = Supply(s);
            var sch = Schematic(s, p);
            var co = s.Add(BlockKind.Corners).Set("Temperatures", "-55; 25; 85");
            var wc = s.Add(BlockKind.WorstCase).Set("Direction", "min");
            var cb = s.Add(BlockKind.Combine).Set("Mode", "union");
            var sim = s.Add(BlockKind.Simulate);
            var lim = s.Add(BlockKind.Limits).Set("Criteria", string.Join(" | ", p?.QualCriteria.Select(c => c.Name) ?? new string[0]));
            var rep = s.Add(BlockKind.Report).Set("Title", "Qualification at corners");
            var mat = s.Add(BlockKind.RequirementsMatrix);
            var plots = s.Add(BlockKind.Plots);
            Wire(s, sw, sch); Wire(s, sch, wc); Wire(s, co, cb); Wire(s, wc, cb); Wire(s, sch, sim); Wire(s, cb, sim);
            Wire(s, sim, lim); Wire(s, lim, rep, "verdicts"); Wire(s, lim, mat); Wire(s, sim, plots);
            s.AutoLayout();
            return s;
        }

        static Study Compare(SpicaProject p)
        {
            var s = new Study { Name = "Compare candidates" };
            var sw = Supply(s).Set("Net", "IN");
            var a = Schematic(s, p, "A");
            var b = s.Add(BlockKind.Schematic).Set("Name", "B");
            var cand = s.Add(BlockKind.Candidates).Set("Signal", "IN = A: V(in), B: V(in)").Add("Signal", "OUT = A: V(out), B: V(out)");
            var nom = s.Add(BlockKind.Nominal);
            var sim = s.Add(BlockKind.Simulate);
            var lim = s.Add(BlockKind.Limits);
            var cmp = s.Add(BlockKind.Compare);
            var plots = s.Add(BlockKind.Plots).Set("Signals", "IN OUT");
            Wire(s, sw, a); Wire(s, sw, b); Wire(s, a, cand); Wire(s, b, cand); Wire(s, cand, sim); Wire(s, nom, sim);
            Wire(s, sim, lim); Wire(s, lim, cmp, "verdicts"); Wire(s, sim, plots);
            s.AutoLayout();
            return s;
        }

        static Study Tolerance(SpicaProject p)
        {
            var s = new Study { Name = "Tolerance" };
            var sch = Schematic(s, p);
            var mc = s.Add(BlockKind.MonteCarlo).Set("Runs", (p?.Runs ?? 1000).ToString(System.Globalization.CultureInfo.InvariantCulture));
            var sim = s.Add(BlockKind.Simulate);
            var eb = s.Add(BlockKind.ErrorBudget).Set("Errors", string.Join(" | ", p?.Errors.Select(e => e.Name) ?? new string[0]));
            var spec = s.Add(BlockKind.Spec);
            var rep = s.Add(BlockKind.Report);
            Wire(s, sch, sim); Wire(s, mc, sim); Wire(s, sim, eb); Wire(s, sim, spec);
            Wire(s, eb, rep, "verdicts"); Wire(s, spec, rep, "verdicts"); Wire(s, sim, rep, "data");
            s.AutoLayout();
            return s;
        }

        static Study InputShort(SpicaProject p)
        {
            var s = new Study { Name = "Input short" };
            var f = s.Add(BlockKind.Fault).Set("From", "in").Set("To", "0").Set("Start", "10m").Set("Duration", "100m");
            var sch = Schematic(s, p);
            var nom = s.Add(BlockKind.Nominal);
            var sim = s.Add(BlockKind.Simulate);
            var st = s.Add(BlockKind.Stress);
            var db = s.Add(BlockKind.PartsDatabase);
            var sv = s.Add(BlockKind.Survival);
            var rep = s.Add(BlockKind.Report).Set("Title", "Input short");
            Wire(s, f, sch); Wire(s, sch, sim); Wire(s, nom, sim); Wire(s, sim, st); Wire(s, st, sv); Wire(s, db, sv); Wire(s, sv, rep, "verdicts");
            s.AutoLayout();
            return s;
        }

        static Study Derating(SpicaProject p)
        {
            var s = new Study { Name = "Derating" };
            var sch = Schematic(s, p);
            var nom = s.Add(BlockKind.Nominal);
            var co = s.Add(BlockKind.Corners).Set("Temperatures", "-55; 85");
            var cb = s.Add(BlockKind.Combine).Set("Mode", "union");
            var sim = s.Add(BlockKind.Simulate);
            var st = s.Add(BlockKind.Stress);
            var db = s.Add(BlockKind.PartsDatabase);
            var de = s.Add(BlockKind.Derating);
            var rep = s.Add(BlockKind.Report).Set("Title", "Derating");
            Wire(s, nom, cb); Wire(s, co, cb); Wire(s, sch, sim); Wire(s, cb, sim); Wire(s, sim, st); Wire(s, st, de); Wire(s, db, de); Wire(s, de, rep, "verdicts");
            s.AutoLayout();
            return s;
        }
    }
}
