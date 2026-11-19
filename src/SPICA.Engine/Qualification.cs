using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Spica.Core
{
    /// <summary>
    /// One qualification test: a supply waveform written as steps ("start Vnom; hold 50m; jump 0; hold 10m; ..."),
    /// the paragraph of the standard it comes from and the equipment categories it applies to.
    /// </summary>
    public sealed class QualTest
    {
        public string Name = "";
        public string Section = "";
        public List<string> Categories = new List<string>();
        public string Steps = "";
        public string Edge = "1u";            // time of a jump: PWL points need increasing times
        public string Note = "";

        public bool AppliesTo(string category) =>
            Categories.Count == 0 || Categories.Contains(category, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A library of qualification tests, kept as a text file the user reviews and edits:
    /// [Category X] blocks set variables (Vnom, levels, times) and [Test name] blocks describe the waveforms.
    /// </summary>
    public sealed class QualLibrary
    {
        public string Standard = "";
        /// <summary>Short name on the sub-tab ("DO-160G §16"); the file name when the library has no Tab line.</summary>
        public string Tab = "";
        /// <summary>The file it was read from (its name keys the setup's choices for this library).</summary>
        public string Path = "";
        public string Key => System.IO.Path.GetFileNameWithoutExtension(Path ?? "");
        public readonly Dictionary<string, Dictionary<string, string>> Categories =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> CategoryOrder = new List<string>();
        public readonly List<QualTest> Tests = new List<QualTest>();
        public readonly List<string> Problems = new List<string>();

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The folder of the libraries, one file per standard (one sub-tab each): next to SPICA, or in AppData.</summary>
        public static string Folder => AppSettings.Resolve("qualification");

        const string DefaultFile = "do160g_s16_28vdc.txt";

        /// <summary>Every library in the folder; the DO-160G Section 16 one is written there from the defaults on first use.</summary>
        public static List<QualLibrary> LoadAll()
        {
            var list = new List<QualLibrary>();
            string dir = Folder;
            try
            {
                Directory.CreateDirectory(dir);
                string def = System.IO.Path.Combine(dir, DefaultFile);
                if (!File.Exists(def)) File.WriteAllText(def, Do160G28Vdc, new UTF8Encoding(false));
                foreach (var f in Directory.GetFiles(dir, "*.txt").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var lib = Parse(File.ReadAllText(f, Encoding.UTF8));
                    lib.Path = f;
                    if (lib.Tab.Length == 0) lib.Tab = System.IO.Path.GetFileNameWithoutExtension(f);
                    list.Add(lib);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            if (list.Count == 0)
            {
                var lib = Parse(Do160G28Vdc);
                lib.Path = DefaultFile;
                list.Add(lib);
            }
            return list;
        }

        public static QualLibrary Parse(string text)
        {
            var lib = new QualLibrary();
            Dictionary<string, string> cat = null;
            QualTest test = null;
            int no = 0;
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                no++;
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line.EndsWith("]"))
                {
                    string head = line.Substring(1, line.Length - 2).Trim();
                    cat = null; test = null;
                    if (head.StartsWith("Category ", StringComparison.OrdinalIgnoreCase))
                    {
                        string name = head.Substring(9).Trim();
                        cat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        lib.Categories[name] = cat;
                        if (!lib.CategoryOrder.Contains(name, StringComparer.OrdinalIgnoreCase)) lib.CategoryOrder.Add(name);
                    }
                    else if (!head.Equals("Library", StringComparison.OrdinalIgnoreCase))
                    {
                        test = new QualTest { Name = head };
                        lib.Tests.Add(test);
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) { lib.Problems.Add($"line {no}: expected name = value"); continue; }
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                // a comment after the value: "Vmax = 30.3   ; check"
                int sc = val.IndexOf(';');
                if (cat != null && sc >= 0) val = val.Substring(0, sc).Trim();
                if (cat != null) { cat[key] = val; continue; }
                if (test == null)
                {
                    if (key.Equals("Standard", StringComparison.OrdinalIgnoreCase)) lib.Standard = val;
                    else if (key.Equals("Tab", StringComparison.OrdinalIgnoreCase)) lib.Tab = val;
                    continue;
                }
                switch (key.ToLowerInvariant())
                {
                    case "section": test.Section = val; break;
                    case "categories": test.Categories = val.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList(); break;
                    case "steps": test.Steps = val; break;
                    case "edge": test.Edge = val; break;
                    case "note": test.Note = val; break;
                    default: lib.Problems.Add($"line {no}: unknown key '{key}'"); break;
                }
            }
            return lib;
        }

        /// <summary>The variables of a category, evaluated (a value may use the ones before it).</summary>
        public Dictionary<string, double> Variables(string category)
        {
            var v = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (category != null && Categories.TryGetValue(category, out var c))
                foreach (var kv in c)
                    v[kv.Key] = Expr.TryParse(kv.Value, out var e, out _, true) ? e.Eval(v) : double.NaN;
            return v;
        }

        public IEnumerable<QualTest> TestsFor(string category) => Tests.Where(t => t.AppliesTo(category));

        // ------------------------------------------------------------------ waveforms

        /// <summary>
        /// The waveform of a test as PWL points (time, volts). Steps, separated by ';':
        /// start V | hold T | to V in T | jump V | repeat N ( steps ). Values are expressions of the category's variables.
        /// </summary>
        public static List<double[]> Waveform(QualTest t, IDictionary<string, double> vars, out string error)
        {
            error = null;
            var pts = new List<double[]>();
            try
            {
                double edge = Value(t.Edge, vars);
                if (!(edge > 0)) throw new FormatException("Edge must be a time above 0");
                double now = 0, level = vars.TryGetValue("Vnom", out var vn) ? vn : 0;
                bool started = false;
                void Point(double time, double v) { pts.Add(new[] { time, v }); }
                void Run(string steps, int depth)
                {
                    if (depth > 4) throw new FormatException("repeat nested too deep");
                    foreach (var step in SplitSteps(steps))
                    {
                        var words = step.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        string verb = words[0].ToLowerInvariant(), rest = words.Length > 1 ? words[1].Trim() : "";
                        if (!started && verb != "start") { Point(0, level); started = true; }
                        switch (verb)
                        {
                            case "start":
                                if (started) throw new FormatException("'start' must be the first step");
                                level = Value(rest, vars); Point(0, level); started = true; break;
                            case "hold":
                                {
                                    double d = Value(rest, vars);
                                    if (d < 0) throw new FormatException($"negative time in '{step}'");
                                    now += d; Point(now, level); break;
                                }
                            case "jump":
                                now += edge; level = Value(rest, vars); Point(now, level); break;
                            case "to":
                                {
                                    int ix = rest.LastIndexOf(" in ", StringComparison.OrdinalIgnoreCase);
                                    if (ix < 0) throw new FormatException($"'{step}': write 'to <level> in <time>'");
                                    double v = Value(rest.Substring(0, ix), vars), d = Value(rest.Substring(ix + 4), vars);
                                    if (!(d > 0)) throw new FormatException($"'{step}': the ramp time must be above 0");
                                    now += d; level = v; Point(now, level); break;
                                }
                            case "repeat":
                                {
                                    int open = rest.IndexOf('('), close = rest.LastIndexOf(')');
                                    if (open < 0 || close < open) throw new FormatException($"'{step}': write 'repeat <n> ( steps )'");
                                    int n = (int)Math.Round(Value(rest.Substring(0, open), vars));
                                    if (n < 1 || n > 1000) throw new FormatException($"'{step}': repeat 1 to 1000 times");
                                    string inner = rest.Substring(open + 1, close - open - 1);
                                    for (int k = 0; k < n; k++) Run(inner, depth + 1);
                                    break;
                                }
                            default: throw new FormatException($"unknown step '{step}' (start, hold, to .. in, jump, repeat)");
                        }
                    }
                }
                Run(t.Steps, 0);
                if (pts.Count < 2) throw new FormatException("the waveform needs at least one 'hold'");
            }
            catch (FormatException ex) { error = ex.Message; }
            return pts;
        }

        static double Value(string text, IDictionary<string, double> vars)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("a level or time is missing");
            if (!Expr.TryParse(text, out var e, out var err, true)) throw new FormatException($"'{text}': {err}");
            double v = e.Eval(vars);
            if (double.IsNaN(v) || double.IsInfinity(v)) throw new FormatException($"'{text}' has no value (unknown name?)");
            return v;
        }

        /// <summary>Steps split at ';', keeping the inside of repeat ( ... ) together.</summary>
        static IEnumerable<string> SplitSteps(string s)
        {
            int depth = 0, start = 0;
            for (int i = 0; i <= s.Length; i++)
            {
                char c = i < s.Length ? s[i] : ';';
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ';' && depth == 0)
                {
                    string part = s.Substring(start, i - start).Trim();
                    if (part.Length > 0) yield return part;
                    start = i + 1;
                }
            }
            if (depth != 0) throw new FormatException("unbalanced parentheses");
        }

        static string G(double v) => v.ToString("0.#########", Inv);

        /// <summary>LTspice source value: PWL(t1 v1 t2 v2 ...).</summary>
        public static string PwlValue(List<double[]> pts) => "PWL(" + string.Join(" ", pts.Select(p => G(p[0]) + " " + G(p[1]))) + ")";

        /// <summary>The same points as a PWL file (time value per line), for PWL file=...</summary>
        public static string PwlFile(List<double[]> pts) => string.Join("\r\n", pts.Select(p => G(p[0]) + "\t" + G(p[1]))) + "\r\n";

        // ------------------------------------------------------------------ the default library

        /// <summary>
        /// RTCA DO-160G Section 16, 28 VDC equipment. Starting values written from memory, NOT checked against the
        /// standard: the user reviews every level and time in the file before relying on a result.
        /// </summary>
        public const string Do160G28Vdc =
@"; SPICA qualification library
; RTCA DO-160G, Section 16 (Power Input) - 28 VDC equipment
;
; *** STARTING VALUES, NOT CHECKED AGAINST THE STANDARD ***
; They were written as a template. Check every level, duration, repetition and paragraph
; number with your copy of DO-160G before using a result, and correct this file.
;
; How the file works
;   [Category X]  variables of an equipment category (any name), used by the tests below.
;                 A value may use the variables above it, e.g.  Half = Vnom/2
;   [Test name]   one test:
;     Section    = paragraph of the standard (shown in the report)
;     Categories = categories it applies to (empty = all)
;     Edge       = time of a 'jump' (default 1u)
;     Note       = free text
;     Steps      = the supply waveform, steps separated by ';'
;                    start V          level at t = 0 (default Vnom)
;                    hold T           keep the level for T
;                    to V in T        ramp to V in T
;                    jump V           go to V within Edge
;                    repeat N ( ... ) the steps in brackets N times
;   Numbers take SPICE suffixes and units: 10m, 10ms, 28V, 1.5k.
;   Lines starting with ';' are comments.

[Library]
Standard = RTCA DO-160G Section 16, 28 VDC
Tab      = DO-160G 16 Power input

; ---------------------------------------------------------------- categories (verify)
[Category A]
Vnom     = 28
Vmax     = 30.3      ; normal operating, maximum
Vmin     = 22.0      ; normal operating, minimum
SurgeHi  = 40        ; normal surge, overvoltage
SurgeHiT = 30m
SurgeLo  = 15        ; normal surge, undervoltage
SurgeLoT = 30m
AbnHi    = 46.3      ; abnormal surge, overvoltage
AbnHiT   = 100m
Pre      = 50m       ; time at Vnom before the event
Post     = 500m      ; time at Vnom after the event (recovery)
Gap      = 1         ; time between repeated surges

[Category B]
Vnom     = 28
Vmax     = 30.3
Vmin     = 22.0
SurgeHi  = 40
SurgeHiT = 30m
SurgeLo  = 15
SurgeLoT = 30m
AbnHi    = 46.3
AbnHiT   = 100m
Pre      = 50m
Post     = 500m
Gap      = 1

[Category Z]
Vnom     = 28
Vmax     = 32.2
Vmin     = 22.0
SurgeHi  = 50
SurgeHiT = 50m
SurgeLo  = 12
SurgeLoT = 30m
AbnHi    = 80
AbnHiT   = 100m
Pre      = 50m
Post     = 500m
Gap      = 1

; ---------------------------------------------------------------- normal operating voltage
[Normal operating voltage, maximum]
Section    = 16.6.1.1
Steps      = start Vmax; hold Pre + Post

[Normal operating voltage, minimum]
Section    = 16.6.1.1
Steps      = start Vmin; hold Pre + Post

; ---------------------------------------------------------------- momentary power interruptions
[Power interruption, single, 10 ms]
Section    = 16.6.1.4
Steps      = start Vnom; hold Pre; jump 0; hold 10m; jump Vnom; hold Post

[Power interruption, single, 25 ms]
Section    = 16.6.1.4
Steps      = start Vnom; hold Pre; jump 0; hold 25m; jump Vnom; hold Post

[Power interruption, single, 50 ms]
Section    = 16.6.1.4
Steps      = start Vnom; hold Pre; jump 0; hold 50m; jump Vnom; hold Post

[Power interruption, single, 75 ms]
Section    = 16.6.1.4
Steps      = start Vnom; hold Pre; jump 0; hold 75m; jump Vnom; hold Post

[Power interruption, single, 100 ms]
Section    = 16.6.1.4
Steps      = start Vnom; hold Pre; jump 0; hold 100m; jump Vnom; hold Post

[Power interruption, single, 200 ms]
Section    = 16.6.1.4
Steps      = start Vnom; hold Pre; jump 0; hold 200m; jump Vnom; hold Post

[Power interruption, double, 50 ms + 50 ms]
Section    = 16.6.1.4
Note       = Template: two interruptions with power back in between. Set the durations and the gap from the standard.
Steps      = start Vnom; hold Pre; jump 0; hold 50m; jump Vnom; hold 100m; jump 0; hold 50m; jump Vnom; hold Post

; ---------------------------------------------------------------- normal surges
[Normal surge, overvoltage]
Section    = 16.6.1.3
Note       = Applied three times.
Steps      = start Vnom; hold Pre; repeat 3 ( jump SurgeHi; hold SurgeHiT; jump Vnom; hold Gap )

[Normal surge, undervoltage]
Section    = 16.6.1.3
Note       = Applied three times.
Steps      = start Vnom; hold Pre; repeat 3 ( jump SurgeLo; hold SurgeLoT; jump Vnom; hold Gap )

; ---------------------------------------------------------------- abnormal surges
[Abnormal surge, overvoltage]
Section    = 16.6.2.4
Note       = Applied three times.
Steps      = start Vnom; hold Pre; repeat 3 ( jump AbnHi; hold AbnHiT; jump Vnom; hold Gap )
";
    }
}
