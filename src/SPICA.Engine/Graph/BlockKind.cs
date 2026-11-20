using System;
using System.Collections.Generic;
using System.Linq;

namespace Spica.Core.Graph
{
    /// <summary>The families of blocks, in the order the library lists them (left to right in a study).</summary>
    public enum BlockFamily { Circuit, Analysis, Simulation }

    /// <summary>
    /// A kind of block: its ports and its properties with their defaults. This list is the one place that says what
    /// can be wired to what.
    /// </summary>
    public sealed class BlockKind
    {
        public string Name;
        public string Title;              // shown in the editor: "Monte Carlo"
        public BlockFamily Family;
        public string Summary;
        public readonly List<PortSpec> Inputs = new List<PortSpec>();
        public readonly List<PortSpec> Outputs = new List<PortSpec>();
        public readonly List<KeyValuePair<string, string>> Defaults = new List<KeyValuePair<string, string>>();

        public PortSpec Input(string name) => Inputs.FirstOrDefault(p => p.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase));
        public PortSpec Output(string name) => Outputs.FirstOrDefault(p => p.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase));

        static readonly List<BlockKind> all = new List<BlockKind>();
        public static IReadOnlyList<BlockKind> All => all;

        public static BlockKind Find(string name) => all.FirstOrDefault(k => k.Name.Equals((name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ the catalogue

        sealed class Def
        {
            readonly BlockKind k;
            public Def(string name, BlockFamily fam, string title, string summary)
            {
                k = new BlockKind { Name = name, Family = fam, Title = title, Summary = summary };
                all.Add(k);
            }
            public Def In(string n, PortType t, bool many = false) { k.Inputs.Add(new PortSpec(n, t, many)); return this; }
            public Def Out(string n, PortType t) { k.Outputs.Add(new PortSpec(n, t)); return this; }
            public Def P(string key, string def = "") { k.Defaults.Add(new KeyValuePair<string, string>(key, def)); return this; }
        }

        public const string Schematic = "Schematic", Nominal = "Nominal", MonteCarlo = "MonteCarlo", WorstCase = "WorstCase", Simulate = "Simulate";

        static BlockKind()
        {
            const PortType C = PortType.Circuit, K = PortType.Conditions;

            new Def(Schematic, BlockFamily.Circuit, "Schematic", "An LTspice schematic (.asc), simulated as drawn.")
                .Out("circuit", C).P("File");

            new Def(Nominal, BlockFamily.Analysis, "Nominal", "One run with every tolerance at its nominal value.")
                .Out("conditions", K).P("Temperature");
            new Def(MonteCarlo, BlockFamily.Analysis, "Monte Carlo", "N units drawn from the tolerances.")
                .Out("conditions", K).P("Runs", "100").P("Seed", "0").P("Paired", "0").P("Temperature");
            new Def(WorstCase, BlockFamily.Analysis, "Worst Case", "The nominal unit, then every combination of the tolerance edges (2^n runs).")
                .Out("conditions", K).P("Sigma", "3").P("Temperature");

            new Def(Simulate, BlockFamily.Simulation, "Simulate", "Runs LTspice: the circuit under every analysis wired in, as queue campaigns; reuses earlier results of the same netlist.")
                .In("circuit", C).In("conditions", K, many: true).P("Save");
        }
    }
}
