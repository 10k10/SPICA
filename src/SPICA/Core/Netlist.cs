using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Spica.Core
{
    /// <summary>
    /// A flattened LTspice netlist (as produced by "LTspice -netlist"), with the information SPICA
    /// needs: node names, device currents, .meas statements and the signals they depend on.
    /// </summary>
    public sealed class Netlist
    {
        public string SourcePath;          // the .asc
        public Encoding Encoding = Encoding.Default;
        public readonly List<string> Lines = new List<string>();      // logical lines, continuations joined

        public readonly List<string> Nodes = new List<string>();      // "out", "N001"...
        public readonly List<string> Currents = new List<string>();   // "I(R1)", "Ic(Q1)"...
        public readonly List<string> MeasNames = new List<string>();
        public readonly List<string> RequiredSignals = new List<string>(); // referenced by .meas / .four
        public readonly List<string> ExistingSaves = new List<string>();
        public readonly List<string> RemovedSteps = new List<string>();
        public string Analysis = "";

        public static Netlist Parse(string text, string sourcePath, Encoding enc)
        {
            var n = new Netlist { SourcePath = sourcePath, Encoding = enc ?? Encoding.Default };
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (raw.StartsWith("+") && n.Lines.Count > 0)
                    n.Lines[n.Lines.Count - 1] += " " + raw.Substring(1).Trim();
                else
                    n.Lines.Add(raw);
            }
            n.Analyze();
            return n;
        }

        // ------------------------------------------------------------------ analysis

        static readonly HashSet<string> AnalysisWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "tran", "ac", "dc", "op", "tf", "noise" };

        void Analyze()
        {
            var nodeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var curSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reqSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nodes = new List<string>();

            foreach (var line in Lines)
            {
                var l = line.Trim();
                if (l.Length == 0 || l[0] == '*' || l[0] == ';') continue;
                if (l[0] == '.')
                {
                    var tok = Tokenize(l);
                    string kw = tok[0].ToLowerInvariant();
                    if (kw == ".end") break;
                    if (kw == ".step") RemovedSteps.Add(l);
                    else if (kw == ".meas" || kw == ".measure")
                    {
                        int i = 1;
                        if (tok.Count > 2 && AnalysisWords.Contains(tok[1])) i = 2;
                        if (tok.Count > i) MeasNames.Add(tok[i]);
                        foreach (var s in ExtractSignals(l)) if (reqSet.Add(s)) RequiredSignals.Add(s);
                    }
                    else if (kw == ".four")
                    {
                        foreach (var s in ExtractSignals(l)) if (reqSet.Add(s)) RequiredSignals.Add(s);
                    }
                    else if (kw == ".save")
                    {
                        for (int i = 1; i < tok.Count; i++) ExistingSaves.Add(tok[i]);
                    }
                    else if (kw == ".tran" || kw == ".ac" || kw == ".dc" || kw == ".op" || kw == ".noise" || kw == ".tf")
                    {
                        Analysis = l;
                    }
                    continue;
                }
                ParseElement(l, nodeSet, nodes, curSet);
            }

            // Named nets first (alphabetical), then LTspice auto-named N001... nets.
            var auto = new Regex(@"^N\d+$", RegexOptions.IgnoreCase);
            Nodes.AddRange(nodes.Where(x => !auto.IsMatch(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            Nodes.AddRange(nodes.Where(x => auto.IsMatch(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }

        void ParseElement(string l, HashSet<string> nodeSet, List<string> nodes, HashSet<string> curSet)
        {
            var tok = Tokenize(l);
            if (tok.Count < 2) return;
            string name = tok[0];
            char t = char.ToUpperInvariant(name[0]);
            int nn;
            switch (t)
            {
                case 'R': case 'C': case 'L': case 'D': case 'V': case 'I':
                case 'B': case 'F': case 'H': case 'W':
                    nn = 2; break;
                case 'E': case 'G':
                    nn = 0;
                    for (int i = 1; i < tok.Count && i <= 4; i++)
                    {
                        var s = tok[i];
                        if (s.Contains("=") || s.Contains("{") || s.Contains("(") ) break;
                        nn++;
                    }
                    break;
                case 'Q': case 'J': case 'Z': case 'U': nn = 3; break;
                case 'M': case 'S': case 'T': case 'O': nn = 4; break;
                case 'A': nn = Math.Min(8, tok.Count - 2); break;
                case 'X':
                    {
                        int last = tok.Count - 1;
                        for (int i = 1; i < tok.Count; i++)
                            if (tok[i].Contains("=") || tok[i].Equals("params:", StringComparison.OrdinalIgnoreCase)) { last = i - 1; break; }
                        nn = Math.Max(0, last - 1); // tok[last] is the subcircuit name
                        break;
                    }
                default: nn = 0; break;
            }
            for (int i = 1; i <= nn && i < tok.Count; i++)
            {
                var node = tok[i];
                if (node == "0" || node.Length == 0) continue;
                if (nodeSet.Add(node)) nodes.Add(node);
            }

            void Cur(string c) { if (curSet.Add(c)) Currents.Add(c); }
            switch (t)
            {
                case 'R': case 'C': case 'L': case 'V': case 'I': case 'D':
                case 'B': case 'E': case 'F': case 'G': case 'H': case 'W': case 'S':
                    Cur($"I({name})"); break;
                case 'Q': Cur($"Ic({name})"); Cur($"Ib({name})"); Cur($"Ie({name})"); break;
                case 'M': case 'J': case 'Z': Cur($"Id({name})"); Cur($"Ig({name})"); Cur($"Is({name})"); break;
            }
        }

        static readonly Regex SignalRx = new Regex(@"(?<![\w.$])([VI][a-z]{0,2})\s*\(\s*([^()]+?)\s*\)", RegexOptions.IgnoreCase);

        /// <summary>Signals (as .save tokens) referenced in an expression line.</summary>
        public static IEnumerable<string> ExtractSignals(string line)
        {
            foreach (Match m in SignalRx.Matches(line))
            {
                string fn = m.Groups[1].Value;
                string arg = m.Groups[2].Value;
                if (fn.Equals("if", StringComparison.OrdinalIgnoreCase) || fn.Equals("inv", StringComparison.OrdinalIgnoreCase)) continue;
                if (char.ToUpperInvariant(fn[0]) == 'V')
                {
                    foreach (var part in arg.Split(','))
                    {
                        var node = part.Trim();
                        if (node.Length > 0 && node != "0") yield return $"V({node})";
                    }
                }
                else
                {
                    yield return $"{fn}({arg})";
                }
            }
        }

        /// <summary>Whitespace tokenizer that keeps (), {} and quoted groups together.</summary>
        public static List<string> Tokenize(string l)
        {
            var res = new List<string>();
            var sb = new StringBuilder();
            int depth = 0; bool quote = false;
            foreach (char c in l)
            {
                if (c == '"') quote = !quote;
                if (!quote)
                {
                    if (c == '(' || c == '{') depth++;
                    else if ((c == ')' || c == '}') && depth > 0) depth--;
                }
                if (char.IsWhiteSpace(c) && depth == 0 && !quote)
                {
                    if (sb.Length > 0) { res.Add(sb.ToString()); sb.Clear(); }
                }
                else sb.Append(c);
            }
            if (sb.Length > 0) res.Add(sb.ToString());
            if (res.Count == 0) res.Add("");
            return res;
        }

        // ------------------------------------------------------------------ chunk generation

        /// <summary>
        /// Builds the netlist of one chunk (runs <see cref="ChunkSpec.First"/>..<see cref="ChunkSpec.Last"/>).
        /// All .step lines are replaced by SPICA's own run stepping, the per-instance thread limit is set,
        /// the temperature and SPICA's sample tables are injected, and (optionally) the .save list
        /// restricts what goes into the .raw.
        /// </summary>
        public string BuildChunk(ChunkSpec c)
        {
            var sb = new StringBuilder();
            bool ended = false;
            bool ownTemp = c.FixedTemp.HasValue || c.TempOfRun != null;
            foreach (var line in c.Mc != null ? c.Mc.Lines : Lines)
            {
                var t = line.TrimStart();
                var low = t.ToLowerInvariant();
                if (low.StartsWith(".step")) continue;
                if (low.StartsWith(".backanno")) continue;
                if (c.Save != null && low.StartsWith(".save")) continue;
                if (ownTemp && IsTempLine(low)) continue;
                if (low.StartsWith(".option"))
                {
                    var cleaned = Regex.Replace(t, @"\bthreads\s*=\s*\S+", "", RegexOptions.IgnoreCase);
                    if (ownTemp) cleaned = Regex.Replace(cleaned, @"\btemp\s*=\s*(\{[^}]*\}|\S+)", "", RegexOptions.IgnoreCase);
                    cleaned = cleaned.TrimEnd();
                    if (Tokenize(cleaned).Count <= 1) continue;
                    sb.Append(cleaned).Append("\r\n");
                    continue;
                }
                if (low == ".end" || low.StartsWith(".end ") || low.StartsWith(".end\t"))
                {
                    AppendSpica(sb, c);
                    sb.Append(".end\r\n");
                    ended = true;
                    break;
                }
                sb.Append(FixPaths(line, c.BaseDir)).Append("\r\n");
            }
            if (!ended)
            {
                AppendSpica(sb, c);
                sb.Append(".end\r\n");
            }
            return sb.ToString();
        }

        static bool IsTempLine(string low) => low.StartsWith(".temp ") || low.StartsWith(".temp\t") || low == ".temp";

        /// <summary>The schematic's own .temp lines (ignored when SPICA sets the temperature).</summary>
        public IEnumerable<string> TempLines => Lines.Where(l => IsTempLine(l.TrimStart().ToLowerInvariant())).Select(l => l.Trim());

        public bool IsOpAnalysis => Analysis.StartsWith(".op", StringComparison.OrdinalIgnoreCase);

        void AppendSpica(StringBuilder sb, ChunkSpec c)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            sb.Append("* ---- SPICA chunk: runs ").Append(c.First).Append("..").Append(c.Last);
            if (c.FixedTemp.HasValue) sb.Append(" at ").Append(c.FixedTemp.Value.ToString("R", inv)).Append(" C");
            sb.Append(" ----\r\n");
            if (c.Last > c.First)
            {
                // A stepped .op is treated by LTspice as a sweep over the first .step, so its .meas
                // print only one row. A dummy first dimension makes them report one row per run.
                if (IsOpAnalysis) sb.Append(".step param spica_op list 0 1\r\n");
                sb.Append($".step param run {c.First} {c.Last} 1\r\n");
            }
            else sb.Append($".param run={c.First}\r\n");   // LTspice refuses a .step with a single point
            sb.Append($".options threads={Math.Max(1, c.Threads)}\r\n");
            c.Mc?.AppendParams(sb, c.Seed, c.First, c.Last);
            if (c.FixedTemp.HasValue) sb.Append(".temp ").Append(c.FixedTemp.Value.ToString("R", inv)).Append("\r\n");
            else if (c.TempOfRun != null)
            {
                McPlan.AppendTable(sb, "spica_temp", c.First, c.Last, c.TempOfRun);
                sb.Append(".temp {spica_temp}\r\n");
            }
            if (c.Save != null && c.Save.Count > 0)
            {
                for (int i = 0; i < c.Save.Count; i += 10)
                    sb.Append(".save ").Append(string.Join(" ", c.Save.Skip(i).Take(10))).Append("\r\n");
            }
        }

        static readonly Regex IncludeRx = new Regex(@"^(\.(?:inc|include|lib)\s+)(""[^""]+""|\S+)(.*)$", RegexOptions.IgnoreCase);
        static readonly Regex FileArgRx = new Regex(@"(\bfile\s*=\s*)(""[^""]+""|[^\s)]+)", RegexOptions.IgnoreCase);

        /// <summary>Makes include / lib / PWL file references absolute when they are relative to the schematic folder.</summary>
        static string FixPaths(string line, string baseDir)
        {
            if (string.IsNullOrEmpty(baseDir)) return line;
            string Abs(string p)
            {
                string bare = p.Trim('"');
                if (bare.Length == 0 || Path.IsPathRooted(bare)) return p;
                try
                {
                    string full = Path.GetFullPath(Path.Combine(baseDir, bare));
                    return File.Exists(full) ? "\"" + full + "\"" : p;
                }
                catch { return p; }
            }
            var m = IncludeRx.Match(line.TrimStart());
            if (m.Success) return m.Groups[1].Value + Abs(m.Groups[2].Value) + m.Groups[3].Value;
            if (line.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0)
                return FileArgRx.Replace(line, mm => mm.Groups[1].Value + Abs(mm.Groups[2].Value));
            return line;
        }

        /// <summary>Hash of the circuit (ignoring comments and stepping) used to key scheduler profiles.</summary>
        public string CircuitHash()
        {
            var sb = new StringBuilder();
            foreach (var line in Lines)
            {
                var t = line.Trim();
                if (t.Length == 0 || t[0] == '*' || t.StartsWith(".step", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith(".backanno", StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append(t).Append('\n');
            }
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(h, 0, 8).Replace("-", "");
            }
        }
    }

    /// <summary>What one chunk netlist contains.</summary>
    public sealed class ChunkSpec
    {
        public int First, Last, Threads = 1;
        public IList<string> Save;            // null = no .save (LTspice saves everything)
        public string BaseDir;
        public double? FixedTemp;             // temperature corner of this chunk
        public Func<int, double> TempOfRun;   // per-run random temperature
        public McPlan Mc;                     // SPICA's sample generator (null = LTspice's own mc())
        public uint Seed;
    }
}
