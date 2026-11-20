using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Spica.Core.Graph
{
    /// <summary>A circuit ready to simulate: a schematic and its netlist.</summary>
    public sealed class Circuit
    {
        public string Name = "";
        public string Asc = "";
        public Netlist Netlist;
    }

    /// <summary>
    /// The temperatures an analysis runs at, from a Temperature block: fixed points (a list, or the corners of a range),
    /// at each of which every unit runs; or one drawn at random for each unit.
    /// </summary>
    public sealed class TempSpec
    {
        public List<double> Points = new List<double>();   // fixed: every unit at each
        public TempDraw Random;                             // drawn: one per unit (Monte Carlo only)
        public string Text = "";                            // in a few words, for the names of the campaigns

        /// <summary>How many temperatures each unit runs at.</summary>
        public int Count => Random != null ? 1 : Math.Max(1, Points.Count);
    }

    /// <summary>One analysis a circuit runs under: how the tolerances are set, and at which temperatures.</summary>
    public sealed class Condition
    {
        public string Name = "nominal";
        public RunKind Kind = RunKind.Nominal;
        public int Runs = 1;
        public uint Seed;
        public bool Paired;
        public double Sigma = 3;
        public List<double> Temps = new List<double>();     // empty = the schematic's own temperature
        public TempDraw RandomTemp;                         // or one drawn for each run

        public string Key => string.Join("|", Kind, Runs, Seed, Paired, Sigma.ToString("R", CultureInfo.InvariantCulture),
                                         string.Join(";", Temps.Select(t => t.ToString("R", CultureInfo.InvariantCulture)))) +
                             (RandomTemp != null ? "|" + RandomTemp.Key : "");
    }

    /// <summary>The campaign of one circuit under one condition: where its results are.</summary>
    public sealed class RunSet
    {
        public Circuit Circuit;
        public Condition Condition;
        public string Dir;                          // the campaign folder (results.csv, chunks with the .raw files)
        public bool Cached;
        public string Error;

        public string Label => Condition.Name;

        ResultSet results;
        public ResultSet Results => results ?? (results = Dir == null ? null : ResultSet.Load(Dir));
    }
}
