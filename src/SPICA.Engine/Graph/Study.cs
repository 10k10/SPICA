using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Spica.Core.Graph
{
    /// <summary>What a wire carries. A wire only joins two ports of the same type (docs/ARCHITECTURE.md, section 3).</summary>
    public enum PortType { Circuit, Conditions, Temperature }

    /// <summary>A port of a block kind: its name, its type, whether it takes several wires, whether it may stay empty.</summary>
    public sealed class PortSpec
    {
        public string Name;
        public PortType Type;
        public bool Many;          // an input that takes any number of wires (Simulate's conditions)
        public bool Optional;      // an input that may be left unwired (an analysis's temperature: the schematic's own)

        public PortSpec(string name, PortType type, bool many = false, bool optional = false)
        { Name = name; Type = type; Many = many; Optional = optional; }
    }

    /// <summary>One block of a study: its kind, where it sits on the canvas and its properties (ordered; a key may repeat).</summary>
    public sealed class Block
    {
        public int Id;
        public string Kind = "";
        public int X, Y;
        public readonly List<KeyValuePair<string, string>> Props = new List<KeyValuePair<string, string>>();

        public BlockKind Spec => BlockKind.Find(Kind);

        public string Get(string key, string def = "")
        {
            foreach (var kv in Props) if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return def;
        }

        /// <summary>Sets the first value of a key (adds it when missing).</summary>
        public Block Set(string key, string value)
        {
            if (Reserved(key)) throw new ArgumentException($"'{key}' is kept for the block itself.");
            int i = Props.FindIndex(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            var kv2 = new KeyValuePair<string, string>(key, value ?? "");
            if (i >= 0) Props[i] = kv2; else Props.Add(kv2);
            return this;
        }

        /// <summary>Adds one more value of a repeatable key (e.g. one Criterion line per criterion).</summary>
        public Block Add(string key, string value)
        {
            if (Reserved(key)) throw new ArgumentException($"'{key}' is kept for the block itself.");
            Props.Add(new KeyValuePair<string, string>(key, value ?? ""));
            return this;
        }

        /// <summary>Keys the file format uses for the block itself, so no property may have them.</summary>
        public static bool Reserved(string key) => key.Equals("Kind", StringComparison.OrdinalIgnoreCase) || key.Equals("At", StringComparison.OrdinalIgnoreCase);

        /// <summary>A short label for lists and logs: "3 Simulate".</summary>
        public override string ToString() => $"{Id} {Kind}";

        public Block Clone()
        {
            var b = new Block { Id = Id, Kind = Kind, X = X, Y = Y };
            b.Props.AddRange(Props);
            return b;
        }
    }

    /// <summary>A wire from an output port of one block to an input port of another.</summary>
    public sealed class Wire
    {
        public int From, To;
        public string FromPort = "", ToPort = "";

        public override string ToString() => $"{From}.{FromPort} -> {To}.{ToPort}";

        public static bool TryParse(string s, out Wire w)
        {
            w = null;
            var parts = (s ?? "").Split(new[] { "->" }, StringSplitOptions.None);
            if (parts.Length != 2 || !End(parts[0], out int a, out string pa) || !End(parts[1], out int b, out string pb)) return false;
            w = new Wire { From = a, FromPort = pa, To = b, ToPort = pb };
            return true;
        }

        static bool End(string s, out int id, out string port)
        {
            s = s.Trim();
            int dot = s.IndexOf('.');
            port = dot > 0 ? s.Substring(dot + 1).Trim() : "";
            return int.TryParse(dot > 0 ? s.Substring(0, dot) : s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && port.Length > 0;
        }
    }

    /// <summary>A problem found by <see cref="Study.Validate"/>: the block (or 0 for the whole study) and the message.</summary>
    public sealed class StudyIssue
    {
        public int Block;
        public string Message;
        public override string ToString() => Block > 0 ? $"block {Block}: {Message}" : Message;
    }

    /// <summary>
    /// A study: a graph of blocks joined by typed wires, run as a whole (docs/ARCHITECTURE.md). Blocks are numbered
    /// within the study; wires name the ports at both ends.
    /// </summary>
    public sealed class Study
    {
        public string Name = "";
        public string Note = "";
        public readonly List<Block> Blocks = new List<Block>();
        public readonly List<Wire> Wires = new List<Wire>();

        public Block this[int id] => Blocks.FirstOrDefault(b => b.Id == id);

        public Block Add(string kind, int x = 0, int y = 0)
        {
            var spec = BlockKind.Find(kind) ?? throw new ArgumentException($"Unknown block kind '{kind}'.");
            var b = new Block { Id = Blocks.Count == 0 ? 1 : Blocks.Max(k => k.Id) + 1, Kind = spec.Name, X = x, Y = y };
            foreach (var p in spec.Defaults) b.Props.Add(p);
            Blocks.Add(b);
            return b;
        }

        public void Remove(Block b)
        {
            Blocks.Remove(b);
            Wires.RemoveAll(w => w.From == b.Id || w.To == b.Id);
        }

        /// <summary>
        /// Joins an output of <paramref name="from"/> to an input of <paramref name="to"/> (by port name; empty = the only
        /// port of the right type). Returns null on success, else why it cannot be wired.
        /// </summary>
        public string Connect(Block from, Block to, string fromPort = "", string toPort = "")
        {
            var a = from.Spec; var b = to.Spec;
            if (a == null || b == null) return "Unknown block kind.";
            if (from == to) return "A block cannot be wired to itself.";
            var o = string.IsNullOrEmpty(fromPort) ? (a.Outputs.Count == 1 ? a.Outputs[0] : null) : a.Output(fromPort);
            if (o == null) return $"{from.Kind} has no output {fromPort}.";
            var i = string.IsNullOrEmpty(toPort) ? b.Inputs.FirstOrDefault(p => p.Type == o.Type) : b.Input(toPort);
            if (i == null) return $"{to.Kind} takes no {o.Type} input.";
            if (i.Type != o.Type) return $"{o.Type} cannot go into {to.Kind}.{i.Name}, which takes {i.Type}.";
            if (Reaches(to.Id, from.Id)) return "That wire would make a loop.";
            if (Wires.Any(w => w.From == from.Id && w.FromPort == o.Name && w.To == to.Id && w.ToPort == i.Name)) return "These ports are already wired.";
            if (!i.Many) Wires.RemoveAll(w => w.To == to.Id && w.ToPort.Equals(i.Name, StringComparison.OrdinalIgnoreCase));
            Wires.Add(new Wire { From = from.Id, FromPort = o.Name, To = to.Id, ToPort = i.Name });
            return null;
        }

        /// <summary>Whether block b can be reached from block a along the wires.</summary>
        public bool Reaches(int a, int b)
        {
            var seen = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(a);
            while (stack.Count > 0)
            {
                int x = stack.Pop();
                if (x == b) return true;
                if (!seen.Add(x)) continue;
                foreach (var w in Wires) if (w.From == x) stack.Push(w.To);
            }
            return false;
        }

        public IEnumerable<Block> Upstream(Block b, string port) =>
            Wires.Where(w => w.To == b.Id && w.ToPort.Equals(port, StringComparison.OrdinalIgnoreCase)).Select(w => this[w.From]).Where(x => x != null);

        /// <summary>
        /// Everything that stops the study from running: unknown kinds, wires to missing blocks or ports or of the wrong
        /// type, several wires into a single input, loops, inputs left empty.
        /// </summary>
        public List<StudyIssue> Validate()
        {
            var list = new List<StudyIssue>();
            void Bad(int id, string m) => list.Add(new StudyIssue { Block = id, Message = m });
            foreach (var g in Blocks.GroupBy(b => b.Id).Where(g => g.Count() > 1)) Bad(g.Key, "two blocks have this number");
            foreach (var b in Blocks) if (b.Spec == null) Bad(b.Id, $"unknown kind '{b.Kind}'");
            foreach (var w in Wires)
            {
                var a = this[w.From]; var b = this[w.To];
                if (a == null || b == null) { Bad(0, $"wire {w}: no block {(a == null ? w.From : w.To)}"); continue; }
                if (a.Spec == null || b.Spec == null) continue;
                var o = a.Spec.Output(w.FromPort); var i = b.Spec.Input(w.ToPort);
                if (o == null) { Bad(a.Id, $"wire {w}: {a.Kind} has no output {w.FromPort}"); continue; }
                if (i == null) { Bad(b.Id, $"wire {w}: {b.Kind} has no input {w.ToPort}"); continue; }
                if (o.Type != i.Type) Bad(b.Id, $"wire {w}: {o.Type} into an input of type {i.Type}");
            }
            foreach (var b in Blocks.Where(x => x.Spec != null))
                foreach (var i in b.Spec.Inputs)
                {
                    int n = Wires.Count(w => w.To == b.Id && w.ToPort.Equals(i.Name, StringComparison.OrdinalIgnoreCase));
                    if (n == 0 && !i.Optional) Bad(b.Id, $"its {i.Type} input is not wired");
                    if (n > 1 && !i.Many) Bad(b.Id, $"its {i.Type} input takes one wire, it has {n}");
                }
            if (Order() == null) Bad(0, "the wires make a loop");
            return list;
        }

        /// <summary>The blocks in an order where every block comes after the ones that feed it (by number when free); null with a loop.</summary>
        public List<Block> Order()
        {
            var indeg = Blocks.ToDictionary(b => b.Id, b => 0);
            foreach (var w in Wires) if (indeg.ContainsKey(w.To) && indeg.ContainsKey(w.From)) indeg[w.To]++;
            var ready = new SortedSet<int>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
            var order = new List<Block>();
            while (ready.Count > 0)
            {
                int id = ready.Min;
                ready.Remove(id);
                order.Add(this[id]);
                foreach (var w in Wires.Where(w => w.From == id && indeg.ContainsKey(w.To)))
                    if (--indeg[w.To] == 0) ready.Add(w.To);
            }
            return order.Count == Blocks.Count ? order : null;
        }

        /// <summary>
        /// Brings a study read from an older version up to this one: a temperature typed into an analysis (before 0.10's
        /// Temperature block) becomes a Temperature block wired into it.
        /// </summary>
        public void Migrate()
        {
            foreach (var b in Blocks.ToList())
            {
                if (b.Spec?.Input("temperature") == null) continue;
                string t = b.Get("Temperature").Trim();
                b.Props.RemoveAll(kv => kv.Key.Equals("Temperature", StringComparison.OrdinalIgnoreCase));
                if (t.Length == 0) continue;
                var tb = Add(BlockKind.Temperature, b.X - 240, b.Y).Set("Mode", "List").Set("Points", t);
                Connect(tb, b, "temperature", "temperature");
            }
        }

        public Study Clone()
        {
            var s = new Study { Name = Name, Note = Note };
            s.Blocks.AddRange(Blocks.Select(b => b.Clone()));
            s.Wires.AddRange(Wires.Select(w => new Wire { From = w.From, FromPort = w.FromPort, To = w.To, ToPort = w.ToPort }));
            return s;
        }

        // ------------------------------------------------------------------ file format (inside the .spica)

        /// <summary>
        /// Writes the study as setup sections:
        /// [Study name] with its wires, then one [Block n] per block (Kind, At, then its properties).
        /// </summary>
        public void Write(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine($"[Study {Name}]");
            if (Note.Length > 0) sb.AppendLine("Note=" + Escape(Note));
            foreach (var w in Wires) sb.AppendLine("Wire=" + w);
            foreach (var b in Blocks.OrderBy(x => x.Id))
            {
                sb.AppendLine($"[Block {b.Id}]");
                sb.AppendLine("Kind=" + b.Kind);
                sb.AppendLine($"At={b.X.ToString(CultureInfo.InvariantCulture)},{b.Y.ToString(CultureInfo.InvariantCulture)}");
                foreach (var kv in b.Props) sb.AppendLine($"{kv.Key}={Escape(kv.Value)}");
            }
        }

        /// <summary>Reads one line of a [Study] section (<paramref name="block"/> null) or of a [Block] section that follows it.</summary>
        public void Read(Block block, string key, string val)
        {
            if (block == null)
            {
                if (key.Equals("Wire", StringComparison.OrdinalIgnoreCase) && Wire.TryParse(val, out var w)) Wires.Add(w);
                else if (key.Equals("Note", StringComparison.OrdinalIgnoreCase)) Note = Unescape(val);
                return;
            }
            if (key.Equals("Kind", StringComparison.OrdinalIgnoreCase)) block.Kind = BlockKind.Find(val)?.Name ?? val;
            else if (key.Equals("At", StringComparison.OrdinalIgnoreCase))
            {
                var xy = val.Split(',');
                if (xy.Length == 2) { int.TryParse(xy[0].Trim(), out block.X); int.TryParse(xy[1].Trim(), out block.Y); }
            }
            else block.Add(key, Unescape(val));
        }

        /// <summary>Several lines on one setup line: \n for a line break, \\ for a backslash.</summary>
        public static string Escape(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\\n");

        public static string Unescape(string s)
        {
            s = s ?? "";
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length && (s[i + 1] == 'n' || s[i + 1] == '\\')) { sb.Append(s[i + 1] == 'n' ? "\r\n" : "\\"); i++; }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
