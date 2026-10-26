using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Spica.Core
{
    public enum SiteKind { Uniform, Gaussian }

    /// <summary>One random call site (an mc(), flat() or gauss() occurrence) replaced by a SPICA parameter.</summary>
    public sealed class McSite
    {
        public int Index;            // 1-based; the parameter is spica_u{Index}
        public SiteKind Kind;        // Uniform: value in [-1, 1]; Gaussian: standard normal
        public string Label;         // element / parameter name the site belongs to
        public string Original;      // the original call text, for the log
        public string Param => "spica_u" + Index;
    }

    /// <summary>
    /// SPICA's deterministic sample generator. LTspice's own mc()/gauss()/flat() draw new values in
    /// every process, so the same run number means a different unit at each temperature. Here every
    /// call site gets a parameter whose value is a pure function of (seed, site, run), injected into
    /// each chunk as a table(run, ...). The same run is therefore the same unit in any chunk, at any
    /// temperature, and the whole campaign is reproducible from its seed.
    /// </summary>
    public sealed class McPlan
    {
        public readonly List<string> Lines = new List<string>();     // rewritten netlist lines
        public readonly List<McSite> Sites = new List<McSite>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Inlined = new List<string>();   // .func names expanded at their call sites

        static readonly string[] RandomFuncs = { "mc", "flat", "gauss" };

        sealed class FuncDef
        {
            public string Name;
            public List<string> Params;
            public string Body;
        }

        public static McPlan Build(IList<string> lines)
        {
            var plan = new McPlan();
            var funcs = ParseFuncs(lines);

            // Functions whose body draws random numbers, directly or through another such function.
            var randomFuncs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var f in funcs.Values)
                    if (!randomFuncs.Contains(f.Name) && (HasCall(f.Body, RandomFuncs) || HasCall(f.Body, randomFuncs)))
                    {
                        randomFuncs.Add(f.Name);
                        grew = true;
                    }
            }

            int subckt = 0;
            foreach (var line in lines)
            {
                string t = line.TrimStart();
                string low = t.ToLowerInvariant();
                if (t.Length == 0 || t[0] == '*') { plan.Lines.Add(line); continue; }
                if (low.StartsWith(".subckt")) subckt++;
                else if (low.StartsWith(".ends")) subckt = Math.Max(0, subckt - 1);

                // .func definitions stay as they are (sub-circuits may still call them).
                if (low.StartsWith(".func") || low.StartsWith(".meas") || low.StartsWith(".step"))
                {
                    plan.Lines.Add(line);
                    continue;
                }

                SplitComment(line, out string code, out string comment);
                bool hasRandom = HasCall(code, RandomFuncs) || HasCall(code, randomFuncs);
                if (!hasRandom) { plan.Lines.Add(line); continue; }

                if (subckt > 0)
                {
                    plan.Warnings.Add($"Random value inside a .subckt definition is left to LTspice (not paired): {Short(t)}");
                    plan.Lines.Add(line);
                    continue;
                }

                string expanded = code;
                for (int depth = 0; depth < 12 && HasCall(expanded, randomFuncs); depth++)
                    expanded = ExpandFuncs(expanded, funcs, randomFuncs, plan.Inlined);

                string label = LabelOf(t);
                int before = plan.Sites.Count;
                string rewritten = RewriteCalls(expanded, plan, label);
                if (plan.Sites.Count - before > 1)
                    for (int i = before; i < plan.Sites.Count; i++) plan.Sites[i].Label = $"{label}#{i - before + 1}";
                plan.Lines.Add(rewritten + comment);
            }
            return plan;
        }

        // ------------------------------------------------------------------ values

        /// <summary>The value of a site for a run: uniform in [-1, 1] or standard normal.</summary>
        public static double Value(uint seed, McSite site, int run)
        {
            double u = Uniform01(seed, (ulong)site.Index, run);
            return site.Kind == SiteKind.Uniform ? 2 * u - 1 : Stats.NormalInv(u);
        }

        /// <summary>Uniform (0, 1), a pure function of (seed, stream, run).</summary>
        public static double Uniform01(uint seed, ulong stream, int run)
        {
            ulong h = Mix(Mix(Mix(seed + 0x5350494341UL) ^ (stream * 0xD1B54A32D192ED03UL)) ^ (ulong)(uint)run);
            return ((h >> 11) + 0.5) * (1.0 / 9007199254740992.0);
        }

        static ulong Mix(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Stream used for the per-run random temperature (never collides with a site index).</summary>
        public const ulong TempStream = 0x7E3D_0000_0001UL;

        // ------------------------------------------------------------------ rewriting

        static Dictionary<string, FuncDef> ParseFuncs(IList<string> lines)
        {
            var map = new Dictionary<string, FuncDef>(StringComparer.OrdinalIgnoreCase);
            var rx = new Regex(@"^\.func\s+([A-Za-z_][\w]*)\s*\(([^)]*)\)\s*(.*)$", RegexOptions.IgnoreCase);
            foreach (var line in lines)
            {
                SplitComment(line.Trim(), out string code, out _);
                var m = rx.Match(code);
                if (!m.Success) continue;
                string body = m.Groups[3].Value.Trim();
                if (body.StartsWith("=")) body = body.Substring(1).Trim();
                if (body.StartsWith("{") && body.EndsWith("}")) body = body.Substring(1, body.Length - 2).Trim();
                map[m.Groups[1].Value] = new FuncDef
                {
                    Name = m.Groups[1].Value,
                    Params = m.Groups[2].Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
                    Body = body,
                };
            }
            return map;
        }

        static string ExpandFuncs(string code, Dictionary<string, FuncDef> funcs, HashSet<string> randomFuncs, List<string> inlined)
        {
            return ReplaceCalls(code, randomFuncs, (name, args) =>
            {
                var f = funcs[name];
                if (!inlined.Contains(f.Name, StringComparer.OrdinalIgnoreCase)) inlined.Add(f.Name);
                string body = f.Body;
                for (int i = 0; i < f.Params.Count; i++)
                {
                    string arg = i < args.Count ? args[i] : "0";
                    body = Regex.Replace(body, @"(?<![\w.$])" + Regex.Escape(f.Params[i]) + @"(?![\w(])", "(" + arg + ")", RegexOptions.IgnoreCase);
                }
                return "(" + body + ")";
            });
        }

        static string RewriteCalls(string code, McPlan plan, string label)
        {
            return ReplaceCalls(code, RandomFuncs, (name, args) =>
            {
                // Arguments may contain random calls themselves (e.g. mc(mc(1k,.1),.05)).
                var a = args.Select(x => HasCall(x, RandomFuncs) ? RewriteCalls(x, plan, label) : x).ToList();
                var site = new McSite { Index = plan.Sites.Count + 1, Label = label, Original = $"{name}({string.Join(",", args)})" };
                plan.Sites.Add(site);
                switch (name.ToLowerInvariant())
                {
                    case "mc":     // uniform in nominal*(1 +/- tol)
                        site.Kind = SiteKind.Uniform;
                        return $"(({Arg(a, 0)})*(1+({Arg(a, 1)})*{site.Param}))";
                    case "flat":   // uniform in [-x, x]
                        site.Kind = SiteKind.Uniform;
                        return $"(({Arg(a, 0)})*{site.Param})";
                    default:       // gauss(sigma): normal, mean 0
                        site.Kind = SiteKind.Gaussian;
                        return $"(({Arg(a, 0)})*{site.Param})";
                }
            });
        }

        static string Arg(List<string> a, int i) => i < a.Count ? a[i] : "0";

        /// <summary>Replaces every call name(args) of one of <paramref name="names"/> (outermost first).</summary>
        static string ReplaceCalls(string code, IEnumerable<string> names, Func<string, List<string>, string> repl)
        {
            var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            if (set.Count == 0) return code;
            var sb = new StringBuilder();
            int i = 0;
            while (i < code.Length)
            {
                if ((char.IsLetter(code[i]) || code[i] == '_') && (i == 0 || !IsIdent(code[i - 1])))
                {
                    int j = i;
                    while (j < code.Length && IsIdent(code[j])) j++;
                    string ident = code.Substring(i, j - i);
                    int k = j;
                    while (k < code.Length && code[k] == ' ') k++;
                    if (set.Contains(ident) && k < code.Length && code[k] == '(')
                    {
                        int close = MatchParen(code, k);
                        if (close > k)
                        {
                            var args = SplitArgs(code.Substring(k + 1, close - k - 1));
                            sb.Append(repl(ident, args));
                            i = close + 1;
                            continue;
                        }
                    }
                    sb.Append(ident);
                    i = j;
                    continue;
                }
                sb.Append(code[i]);
                i++;
            }
            return sb.ToString();
        }

        static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '$' || c == '§';

        static int MatchParen(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        static List<string> SplitArgs(string s)
        {
            var list = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(' || c == '{') depth++;
                else if (c == ')' || c == '}') depth--;
                else if (c == ',' && depth == 0) { list.Add(s.Substring(start, i - start).Trim()); start = i + 1; }
            }
            list.Add(s.Substring(start).Trim());
            return list;
        }

        static bool HasCall(string code, IEnumerable<string> names)
        {
            foreach (var n in names)
                if (Regex.IsMatch(code, @"(?<![\w.$§])" + Regex.Escape(n) + @"\s*\(", RegexOptions.IgnoreCase)) return true;
            return false;
        }

        /// <summary>LTspice netlists use ';' for trailing comments (e.g. the pin map after X§U1 ...).</summary>
        static void SplitComment(string line, out string code, out string comment)
        {
            int c = line.IndexOf(';');
            code = c >= 0 ? line.Substring(0, c) : line;
            comment = c >= 0 ? line.Substring(c) : "";
        }

        static string LabelOf(string t)
        {
            var tok = Netlist.Tokenize(t);
            if (tok.Count == 0) return "?";
            string k = tok[0].ToLowerInvariant();
            if ((k == ".param" || k == ".model") && tok.Count > 1)
            {
                string n = tok[1];
                int eq = n.IndexOf('=');
                return eq > 0 ? n.Substring(0, eq) : n;
            }
            return tok[0].Replace("§", "");
        }

        static string Short(string s) => s.Length > 90 ? s.Substring(0, 87) + "..." : s;

        // ------------------------------------------------------------------ chunk injection

        /// <summary>The .param lines that give each site its values for runs first..last.</summary>
        public void AppendParams(StringBuilder sb, uint seed, int first, int last)
        {
            foreach (var s in Sites)
                AppendTable(sb, s.Param, first, last, r => Value(seed, s, r));
        }

        public static void AppendTable(StringBuilder sb, string param, int first, int last, Func<int, double> value)
        {
            var inv = CultureInfo.InvariantCulture;
            sb.Append(".param ").Append(param).Append(" = ");
            if (first == last) { sb.Append(value(first).ToString("R", inv)).Append("\r\n"); return; }
            sb.Append("table(run");
            for (int r = first; r <= last; r++)
                sb.Append(',').Append(r.ToString(inv)).Append(',').Append(value(r).ToString("R", inv));
            sb.Append(")\r\n");
        }
    }
}
