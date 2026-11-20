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
        public static readonly Color Navy = Color.FromArgb(0, 0, 128), Maroon = Color.FromArgb(128, 0, 0), Green = Color.FromArgb(0, 128, 0),
            Teal = Color.FromArgb(0, 128, 128), Purple = Color.FromArgb(128, 0, 128), Olive = Color.FromArgb(128, 128, 0),
            Blue = Color.FromArgb(0, 0, 255), Fuchsia = Color.FromArgb(255, 0, 255), Silver = Color.FromArgb(192, 192, 192);

        public readonly List<PlotTrace> Traces = new List<PlotTrace>();
        public readonly List<PlotCursor> Cursors = new List<PlotCursor>();
        public string XLabel = "", YLabel = "", EmptyText = "";
        /// <summary>The pane chosen: a navy frame on the screen (not in pictures).</summary>
        public bool Chosen;

        /// <summary>A trace's name clicked (left or right button).</summary>
        public event Action<PlotTrace, MouseEventArgs> LabelClicked;
        /// <summary>A right-click on the plot, not on a name.</summary>
        public event Action<Point> MenuRequested;
        /// <summary>A cursor dragged to a new X.</summary>
        public event Action<PlotCursor> CursorMoved;
        /// <summary>The user zoomed, panned or reset: linked panes follow the time range.</summary>
        public event EventHandler XViewChanged;

        Rectangle plot;
        double x0, x1, y0, y1;
        Point? mouse, dragFrom, panFrom;
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
        public void FixX(double a, double b) { if (b > a) fixedX = new KeyValuePair<double, double>(a, b); Invalidate(); }

        public void ResetZoom()
        {
            view = null;
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
            Render(e.Graphics, ClientRectangle, true);
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
            foreach (var t in Traces) DrawTrace(g, t);
            foreach (var c in Cursors) DrawCursor(g, c);
            g.Clip = clip;
            g.DrawRectangle(Pens.Black, plot);
            foreach (var l in labels)
                TextRenderer.DrawText(g, l.Value.Label, Font, l.Key, l.Value.Color, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
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
            var widths = items.Select(t => TextRenderer.MeasureText(t.Label, Font, Size.Empty, TextFormatFlags.NoPrefix).Width + 16).ToList();
            int max = Math.Max(60, bounds.Width - 70), i = 0, rows = 0;
            while (i < items.Count)
            {
                int j = i, w = 0;
                while (j < items.Count && (j == i || w + widths[j] <= max)) w += widths[j++];
                int x = bounds.Left + 62 + Math.Max(0, (bounds.Width - 76 - w) / 2);
                for (; i < j; i++)
                {
                    labels.Add(new KeyValuePair<Rectangle, PlotTrace>(new Rectangle(x, y + rows * 15, widths[i] - 8, 15), items[i]));
                    x += widths[i];
                }
                rows++;
            }
            return rows * 15 + 12;     // and room for the cursors' numbers
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
        float MapX(double x) => (float)(plot.Left + (x - x0) / (x1 - x0) * plot.Width);
        float MapY(double y) => (float)(plot.Bottom - (y - y0) / (y1 - y0) * plot.Height);
        double InvX(int px) => x0 + (px - plot.Left) * (x1 - x0) / plot.Width;
        double InvY(int py) => y0 + (plot.Bottom - py) * (y1 - y0) / plot.Height;

        void DrawAxes(Graphics g, Rectangle bounds)
        {
            using (var grid = new Pen(Silver) { DashStyle = DashStyle.Dot })
            {
                foreach (var v in Ticks(x0, x1, Math.Max(2, plot.Width / 80)))
                {
                    float x = MapX(v);
                    g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    g.DrawLine(Pens.Black, x, plot.Bottom, x, plot.Bottom + 4);
                    string s = Fmt(v);
                    var sz = TextRenderer.MeasureText(s, Font);
                    TextRenderer.DrawText(g, s, Font, new Point((int)x - sz.Width / 2, plot.Bottom + 5), Color.Black);
                }
                foreach (var v in Ticks(y0, y1, Math.Max(2, plot.Height / 30)))
                {
                    float y = MapY(v);
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    g.DrawLine(Pens.Black, plot.Left - 4, y, plot.Left, y);
                    string s = Fmt(v);
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

        void DrawTrace(Graphics g, PlotTrace t)
        {
            var pts = new List<PointF>();
            int n = Math.Min(t.X.Length, t.Y.Length);
            using (var p = new Pen(t.Color, 1))
                for (int i = 0; i <= n; i++)
                {
                    if (i < n && Ok(t.X[i]) && Ok(t.Y[i])) { pts.Add(new PointF(MapX(t.X[i]), MapY(t.Y[i]))); continue; }
                    if (i < n && !Ok(t.X[i])) continue;       // a NaN value breaks the line
                    if (pts.Count > 1) g.DrawLines(p, pts.ToArray());
                    pts.Clear();
                }
        }

        void DrawCursor(Graphics g, PlotCursor c)
        {
            if (!Ok(c.X)) return;
            float x = MapX(c.X);
            using (var p = new Pen(c.Color, 1) { DashStyle = DashStyle.Dash }) g.DrawLine(p, x, plot.Top, x, plot.Bottom);
            if (!Ok(c.Y)) return;
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
                if (t != null) { LabelClicked?.Invoke(t, e); return; }
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
            view = (xa, xb, ya, yb);
            x0 = xa; x1 = xb;
            Invalidate();
            XViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
