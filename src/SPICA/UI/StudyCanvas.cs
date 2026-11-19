using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Spica.Core;
using Spica.Core.Graph;

namespace Spica.UI
{
    /// <summary>
    /// The study editor's canvas: blocks with typed ports, wires drawn as curves in the colour of their type.
    /// - Drag a block to move it; drag from an output port to an input port to wire them (the types must match).
    /// - Click a wire to select it; Delete removes the selected block or wire.
    /// - Right-click a port for a new block of a kind that fits; right-click the canvas to add a block there.
    /// - Drag the empty canvas (or middle-drag) to pan; wheel to zoom; Space or a double-click on the empty canvas fits.
    /// - A double-click on a block opens its configurator (<see cref="BlockOpened"/>); a block that misses something
    ///   it needs is drawn with a red border, and the pointer over it says what.
    /// - Blocks dropped from the library (a <see cref="BlockKind"/> name as text) are added where they fall.
    /// </summary>
    public sealed class StudyCanvas : Control
    {
        // ------------------------------------------------------------------ look

        public static readonly Dictionary<PortType, Color> TypeColor = new Dictionary<PortType, Color>
        {
            [PortType.Stimulus] = Color.FromArgb(14, 143, 168), [PortType.Circuit] = Color.FromArgb(31, 95, 191),
            [PortType.Conditions] = Color.FromArgb(184, 134, 11), [PortType.Data] = Color.FromArgb(46, 139, 87),
            [PortType.Table] = Color.FromArgb(138, 79, 191), [PortType.Ratings] = Color.FromArgb(160, 82, 45),
            [PortType.Verdicts] = Color.FromArgb(192, 57, 43),
        };

        public static readonly Dictionary<BlockFamily, Color> FamilyColor = new Dictionary<BlockFamily, Color>
        {
            [BlockFamily.Stimulus] = Color.FromArgb(11, 114, 133), [BlockFamily.Circuit] = Color.FromArgb(31, 95, 191),
            [BlockFamily.Variation] = Color.FromArgb(138, 109, 0), [BlockFamily.Simulation] = Color.FromArgb(0, 0, 128),
            [BlockFamily.Collection] = Color.FromArgb(106, 61, 154), [BlockFamily.Evaluation] = Color.FromArgb(155, 28, 28),
            [BlockFamily.External] = Color.FromArgb(122, 74, 30), [BlockFamily.Output] = Color.FromArgb(47, 79, 79),
        };

        /// <summary>The app's own 16 px icon for a block kind.</summary>
        public static Image KindIcon(string kind)
        {
            switch (kind)
            {
                case BlockKind.Schematic: return Win98.Icon("open16");
                case BlockKind.Candidates: return Win98.Icon("folder16");
                case BlockKind.SupplyWaveform: case BlockKind.Spectrum: case BlockKind.Plots: return Win98.Icon("chart16");
                case BlockKind.Fault: return Win98.Icon("stop16");
                case BlockKind.Susceptibility: return Win98.Icon("remote16");
                case BlockKind.Measure: return Win98.Icon("console16");
                case BlockKind.MonteCarlo: return Win98.Icon("clock16");
                case BlockKind.WorstCase: return Win98.Icon("hw16");
                case BlockKind.Combine: return Win98.Icon("queue16");
                case BlockKind.Simulate: return Win98.Icon("run16");
                case BlockKind.Stress: return Win98.Icon("memory16");
                case BlockKind.Limits: case BlockKind.Derating: case BlockKind.Survival: case BlockKind.SusceptibilityCheck: case BlockKind.Spec: return Win98.Icon("check16");
                case BlockKind.ErrorBudget: case BlockKind.Compare: case BlockKind.RequirementsMatrix: return Win98.Icon("results16");
                case BlockKind.PartsDatabase: return Win98.Icon("disk16");
                case BlockKind.Report: return Win98.Icon("doc16");
                case BlockKind.Export: return Win98.Icon("save16");
                default: return Win98.Icon("options16");
            }
        }

        const int BW = 184, Head = 19, Sub = 16, Row = 17, Pad = 4;

        static int BlockHeight(Block b)
        {
            var k = b.Spec;
            int rows = k == null ? 1 : Math.Max(1, Math.Max(k.Inputs.Count, k.Outputs.Count));
            return Head + Sub + Pad + rows * Row + Pad;
        }

        static Rectangle Bounds(Block b) => new Rectangle(b.X, b.Y, BW, BlockHeight(b));

        static PointF PortPoint(Block b, bool input, int i) =>
            new PointF(b.X + (input ? 0 : BW), b.Y + Head + Sub + Pad + Row * i + Row / 2f);

        // ------------------------------------------------------------------ state

        Study study;
        public Study Study
        {
            get => study;
            set { study = value; Selected = null; SelectedWire = null; Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty); }
        }

        public Block Selected { get; private set; }
        public Wire SelectedWire { get; private set; }

        /// <summary>A line under each block's title (e.g. "net in, 4 tests"); null = the first property.</summary>
        public Func<Block, string> Caption;
        /// <summary>What a block misses to run (a red border when not empty); null = nothing checked.</summary>
        public Func<Block, List<string>> Problems;
        /// <summary>Whether a block kind runs in this version (the others are drawn faded).</summary>
        public Func<string, bool> Runs = k => true;
        /// <summary>A double-click on a block: open its configurator.</summary>
        public event Action<Block> BlockOpened;
        readonly ToolTip tip = new ToolTip { InitialDelay = 300, AutoPopDelay = 20000 };
        string tipText = "";

        /// <summary>A 16 px magnifying glass in the style of the app's icons (for Fit).</summary>
        public static Image MagnifierIcon()
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var handle = new Pen(Color.FromArgb(96, 64, 32), 3f)) g.DrawLine(handle, 10, 10, 14, 14);
                using (var glass = new SolidBrush(Color.FromArgb(200, 230, 255))) g.FillEllipse(glass, 1, 1, 10, 10);
                using (var shine = new SolidBrush(Color.White)) g.FillEllipse(shine, 3, 3, 4, 3);
                using (var rim = new Pen(Color.Black, 1.5f)) g.DrawEllipse(rim, 1, 1, 10, 10);
            }
            return bmp;
        }

        /// <summary>State badges of a run (empty = none shown).</summary>
        public readonly Dictionary<int, BlockState> States = new Dictionary<int, BlockState>();

        public event EventHandler SelectionChanged;
        /// <summary>The study was edited (blocks moved, added, removed, wired).</summary>
        public event EventHandler StudyChanged;
        /// <summary>Something to tell the user (why a wire was refused...).</summary>
        public event Action<string> Message;

        float zoom = 1f;
        PointF pan = new PointF(0, 0);

        enum Drag { None, Move, Wire, Pan }
        Drag drag;
        Point dragStart, mouse;
        PointF panStart;
        Block dragBlock;
        Point blockStart;
        int wirePort;
        bool moved;

        public StudyCanvas()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.White;
            Font = Win98.Font;
            AllowDrop = true;
        }

        // ------------------------------------------------------------------ coordinates

        PointF ToWorld(Point p) => new PointF((p.X - pan.X) / zoom, (p.Y - pan.Y) / zoom);
        Point ToScreen(PointF w) => new Point((int)(w.X * zoom + pan.X), (int)(w.Y * zoom + pan.Y));

        /// <summary>Shows the whole study (at most at full size).</summary>
        public void FitAll()
        {
            if (study == null || study.Blocks.Count == 0 || Width < 50 || Height < 50) { zoom = 1; pan = new PointF(0, 0); Invalidate(); return; }
            var r = study.Blocks.Select(Bounds).Aggregate(Rectangle.Union);
            r.Inflate(30, 30);
            // never so small that the text cannot be read: a wide study then starts at its left edge, and pans
            // zoomed out the blocks keep a readable title (see DrawBlock), so a study can be fitted further out
            zoom = Math.Max(0.5f, Math.Min(1f, Math.Min(Width / (float)r.Width, Height / (float)r.Height)));
            float sx = Width - r.Width * zoom, sy = Height - r.Height * zoom;
            pan = new PointF(-r.X * zoom + Math.Max(0, sx / 2), -r.Y * zoom + Math.Max(0, Math.Min(sy / 2, 40)));
            Invalidate();
        }

        /// <summary>Pans so that a block is in view (centred when it was not), keeping the zoom.</summary>
        public void Reveal(Block b)
        {
            if (b == null) return;
            var r = Bounds(b);
            var a = ToScreen(new PointF(r.X, r.Y)); var z = ToScreen(new PointF(r.Right, r.Bottom));
            if (a.X >= 0 && a.Y >= 0 && z.X <= Width && z.Y <= Height) return;
            pan = new PointF(Width / 2f - (r.X + r.Width / 2f) * zoom, Height / 2f - (r.Y + r.Height / 2f) * zoom);
            Invalidate();
        }

        public void Select(Block b)
        {
            Selected = b; SelectedWire = null;
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        void Changed() { Invalidate(); StudyChanged?.Invoke(this, EventArgs.Empty); }

        /// <summary>Adds a block of a kind at a canvas point (or in the middle of the view) and selects it.</summary>
        public Block AddBlock(string kind, Point? screen = null)
        {
            if (study == null) return null;
            var w = ToWorld(screen ?? new Point(Width / 2 - (int)(BW * zoom / 2), Height / 3));
            var b = study.Add(kind, Snap(w.X), Snap(w.Y));
            Select(b);
            Changed();
            return b;
        }

        static int Snap(float v) => (int)Math.Round(v / 8f) * 8;

        /// <summary>Zoomed out far enough that the small text cannot be read: blocks show their title large, nothing else.</summary>
        bool Far => zoom < 0.85f;

        static Color Blend(Color a, Color b, double t) =>
            Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        // ------------------------------------------------------------------ hit testing

        Block BlockAt(PointF w) => study?.Blocks.LastOrDefault(b => Bounds(b).Contains(Point.Round(w)));

        /// <summary>The port under a canvas point: the block, input or output, its index.</summary>
        bool PortAt(PointF w, out Block block, out bool input, out int index)
        {
            block = null; input = false; index = -1;
            if (study == null) return false;
            float r = 7 / Math.Min(1f, zoom);
            foreach (var b in Enumerable.Reverse(study.Blocks))
            {
                var k = b.Spec;
                if (k == null) continue;
                for (int i = 0; i < k.Inputs.Count; i++)
                    if (Dist(PortPoint(b, true, i), w) <= r) { block = b; input = true; index = i; return true; }
                for (int i = 0; i < k.Outputs.Count; i++)
                    if (Dist(PortPoint(b, false, i), w) <= r) { block = b; input = false; index = i; return true; }
            }
            return false;
        }

        static float Dist(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        bool WireEnds(Wire w, out PointF a, out PointF b)
        {
            a = b = PointF.Empty;
            var fa = study[w.From]; var tb = study[w.To];
            if (fa?.Spec == null || tb?.Spec == null) return false;
            int oi = fa.Spec.Outputs.FindIndex(p => p.Name.Equals(w.FromPort, StringComparison.OrdinalIgnoreCase));
            int ii = tb.Spec.Inputs.FindIndex(p => p.Name.Equals(w.ToPort, StringComparison.OrdinalIgnoreCase));
            if (oi < 0 || ii < 0) return false;
            a = PortPoint(fa, false, oi); b = PortPoint(tb, true, ii);
            return true;
        }

        static PointF[] Curve(PointF a, PointF b)
        {
            float dx = Math.Max(40, Math.Abs(b.X - a.X) / 2);
            return new[] { a, new PointF(a.X + dx, a.Y), new PointF(b.X - dx, b.Y), b };
        }

        static PointF Bezier(PointF[] c, float t)
        {
            float u = 1 - t;
            return new PointF(u * u * u * c[0].X + 3 * u * u * t * c[1].X + 3 * u * t * t * c[2].X + t * t * t * c[3].X,
                              u * u * u * c[0].Y + 3 * u * u * t * c[1].Y + 3 * u * t * t * c[2].Y + t * t * t * c[3].Y);
        }

        Wire WireAt(PointF w)
        {
            if (study == null) return null;
            float tol = 5 / Math.Min(1f, zoom);
            foreach (var wire in study.Wires)
            {
                if (!WireEnds(wire, out var a, out var b)) continue;
                var c = Curve(a, b);
                for (int i = 0; i <= 24; i++) if (Dist(Bezier(c, i / 24f), w) <= tol) return wire;
            }
            return null;
        }

        // ------------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.White);
            // the dot grid, in screen space
            float step = 16 * zoom;
            if (step >= 6)
                using (var dot = new SolidBrush(Color.FromArgb(200, 200, 200)))
                    for (float x = pan.X % step; x < Width; x += step)
                        for (float y = pan.Y % step; y < Height; y += step)
                            g.FillRectangle(dot, x, y, 1, 1);
            if (study == null)
            {
                TextRenderer.DrawText(g, "No study.  New... in the bar above starts one: blank, from a template, or from the Setup and Qualification tabs.",
                    Font, ClientRectangle, Win98.Shadow, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            if (study.Blocks.Count == 0)
            {
                TextRenderer.DrawText(g, "Add blocks from the library on the left (double-click or drag), or right-click here.",
                    Font, ClientRectangle, Win98.Shadow, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            var st = g.Save();
            g.TranslateTransform(pan.X, pan.Y);
            g.ScaleTransform(zoom, zoom);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            foreach (var w in study.Wires) DrawWire(g, w);
            foreach (var b in study.Blocks) DrawBlock(g, b);
            foreach (var w in study.Wires) DrawNetLabel(g, w);

            if (drag == Drag.Wire && dragBlock?.Spec != null)
            {
                var a = PortPoint(dragBlock, false, wirePort);
                var m = ToWorld(mouse);
                var col = TypeColor[dragBlock.Spec.Outputs[wirePort].Type];
                using (var p = new Pen(col, 2f) { DashStyle = DashStyle.Dash })
                {
                    var c = Curve(a, m);
                    g.DrawBezier(p, c[0], c[1], c[2], c[3]);
                }
            }
            g.Restore(st);
        }

        void DrawWire(Graphics g, Wire w)
        {
            if (!WireEnds(w, out var a, out var b)) return;
            var type = study[w.From].Spec.Outputs.First(p => p.Name.Equals(w.FromPort, StringComparison.OrdinalIgnoreCase)).Type;
            var c = Curve(a, b);
            bool sel = w == SelectedWire;
            if (sel) using (var halo = new Pen(Color.FromArgb(255, 255, 160), 7f)) g.DrawBezier(halo, c[0], c[1], c[2], c[3]);
            using (var p = new Pen(TypeColor[type], sel ? 3.5f : 2.2f)) g.DrawBezier(p, c[0], c[1], c[2], c[3]);
        }

        /// <summary>On a stimulus wire, the net it goes into (or the two nets of a fault), so the connection is visible.</summary>
        void DrawNetLabel(Graphics g, Wire w)
        {
            var from = study[w.From];
            if (from?.Spec == null || !WireEnds(w, out var a, out var b)) return;
            if (from.Spec.Outputs.FirstOrDefault()?.Type != PortType.Stimulus) return;
            string text = NetText(from);
            if (text.Length == 0) return;
            var mid = Bezier(Curve(a, b), 0.5f);
            using (var font = Far ? new Font(Win98.Font.FontFamily, 7.5f / zoom) : (Font)Win98.Font.Clone())
            {
                var sz = g.MeasureString(text, font);
                var r = new RectangleF(mid.X - sz.Width / 2 - 2, mid.Y - sz.Height / 2, sz.Width + 4, sz.Height);
                using (var bg = new SolidBrush(Win98.InfoBack)) g.FillRectangle(bg, r);
                using (var pen = new Pen(TypeColor[PortType.Stimulus])) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                using (var br = new SolidBrush(Color.FromArgb(11, 79, 92))) g.DrawString(text, font, br, r.X + 2, r.Y);
            }
        }

        public static string NetText(Block stim)
        {
            if (stim.Kind == BlockKind.Fault)
            {
                string a = stim.Get("From"), b = stim.Get("To");
                return a.Length == 0 ? "" : $"{a} → {(b.Length == 0 ? "?" : b)}";
            }
            string net = stim.Get("Net");
            return net.Length == 0 ? "net ?" : "net " + net;
        }

        void DrawBlock(Graphics g, Block b)
        {
            var k = b.Spec;
            var r = Bounds(b);
            bool sel = b == Selected;
            using (var sh = new SolidBrush(Color.FromArgb(128, 128, 128))) g.FillRectangle(sh, r.X + 3, r.Y + 3, r.Width, r.Height);
            using (var body = new SolidBrush(Color.FromArgb(244, 244, 244))) g.FillRectangle(body, r);
            bool runs = k != null && Runs(b.Kind);
            var headCol = k == null ? Color.Gray : runs ? FamilyColor[k.Family] : Blend(FamilyColor[k.Family], Color.FromArgb(160, 160, 160), 0.6);
            var missing = Problems?.Invoke(b);
            bool bad = missing != null && missing.Count > 0;
            using (var hb = new SolidBrush(headCol)) g.FillRectangle(hb, r.X, r.Y, r.Width, Head);
            var oldSm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            using (var frame = new Pen(sel ? Win98.Navy : Color.Black, sel ? 2.5f : 1f)) g.DrawRectangle(frame, r);
            if (bad)
                using (var red = new Pen(Color.FromArgb(220, 0, 0), 3f)) g.DrawRectangle(red, r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6);
            g.SmoothingMode = oldSm;

            var oldIm = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            if (!Far) g.DrawImage(KindIcon(b.Kind), r.X + 2, r.Y + 1, 16, 16);
            g.InterpolationMode = oldIm;
            if (bad)
            {
                // a red "!" on the corner: something to set before it runs
                var dot = new RectangleF(r.Right - 9, r.Y - 9, 16, 16);
                using (var rb = new SolidBrush(Color.FromArgb(220, 0, 0))) g.FillEllipse(rb, dot);
                using (var wp = new Pen(Color.White, 1.5f)) g.DrawEllipse(wp, dot);
                using (var wb = new SolidBrush(Color.White)) g.DrawString("!", Win98.BoldFont, wb, dot.X + 5, dot.Y + 1);
            }
            // Zoomed out, a block shows its title at a size that can be read on the screen, over the title bar and the
            // line under it; the caption and the port names come back when zoomed in.
            if (Far)
            {
                using (var tf = new Font(Win98.BoldFont.FontFamily, 8.5f / zoom, FontStyle.Bold))
                using (var white = new SolidBrush(Color.White))
                using (var dark = new SolidBrush(Color.FromArgb(32, 32, 32)))
                {
                    var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
                    g.DrawString(k?.Title ?? b.Kind, tf, white, new RectangleF(r.X + 3, r.Y, r.Width - 6, Head + 2), fmt);
                    string capFar = Caption?.Invoke(b) ?? "";
                    using (var cf = new Font(Win98.Font.FontFamily, 7.5f / zoom))
                        g.DrawString(capFar, cf, dark, new RectangleF(r.X + 3, r.Y + Head + 1, r.Width - 6, BlockHeight(b) - Head - 2), new StringFormat { Trimming = StringTrimming.EllipsisWord });
                }
                if (States.TryGetValue(b.Id, out var stFar))
                {
                    // the run state as a coloured dot on the corner
                    var col = stFar == BlockState.Done ? Color.FromArgb(0, 150, 0) : stFar == BlockState.Cached ? Color.FromArgb(0, 120, 200) : stFar == BlockState.Running ? Color.FromArgb(0, 0, 160)
                            : stFar == BlockState.Error ? Color.FromArgb(200, 0, 0) : Color.Gray;
                    float dd = 7 / zoom;
                    using (var db = new SolidBrush(col)) g.FillEllipse(db, r.Right - dd - 3, r.Bottom - dd - 3, dd, dd);
                }
                if (k != null)
                    using (var edgeFar = new Pen(Color.Black))
                    {
                        for (int i = 0; i < k.Inputs.Count; i++) { var p = PortPoint(b, true, i); using (var f = new SolidBrush(TypeColor[k.Inputs[i].Type])) g.FillEllipse(f, p.X - 6, p.Y - 6, 12, 12); g.DrawEllipse(edgeFar, p.X - 6, p.Y - 6, 12, 12); }
                        for (int i = 0; i < k.Outputs.Count; i++) { var p = PortPoint(b, false, i); using (var f = new SolidBrush(TypeColor[k.Outputs[i].Type])) g.FillEllipse(f, p.X - 6, p.Y - 6, 12, 12); g.DrawEllipse(edgeFar, p.X - 6, p.Y - 6, 12, 12); }
                    }
                return;
            }
            using (var white = new SolidBrush(Color.White))
            {
                g.DrawString(k?.Title ?? b.Kind, Win98.BoldFont, white, new RectangleF(r.X + 20, r.Y + 3, r.Width - 22, Head - 3), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                if (States.TryGetValue(b.Id, out var s))
                {
                    string t = s == BlockState.Done ? "done" : s == BlockState.Cached ? "cached" : s == BlockState.Running ? "running" : s == BlockState.Error ? "error" : s == BlockState.Skipped ? "skipped" : "";
                    var sz = g.MeasureString(t, Win98.Font);
                    using (var hb2 = new SolidBrush(headCol)) g.FillRectangle(hb2, r.Right - sz.Width - 4, r.Y + 2, sz.Width + 2, Head - 4);
                    g.DrawString(t, Win98.Font, white, r.Right - sz.Width - 3, r.Y + 3);
                }
            }
            string cap = Caption?.Invoke(b) ?? b.Props.FirstOrDefault().Value ?? "";
            using (var grey = new SolidBrush(Color.FromArgb(64, 64, 64)))
                g.DrawString(cap, Win98.Font, grey, new RectangleF(r.X + 4, r.Y + Head + 2, r.Width - 8, Sub), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            if (k == null) return;

            using (var black = new SolidBrush(Color.Black))
            using (var edge = new Pen(Color.Black))
            {
                var right = new StringFormat { Alignment = StringAlignment.Far };
                for (int i = 0; i < k.Inputs.Count; i++)
                {
                    var p = PortPoint(b, true, i);
                    var spec = k.Inputs[i];
                    using (var fill = new SolidBrush(TypeColor[spec.Type])) g.FillEllipse(fill, p.X - 5, p.Y - 5, 10, 10);
                    g.DrawEllipse(edge, p.X - 5, p.Y - 5, 10, 10);
                    string label = spec.Name + (spec.Many ? "…" : "");
                    if (spec.Type == PortType.Stimulus)
                    {
                        var nets = study.Upstream(b, spec.Name).Select(NetText).Where(t => t.Length > 0).ToList();
                        label = nets.Count == 0 ? "stimulus (none)" : "stimulus: " + string.Join(", ", nets);
                    }
                    g.DrawString(label, Win98.Font, black, new RectangleF(p.X + 8, p.Y - 7, BW - 16, Row), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                }
                for (int i = 0; i < k.Outputs.Count; i++)
                {
                    var p = PortPoint(b, false, i);
                    var spec = k.Outputs[i];
                    using (var fill = new SolidBrush(TypeColor[spec.Type])) g.FillEllipse(fill, p.X - 5, p.Y - 5, 10, 10);
                    g.DrawEllipse(edge, p.X - 5, p.Y - 5, 10, 10);
                    g.DrawString(spec.Type.ToString(), Win98.Font, black, new RectangleF(p.X - BW / 2f, p.Y - 7, BW / 2f - 8, Row), right);
                }
            }
        }

        // ------------------------------------------------------------------ mouse and keys

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (study == null) return;
            dragStart = mouse = e.Location; moved = false;
            var w = ToWorld(e.Location);
            if (e.Button == MouseButtons.Middle) { drag = Drag.Pan; panStart = pan; Cursor = Cursors.SizeAll; return; }
            if (e.Button != MouseButtons.Left) return;
            if (PortAt(w, out var pb, out bool input, out int pi) && !input)
            {
                drag = Drag.Wire; dragBlock = pb; wirePort = pi;
                return;
            }
            var b = BlockAt(w);
            if (b != null)
            {
                // the block comes to the front
                study.Blocks.Remove(b); study.Blocks.Add(b);
                Select(b);
                drag = Drag.Move; dragBlock = b; blockStart = new Point(b.X, b.Y);
                return;
            }
            var wire = WireAt(w);
            if (wire != null) { Selected = null; SelectedWire = wire; Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty); return; }
            if (Selected != null || SelectedWire != null) { Selected = null; SelectedWire = null; Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty); }
            drag = Drag.Pan; panStart = pan;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            if (Math.Abs(e.X - dragStart.X) + Math.Abs(e.Y - dragStart.Y) > 3) moved = true;
            switch (drag)
            {
                case Drag.Move:
                    dragBlock.X = Snap(blockStart.X + (e.X - dragStart.X) / zoom);
                    dragBlock.Y = Snap(blockStart.Y + (e.Y - dragStart.Y) / zoom);
                    Invalidate();
                    break;
                case Drag.Pan:
                    pan = new PointF(panStart.X + e.X - dragStart.X, panStart.Y + e.Y - dragStart.Y);
                    Cursor = Cursors.SizeAll;
                    Invalidate();
                    break;
                case Drag.Wire:
                    Invalidate();
                    break;
                default:
                    var w = ToWorld(e.Location);
                    var over = BlockAt(w);
                    var why = over == null ? null : Problems?.Invoke(over);
                    string t = why == null || why.Count == 0 ? "" : (over.Spec?.Title ?? over.Kind) + " misses:\n- " + string.Join("\n- ", why) + "\n(double-click to set it up)";
                    if (t != tipText) { tipText = t; tip.SetToolTip(this, t); }
                    Cursor = PortAt(w, out _, out bool input, out _) ? (input ? Cursors.Hand : Cursors.Cross) : BlockAt(w) != null ? Cursors.SizeAll : WireAt(w) != null ? Cursors.Hand : Cursors.Default;
                    break;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var was = drag;
            drag = Drag.None;
            Cursor = Cursors.Default;
            if (study == null) return;
            var w = ToWorld(e.Location);
            if (e.Button == MouseButtons.Right) { ShowMenu(e.Location, w); return; }
            if (was == Drag.Move && moved) Changed();
            if (was == Drag.Wire)
            {
                if (PortAt(w, out var tb, out bool input, out int ti) && input)
                {
                    var why = study.Connect(dragBlock, tb, dragBlock.Spec.Outputs[wirePort].Name, tb.Spec.Inputs[ti].Name);
                    if (why != null) Message?.Invoke("Not wired: " + why);
                    else { Message?.Invoke($"Wired {dragBlock.Spec.Title} → {tb.Spec.Title} ({dragBlock.Spec.Outputs[wirePort].Type})."); Changed(); }
                }
                else if (moved) Message?.Invoke("Drop the wire on an input port (left side of a block) of the same colour.");
                Invalidate();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left || study == null) return;
            var b = BlockAt(ToWorld(e.Location));
            if (b == null) FitAll();
            else { drag = Drag.None; BlockOpened?.Invoke(b); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var before = ToWorld(e.Location);
            zoom = Math.Max(0.3f, Math.Min(2f, zoom * (float)Math.Pow(1.15, e.Delta / 120.0)));
            pan = new PointF(e.X - before.X * zoom, e.Y - before.Y * zoom);
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Delete || keyData == Keys.Space || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Delete) DeleteSelection();
            if (e.KeyCode == Keys.Space) { FitAll(); e.Handled = true; }
        }

        public void DeleteSelection()
        {
            if (study == null) return;
            if (Selected != null) { study.Remove(Selected); Message?.Invoke($"Removed {Selected.Spec?.Title ?? Selected.Kind}."); Selected = null; }
            else if (SelectedWire != null) { study.Wires.Remove(SelectedWire); Message?.Invoke("Wire removed."); SelectedWire = null; }
            else return;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Changed();
        }

        // ------------------------------------------------------------------ menus

        ContextMenuStrip menu;

        void ShowMenu(Point screen, PointF w)
        {
            menu?.Dispose();
            menu = new ContextMenuStrip();
            if (PortAt(w, out var pb, out bool input, out int pi))
            {
                var spec = input ? pb.Spec.Inputs[pi] : pb.Spec.Outputs[pi];
                // a new block of a kind that fits this port, placed beside it and wired
                var fits = BlockKind.All.Where(k => input ? k.Outputs.Any(o => o.Type == spec.Type) : k.Inputs.Any(i => i.Type == spec.Type)).ToList();
                menu.Items.Add(new ToolStripMenuItem((input ? "Feed " : "Connect ") + $"{spec.Type} {(input ? "from" : "to")} a new block:") { Enabled = false });
                foreach (var k in fits)
                {
                    var kind = k;
                    menu.Items.Add(new ToolStripMenuItem(k.Title, KindIcon(k.Name), (s, e) =>
                    {
                        var nb = study.Add(kind.Name, Snap(pb.X + (input ? -BW - 56 : BW + 56)), Snap(pb.Y));
                        var why = input ? study.Connect(nb, pb, "", spec.Name) : study.Connect(pb, nb, spec.Name, "");
                        if (why != null) Message?.Invoke(why);
                        Select(nb);
                        Changed();
                    }));
                }
                var wired = study.Wires.Where(x => input ? x.To == pb.Id && x.ToPort == spec.Name : x.From == pb.Id && x.FromPort == spec.Name).ToList();
                if (wired.Count > 0)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add(new ToolStripMenuItem($"Remove the {wired.Count} wire{(wired.Count == 1 ? "" : "s")} of this port", null, (s, e) => { foreach (var x in wired) study.Wires.Remove(x); Changed(); }));
                }
            }
            else if (BlockAt(w) is Block b)
            {
                Select(b);
                menu.Items.Add(new ToolStripMenuItem("&Duplicate", null, (s, e) =>
                {
                    var c = study.Add(b.Kind, b.X + 24, b.Y + 24);
                    c.Props.Clear(); c.Props.AddRange(b.Props);
                    Select(c); Changed();
                }));
                menu.Items.Add(new ToolStripMenuItem("&Remove", Win98.Icon("stop16"), (s, e) => DeleteSelection()) { ShortcutKeyDisplayString = "Del" });
            }
            else
            {
                var at = screen;
                menu.Items.Add(new ToolStripMenuItem("Add a block here:") { Enabled = false });
                foreach (BlockFamily f in Enum.GetValues(typeof(BlockFamily)))
                {
                    var sub = new ToolStripMenuItem(BlockKind.FamilyTitle(f));
                    foreach (var k in BlockKind.All.Where(x => x.Family == f))
                    {
                        var kind = k.Name;
                        sub.DropDownItems.Add(new ToolStripMenuItem(k.Title, KindIcon(k.Name), (s, e) => AddBlock(kind, at)));
                    }
                    menu.Items.Add(sub);
                }
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("&Fit the study in view", null, (s, e) => FitAll()));
            }
            Win98Renderer.Apply(menu);
            Win98Renderer.StyleDropDowns(menu.Items);
            menu.Show(this, screen);
        }

        // ------------------------------------------------------------------ drop from the library

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            e.Effect = e.Data.GetDataPresent(DataFormats.Text) && BlockKind.Find((string)e.Data.GetData(DataFormats.Text)) != null ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            var kind = e.Data.GetData(DataFormats.Text) as string;
            if (BlockKind.Find(kind) == null) return;
            var p = PointToClient(new Point(e.X, e.Y));
            AddBlock(kind, new Point(p.X - (int)(BW * zoom / 2), p.Y - 10));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { menu?.Dispose(); tip.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
