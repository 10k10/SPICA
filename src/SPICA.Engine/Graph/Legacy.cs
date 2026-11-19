using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Spica.Core.Graph
{
    /// <summary>
    /// The studies an older setup (no [Study] sections) stands for, so that nothing is lost when it opens in the
    /// graph editor:
    /// - the tolerance campaign: Schematic → Simulate ← Monte Carlo or Worst Case (× Corners), then
    ///   Error Budget / Spec → Report;
    /// - the qualification, when tests are ticked: Supply Waveform per standard → Schematic → Simulate
    ///   ← Nominal or Monte Carlo (× Corners), then Limits → Report and Plots.
    /// </summary>
    public static class LegacyStudies
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string N(double d) => SpicaProject.Num(d);

        public static List<Study> From(SpicaProject p)
        {
            var list = new List<Study>();
            list.Add(Tolerance(p));
            var q = Qualification(p);
            if (q != null) list.Add(q);
            return list;
        }

        static Block NewSchematic(Study s, SpicaProject p)
        {
            var sch = s.Add(BlockKind.Schematic);
            sch.Set("File", p.Schematic ?? "");
            sch.Set("Name", string.IsNullOrEmpty(p.Schematic) ? "" : Path.GetFileNameWithoutExtension(p.Schematic));
            return sch;
        }

        /// <summary>The temperature setting: corners become a Corners block combined (product) with the variation; random goes into it.</summary>
        static Block WithTemperature(Study s, SpicaProject p, Block variation)
        {
            if (p.Temp == TempMode.Random) { variation.Set("Temperature", $"random {N(p.TempMin)} .. {N(p.TempMax)}"); return variation; }
            if (p.Temp != TempMode.Corners) return variation;
            var corners = s.Add(BlockKind.Corners).Set("Temperatures", string.Join("; ", p.Corners.Select(N)));
            var comb = s.Add(BlockKind.Combine).Set("Mode", "product");
            s.Connect(variation, comb);
            s.Connect(corners, comb);
            return comb;
        }

        static Study Tolerance(SpicaProject p)
        {
            var s = new Study { Name = "Tolerance", Note = "Made from the setup's Setup tab (tolerance campaign)." };
            var sch = NewSchematic(s, p);
            Block variation;
            if (p.WorstCase != WcMethod.Off)
                variation = s.Add(BlockKind.WorstCase).Set("Method", p.WorstCase.ToString()).Set("Sigma", N(p.WcSigma)).Set("Objective", "every error and measurement");
            else
            {
                variation = s.Add(BlockKind.MonteCarlo).Set("Runs", p.Runs.ToString(Inv)).Set("Paired", p.Paired ? "1" : "0").Set("Seed", p.Seed.ToString(Inv));
                if (p.Stop.Enabled) variation.Set("Stop", $"spread {N(p.Stop.SpreadPct)} %, yield {N(p.Stop.YieldPct)} %, at least {p.Stop.MinRuns} runs");
            }
            if (variation.Kind == BlockKind.WorstCase) s.Connect(sch, variation);
            var cond = WithTemperature(s, p, variation);

            var sim = s.Add(BlockKind.Simulate).Set("KeepRaw", p.KeepRaw ? "1" : "0").Set("Save", p.SaveSelected ? string.Join(" ", p.Signals) : "all");
            s.Connect(sch, sim);
            s.Connect(cond, sim);

            var report = s.Add(BlockKind.Report).Set("Title", p.Report.Title).Set("Style", p.Report.Kind.ToString());
            if (p.Errors.Count > 0)
            {
                var eb = s.Add(BlockKind.ErrorBudget).Set("Errors", string.Join(" | ", p.Errors.Select(e => e.Name)))
                          .Set("Sigma", N(p.DesignSigma)).Set("Pessimistic", p.PessimisticSigma ? "1" : "0");
                s.Connect(sim, eb);
                s.Connect(eb, report, "", "verdicts");
            }
            if (p.Specs.Values.Any(x => x.IsSet))
            {
                var spec = s.Add(BlockKind.Spec).Set("Measurements", "all");
                s.Connect(sim, spec);
                s.Connect(spec, report, "", "verdicts");
            }
            s.Connect(sim, report, "", "data");
            s.AutoLayout();
            return s;
        }

        static Study Qualification(SpicaProject p)
        {
            var chosen = p.Qual.Where(kv => kv.Value.Tests.Count > 0).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
            if (chosen.Count == 0) return null;
            var s = new Study { Name = "Qualification", Note = "Made from the setup's Qualification tab." };
            // the stimuli first, numbered left to right as they are drawn
            var stimuli = chosen.Select(kv => s.Add(BlockKind.SupplyWaveform).Set("Library", kv.Key).Set("Category", kv.Value.Category)
                                               .Set("Net", kv.Value.Net).Set("Rser", N(kv.Value.Rser)).Set("Tests", string.Join(" | ", kv.Value.Tests))).ToList();
            var sch = NewSchematic(s, p);
            foreach (var sw in stimuli) s.Connect(sw, sch);
            var variation = p.QualNominal ? s.Add(BlockKind.Nominal)
                                          : s.Add(BlockKind.MonteCarlo).Set("Runs", p.QualRuns.ToString(Inv)).Set("Paired", p.Paired ? "1" : "0");
            var cond = WithTemperature(s, p, variation);
            var sim = s.Add(BlockKind.Simulate).Set("KeepRaw", "1");
            s.Connect(sch, sim);
            s.Connect(cond, sim);
            var lim = s.Add(BlockKind.Limits).Set("Criteria", string.Join(" | ", p.QualCriteria.Select(c => c.Name)));
            s.Connect(sim, lim);
            var report = s.Add(BlockKind.Report).Set("Title", "Qualification");
            s.Connect(lim, report, "", "verdicts");
            var plots = s.Add(BlockKind.Plots).Set("Signals", string.Join(" ", p.QualPlot));
            s.Connect(sim, plots);
            s.AutoLayout();
            return s;
        }
    }
}
