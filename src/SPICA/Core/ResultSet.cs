using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Spica.Core
{
    /// <summary>
    /// The results of one campaign in analysable form: one row per run and temperature, numeric
    /// values per measurement, plus the setup (spec limits, TF definition) used to judge them.
    /// Built from a running engine or loaded from a campaign folder (results.csv + campaign.spica).
    /// </summary>
    public sealed class ResultSet
    {
        public sealed class Row
        {
            public int Run;
            public double Temp = double.NaN;
            public int Corner;
            public RunStatus Status;
            public double[] V;
            public string[] Text;
            public string Note = "";
        }

        public const string TempColumn = "TEMP";
        public const string RunColumn = "run";

        public string Folder;
        public string Title = "";
        public readonly List<string> Meas = new List<string>();
        public readonly List<Row> Rows = new List<Row>();
        public SpicaProject Setup = new SpicaProject();
        public TempMode Mode = TempMode.Default;
        public readonly List<double> Corners = new List<double>();
        public bool Paired;
        public uint Seed;

        public bool HasTemp => Mode != TempMode.Default;

        public int Col(string meas)
        {
            for (int i = 0; i < Meas.Count; i++) if (Meas[i].Equals(meas, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>Columns that can be analysed: measurements, then TEMP (if it varies).</summary>
        public List<string> NumericColumns()
        {
            var l = new List<string>(Meas);
            if (HasTemp) l.Add(TempColumn);
            return l;
        }

        // ------------------------------------------------------------------ groups

        /// <summary>Group 0 is every row; groups 1..n are the temperature corners.</summary>
        public List<string> GroupNames()
        {
            var g = new List<string> { Mode == TempMode.Corners ? "All temperatures" : "All runs" };
            if (Mode == TempMode.Corners) foreach (var t in Corners) g.Add(TempLabel(t));
            return g;
        }

        public static string TempLabel(double t) => t.ToString("0.###", CultureInfo.InvariantCulture) + " °C";

        public IEnumerable<Row> InGroup(int group) =>
            group <= 0 || Mode != TempMode.Corners ? Rows : Rows.Where(r => r.Corner == group - 1);

        public double Get(Row r, string column)
        {
            if (column == TempColumn) return r.Temp;
            if (column == RunColumn) return r.Run;
            int c = Col(column);
            return c >= 0 && r.Status == RunStatus.Ok ? r.V[c] : double.NaN;
        }

        public double[] Values(string column, int group) =>
            InGroup(group).Select(r => Get(r, column)).Where(v => !double.IsNaN(v)).ToArray();

        /// <summary>Paired (x, y) arrays for rows where both exist; also returns the row temperatures.</summary>
        public void Pairs(string xCol, string yCol, int group, out double[] x, out double[] y, out double[] t)
        {
            var xs = new List<double>(); var ys = new List<double>(); var ts = new List<double>();
            foreach (var r in InGroup(group))
            {
                double a = Get(r, xCol), b = Get(r, yCol);
                if (double.IsNaN(a) || double.IsNaN(b)) continue;
                xs.Add(a); ys.Add(b); ts.Add(r.Temp);
            }
            x = xs.ToArray(); y = ys.ToArray(); t = ts.ToArray();
        }

        /// <summary>Spec verdict of one row: true pass, false fail, null when no spec applies.</summary>
        public bool? RowPasses(Row r)
        {
            bool any = false;
            for (int i = 0; i < Meas.Count; i++)
            {
                var s = Setup.Spec(Meas[i]);
                if (s == null || !s.IsSet) continue;
                any = true;
                if (r.Status != RunStatus.Ok || !s.Passes(r.V[i])) return false;
            }
            return any ? true : (bool?)null;
        }

        public bool AnySpec => Meas.Any(m => Setup.Spec(m)?.IsSet == true);

        // ------------------------------------------------------------------ construction

        public static ResultSet FromResults(IList<RunResult> list, IList<string> meas, CampaignOptions opt, SpicaProject setup)
        {
            var rs = new ResultSet
            {
                Folder = opt.OutputDir,
                Title = Path.GetFileName(opt.AscPath) + "  -  " + Path.GetFileName(opt.OutputDir),
                Setup = setup ?? new SpicaProject(),
                Paired = opt.Mc != null,
                Seed = opt.Seed,
            };
            rs.Meas.AddRange(meas);
            if (opt.Corners != null && opt.Corners.Count > 0) { rs.Mode = TempMode.Corners; rs.Corners.AddRange(opt.Corners); }
            else if (opt.RandomTemp) rs.Mode = TempMode.Random;
            foreach (var r in list) rs.Rows.Add(rs.MakeRow(r.Run, r.Temp, r.Corner, r.Status, m => r.Values.TryGetValue(m, out var v) ? v : "", r.Note));
            rs.SortRows();
            return rs;
        }

        Row MakeRow(int run, double temp, int corner, RunStatus st, Func<string, string> text, string note)
        {
            var row = new Row { Run = run, Temp = temp, Corner = corner, Status = st, Note = note ?? "", V = new double[Meas.Count], Text = new string[Meas.Count] };
            for (int i = 0; i < Meas.Count; i++)
            {
                row.Text[i] = text(Meas[i]) ?? "";
                row.V[i] = LogParser.TryNumber(row.Text[i], out var d) ? d : double.NaN;
            }
            return row;
        }

        void SortRows() => Rows.Sort((a, b) => a.Corner != b.Corner ? a.Corner.CompareTo(b.Corner) : a.Run.CompareTo(b.Run));

        /// <summary>Loads a campaign folder (results.csv, and campaign.spica when present).</summary>
        public static ResultSet Load(string folder)
        {
            string csv = Path.Combine(folder, "results.csv");
            if (!File.Exists(csv)) throw new FileNotFoundException("No results.csv in this folder.", csv);
            var rs = new ResultSet { Folder = folder };
            string spica = Path.Combine(folder, "campaign.spica");
            if (File.Exists(spica))
            {
                rs.Setup = SpicaProject.Load(spica);
                rs.Paired = rs.Setup.Paired;
                rs.Seed = rs.Setup.Seed;
            }
            if (File.Exists(Path.Combine(folder, "samples.csv"))) rs.Paired = true;   // written only by the paired generator
            rs.Title = (rs.Setup.Schematic.Length > 0 ? Path.GetFileName(rs.Setup.Schematic) + "  -  " : "") + Path.GetFileName(folder.TrimEnd('\\', '/'));

            var lines = File.ReadAllLines(csv, Encoding.UTF8);
            if (lines.Length == 0) return rs;
            var head = SplitCsv(lines[0]);
            int iRun = head.FindIndex(h => h.Equals("run", StringComparison.OrdinalIgnoreCase));
            int iTemp = head.FindIndex(h => h.Equals("temp", StringComparison.OrdinalIgnoreCase));
            int iStatus = head.FindIndex(h => h.Equals("status", StringComparison.OrdinalIgnoreCase));
            int iNote = head.FindIndex(h => h.Equals("note", StringComparison.OrdinalIgnoreCase));
            var measIdx = new List<int>();
            for (int i = 0; i < head.Count; i++)
                if (i != iRun && i != iTemp && i != iStatus && i != iNote) { rs.Meas.Add(head[i]); measIdx.Add(i); }

            var inv = CultureInfo.InvariantCulture;
            var temps = new List<double>();
            var parsed = new List<KeyValuePair<List<string>, double>>();
            for (int li = 1; li < lines.Length; li++)
            {
                if (lines[li].Trim().Length == 0) continue;
                var f = SplitCsv(lines[li]);
                double t = iTemp >= 0 && iTemp < f.Count && double.TryParse(f[iTemp], NumberStyles.Float, inv, out var tv) ? tv : double.NaN;
                parsed.Add(new KeyValuePair<List<string>, double>(f, t));
                if (!double.IsNaN(t)) temps.Add(t);
            }

            // Temperature mode: from the setup when known, otherwise inferred from the data.
            var distinct = temps.Distinct().OrderBy(v => v).ToList();
            if (File.Exists(spica)) rs.Mode = temps.Count == 0 ? TempMode.Default : rs.Setup.Temp;
            else if (temps.Count == 0) rs.Mode = TempMode.Default;
            else rs.Mode = distinct.Count <= 32 && temps.Count >= 2 * distinct.Count ? TempMode.Corners : TempMode.Random;
            // a stopped campaign may not have reached every corner yet: the setup knows them all
            if (rs.Mode == TempMode.Corners)
                rs.Corners.AddRange(File.Exists(spica) && rs.Setup.Corners.Count > 0 ? rs.Setup.Corners.Distinct().ToList() : distinct);

            foreach (var kv in parsed)
            {
                var f = kv.Key;
                string Field(int i) => i >= 0 && i < f.Count ? f[i] : "";
                int.TryParse(Field(iRun), NumberStyles.Integer, inv, out int run);
                Enum.TryParse(Field(iStatus), true, out RunStatus st);
                int corner = rs.Mode == TempMode.Corners ? Math.Max(0, rs.Corners.IndexOf(kv.Value)) : 0;
                var row = new Row { Run = run, Temp = kv.Value, Corner = corner, Status = st, Note = Field(iNote), V = new double[rs.Meas.Count], Text = new string[rs.Meas.Count] };
                for (int i = 0; i < measIdx.Count; i++)
                {
                    row.Text[i] = Field(measIdx[i]);
                    row.V[i] = LogParser.TryNumber(row.Text[i], out var d) ? d : double.NaN;
                }
                rs.Rows.Add(row);
            }
            rs.SortRows();
            return rs;
        }

        static List<string> SplitCsv(string line)
        {
            var res = new List<string>();
            var sb = new StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (q)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (c == '"') q = false;
                    else sb.Append(c);
                }
                else if (c == '"') q = true;
                else if (c == ',') { res.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            res.Add(sb.ToString());
            return res;
        }

        /// <summary>Writes the analysis part of the setup (specs, TF) back to the campaign folder.</summary>
        public void SaveSetup()
        {
            if (string.IsNullOrEmpty(Folder) || !Directory.Exists(Folder)) return;
            try
            {
                var copy = Setup.Clone();
                copy.Seed = Seed;
                copy.Save(Path.Combine(Folder, "campaign.spica"));
            }
            catch { }
        }
    }
}
