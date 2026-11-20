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
    /// <summary>
    /// A study's last run and how its waveforms were shown: the campaigns it made, and the panes with their traces.
    /// Kept in the project, so that it opens with its results as they were left.
    /// </summary>
    public sealed class StudyView
    {
        public string Study = "";
        public DateTime Ended;
        public readonly List<(string Label, bool Cached, string Dir)> Sets = new List<(string, bool, string)>();
        public readonly List<string> Problems = new List<string>();
        public List<List<string>> Panes;            // null: never shown
        public string Runs;

        /// <summary>The whole view as text (to tell whether it changed).</summary>
        public string Key => string.Join("\n", new[] { Study, Ended.ToString("s", CultureInfo.InvariantCulture), Runs ?? "" }
            .Concat(Sets.Select(s => $"{s.Label}|{s.Cached}|{s.Dir}")).Concat(Problems)
            .Concat((Panes ?? new List<List<string>>()).Select(x => "pane " + string.Join("|", x))));
    }

    public sealed class SpicaProject
    {
        public string FilePath;               // where it was loaded from or saved to (null = never saved)
        public string Schematic = "";         // absolute path of the .asc opened with it
        public List<Graph.Study> Studies = new List<Graph.Study>();
        /// <summary>Each study's last run and view (by study name).</summary>
        public List<StudyView> Views = new List<StudyView>();
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
            // the results: each study's last run (its campaign folders, relative to the project when beside it) and its panes
            foreach (var v in Views)
            {
                sb.AppendLine();
                sb.AppendLine($"[Results {v.Study}]");
                sb.AppendLine("Ended=" + v.Ended.ToString("yyyy-MM-dd HH:mm:ss", Inv));
                foreach (var s in v.Sets) sb.AppendLine($"Set={s.Label}|{(s.Cached ? 1 : 0)}|{RelativeTo(path, s.Dir)}");
                foreach (var x in v.Problems) sb.AppendLine("Problem=" + x.Replace("\r", " ").Replace("\n", " "));
                foreach (var x in v.Panes ?? new List<List<string>>()) sb.AppendLine("Pane=" + string.Join("|", x));
                if (v.Runs != null) sb.AppendLine("Runs=" + v.Runs);
            }
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
            StudyView view = null;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    // a [Block n] belongs to the [Study] above it; any other section ends the study
                    view = null;
                    if (section.StartsWith("Results ", StringComparison.OrdinalIgnoreCase))
                    {
                        view = new StudyView { Study = section.Substring(8).Trim() };
                        p.Views.Add(view);
                        study = null; block = null;
                    }
                    else if (section.StartsWith("Study ", StringComparison.OrdinalIgnoreCase))
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
                if (view != null)
                {
                    switch (key)
                    {
                        case "Ended": if (DateTime.TryParseExact(val, "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out var t)) view.Ended = t; break;
                        case "Set":
                            var parts = val.Split(new[] { '|' }, 3);
                            if (parts.Length == 3) view.Sets.Add((parts[0], parts[1] == "1", Absolute(path, parts[2])));
                            break;
                        case "Problem": view.Problems.Add(val); break;
                        case "Pane": (view.Panes ?? (view.Panes = new List<List<string>>())).Add(val.Split('|').Where(x => x.Length > 0).ToList()); break;
                        case "Runs": view.Runs = val; break;
                    }
                    continue;
                }
                // [Project], or [Campaign] in the files of the versions before 0.10
                if ((section.Equals("Project", StringComparison.OrdinalIgnoreCase) || section.Equals("Campaign", StringComparison.OrdinalIgnoreCase)) &&
                    key.Equals("Schematic", StringComparison.OrdinalIgnoreCase))
                    p.Schematic = Absolute(path, val);
            }
            foreach (var st in p.Studies) st.Migrate();
            p.Dirty = false;
            return p;
        }

        public SpicaProject Clone() => new SpicaProject
        {
            FilePath = FilePath, Schematic = Schematic, Dirty = Dirty, Studies = Studies.Select(x => x.Clone()).ToList(), Views = Views.ToList(),
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
