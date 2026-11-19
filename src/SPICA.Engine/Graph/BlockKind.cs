using System;
using System.Collections.Generic;
using System.Linq;

namespace Spica.Core.Graph
{
    /// <summary>The families of blocks, in the order the library lists them.</summary>
    public enum BlockFamily { Stimulus, Circuit, Variation, Simulation, Collection, Evaluation, External, Output }

    /// <summary>
    /// A kind of block: its ports and its properties with their defaults. The list (docs/ARCHITECTURE.md, section 4)
    /// is the one place that says what can be wired to what. Block properties name definitions kept in the project
    /// (errors, criteria, specs) instead of copying them, so that one criterion serves several studies.
    /// </summary>
    public sealed class BlockKind
    {
        public string Name;
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
            public Def(string name, BlockFamily fam, string summary) { k = new BlockKind { Name = name, Family = fam, Summary = summary }; all.Add(k); }
            public Def In(string n, PortType t, bool many = false, bool optional = false) { k.Inputs.Add(new PortSpec(n, t, many, optional)); return this; }
            public Def Out(string n, PortType t) { k.Outputs.Add(new PortSpec(n, t)); return this; }
            public Def P(string key, string def = "") { k.Defaults.Add(new KeyValuePair<string, string>(key, def)); return this; }
        }

        public const string Schematic = "Schematic", Candidates = "Candidates", SupplyWaveform = "SupplyWaveform", Fault = "Fault",
            Susceptibility = "Susceptibility", Measure = "Measure", Nominal = "Nominal", MonteCarlo = "MonteCarlo", Corners = "Corners",
            WorstCase = "WorstCase", Combine = "Combine", Simulate = "Simulate", Stress = "Stress", Spectrum = "Spectrum", Limits = "Limits",
            ErrorBudget = "ErrorBudget", Spec = "Spec", Survival = "Survival", Derating = "Derating", SusceptibilityCheck = "SusceptibilityCheck",
            PartsDatabase = "PartsDatabase", Report = "Report", RequirementsMatrix = "RequirementsMatrix", Plots = "Plots", Compare = "Compare", Export = "Export";

        static BlockKind()
        {
            const PortType S = PortType.Stimulus, C = PortType.Circuit, K = PortType.Conditions, D = PortType.Data, T = PortType.Table, R = PortType.Ratings, V = PortType.Verdicts;
            var st = BlockFamily.Stimulus; var ci = BlockFamily.Circuit; var va = BlockFamily.Variation; var si = BlockFamily.Simulation;
            var co = BlockFamily.Collection; var ev = BlockFamily.Evaluation; var ex = BlockFamily.External; var ou = BlockFamily.Output;

            // stimuli: what is applied, and where; they feed a schematic
            new Def(SupplyWaveform, st, "A test waveform from a library (e.g. DO-160 Section 16) driving a net, in place of its source; one case per test.")
                .Out("stimulus", S).P("Library").P("Category").P("Net").P("Rser", "0").P("Tests");
            new Def(Fault, st, "A short or an open between two nets from a time on (input to GND, to 28 V...); one case per fault.")
                .Out("stimulus", S).P("From").P("To", "0").P("R", "10m").P("Start", "0").P("Duration");
            new Def(Susceptibility, st, "Noise injected into a net through a coupling model (sine, fixed or swept, or pulses).")
                .Out("stimulus", S).P("Net").P("Signal").P("Coupling").P("Level");

            // circuit
            new Def(Schematic, ci, "An .asc, with the stimuli wired to it; its name is the candidate's name.")
                .In("stimulus", S, many: true, optional: true).Out("circuit", C).P("File").P("Name");
            new Def(Candidates, ci, "Several schematics as one circuit wire; logical signals map each one's own net names.")
                .In("candidates", C, many: true).Out("circuit", C).P("Signal");
            new Def(Measure, ci, "Adds .meas statements and signals to save.")
                .In("circuit", C).Out("circuit", C).P("Meas").P("Save");

            // variation
            new Def(Nominal, va, "One condition: every tolerance at its nominal value.")
                .Out("conditions", K).P("Temperature");
            new Def(MonteCarlo, va, "N units drawn from the tolerances.")
                .Out("conditions", K).P("Runs", "1000").P("Paired", "0").P("Seed", "0").P("Temperature").P("Stop");
            new Def(Corners, va, "Named conditions: temperatures and tolerance edges.")
                .Out("conditions", K).P("Temperatures").P("Edges");
            new Def(WorstCase, va, "Finds the tolerance and temperature point that pushes a measurement furthest, and gives it as a condition.")
                .In("circuit", C).Out("conditions", K).P("Objective").P("Direction", "min").P("Method").P("Sigma", "3");
            new Def(Combine, va, "Union or product of condition sets.")
                .In("conditions", K, many: true).Out("conditions", K).P("Mode", "product");

            // simulation
            new Def(Simulate, si, "Runs LTspice: every case of the circuit at every condition, as queue campaigns (local or remote); reuses cached results.")
                .In("circuit", C).In("conditions", K).Out("data", D).P("Where", "local").P("KeepRaw", "1").P("Save");

            // collection
            new Def(Stress, co, "Voltage, current and power of every part (or chosen ones), from the waveforms.")
                .In("data", D).Out("table", T).P("Parts", "all").P("Quantities", "V I P").P("Window");
            new Def(Spectrum, co, "FFT of a signal.")
                .In("data", D).Out("table", T).P("Signal").P("Window", "hann");

            // evaluation: the definitions they name live in the project
            new Def(Limits, ev, "A signal above, below or between limits inside a window anchored to the test's events.")
                .In("data", D).Out("verdicts", V).P("Criteria");
            new Def(ErrorBudget, ev, "The error budget: mean ± k sigma of each error against its budget.")
                .In("data", D).Out("verdicts", V).P("Errors").P("Sigma", "6").P("Pessimistic", "1");
            new Def(Spec, ev, "Spec limits per measurement, yield and Cpk.")
                .In("data", D).Out("verdicts", V).P("Measurements", "all");
            new Def(Survival, ev, "Absolute maximum ratings not exceeded.")
                .In("table", T).In("ratings", R).Out("verdicts", V);
            new Def(Derating, ev, "Stress within the allowed share of each rating (rules file of the internal standard).")
                .In("table", T).In("ratings", R).Out("verdicts", V).P("Rules");
            new Def(SusceptibilityCheck, ev, "Output deviation during an injection within its limit.")
                .In("data", D).Out("verdicts", V).P("Signal").P("Deviation");

            // external data
            new Def(PartsDatabase, ex, "Component ratings by reference designator, from the user's adapter (part numbers in, ratings out).")
                .Out("ratings", R).P("Adapter").P("Bom");

            // output
            new Def(Report, ou, "An HTML report section per input.")
                .In("verdicts", V, many: true, optional: true).In("data", D, many: true, optional: true).P("Title").P("Style", "Full").P("Sections");
            new Def(RequirementsMatrix, ou, "Requirement x condition, with verdict, margin and evidence.")
                .In("verdicts", V, many: true);
            new Def(Plots, ou, "The waveforms: the stimulus on top, one plot per criterion or signal; candidates overlaid.")
                .In("data", D).P("Signals");
            new Def(Compare, ou, "Candidates side by side: verdicts, margins, distributions and waveforms.")
                .In("verdicts", V, many: true, optional: true).In("data", D, optional: true).P("Measurements");
            new Def(Export, ou, "CSV of the verdicts or the data.")
                .In("verdicts", V, many: true, optional: true).In("data", D, optional: true).P("File");
        }
    }
}
