using System;
using System.Collections.Generic;
using System.Globalization;

namespace Spica.Core
{
    /// <summary>
    /// Small expression evaluator for theoretical transfer functions, e.g. "1.93*x", "2*x + 10m",
    /// "x*(1 + 50e-6*(T-25))". Variables: x (the X column), T (temperature, if known), pi.
    /// Numbers accept SPICE suffixes (f p n u m k meg g t). Functions: sqrt abs exp ln log log10
    /// sin cos tan atan pow min max db.
    /// </summary>
    public sealed class Expr
    {
        abstract class Node { public abstract double Eval(IDictionary<string, double> v); }
        sealed class Num : Node { public double V; public override double Eval(IDictionary<string, double> v) => V; }
        sealed class Var : Node
        {
            public string Name;
            public override double Eval(IDictionary<string, double> v) => v.TryGetValue(Name, out var d) ? d : double.NaN;
        }
        sealed class Un : Node { public char Op; public Node A; public override double Eval(IDictionary<string, double> v) => Op == '-' ? -A.Eval(v) : A.Eval(v); }
        sealed class Bin : Node
        {
            public char Op; public Node A, B;
            public override double Eval(IDictionary<string, double> v)
            {
                double a = A.Eval(v), b = B.Eval(v);
                switch (Op)
                {
                    case '+': return a + b;
                    case '-': return a - b;
                    case '*': return a * b;
                    case '/': return a / b;
                    default: return Math.Pow(a, b);
                }
            }
        }
        sealed class Call : Node
        {
            public string F; public List<Node> Args;
            public override double Eval(IDictionary<string, double> v)
            {
                double A(int i) => i < Args.Count ? Args[i].Eval(v) : double.NaN;
                switch (F)
                {
                    case "sqrt": return Math.Sqrt(A(0));
                    case "abs": return Math.Abs(A(0));
                    case "exp": return Math.Exp(A(0));
                    case "ln": case "log": return Math.Log(A(0));
                    case "log10": return Math.Log10(A(0));
                    case "sin": return Math.Sin(A(0));
                    case "cos": return Math.Cos(A(0));
                    case "tan": return Math.Tan(A(0));
                    case "atan": return Math.Atan(A(0));
                    case "pow": return Math.Pow(A(0), A(1));
                    case "min": return Math.Min(A(0), A(1));
                    case "max": return Math.Max(A(0), A(1));
                    case "db": return 20 * Math.Log10(Math.Abs(A(0)));
                    default: return double.NaN;
                }
            }
        }

        static readonly HashSet<string> Functions = new HashSet<string>
            { "sqrt", "abs", "exp", "ln", "log", "log10", "sin", "cos", "tan", "atan", "pow", "min", "max", "db" };

        readonly Node root;
        public readonly string Text;

        Expr(Node n, string text) { root = n; Text = text; }

        public double Eval(double x, double temp = double.NaN)
        {
            var v = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["x"] = x, ["t"] = temp, ["pi"] = Math.PI };
            return root.Eval(v);
        }

        public static bool TryParse(string text, out Expr expr, out string error)
        {
            expr = null; error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "empty expression"; return false; }
            try
            {
                var p = new Parser(text);
                var n = p.ParseExpr();
                p.SkipWs();
                if (!p.End) throw new FormatException($"unexpected '{text.Substring(p.Pos)}'");
                expr = new Expr(n, text.Trim());
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        sealed class Parser
        {
            readonly string s;
            public int Pos;
            public Parser(string text) { s = text; }
            public bool End => Pos >= s.Length;
            public void SkipWs() { while (Pos < s.Length && char.IsWhiteSpace(s[Pos])) Pos++; }
            char Peek() { SkipWs(); return Pos < s.Length ? s[Pos] : '\0'; }

            public Node ParseExpr()
            {
                var a = ParseTerm();
                while (true)
                {
                    char c = Peek();
                    if (c != '+' && c != '-') return a;
                    Pos++;
                    a = new Bin { Op = c, A = a, B = ParseTerm() };
                }
            }

            Node ParseTerm()
            {
                var a = ParseUnary();
                while (true)
                {
                    char c = Peek();
                    if (c != '*' && c != '/') return a;
                    Pos++;
                    if (c == '*' && Pos < s.Length && s[Pos] == '*') { Pos++; a = new Bin { Op = '^', A = a, B = ParseUnary() }; continue; }
                    a = new Bin { Op = c, A = a, B = ParseUnary() };
                }
            }

            Node ParseUnary()
            {
                char c = Peek();
                if (c == '-' || c == '+') { Pos++; return new Un { Op = c, A = ParseUnary() }; }
                return ParsePower();
            }

            Node ParsePower()
            {
                var a = ParseAtom();
                if (Peek() == '^') { Pos++; return new Bin { Op = '^', A = a, B = ParseUnary() }; }
                return a;
            }

            Node ParseAtom()
            {
                char c = Peek();
                if (c == '(')
                {
                    Pos++;
                    var n = ParseExpr();
                    if (Peek() != ')') throw new FormatException("missing ')'");
                    Pos++;
                    return n;
                }
                if (char.IsDigit(c) || c == '.') return ParseNumber();
                if (char.IsLetter(c) || c == '_')
                {
                    int st = Pos;
                    while (Pos < s.Length && (char.IsLetterOrDigit(s[Pos]) || s[Pos] == '_')) Pos++;
                    string id = s.Substring(st, Pos - st).ToLowerInvariant();
                    if (Peek() == '(')
                    {
                        if (!Functions.Contains(id)) throw new FormatException($"unknown function '{id}'");
                        Pos++;
                        var args = new List<Node>();
                        if (Peek() != ')')
                            while (true)
                            {
                                args.Add(ParseExpr());
                                if (Peek() == ',') { Pos++; continue; }
                                break;
                            }
                        if (Peek() != ')') throw new FormatException("missing ')'");
                        Pos++;
                        return new Call { F = id, Args = args };
                    }
                    if (id != "x" && id != "t" && id != "pi") throw new FormatException($"unknown variable '{id}' (use x, T or pi)");
                    return new Var { Name = id };
                }
                throw new FormatException(End ? "unexpected end" : $"unexpected '{s[Pos]}'");
            }

            Node ParseNumber()
            {
                int st = Pos;
                while (Pos < s.Length && (char.IsDigit(s[Pos]) || s[Pos] == '.')) Pos++;
                if (Pos < s.Length && (s[Pos] == 'e' || s[Pos] == 'E') && Pos + 1 < s.Length &&
                    (char.IsDigit(s[Pos + 1]) || ((s[Pos + 1] == '-' || s[Pos + 1] == '+') && Pos + 2 < s.Length && char.IsDigit(s[Pos + 2]))))
                {
                    Pos += 2;
                    while (Pos < s.Length && char.IsDigit(s[Pos])) Pos++;
                }
                double v = double.Parse(s.Substring(st, Pos - st), NumberStyles.Float, CultureInfo.InvariantCulture);
                // SPICE scale suffix directly after the number
                int ss = Pos;
                while (Pos < s.Length && char.IsLetter(s[Pos])) Pos++;
                string suf = s.Substring(ss, Pos - ss).ToLowerInvariant();
                if (suf.Length > 0)
                {
                    double mul;
                    if (suf.StartsWith("meg")) mul = 1e6;
                    else switch (suf[0])
                        {
                            case 'f': mul = 1e-15; break;
                            case 'p': mul = 1e-12; break;
                            case 'n': mul = 1e-9; break;
                            case 'u': mul = 1e-6; break;
                            case 'm': mul = 1e-3; break;
                            case 'k': mul = 1e3; break;
                            case 'g': mul = 1e9; break;
                            case 't': mul = 1e12; break;
                            default: Pos = ss; mul = 1; break;   // not a suffix: let the caller see it
                        }
                    v *= mul;
                }
                return new Num { V = v };
            }
        }
    }
}
