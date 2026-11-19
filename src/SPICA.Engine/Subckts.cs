using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Spica.Core
{
    /// <summary>
    /// The sub-circuits and models available to a netlist: the ones it defines, and the ones in the files it
    /// includes (.lib / .include, found as LTspice finds them, and what those files include).
    /// </summary>
    public sealed class ModelLibrary
    {
        public sealed class Def
        {
            public string Name, File;          // File = null: defined in the netlist itself
            public List<string> Lines;         // .subckt ... .ends, continuation lines joined
            public bool Encrypted;
        }

        public readonly Dictionary<string, Def> Subckts = new Dictionary<string, Def>(StringComparer.OrdinalIgnoreCase);
        /// <summary>.include files (reference as written = file) whose own top level draws random numbers.</summary>
        public readonly Dictionary<string, string> RandomIncludes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Files = new List<string>();

        static readonly string[] RandomFuncs = { "mc", "flat", "gauss" };

        /// <summary>Only what the netlist itself defines.</summary>
        public static ModelLibrary FromNetlist(IList<string> netlist)
        {
            var lib = new ModelLibrary();
            lib.Index(netlist, null);
            return lib;
        }

        public static ModelLibrary Load(IList<string> netlist, string ascPath, List<string> warnings)
        {
            var lib = new ModelLibrary();
            string ascDir = Path.GetDirectoryName(Path.GetFullPath(ascPath));
            var r = new SourceBundle.Resolver(ascDir, null);
            lib.Index(netlist, null);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in netlist)
            {
                var m = Netlist.IncludeRx.Match(line.Trim());
                if (!m.Success) continue;
                string reference = m.Groups[2].Value.Trim('"');
                string file = r.Model(reference, ascDir);
                if (file == null) continue;
                bool include = !m.Groups[1].Value.TrimStart('.').StartsWith("lib", StringComparison.OrdinalIgnoreCase);
                lib.Read(file, r, seen, 0, include ? reference : null, warnings);
            }
            return lib;
        }

        void Read(string file, SourceBundle.Resolver r, HashSet<string> seen, int depth, string includeRef, List<string> warnings)
        {
            if (depth > 6 || !seen.Add(file)) return;
            List<string> lines;
            try
            {
                if (new FileInfo(file).Length > 64 << 20) return;
                lines = Join(Ltspice.ReadText(file));
            }
            catch { return; }
            Files.Add(file);
            Index(lines, file);

            // the file's own top level (outside .subckt): a .include puts it into the circuit
            int sub = 0;
            bool random = false;
            foreach (var l in lines)
            {
                string t = l.TrimStart(), low = t.ToLowerInvariant();
                if (low.StartsWith(".subckt")) { sub++; continue; }
                if (low.StartsWith(".ends")) { sub = Math.Max(0, sub - 1); continue; }
                if (sub > 0) continue;
                if (low == ".end" || low.StartsWith(".end ")) break;
                if (t.Length > 0 && t[0] != '*' && !low.StartsWith(".func") && HasRandom(t)) random = true;
            }
            if (random)
            {
                if (includeRef != null) RandomIncludes[includeRef] = file;
                else warnings?.Add($"{Path.GetFileName(file)}: random values outside any .subckt in a .lib file; LTspice takes only definitions from a .lib, so they are not simulated");
            }

            foreach (var l in lines)
            {
                var m = Netlist.IncludeRx.Match(l.Trim());
                if (!m.Success) continue;
                string f = r.Model(m.Groups[2].Value.Trim('"'), Path.GetDirectoryName(file));
                if (f != null) Read(f, r, seen, depth + 1, null, warnings);
            }
        }

        /// <summary>Logical lines: '+' continuation lines joined to the line before.</summary>
        internal static List<string> Join(string text)
        {
            var list = new List<string>();
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (raw.TrimStart().StartsWith("+") && list.Count > 0) list[list.Count - 1] += " " + raw.TrimStart().Substring(1).Trim();
                else list.Add(raw);
            }
            return list;
        }

        void Index(IList<string> lines, string file)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].TrimStart();
                if (!t.StartsWith(".subckt", StringComparison.OrdinalIgnoreCase)) continue;
                var tok = Netlist.Tokenize(t);
                if (tok.Count < 2) continue;
                int depth = 0, j = i;
                var block = new List<string>();
                bool enc = false;
                for (; j < lines.Count; j++)
                {
                    string u = lines[j].TrimStart(), low = u.ToLowerInvariant();
                    block.Add(lines[j]);
                    if (low.StartsWith(".subckt")) depth++;
                    else if (low.StartsWith(".ends") && --depth == 0) break;
                    if (low.Contains("encrypted") || low.StartsWith(".protect") || low.Contains("$cdnenc")) enc = true;
                }
                if (!Subckts.ContainsKey(tok[1]))       // the first definition wins, as in LTspice
                    Subckts[tok[1]] = new Def { Name = tok[1], File = file, Lines = block, Encrypted = enc };
                i = j;
            }
        }

        internal static bool HasRandom(string code)
        {
            int c = code.IndexOf(';');
            if (c >= 0) code = code.Substring(0, c);
            foreach (var n in RandomFuncs)
                if (System.Text.RegularExpressions.Regex.IsMatch(code, @"(?<![\w.$§])" + n + @"\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
            return false;
        }
    }

    public sealed partial class McPlan
    {
        /// <summary>"U3: 4 random values inside OPA_X (vendor.lib)": tolerances inside sub-circuits SPICA now draws.</summary>
        public readonly List<string> TakenOver = new List<string>();

        /// <summary>A sub-circuit's random values, as parameters of its definition (spica_r1..n).</summary>
        sealed class SubInfo
        {
            public string Name, NewName, File;
            public List<string> Lines;                         // rewritten definition
            public readonly List<KeyValuePair<SiteKind, string>> Sites = new List<KeyValuePair<SiteKind, string>>();   // kind, label inside
            public readonly List<string> Originals = new List<string>();
            public bool FromLibrary => File != null;
        }

        sealed class Takeover
        {
            readonly ModelLibrary lib;
            readonly Dictionary<string, FuncDef> funcs;
            readonly HashSet<string> randomFuncs;
            readonly McPlan plan;
            readonly Dictionary<string, SubInfo> done = new Dictionary<string, SubInfo>(StringComparer.OrdinalIgnoreCase);
            readonly HashSet<string> busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            readonly HashSet<string> warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<SubInfo> Copied = new List<SubInfo>();      // library definitions to append, in use order

            public Takeover(ModelLibrary lib, Dictionary<string, FuncDef> funcs, HashSet<string> randomFuncs, McPlan plan)
            {
                this.lib = lib; this.funcs = funcs; this.randomFuncs = randomFuncs; this.plan = plan;
            }

            bool Random(string code) => HasCall(code, RandomFuncs) || HasCall(code, randomFuncs);

            /// <summary>The analysed sub-circuit, or null when it has no random value SPICA can take over.</summary>
            public SubInfo Get(string name)
            {
                if (name == null) return null;
                if (done.TryGetValue(name, out var have)) return have;
                if (!lib.Subckts.TryGetValue(name, out var def) || !busy.Add(name)) return null;
                SubInfo info = null;
                try
                {
                    if (def.Encrypted)
                    {
                        if (warned.Add(name))
                            plan.Warnings.Add($"{name}{(def.File != null ? " (" + Path.GetFileName(def.File) + ")" : "")} is encrypted: SPICA cannot see its tolerances");
                    }
                    else info = Analyse(def);
                }
                finally { busy.Remove(name); }
                done[name] = info != null && info.Sites.Count > 0 ? info : null;
                if (done[name] != null && info.FromLibrary) Copied.Add(info);
                return done[name];
            }

            SubInfo Analyse(ModelLibrary.Def def)
            {
                var info = new SubInfo { Name = def.Name, File = def.File, Lines = new List<string>() };
                info.NewName = def.File != null ? def.Name + "__spica" : def.Name;
                int nested = 0;
                for (int i = 0; i < def.Lines.Count; i++)
                {
                    string line = def.Lines[i], t = line.TrimStart(), low = t.ToLowerInvariant();
                    if (i == 0 || i == def.Lines.Count - 1) { info.Lines.Add(line); continue; }    // header, .ends
                    if (low.StartsWith(".subckt")) nested++;
                    else if (low.StartsWith(".ends")) nested--;
                    if (nested > 0 || t.Length == 0 || t[0] == '*' || low.StartsWith(".func") || low.StartsWith(".meas")) { info.Lines.Add(line); continue; }

                    SplitComment(line, out string code, out string comment);
                    string label = LabelOf(t);
                    if (Random(code))
                    {
                        string expanded = code;
                        for (int d = 0; d < 12 && HasCall(expanded, randomFuncs); d++)
                            expanded = ExpandFuncs(expanded, funcs, randomFuncs, plan.Inlined);
                        int before = info.Sites.Count;
                        code = RewriteCalls(expanded, (kind, original) =>
                        {
                            info.Sites.Add(new KeyValuePair<SiteKind, string>(kind, label));
                            info.Originals.Add(original);
                            return "spica_r" + info.Sites.Count;
                        });
                        if (info.Sites.Count - before > 1)
                            for (int k = before; k < info.Sites.Count; k++)
                                info.Sites[k] = new KeyValuePair<SiteKind, string>(info.Sites[k].Key, $"{label}#{k - before + 1}");
                    }
                    if (char.ToUpperInvariant(t[0]) == 'X')
                        code = Bind(code, info.Name, (kind, inner, original) =>
                        {
                            info.Sites.Add(new KeyValuePair<SiteKind, string>(kind, inner));
                            info.Originals.Add(original);
                            return "spica_r" + info.Sites.Count;
                        });
                    info.Lines.Add(code + comment);
                }
                if (info.Sites.Count == 0) return info;

                // the header: renamed when copied from a library, and one parameter per random value
                var head = Netlist.Tokenize(info.Lines[0].Trim());
                head[1] = info.NewName;
                bool hasParams = head.Skip(2).Any(x => x.Equals("params:", StringComparison.OrdinalIgnoreCase) || x.Contains("="));
                var sb = new StringBuilder(string.Join(" ", head));
                if (!hasParams) sb.Append(" params:");
                for (int k = 1; k <= info.Sites.Count; k++) sb.Append(" spica_r").Append(k).Append("=0");
                info.Lines[0] = sb.ToString();
                return info;
            }

            /// <summary>
            /// An X line: when its sub-circuit has random values, points it at the rewritten definition and passes each
            /// value a parameter from <paramref name="alloc"/> (kind, label "X1.R5", original call).
            /// </summary>
            public string Bind(string code, string owner, Func<SiteKind, string, string, string> alloc)
            {
                var toks = Netlist.Tokenize(code.Trim());
                if (toks.Count < 2) return code;
                int model = toks.Count - 1;
                for (int k = 1; k < toks.Count; k++)
                    if (toks[k].Contains("=") || toks[k].Equals("params:", StringComparison.OrdinalIgnoreCase)) { model = k - 1; break; }
                if (model < 1) return code;
                var sub = Get(toks[model]);
                if (sub == null) return code;
                // "X§U9" (a schematic instance) is U9; inside a definition "X1" stays X1
                string inst = toks[0].Contains("§") ? Netlist.InstanceName(toks[0]) : toks[0];
                toks[model] = sub.NewName;
                var sb = new StringBuilder(string.Join(" ", toks));
                for (int k = 0; k < sub.Sites.Count; k++)
                    sb.Append(" spica_r").Append(k + 1).Append("={").Append(alloc(sub.Sites[k].Key, inst + "." + sub.Sites[k].Value, sub.Originals[k])).Append('}');
                if (owner == null)
                    plan.TakenOver.Add($"{inst}: {sub.Sites.Count} random value{(sub.Sites.Count == 1 ? "" : "s")} inside {sub.Name}" +
                                       (sub.FromLibrary ? $" ({Path.GetFileName(sub.File)})" : ""));
                string lead = code.Substring(0, code.Length - code.TrimStart().Length);
                return lead + sb.ToString();
            }
        }

        /// <summary>
        /// A .include whose own top level draws random numbers (e.g. a netlist saved as a library) is put into the netlist
        /// in its place, so those values are paired too. Its own include references are made absolute.
        /// </summary>
        static List<string> InlineIncludes(IList<string> lines, ModelLibrary lib, McPlan plan)
        {
            var result = new List<string>();
            foreach (var line in lines)
            {
                var m = Netlist.IncludeRx.Match(line.Trim());
                if (!m.Success || !lib.RandomIncludes.TryGetValue(m.Groups[2].Value.Trim('"'), out var file)) { result.Add(line); continue; }
                List<string> body;
                try { body = ModelLibrary.Join(Ltspice.ReadText(file)); }
                catch { result.Add(line); continue; }
                string dir = Path.GetDirectoryName(file);
                result.Add("* ---- SPICA: " + line.Trim() + " placed here, so SPICA draws its random values ----");
                foreach (var l in body)
                {
                    string low = l.Trim().ToLowerInvariant();
                    if (low == ".end" || low.StartsWith(".end ")) break;
                    if (low.StartsWith(".backanno")) continue;
                    var im = Netlist.IncludeRx.Match(l.Trim());
                    if (im.Success)
                    {
                        string bare = im.Groups[2].Value.Trim('"');
                        string full = Path.IsPathRooted(bare) ? bare : Path.Combine(dir, bare);
                        result.Add(File.Exists(full) ? im.Groups[1].Value + "\"" + Path.GetFullPath(full) + "\"" + im.Groups[3].Value : l);
                        continue;
                    }
                    result.Add(l);
                }
                result.Add("* ---- SPICA: end of " + Path.GetFileName(file) + " ----");
                plan.TakenOver.Add($"{Path.GetFileName(file)}: included file with random values at its top level, placed in the netlist");
            }
            return result;
        }
    }
}
