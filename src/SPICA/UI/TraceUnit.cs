using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Spica.UI
{
    /// <summary>
    /// The unit of a trace's expression, found as LTspice finds it, by the dimensions of what it reads: volts and
    /// amperes, multiplied and divided. V(out)*I(R1) is in W, V(out)/I(R1) in Ω, I(R1)/V(out) in S, V(out)*V(out) in V²;
    /// a sum needs the same unit on both sides; numbers have none; abs, min, max keep it; sqrt halves it; pow(x, n)
    /// raises it; db() gives dB; exp, ln, log, sin... give a plain number. Unknown: "?" (shown as no unit).
    /// </summary>
    public static class TraceUnit
    {
        /// <summary>Volts and amperes as powers (V¹A⁰ = V), or a named non-SI one (dB), or none known.</summary>
        struct Dim
        {
            public int V, A;
            public string Special;          // "dB", or "?" when the unit cannot be told
            public static Dim None => new Dim();
            public static Dim Unknown => new Dim { Special = "?" };
            public bool IsUnknown => Special == "?";
            public bool Same(Dim o) => V == o.V && A == o.A && Special == o.Special;
        }

        static readonly Regex Signal = new Regex(@"^([VI])[A-Za-z0-9]{0,2}\(([^()]*)\)", RegexOptions.IgnoreCase);

        public static string Of(string expr)
        {
            Dim d;
            try
            {
                var p = new Parser(expr ?? "");
                d = p.Sum();
                p.Ws();
                if (!p.End) d = Dim.Unknown;
            }
            catch { d = Dim.Unknown; }
            return Name(d);
        }

        static string Name(Dim d)
        {
            if (d.Special != null) return d.Special == "?" ? "" : d.Special;
            if (d.V == 0 && d.A == 0) return "";
            if (d.V == 1 && d.A == 0) return "V";
            if (d.V == 0 && d.A == 1) return "A";
            if (d.V == 1 && d.A == 1) return "W";
            if (d.V == 1 && d.A == -1) return "Ω";
            if (d.V == -1 && d.A == 1) return "S";
            string Pow(string u, int n) => n == 1 ? u : u + (n == 2 ? "²" : n == 3 ? "³" : "^" + n.ToString(CultureInfo.InvariantCulture));
            var up = new List<string>(); var down = new List<string>();
            if (d.V > 0) up.Add(Pow("V", d.V)); if (d.A > 0) up.Add(Pow("A", d.A));
            if (d.V < 0) down.Add(Pow("V", -d.V)); if (d.A < 0) down.Add(Pow("A", -d.A));
            return (up.Count > 0 ? string.Join("·", up) : "1") + (down.Count > 0 ? "/" + string.Join("·", down) : "");
        }

        /// <summary>The same grammar as the expressions (Expr): + - * / ^ **, unary signs, parentheses, numbers, functions.</summary>
        sealed class Parser
        {
            readonly string s;
            int pos;
            public Parser(string s) { this.s = s; }
            public bool End => pos >= s.Length;
            public void Ws() { while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++; }
            char Peek() { Ws(); return pos < s.Length ? s[pos] : '\0'; }

            public Dim Sum()
            {
                var a = Product();
                while (Peek() == '+' || Peek() == '-')
                {
                    pos++;
                    var b = Product();
                    // a sum of different units has none that can be told
                    if (!a.Same(b)) a = a.IsUnknown || b.IsUnknown || a.Special != null || b.Special != null ? Dim.Unknown : IsPlain(a) ? b : IsPlain(b) ? a : Dim.Unknown;
                }
                return a;
            }

            static bool IsPlain(Dim d) => d.Special == null && d.V == 0 && d.A == 0;

            Dim Product()
            {
                var a = Unary();
                while (true)
                {
                    char c = Peek();
                    if (c != '*' && c != '/') return a;
                    pos++;
                    if (c == '*' && pos < s.Length && s[pos] == '*') { pos++; a = Raise(a, Exponent()); continue; }
                    var b = Unary();
                    if (a.Special != null || b.Special != null) { a = IsPlain(b) && a.Special != null ? a : IsPlain(a) && b.Special != null && c == '*' ? b : Dim.Unknown; continue; }
                    a = c == '*' ? new Dim { V = a.V + b.V, A = a.A + b.A } : new Dim { V = a.V - b.V, A = a.A - b.A };
                }
            }

            Dim Unary()
            {
                char c = Peek();
                if (c == '-' || c == '+') { pos++; return Unary(); }
                var a = Atom();
                if (Peek() == '^') { pos++; return Raise(a, Exponent()); }
                return a;
            }

            /// <summary>The power a unit is raised to: a whole number written there; anything else leaves the unit unknown.</summary>
            double Exponent()
            {
                Ws();
                int st = pos;
                if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) pos++;
                while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.')) pos++;
                return double.TryParse(s.Substring(st, pos - st), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;
            }

            static Dim Raise(Dim a, double n)
            {
                if (IsPlain(a)) return a;
                if (a.Special != null || double.IsNaN(n) || n != Math.Floor(n)) return Dim.Unknown;
                return new Dim { V = a.V * (int)n, A = a.A * (int)n };
            }

            Dim Atom()
            {
                Ws();
                if (End) throw new FormatException();
                char c = s[pos];
                if (c == '(') { pos++; var d = Sum(); if (Peek() != ')') throw new FormatException(); pos++; return d; }
                if (char.IsDigit(c) || c == '.')
                {
                    while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '.' ||
                           ((s[pos] == '-' || s[pos] == '+') && pos > 0 && (s[pos - 1] == 'e' || s[pos - 1] == 'E')))) pos++;
                    return Dim.None;
                }
                var m = Signal.Match(s.Substring(pos));
                if (m.Success)
                {
                    pos += m.Length;
                    return char.ToUpperInvariant(m.Groups[1].Value[0]) == 'V' ? new Dim { V = 1 } : new Dim { A = 1 };
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int st = pos;
                    while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
                    string id = s.Substring(st, pos - st).ToLowerInvariant();
                    if (Peek() != '(') return id == "pi" ? Dim.None : Dim.Unknown;
                    pos++;
                    var args = new List<Dim>();
                    double second = double.NaN;
                    if (Peek() != ')')
                        while (true)
                        {
                            int at = pos;
                            args.Add(Sum());
                            if (args.Count == 2) double.TryParse(s.Substring(at, pos - at).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out second);
                            if (Peek() == ',') { pos++; continue; }
                            break;
                        }
                    if (Peek() != ')') throw new FormatException();
                    pos++;
                    switch (id)
                    {
                        case "abs": return args.Count == 1 ? args[0] : Dim.Unknown;
                        case "min": case "max": return args.Count == 2 && args[0].Same(args[1]) ? args[0] : Dim.Unknown;
                        case "sqrt":
                            if (args.Count != 1 || args[0].Special != null || args[0].V % 2 != 0 || args[0].A % 2 != 0) return args.Count == 1 && IsPlain(args[0]) ? Dim.None : Dim.Unknown;
                            return new Dim { V = args[0].V / 2, A = args[0].A / 2 };
                        case "pow": return args.Count == 2 ? Raise(args[0], second) : Dim.Unknown;
                        case "db": return new Dim { Special = "dB" };
                        default: return Dim.None;      // exp, ln, log, log10, sin, cos, tan, atan: a plain number
                    }
                }
                throw new FormatException();
            }
        }
    }
}
