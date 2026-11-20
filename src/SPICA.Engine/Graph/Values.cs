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

        public string Key => string.Join("|", Kind, Runs, Seed, Paired, Sigma.ToString("R", CultureInfo.InvariantCulture),
                                         string.Join(";", Temps.Select(t => t.ToString("R", CultureInfo.InvariantCulture))));
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
