using System;
using System.Collections.Generic;
using System.Linq;

namespace Spica.Core.Graph
{
    /// <summary>The families of blocks, in the order the library lists them (left to right in a study).</summary>
    public enum BlockFamily { Circuit, Conditions, Simulation }

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
            public Def In(string n, PortType t, bool many = false, bool optional = false) { k.Inputs.Add(new PortSpec(n, t, many, optional)); return this; }
            public Def Out(string n, PortType t) { k.Outputs.Add(new PortSpec(n, t)); return this; }
            public Def P(string key, string def = "") { k.Defaults.Add(new KeyValuePair<string, string>(key, def)); return this; }
        }

        public const string Schematic = "Schematic", Temperature = "Temperature", Nominal = "Nominal", MonteCarlo = "MonteCarlo", WorstCase = "WorstCase",
                            Simulate = "Simulate";

        /// <summary>The Temperature block's modes and distributions (its Mode and Distribution settings).</summary>
        public const string TempList = "List", TempCorners = "Corners", TempRandom = "Random", Uniform = "Uniform", Normal = "Normal";

        /// <summary>
        /// The settings of a Temperature block its mode uses (the others are kept, unused): List its points; Corners the
        /// range and its nominal; Random uniform the range; Random normal the mean (Nominal) and deviation (Sigma).
        /// </summary>
        public static bool TempUses(Block b, string key)
        {
            string mode = b.Get("Mode", TempList);
            bool normal = b.Get("Distribution", Uniform).Equals(Normal, StringComparison.OrdinalIgnoreCase);
            switch (key)
            {
                case "Mode": return true;
                case "Points": return mode.Equals(TempList, StringComparison.OrdinalIgnoreCase);
                case "Distribution": return mode.Equals(TempRandom, StringComparison.OrdinalIgnoreCase);
                case "Min": case "Max": return mode.Equals(TempCorners, StringComparison.OrdinalIgnoreCase) || (mode.Equals(TempRandom, StringComparison.OrdinalIgnoreCase) && !normal);
                case "Nominal": return mode.Equals(TempCorners, StringComparison.OrdinalIgnoreCase) || (mode.Equals(TempRandom, StringComparison.OrdinalIgnoreCase) && normal);
                case "Sigma": return mode.Equals(TempRandom, StringComparison.OrdinalIgnoreCase) && normal;
                default: return true;
            }
        }

        static BlockKind()
        {
            const PortType C = PortType.Circuit, K = PortType.Conditions, T = PortType.Temperature;

            new Def(Schematic, BlockFamily.Circuit, "Schematic", "An LTspice schematic (.asc), simulated as drawn.")
                .Out("circuit", C).P("File");

            // the conditions: the temperature, and how the tolerances are set (each takes a temperature)
            new Def(Temperature, BlockFamily.Conditions, "Temperature", "The temperatures an analysis runs at: a list, the corners of a range, or one drawn at random for each Monte Carlo unit.")
                .Out("temperature", T).P("Mode", TempList).P("Distribution", Uniform).P("Points", "-55; 25; 85").P("Min", "-40").P("Nominal", "25").P("Max", "85")
                .P("Sigma", "20");
            new Def(Nominal, BlockFamily.Conditions, "Nominal", "One unit with every tolerance at its nominal value, at each temperature.")
                .In("temperature", T, optional: true).Out("conditions", K);
            new Def(MonteCarlo, BlockFamily.Conditions, "Monte Carlo", "N units drawn from the tolerances, each at every temperature (or at its own, drawn).")
                .In("temperature", T, optional: true).Out("conditions", K).P("Runs", "100").P("Seed", "0").P("Paired", "0");
            new Def(WorstCase, BlockFamily.Conditions, "Worst Case", "The nominal unit, then every combination of the tolerance edges (1 + 2^n units), at each temperature.")
                .In("temperature", T, optional: true).Out("conditions", K).P("Sigma", "3");

            new Def(Simulate, BlockFamily.Simulation, "Simulate", "Runs LTspice: the circuit under every condition wired in, as queue campaigns; reuses earlier results of the same netlist.")
                .In("circuit", C).In("conditions", K, many: true).P("Save");
        }
    }
}
