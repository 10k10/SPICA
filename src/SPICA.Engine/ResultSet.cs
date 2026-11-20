using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Spica.Core
{
    /// <summary>
    /// The runs of one campaign, from its results.csv: one row per run and temperature, with the .meas values. The
    /// waveform viewer finds each row's .raw from its run number and temperature.
    /// </summary>
    public sealed class ResultSet
    {
        public sealed class Row
        {
            public int Run;
            public double Temp = double.NaN;
            public RunStatus Status;
            public double[] V;
            public string Note = "";
        }

        public string Folder;
        public readonly List<string> Meas = new List<string>();
        public readonly List<Row> Rows = new List<Row>();

        public static ResultSet Load(string folder)
        {
            string csv = Path.Combine(folder, "results.csv");
            if (!File.Exists(csv)) throw new FileNotFoundException("No results.csv in this folder.", csv);
            var rs = new ResultSet { Folder = folder };
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
            for (int li = 1; li < lines.Length; li++)
            {
                if (lines[li].Trim().Length == 0) continue;
                var f = SplitCsv(lines[li]);
                string Field(int i) => i >= 0 && i < f.Count ? f[i] : "";
                var row = new Row { Note = Field(iNote), V = new double[rs.Meas.Count] };
                int.TryParse(Field(iRun), NumberStyles.Integer, inv, out row.Run);
                if (double.TryParse(Field(iTemp), NumberStyles.Float, inv, out var t)) row.Temp = t;
                Enum.TryParse(Field(iStatus), true, out row.Status);
                for (int i = 0; i < measIdx.Count; i++) row.V[i] = LogParser.TryNumber(Field(measIdx[i]), out var d) ? d : double.NaN;
                rs.Rows.Add(row);
            }
            // by temperature, then by run
            rs.Rows.Sort((a, b) =>
            {
                int c = (double.IsNaN(a.Temp) ? double.MinValue : a.Temp).CompareTo(double.IsNaN(b.Temp) ? double.MinValue : b.Temp);
                return c != 0 ? c : a.Run.CompareTo(b.Run);
            });
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
    }
}
