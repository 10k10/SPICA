using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Spica.Core
{
    /// <summary>
    /// campaign.state in a campaign folder: whether the campaign is running, was stopped or finished,
    /// and how far it got. A folder left "Running" by a crash or a closed window can be resumed.
    /// </summary>
    public sealed class CampaignState
    {
        public string Status = "";            // Running | Stopped | Finished
        public int Jobs, Done;
        public double ElapsedSeconds;
        public string CircuitHash = "";

        public bool Unfinished => !Status.Equals("Finished", StringComparison.OrdinalIgnoreCase) && Done < Jobs;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static string PathIn(string folder) => Path.Combine(folder, "campaign.state");

        public void Save(string folder)
        {
            var sb = new StringBuilder();
            sb.AppendLine("; SPICA campaign state");
            sb.AppendLine("Status=" + Status);
            sb.AppendLine("Jobs=" + Jobs.ToString(Inv));
            sb.AppendLine("Done=" + Done.ToString(Inv));
            sb.AppendLine("ElapsedSeconds=" + ElapsedSeconds.ToString("0.###", Inv));
            sb.AppendLine("CircuitHash=" + CircuitHash);
            sb.AppendLine("Updated=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv));
            File.WriteAllText(PathIn(folder), sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>The state of a campaign folder, or null for folders written before campaign.state existed.</summary>
        public static CampaignState Load(string folder)
        {
            string path = PathIn(folder);
            if (!File.Exists(path)) return null;
            var s = new CampaignState();
            foreach (var raw in File.ReadAllLines(path))
            {
                int eq = raw.IndexOf('=');
                if (raw.StartsWith(";") || eq < 0) continue;
                string key = raw.Substring(0, eq).Trim(), val = raw.Substring(eq + 1).Trim();
                switch (key.ToLowerInvariant())
                {
                    case "status": s.Status = val; break;
                    case "jobs": int.TryParse(val, NumberStyles.Integer, Inv, out s.Jobs); break;
                    case "done": int.TryParse(val, NumberStyles.Integer, Inv, out s.Done); break;
                    case "elapsedseconds": double.TryParse(val, NumberStyles.Float, Inv, out s.ElapsedSeconds); break;
                    case "circuithash": s.CircuitHash = val; break;
                }
            }
            return s;
        }

        /// <summary>The finished rows of a campaign folder as engine results, for resuming it.</summary>
        public static List<RunResult> LoadResults(string folder, IList<double> corners)
        {
            var rs = ResultSet.Load(folder);
            var list = new List<RunResult>();
            foreach (var row in rs.Rows)
            {
                int corner = 0;
                if (corners != null && corners.Count > 0)
                {
                    corner = -1;
                    for (int i = 0; i < corners.Count; i++)
                        if (Math.Abs(corners[i] - row.Temp) <= 1e-9 * Math.Max(1, Math.Abs(corners[i]))) { corner = i; break; }
                    if (corner < 0) continue;      // a temperature the setup does not have: simulate again
                }
                var r = new RunResult { Run = row.Run, Corner = corner, Temp = row.Temp, Status = row.Status, Note = row.Note ?? "", Values = new Dictionary<string, string>() };
                for (int i = 0; i < rs.Meas.Count; i++)
                    if (!string.IsNullOrEmpty(row.Text[i])) r.Values[rs.Meas[i]] = row.Text[i];
                list.Add(r);
            }
            return list;
        }
    }

    static class NativeMethods
    {
        public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint flags);
    }
}
