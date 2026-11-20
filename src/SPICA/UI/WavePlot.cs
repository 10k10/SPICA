using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Spica.UI
{
    /// <summary>One curve of a pane: its points, its colour, and its name in the pane's label row (null: drawn, not named).</summary>
    public sealed class PlotTrace
    {
        public string Label;
        public Color Color = WavePlot.Navy;
        public double[] X = new double[0], Y = new double[0];
        public object Tag;
    }

    /// <summary>A cursor: a vertical line at X, with a cross where it meets the trace it is on (Y; NaN: the line only).</summary>
    public sealed class PlotCursor
    {
        public string Name = "1";
        public double X = double.NaN, Y = double.NaN;
        public Color Color = Color.Black;
    }

    /// <summary>
    /// A waveform pane in the manner of LTspice: the traces' names in their colours along the top (no title), white
    /// plot, dotted grid. Drag a box to zoom (flat: time only, narrow: value only), wheel to zoom, middle-drag to pan,
    /// double-click to reset. A click on a trace name, a right-click and a cursor dragged are told to the owner.
    /// </summary>
    public sealed class WavePlot : Control
    {
        public static readonly Color Navy = Color.FromArgb(0, 0, 128), Teal = Color.FromArgb(0, 128, 128), Purple = Color.FromArgb(128, 0, 128),
            Grid = Color.FromArgb(222, 222, 222);

        /// <summary>The colours of the traces, in order: strong and far apart on white.</summary>
        public static readonly Color[] Palette =
        {
            Color.FromArgb(0, 0, 220), Color.FromArgb(210, 0, 0), Color.FromArgb(0, 145, 0), Color.FromArgb(190, 0, 190),
            Color.FromArgb(230, 115, 0), Color.FromArgb(0, 140, 150), Color.FromArgb(120, 70, 20), Color.FromArgb(100, 0, 210),
        };

        public readonly List<PlotTrace> Traces = new List<PlotTrace>();
        public readonly List<PlotCursor> Cursors = new List<PlotCursor>();
        public string XLabel = "", YLabel = "", EmptyText = "";
        /// <summary>The pane chosen: a navy frame on the screen (not in pictures).</summary>
        public bool Chosen;

        /// <summary>A trace's name clicked (left or right button).</summary>
        public event Action<PlotTrace, MouseEventArgs> LabelClicked;
        /// <summary>A trace's name dragged out (the owner starts the drag and drop).</summary>
        public event Action<PlotTrace> LabelDragged;
        /// <summary>A right-click on the plot, not on a name.</summary>
        public event Action<Point> MenuRequested;
        /// <summary>A cursor dragged to a new X.</summary>
        public event Action<PlotCursor> CursorMoved;
        /// <summary>The user zoomed, panned or reset: linked panes follow the time range.</summary>
        public event EventHandler XViewChanged;

        Rectangle plot;
        double x0, x1, y0, y1;
        Point? mouse, dragFrom, panFrom, labelDown;
        PlotTrace labelPressed;
        /// <summary>The whole simulation: the time can be zoomed into it, never out of it.</summary>
        KeyValuePair<double, double>? limits;
        Rectangle dragBox;
        PlotCursor dragging;
        (double X0, double X1, double Y0, double Y1)? view, panStart;
        KeyValuePair<double, double>? fixedX;
        readonly List<KeyValuePair<Rectangle, PlotTrace>> labels = new List<KeyValuePair<Rectangle, PlotTrace>>();

        public WavePlot()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            Font = Win98.Font;
        }

        public bool IsZoomed => view.HasValue;
        public double ViewX0 => x0;
        public double ViewX1 => x1;

        /// <summary>The time range of another pane (null: back to the data), keeping this pane's own value zoom.</summary>
        public void LinkX(double? a, double? b)
        {
            if (a.HasValue && b.HasValue && b.Value > a.Value)
            {
                fixedX = new KeyValuePair<double, double>(a.Value, b.Value);
                if (view.HasValue) view = (a.Value, b.Value, view.Value.Y0, view.Value.Y1);
            }
            else { fixedX = null; view = null; }
            Invalidate();
        }

        /// <summary>The time axis from 0 to the end of the simulation, whatever the data drawn.</summary>
        public void FixX(double a, double b)
        {
            if (!(b > a)) return;
            fixedX = limits = new KeyValuePair<double, double>(a, b);
            Invalidate();
        }

        public void ResetZoom()
        {
            view = null;
            if (limits.HasValue) fixedX = limits;
            Invalidate();
            XViewChanged?.Invoke(this, EventArgs.Empty);
        }

        public Bitmap ToBitmap(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp)) Render(g, new Rectangle(0, 0, w, h), false);
            return bmp;
        }

        // ------------------------------------------------------------------ drawing

        protected override void OnPaint(PaintEventArgs e)
        {
            // a pane that cannot be drawn says so and goes back to the whole simulation: it never turns into a red cross
            try { Render(e.Graphics, ClientRectangle, true); }
            catch (Exception ex)
            {
                e.Graphics.FillRectangle(Brushes.White, ClientRectangle);
                TextRenderer.DrawText(e.Graphics, "This pane could not be drawn (" + ex.Message + "): back to the whole simulation.", Font, ClientRectangle,
                    Win98.Shadow, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                if (view.HasValue) BeginInvoke(new Action(ResetZoom));
            }
            if (Chosen) using (var p = new Pen(Navy, 3)) e.Graphics.DrawRectangle(p, 1, 1, Width - 3, Height - 3);
        }

        void Render(Graphics g, Rectangle bounds, bool screen)
        {
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangle(Brushes.White, bounds);
            if (!Traces.Any(t => t.X.Length > 0))
            {
                labels.Clear();
                TextRenderer.DrawText(g, EmptyText, Font, bounds, Win98.Shadow, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            ComputeRange();
            int top = bounds.Top + 6 + LayoutLabels(bounds, bounds.Top + 4);
            int bottom = bounds.Bottom - (XLabel.Length > 0 ? 38 : 24);
            plot = Rectangle.FromLTRB(bounds.Left + 62, top, Math.Max(bounds.Left + 72, bounds.Right - 14), Math.Max(top + 10, bottom));

            DrawAxes(g, bounds);
            var clip = g.Clip;
            g.SetClip(Rectangle.Inflate(plot, -1, -1));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var t in Traces) DrawTrace(g, t);
            g.SmoothingMode = SmoothingMode.None;
            foreach (var c in Cursors) DrawCursor(g, c);
            g.Clip = clip;
            g.DrawRectangle(Pens.Black, plot);
            foreach (var l in labels)
                TextRenderer.DrawText(g, l.Value.Label, Win98.BoldFont, l.Key, l.Value.Color, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            foreach (var c in Cursors.Where(c => Ok(c.X) && c.X >= x0 && c.X <= x1))
            {
                // the cursor's number above the plot, on its line
                var r = new Rectangle((int)MapX(c.X) - 6, plot.Top - 13, 12, 12);
                using (var b = new SolidBrush(c.Color)) g.FillRectangle(b, r);
                TextRenderer.DrawText(g, c.Name, Font, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            if (!screen) return;

            if (dragFrom.HasValue && dragBox.Width > 0)
                using (var p = new Pen(Color.Black) { DashStyle = DashStyle.Dot }) g.DrawRectangle(p, dragBox.X, dragBox.Y, dragBox.Width, Math.Max(1, dragBox.Height));
            if (view.HasValue)
            {
                const string t = "zoomed - double-click to reset";
                var sz = TextRenderer.MeasureText(t, Font);
                TextRenderer.DrawText(g, t, Font, new Point(plot.Right - sz.Width - 4, plot.Bottom - sz.Height - 3), Win98.Shadow);
            }
            if (mouse.HasValue && plot.Contains(mouse.Value) && dragging == null)
            {
                string txt = $"t = {Fmt(InvX(mouse.Value.X))}   y = {Fmt(InvY(mouse.Value.Y))}";
                var sz = TextRenderer.MeasureText(txt, Font);
                var r = new Rectangle(plot.Left + 4, plot.Top + 4, sz.Width + 6, sz.Height + 2);
                using (var b = new SolidBrush(Win98.InfoBack)) g.FillRectangle(b, r);
                g.DrawRectangle(Pens.Black, r);
                TextRenderer.DrawText(g, txt, Font, new Point(r.X + 3, r.Y + 1), Color.Black);
            }
        }

        /// <summary>The names of the traces, in rows across the top; returns the height they take.</summary>
        int LayoutLabels(Rectangle bounds, int y)
        {
            labels.Clear();
            var items = Traces.Where(t => !string.IsNullOrEmpty(t.Label)).ToList();
            if (items.Count == 0) return 0;
            var widths = items.Select(t => TextRenderer.MeasureText(t.Label, Win98.BoldFont, Size.Empty, TextFormatFlags.NoPrefix).Width + 18).ToList();
            int max = Math.Max(60, bounds.Width - 70), i = 0, rows = 0;
            while (i < items.Count)
            {
                int j = i, w = 0;
                while (j < items.Count && (j == i || w + widths[j] <= max)) w += widths[j++];
                int x = bounds.Left + 62 + Math.Max(0, (bounds.Width - 76 - w) / 2);
                for (; i < j; i++)
                {
                    labels.Add(new KeyValuePair<Rectangle, PlotTrace>(new Rectangle(x, y + rows * 16, widths[i] - 8, 16), items[i]));
                    x += widths[i];
                }
                rows++;
            }
            return rows * 16 + 12;     // and room for the cursors' numbers
        }

        void ComputeRange()
        {
            double xa = double.PositiveInfinity, xb = double.NegativeInfinity, ya = double.PositiveInfinity, yb = double.NegativeInfinity;
            foreach (var t in Traces)
                for (int i = 0; i < Math.Min(t.X.Length, t.Y.Length); i++)
                {
                    if (Ok(t.X[i])) { xa = Math.Min(xa, t.X[i]); xb = Math.Max(xb, t.X[i]); }
                    if (Ok(t.Y[i])) { ya = Math.Min(ya, t.Y[i]); yb = Math.Max(yb, t.Y[i]); }
                }
            if (double.IsInfinity(xa)) { xa = 0; xb = 1; }
            if (double.IsInfinity(ya)) { ya = 0; yb = 1; }
            if (!(xb > xa)) { double d = Math.Abs(xa) * 0.1 + 1e-12; xa -= d; xb += d; }
            if (!(yb > ya)) { double d = Math.Abs(ya) * 0.1 + 1e-12; ya -= d; yb += d; }
            double py = (yb - ya) * 0.06;
            x0 = xa; x1 = xb; y0 = ya - py; y1 = yb + py;
            if (fixedX.HasValue) { x0 = fixedX.Value.Key; x1 = fixedX.Value.Value; }
            if (view.HasValue) (x0, x1, y0, y1) = view.Value;
        }

        static bool Ok(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        /// <summary>The range of the values drawn (null: none).</summary>
        (double Lo, double Hi)? DataY()
        {
            double a = double.PositiveInfinity, b = double.NegativeInfinity;
            foreach (var t in Traces) foreach (var v in t.Y) if (Ok(v)) { a = Math.Min(a, v); b = Math.Max(b, v); }
            return b > a ? (a, b) : ((double, double)?)null;
        }
        float MapX(double x) => (float)(plot.Left + (x - x0) / (x1 - x0) * plot.Width);
        float MapY(double y) => (float)(plot.Bottom - (y - y0) / (y1 - y0) * plot.Height);
        double InvX(int px) => x0 + (px - plot.Left) * (x1 - x0) / plot.Width;
        double InvY(int py) => y0 + (plot.Bottom - py) * (y1 - y0) / plot.Height;

        void DrawAxes(Graphics g, Rectangle bounds)
        {
            using (var grid = new Pen(Grid))
            {
                var xt = Ticks(x0, x1, Math.Max(2, plot.Width / 80)).ToList();
                foreach (var v in xt)
                {
                    float x = MapX(v);
                    g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    g.DrawLine(Pens.Black, x, plot.Bottom, x, plot.Bottom + 4);
                    string s = TickText(v, xt);
                    var sz = TextRenderer.MeasureText(s, Font);
                    TextRenderer.DrawText(g, s, Font, new Point((int)x - sz.Width / 2, plot.Bottom + 5), Color.Black);
                }
                var yt = Ticks(y0, y1, Math.Max(2, plot.Height / 30)).ToList();
                foreach (var v in yt)
                {
                    float y = MapY(v);
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    g.DrawLine(Pens.Black, plot.Left - 4, y, plot.Left, y);
                    string s = TickText(v, yt);
                    var sz = TextRenderer.MeasureText(s, Font);
                    TextRenderer.DrawText(g, s, Font, new Point(plot.Left - 6 - sz.Width, (int)y - sz.Height / 2), Color.Black);
                }
            }
            if (XLabel.Length > 0)
                TextRenderer.DrawText(g, XLabel, Font, new Rectangle(plot.Left, plot.Bottom + 20, plot.Width, 16), Color.Black, TextFormatFlags.HorizontalCenter);
            if (YLabel.Length > 0)
            {
                var st = g.Save();
                g.TranslateTransform(bounds.Left + 4, plot.Top + plot.Height / 2f);
                g.RotateTransform(-90);
                var sz = g.MeasureString(YLabel, Font);
                g.DrawString(YLabel, Font, Brushes.Black, -sz.Width / 2, 0);
                g.Restore(st);
            }
        }

        /// <summary>
        /// A trace as lines, each segment clipped to the plot (a little outside it) in double precision before it
        /// becomes pixels: zoomed far in, the points off the plot are millions of pixels away, more than GDI+ can draw.
        /// Only the segments in the time shown are looked at; a NaN value breaks the line.
        /// </summary>
        void DrawTrace(Graphics g, PlotTrace t)
        {
            int n = Math.Min(t.X.Length, t.Y.Length);
            if (n < 2) return;
            double sx = plot.Width / (x1 - x0), sy = plot.Height / (y1 - y0);
            double PX(double x) => plot.Left + (x - x0) * sx;
            double PY(double y) => plot.Bottom - (y - y0) * sy;
            double l = plot.Left - 4, r = plot.Right + 4, top = plot.Top - 4, bot = plot.Bottom + 4;
            // the first segment that can reach the time shown (the time only grows)
            int lo = Array.BinarySearch(t.X, 0, n, x0);
            int start = Math.Max(0, (lo < 0 ? ~lo : lo) - 1);
            var pts = new List<PointF>();
            using (var p = new Pen(t.Color, 1.6f) { LineJoin = LineJoin.Round })
            {
                void Flush() { if (pts.Count > 1) g.DrawLines(p, pts.ToArray()); pts.Clear(); }
                for (int i = start; i + 1 < n; i++)
                {
                    if (t.X[i] > x1) break;
                    if (!Ok(t.X[i]) || !Ok(t.Y[i]) || !Ok(t.X[i + 1]) || !Ok(t.Y[i + 1])) { Flush(); continue; }
                    double ax = PX(t.X[i]), ay = PY(t.Y[i]), bx = PX(t.X[i + 1]), by = PY(t.Y[i + 1]);
                    if (!Clip(ref ax, ref ay, ref bx, ref by, l, top, r, bot)) { Flush(); continue; }
                    var a = new PointF((float)ax, (float)ay);
                    if (pts.Count == 0 || pts[pts.Count - 1] != a) { Flush(); pts.Add(a); }
                    pts.Add(new PointF((float)bx, (float)by));
                }
                Flush();
            }
        }

        /// <summary>Liang-Barsky: the part of a segment inside a rectangle; false when none of it is.</summary>
        static bool Clip(ref double ax, ref double ay, ref double bx, ref double by, double l, double top, double r, double bot)
        {
            double dx = bx - ax, dy = by - ay, t0 = 0, t1 = 1;
            bool Edge(double pp, double q)
            {
                if (pp == 0) return q >= 0;
                double u = q / pp;
                if (pp < 0) { if (u > t1) return false; if (u > t0) t0 = u; }
                else { if (u < t0) return false; if (u < t1) t1 = u; }
                return true;
            }
            if (!Edge(-dx, ax - l) || !Edge(dx, r - ax) || !Edge(-dy, ay - top) || !Edge(dy, bot - ay)) return false;
            double nax = ax + t0 * dx, nay = ay + t0 * dy;
            bx = ax + t1 * dx; by = ay + t1 * dy;
            ax = nax; ay = nay;
            return true;
        }

        void DrawCursor(Graphics g, PlotCursor c)
        {
            if (!Ok(c.X) || c.X < x0 || c.X > x1) return;
            float x = MapX(c.X);
            using (var p = new Pen(c.Color, 1) { DashStyle = DashStyle.Dash }) g.DrawLine(p, x, plot.Top, x, plot.Bottom);
            if (!Ok(c.Y) || c.Y < y0 || c.Y > y1) return;
            float y = MapY(c.Y);
            using (var p = new Pen(c.Color, 1) { DashStyle = DashStyle.Dash }) g.DrawLine(p, plot.Left, y, plot.Right, y);
            using (var p = new Pen(c.Color, 2)) g.DrawRectangle(p, x - 4, y - 4, 8, 8);
        }

        static IEnumerable<double> Ticks(double a, double b, int maxTicks)
        {
            double span = b - a;
            if (!(span > 0)) yield break;
            double raw = span / Math.Max(1, maxTicks);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * mag).First(st => st >= raw);
            for (double v = Math.Ceiling(a / step) * step; v <= b + step * 1e-9; v += step) yield return Math.Abs(v) < step * 1e-9 ? 0 : v;
        }

        /// <summary>
        /// A tick's number with as many decimals as the step between ticks needs, with the SI prefix of the largest
        /// tick: zoomed far in, 140.70012m, 140.70014m... instead of the same 0.1407 five times.
        /// </summary>
        static string TickText(double v, List<double> ticks) =>
            ticks.Count < 2 ? Fmt(v) : Fmt(v, ticks[1] - ticks[0], ticks.Max(Math.Abs));

        /// <summary>A value told apart from values <paramref name="step"/> away (the SI prefix of <paramref name="big"/>).</summary>
        public static string Fmt(double v, double step, double big)
        {
            if (!(step > 0) || big == 0 || double.IsNaN(v)) return Fmt(v);
            string[] pre = { "f", "p", "n", "µ", "m", "", "k", "M", "G", "T" };
            int e3 = Math.Max(-5, Math.Min(4, (int)Math.Floor(Math.Log10(big) / 3)));
            double unit = Math.Pow(1000, e3);
            int dec = Math.Max(0, Math.Min(12, (int)Math.Ceiling(-Math.Log10(step / unit) - 1e-9)));
            return (v / unit).ToString("F" + dec, CultureInfo.InvariantCulture) + pre[e3 + 5];
        }

        /// <summary>A value with its SI prefix (1.5m, 12k, 0.25).</summary>
        public static string Fmt(double v)
        {
            if (double.IsNaN(v)) return "-";
            double a = Math.Abs(v);
            if (a == 0) return "0";
            if (a >= 1e4 || a < 1e-2)
            {
                string[] pre = { "f", "p", "n", "µ", "m", "", "k", "M", "G", "T" };
                int e3 = Math.Max(-5, Math.Min(4, (int)Math.Floor(Math.Log10(a) / 3)));
                return (v / Math.Pow(1000, e3)).ToString("0.###", CultureInfo.InvariantCulture) + pre[e3 + 5];
            }
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ mouse

        PlotTrace LabelAt(Point p) => labels.FirstOrDefault(l => l.Key.Contains(p)).Value;
        PlotCursor CursorAt(Point p) => plot.Contains(p) ? Cursors.FirstOrDefault(c => Ok(c.X) && Math.Abs(MapX(c.X) - p.X) <= 4) : null;

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            // the wheel goes to the focused control: take it unless the user is typing somewhere
            var f = FindForm();
            if (f != null && !(f.ActiveControl is TextBoxBase)) Focus();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                var t = LabelAt(e.Location);
                if (t != null) { labelPressed = t; labelDown = e.Location; return; }
                dragging = CursorAt(e.Location);
                if (dragging == null && plot.Contains(e.Location)) { dragFrom = e.Location; dragBox = Rectangle.Empty; }
            }
            else if (e.Button == MouseButtons.Middle && plot.Contains(e.Location))
            {
                panFrom = e.Location;
                panStart = (x0, x1, y0, y1);
                Cursor = System.Windows.Forms.Cursors.SizeAll;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            if (labelPressed != null && labelDown.HasValue && (Math.Abs(e.X - labelDown.Value.X) > 4 || Math.Abs(e.Y - labelDown.Value.Y) > 4))
            {
                var t = labelPressed;
                labelPressed = null; labelDown = null;
                LabelDragged?.Invoke(t);
                return;
            }
            if (dragging != null)
            {
                dragging.X = Math.Max(x0, Math.Min(x1, InvX(Math.Max(plot.Left, Math.Min(plot.Right, e.X)))));
                CursorMoved?.Invoke(dragging);
            }
            else if (dragFrom.HasValue)
            {
                var a = dragFrom.Value;
                int bx = Math.Max(plot.Left, Math.Min(plot.Right, e.X)), by = Math.Max(plot.Top, Math.Min(plot.Bottom, e.Y));
                dragBox = Rectangle.FromLTRB(Math.Min(a.X, bx), Math.Min(a.Y, by), Math.Max(a.X, bx), Math.Max(a.Y, by));
            }
            else if (panFrom.HasValue && panStart.HasValue && plot.Width > 0 && plot.Height > 0)
            {
                var s = panStart.Value;
                double dx = (e.X - panFrom.Value.X) * (s.X1 - s.X0) / plot.Width, dy = (e.Y - panFrom.Value.Y) * (s.Y1 - s.Y0) / plot.Height;
                SetView(s.X0 - dx, s.X1 - dx, s.Y0 + dy, s.Y1 + dy);
            }
            Cursor = LabelAt(e.Location) != null ? System.Windows.Forms.Cursors.Hand : dragging != null || CursorAt(e.Location) != null ? System.Windows.Forms.Cursors.SizeWE
                   : panFrom.HasValue ? System.Windows.Forms.Cursors.SizeAll : plot.Contains(e.Location) ? System.Windows.Forms.Cursors.Cross : System.Windows.Forms.Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Middle) { panFrom = null; Cursor = System.Windows.Forms.Cursors.Default; return; }
            if (e.Button == MouseButtons.Right)
            {
                var t = LabelAt(e.Location);
                if (t != null) LabelClicked?.Invoke(t, e);
                else MenuRequested?.Invoke(e.Location);
                return;
            }
            if (labelPressed != null)
            {
                var t = labelPressed;
                labelPressed = null; labelDown = null;
                LabelClicked?.Invoke(t, e);
                return;
            }
            if (dragging != null) { dragging = null; return; }
            if (!dragFrom.HasValue) return;
            var b = dragBox;
            dragFrom = null; dragBox = Rectangle.Empty;
            bool zx = b.Width >= 6, zy = b.Height >= 6;
            if (zx && b.Height < 12) zy = false;            // a flat box zooms the time only, a narrow one the value only
            if (zy && b.Width < 12) zx = false;
            if (zx || zy) SetView(zx ? InvX(b.Left) : x0, zx ? InvX(b.Right) : x1, zy ? InvY(b.Bottom) : y0, zy ? InvY(b.Top) : y1);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!plot.Contains(e.Location) || Traces.Count == 0) return;
            double f = Math.Pow(0.8, e.Delta / 120.0), cx = InvX(e.X), cy = InvY(e.Y);
            bool onlyX = (ModifierKeys & Keys.Shift) != 0, onlyY = (ModifierKeys & Keys.Control) != 0;
            SetView(onlyY ? x0 : cx - (cx - x0) * f, onlyY ? x1 : cx + (x1 - cx) * f, onlyX ? y0 : cy - (cy - y0) * f, onlyX ? y1 : cy + (y1 - cy) * f);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && LabelAt(e.Location) == null) ResetZoom();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            mouse = null;
            Invalidate();
        }

        void SetView(double xa, double xb, double ya, double yb)
        {
            if (!(xb > xa) || !(yb > ya) || double.IsInfinity(xb - xa) || double.IsInfinity(yb - ya)) return;
            double minX = limits.HasValue ? (limits.Value.Value - limits.Value.Key) * 1e-9 : 1e-15;
            if (xb - xa < minX) { double m = (xa + xb) / 2; xa = m - minX / 2; xb = m + minX / 2; }
            double minY = Math.Max(1e-15, Math.Max(Math.Abs(ya), Math.Abs(yb)) * 1e-9);
            if (yb - ya < minY) { double m = (ya + yb) / 2; ya = m - minY / 2; yb = m + minY / 2; }
            if (limits.HasValue)
            {
                double lo = limits.Value.Key, hi = limits.Value.Value, span = Math.Min(xb - xa, hi - lo);
                if (xa < lo) xa = lo;
                if (xa + span > hi) xa = hi - span;
                xb = xa + span;
            }
            var data = DataY();
            if (data.HasValue && yb - ya > 4 * (data.Value.Hi - data.Value.Lo))
            {
                double mid = (ya + yb) / 2, half = 2 * (data.Value.Hi - data.Value.Lo);
                ya = mid - half; yb = mid + half;
            }
            view = (xa, xb, ya, yb);
            x0 = xa; x1 = xb;
            Invalidate();
            XViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
