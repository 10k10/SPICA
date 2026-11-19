using System;
using System.Collections.Generic;
using System.Linq;

namespace Spica.Core.Graph
{
    /// <summary>One thing a stimulus block applies: a library test into a net, or a fault between two nets.</summary>
    public sealed class StimulusCase
    {
        public string Name = "";
        // a supply waveform
        public QualLibrary Library;
        public QualTest Test;
        public string Category = "";
        public string Net = "";
        public double Rser;
        // a fault: a switch from From to To, closed from Start for Duration (0 = to the end), with resistance R
        public bool IsFault;
        public string From = "", To = "0";
        public double R = 0.01, Start, Duration;
    }

    /// <summary>A circuit made ready to simulate: a candidate schematic with one stimulus case applied (or none).</summary>
    public sealed class CircuitVariant
    {
        public string Candidate = "";
        public string Asc = "";
        public Netlist Netlist;
        public StimulusCase Stimulus;
        /// <summary>Logical signal → this candidate's own (OUT → V(vo)); empty when there is one candidate.</summary>
        public Dictionary<string, string> Signals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string CaseName => Stimulus?.Name ?? "as drawn";
        public string Label => (Candidate.Length > 0 ? Candidate + " · " : "") + CaseName;

        /// <summary>A signal or net named the logical way, in this candidate's own names.</summary>
        public string Map(string s)
        {
            if (string.IsNullOrEmpty(s) || Signals.Count == 0) return s;
            if (Signals.TryGetValue(s, out var v)) return v;
            // V(OUT) where OUT is logical
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^([VI][a-z]{0,2})\((.+)\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && Signals.TryGetValue(m.Groups[2].Value, out var inner))
            {
                var mi = System.Text.RegularExpressions.Regex.Match(inner, @"^[VI][a-z]{0,2}\((.+)\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return mi.Success ? inner : $"{m.Groups[1].Value}({inner})";
            }
            return s;
        }

        /// <summary>A net named the logical way, as this candidate's net (IN → vin, when IN = V(vin)).</summary>
        public string MapNet(string net)
        {
            var v = Map(net);
            var m = System.Text.RegularExpressions.Regex.Match(v ?? "", @"^V\((.+)\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : v;
        }
    }

    /// <summary>One condition a circuit runs at: nominal or N Monte Carlo runs, at a temperature or the schematic's own.</summary>
    public sealed class Condition
    {
        public string Name = "nominal";
        public bool Nominal = true;
        public int Runs = 1;
        public bool Paired;
        public uint Seed;
        public double? Temp;                       // °C; null = as in the schematic
        public double TempMin = double.NaN, TempMax = double.NaN;   // random temperature per run, when both are set

        public Condition Clone() => (Condition)MemberwiseClone();

        public string Key => $"{(Nominal ? "nom" : "mc" + Runs)}|{Paired}|{Seed}|{Temp}|{TempMin}|{TempMax}";
    }

    /// <summary>The finished campaign of one variant at one condition, with what was measured for the criteria.</summary>
    public sealed class RunSet
    {
        public CircuitVariant Variant;
        public Condition Condition;
        public string Dir;                          // the campaign folder (results.csv, chunks with the .raw files)
        public QualCase Case;                       // the supply waveform, its events and the criteria as built
        public List<QualCriterion> Criteria = new List<QualCriterion>();   // in the candidate's own signal names
        public bool Cached;
        public string Error;

        ResultSet results;
        public ResultSet Results => results ?? (results = Dir == null ? null : ResultSet.Load(Dir));
    }

    /// <summary>A verdict of an evaluation block on one variant at one condition: the same kind of record for every evaluation.</summary>
    public sealed class VerdictRecord
    {
        public string Source = "";                  // the evaluation block's title
        public string Candidate = "", Case = "", Condition = "";
        public string Criterion = "", Requirement = "", Rule = "", Window = "";
        public string Verdict = "-";                // PASS, FAIL, CHECK, "no data"
        public string Worst = "";
        public double Margin = double.NaN;
        public int Runs, Failing;
        public RunSet Run;

        public string Row => (Candidate.Length > 0 ? Candidate + " · " : "") + Case;
    }
}
