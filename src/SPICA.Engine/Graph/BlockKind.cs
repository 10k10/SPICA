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
            public Def In(string n, PortType[] t, bool many = false, bool optional = false, string label = null) { k.Inputs.Add(new PortSpec(n, t, many, optional, label)); return this; }
            public Def In(string n, PortType t) => In(n, new[] { t });
            public Def Out(string n, PortType t) { k.Outputs.Add(new PortSpec(n, new[] { t })); return this; }
            public Def P(string key, string def = "") { k.Defaults.Add(new KeyValuePair<string, string>(key, def)); return this; }
        }

        public const string Schematic = "Schematic", Tolerances = "Tolerances", Temperature = "Temperature", Simulate = "Simulate";

        /// <summary>The Tolerances block's modes (its Mode setting).</summary>
        public const string Nominal = "Nominal", MonteCarlo = "Monte Carlo", WorstCase = "Worst Case";

        /// <summary>The Temperature block's modes and distributions (its Mode and Distribution settings).</summary>
        public const string TempList = "List", TempCorners = "Corners", TempRandom = "Random", Uniform = "Uniform", Normal = "Normal";

        static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether a setting is one the block's mode uses (the inspector shows only those; the others are kept).</summary>
        public static bool Uses(Block b, string key)
        {
            string mode = b.Get("Mode");
            if (b.Kind == Tolerances)
                switch (key)
                {
                    case "Runs": case "Seed": case "Paired": return Is(mode, MonteCarlo);
                    case "Sigma": return Is(mode, WorstCase);
                    default: return true;
                }
            if (b.Kind == Temperature) return TempUses(b, key);
            return true;
        }

        /// <summary>
        /// The settings of a Temperature block its mode uses (the others are kept, unused): List its points; Corners the
        /// range and its nominal; Random uniform the range; Random normal the mean (Nominal) and deviation (Sigma).
        /// </summary>
        static bool TempUses(Block b, string key)
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
            const PortType C = PortType.Circuit, K = PortType.Tolerances, T = PortType.Temperature;

            new Def(Schematic, BlockFamily.Circuit, "Schematic", "An LTspice schematic (.asc), simulated as drawn.")
                .Out("circuit", C).P("File");

            // the conditions, two independent ones: how the parts vary, and the temperature
            new Def(Tolerances, BlockFamily.Conditions, "Tolerances", "How the parts vary: every tolerance at its nominal value, N units drawn from them (Monte Carlo), or every combination of their edges (worst case, 1 + 2^n units).")
                .Out("tolerances", K).P("Mode", Nominal).P("Runs", "100").P("Seed", "0").P("Paired", "1").P("Sigma", "3");
            new Def(Temperature, BlockFamily.Conditions, "Temperature", "The temperatures the circuit runs at: a list, the corners of a range, or one drawn at random for each Monte Carlo unit.")
                .Out("temperature", T).P("Mode", TempList).P("Distribution", Uniform).P("Points", "-55; 25; 85").P("Min", "-40").P("Nominal", "25").P("Max", "85")
                .P("Sigma", "20");

            new Def(Simulate, BlockFamily.Simulation, "Simulate", "Runs LTspice: every set of tolerances wired in at every temperature wired in, one queue campaign each. None wired: nominal, at the schematic's temperature. Reuses earlier results of the same netlist.")
                .In("circuit", C).In("conditions", new[] { K, T }, many: true, optional: true, label: "Conditions").P("Save");
        }
    }
}
