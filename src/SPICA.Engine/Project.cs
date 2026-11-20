using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Spica.Core
{
    /// <summary>
    /// A SPICA project (.spica): the schematic it was opened with and its studies. What a study simulates is in its
    /// blocks; how this PC runs LTspice (where it is, how many instances, where results go) is in SPICA.ini.
    /// </summary>
    public sealed class SpicaProject
    {
        public string FilePath;               // where it was loaded from or saved to (null = never saved)
        public string Schematic = "";         // absolute path of the .asc opened with it
        public List<Graph.Study> Studies = new List<Graph.Study>();
        public bool Dirty;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("; SPICA project - Simulation Platform for Investigation, Compliance and Analysis");
            sb.AppendLine("[Project]");
            sb.AppendLine("Schematic=" + RelativeTo(path, Schematic));
            // each [Study] is followed by its [Block] sections
            foreach (var st in Studies) st.Write(sb);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            FilePath = path;
            Dirty = false;
        }

        /// <summary>
        /// Reads a project. Sections it does not know (the settings of the versions before 0.10) are skipped; a block
        /// of a kind this version does not have stays in its study, marked unknown, so nothing is lost silently.
        /// </summary>
        public static SpicaProject Load(string path)
        {
            var p = new SpicaProject { FilePath = path };
            string section = "";
            Graph.Study study = null;
            Graph.Block block = null;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    // a [Block n] belongs to the [Study] above it; any other section ends the study
                    if (section.StartsWith("Study ", StringComparison.OrdinalIgnoreCase))
                    {
                        study = new Graph.Study { Name = section.Substring(6).Trim() };
                        p.Studies.Add(study);
                        block = null;
                    }
                    else if (study != null && section.StartsWith("Block ", StringComparison.OrdinalIgnoreCase) &&
                             int.TryParse(section.Substring(6).Trim(), NumberStyles.Integer, Inv, out int id))
                    {
                        block = new Graph.Block { Id = id };
                        study.Blocks.Add(block);
                    }
                    else { study = null; block = null; }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                if (study != null) { study.Read(block, key, val); continue; }
                // [Project], or [Campaign] in the files of the versions before 0.10
                if ((section.Equals("Project", StringComparison.OrdinalIgnoreCase) || section.Equals("Campaign", StringComparison.OrdinalIgnoreCase)) &&
                    key.Equals("Schematic", StringComparison.OrdinalIgnoreCase))
                    p.Schematic = Absolute(path, val);
            }
            p.Dirty = false;
            return p;
        }

        public SpicaProject Clone() => new SpicaProject
        {
            FilePath = FilePath, Schematic = Schematic, Dirty = Dirty, Studies = Studies.Select(x => x.Clone()).ToList(),
        };

        static string RelativeTo(string file, string target)
        {
            if (string.IsNullOrEmpty(target)) return "";
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(file));
                string full = Path.GetFullPath(target);
                if (full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(dir.Length + 1);
                return full;
            }
            catch { return target; }
        }

        static string Absolute(string file, string target)
        {
            if (string.IsNullOrEmpty(target)) return "";
            try { return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file)), target)); }
            catch { return target; }
        }
    }
}
