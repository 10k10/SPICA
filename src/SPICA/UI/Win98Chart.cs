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
    public enum SeriesKind { Bars, Points, Line, VLine, HLine, ErrorBars }

    public sealed class ChartSeries
    {
        public SeriesKind Kind;
        public string Label;
        public Color Color = Win98Chart.Navy;
        public double[] X = new double[0], Y = new double[0];
        public double[] Lo, Hi;              // ErrorBars: lower / upper ends
        public double[] Edges;               // Bars: bin edges (X.Length + 1), Y = heights
        public bool Dashed;
        public int Size = 3;                 // point size / line width
        public bool InLegend = true;
    }

    /// <summary>
    /// Plot control in the style of a Windows 98 era engineering tool: white plot area, black frame,
    /// dotted grid, 16-colour VGA palette. Histograms, scatter, lines, limit lines and error bars.
    /// </summary>
    public class Win98Chart : Control
    {
        public static readonly Color Navy = Color.FromArgb(0, 0, 128);
        public static readonly Color Red = Color.FromArgb(255, 0, 0);
        public static readonly Color Maroon = Color.FromArgb(128, 0, 0);
        public static readonly Color Green = Color.FromArgb(0, 128, 0);
        public static readonly Color Teal = Color.FromArgb(0, 128, 128);
        public static readonly Color Purple = Color.FromArgb(128, 0, 128);
        public static readonly Color Olive = Color.FromArgb(128, 128, 0);
        public static readonly Color Blue = Color.FromArgb(0, 0, 255);
        public static readonly Color Fuchsia = Color.FromArgb(255, 0, 255);
        public static readonly Color Silver = Color.FromArgb(192, 192, 192);

        public readonly List<ChartSeries> Series = new List<ChartSeries>();
        public string Title = "", XLabel = "", YLabel = "";
        public string EmptyText = "No data.";
        public bool YFromZero;

        Rectangle plot;
        double x0, x1, y0, y1;
        Point? mouse;

        public Win98Chart()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            Font = Win98.Font;
        }

        public void Clear() { Series.Clear(); Title = XLabel = YLabel = ""; YFromZero = false; EmptyText = "No data."; Invalidate(); }

        public ChartSeries Add(ChartSeries s) { Series.Add(s); Invalidate(); return s; }

        protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics, ClientRectangle, true);

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); mouse = e.Location; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); mouse = null; Invalidate(); }

        public Bitmap ToBitmap(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);   // opaque surface: crisp GDI text
            using (var g = Graphics.FromImage(bmp)) Render(g, new Rectangle(0, 0, w, h), false);
            return bmp;
        }

        // ------------------------------------------------------------------ rendering

        public void Render(Graphics g, Rectangle bounds, bool interactive)
        {
            g.SmoothingMode = SmoothingMode.None;
            using (var bg = new SolidBrush(Color.White)) g.FillRectangle(bg, bounds);

            bool any = Series.Any(s => s.X.Length > 0 || (s.Edges != null && s.Edges.Length > 0));
            if (!any)
            {
                TextRenderer.DrawText(g, EmptyText, Font, bounds, Win98.Shadow, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            ComputeRange();
            int top = bounds.Top + (Title.Length > 0 ? 22 : 8);
            int left = bounds.Left + 62, right = bounds.Right - 14, bottom = bounds.Bottom - (XLabel.Length > 0 ? 40 : 26);
            plot = Rectangle.FromLTRB(left, top, Math.Max(left + 10, right), Math.Max(top + 10, bottom));

            if (Title.Length > 0)
                TextRenderer.DrawText(g, Title, Win98.BoldFont, new Rectangle(bounds.Left, bounds.Top + 4, bounds.Width, 16), Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);

            DrawAxes(g, bounds);
            var oldClip = g.Clip;
            g.SetClip(Rectangle.Inflate(plot, -1, -1));
            foreach (var s in Series) DrawSeries(g, s);
            g.Clip = oldClip;
            using (var frame = new Pen(Color.Black)) g.DrawRectangle(frame, plot);
            DrawLegend(g);

            if (interactive && mouse.HasValue && plot.Contains(mouse.Value))
            {
                string txt = $"x = {Fmt(InvX(mouse.Value.X))}   y = {Fmt(InvY(mouse.Value.Y))}";
                var sz = TextRenderer.MeasureText(txt, Font);
                var r = new Rectangle(plot.Left + 4, plot.Top + 4, sz.Width + 6, sz.Height + 2);
                using (var b = new SolidBrush(Win98.InfoBack)) g.FillRectangle(b, r);
                using (var p = new Pen(Color.Black)) g.DrawRectangle(p, r);
                TextRenderer.DrawText(g, txt, Font, new Point(r.X + 3, r.Y + 1), Color.Black);
            }
        }

        void ComputeRange()
        {
            double xa = double.PositiveInfinity, xb = double.NegativeInfinity, ya = double.PositiveInfinity, yb = double.NegativeInfinity;
            void X(double v) { if (Ok(v)) { xa = Math.Min(xa, v); xb = Math.Max(xb, v); } }
            void Y(double v) { if (Ok(v)) { ya = Math.Min(ya, v); yb = Math.Max(yb, v); } }
            foreach (var s in Series)
            {
                switch (s.Kind)
                {
                    case SeriesKind.Bars:
                        if (s.Edges != null) foreach (var v in s.Edges) X(v);
                        foreach (var v in s.Y) Y(v);
                        Y(0);
                        break;
                    case SeriesKind.VLine: foreach (var v in s.X) X(v); break;
                    case SeriesKind.HLine: foreach (var v in s.Y) Y(v); break;
                    default:
                        foreach (var v in s.X) X(v);
                        foreach (var v in s.Y) Y(v);
                        if (s.Lo != null) foreach (var v in s.Lo) Y(v);
                        if (s.Hi != null) foreach (var v in s.Hi) Y(v);
                        break;
                }
            }
            if (YFromZero) Y(0);
            if (double.IsInfinity(xa)) { xa = 0; xb = 1; }
            if (double.IsInfinity(ya)) { ya = 0; yb = 1; }
            if (!(xb > xa)) { double d = Math.Abs(xa) * 0.1 + 1e-12; xa -= d; xb += d; }
            if (!(yb > ya)) { double d = Math.Abs(ya) * 0.1 + 1e-12; ya -= d; yb += d; }
            double px = (xb - xa) * 0.04, py = (yb - ya) * 0.06;
            x0 = xa - px; x1 = xb + px;
            y0 = (YFromZero && ya >= 0) ? 0 : ya - py; y1 = yb + py;
        }

        static bool Ok(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        float MapX(double x) => (float)(plot.Left + (x - x0) / (x1 - x0) * plot.Width);
        float MapY(double y) => (float)(plot.Bottom - (y - y0) / (y1 - y0) * plot.Height);
        double InvX(int px) => x0 + (px - plot.Left) * (x1 - x0) / plot.Width;
        double InvY(int py) => y0 + (plot.Bottom - py) * (y1 - y0) / plot.Height;

        void DrawAxes(Graphics g, Rectangle bounds)
        {
            using (var grid = new Pen(Silver) { DashStyle = DashStyle.Dot })
            using (var tick = new Pen(Color.Black))
            {
                foreach (var v in Ticks(x0, x1, Math.Max(2, plot.Width / 80)))
                {
                    float x = MapX(v);
                    g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    g.DrawLine(tick, x, plot.Bottom, x, plot.Bottom + 4);
                    string s = Fmt(v);
                    var sz = TextRenderer.MeasureText(s, Font);
                    TextRenderer.DrawText(g, s, Font, new Point((int)x - sz.Width / 2, plot.Bottom + 5), Color.Black);
                }
                foreach (var v in Ticks(y0, y1, Math.Max(2, plot.Height / 40)))
                {
                    float y = MapY(v);
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    g.DrawLine(tick, plot.Left - 4, y, plot.Left, y);
                    string s = Fmt(v);
                    var sz = TextRenderer.MeasureText(s, Font);
                    TextRenderer.DrawText(g, s, Font, new Point(plot.Left - 6 - sz.Width, (int)y - sz.Height / 2), Color.Black);
                }
            }
            if (XLabel.Length > 0)
                TextRenderer.DrawText(g, XLabel, Font, new Rectangle(plot.Left, plot.Bottom + 20, plot.Width, 16), Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            if (YLabel.Length > 0)
            {
                var st = g.Save();
                g.TranslateTransform(bounds.Left + 4, plot.Top + plot.Height / 2f);
                g.RotateTransform(-90);
                var sz = g.MeasureString(YLabel, Font);
                using (var b = new SolidBrush(Color.Black)) g.DrawString(YLabel, Font, b, -sz.Width / 2, 0);
                g.Restore(st);
            }
        }

        void DrawSeries(Graphics g, ChartSeries s)
        {
            switch (s.Kind)
            {
                case SeriesKind.Bars:
                    using (var fill = new SolidBrush(s.Color))
                    using (var edge = new Pen(Color.Black))
                        for (int i = 0; i < s.Y.Length && s.Edges != null && i + 1 < s.Edges.Length; i++)
                        {
                            float a = MapX(s.Edges[i]), b = MapX(s.Edges[i + 1]), top = MapY(s.Y[i]), bot = MapY(Math.Max(0, y0));
                            if (s.Y[i] <= 0) continue;
                            var r = RectangleF.FromLTRB(a, top, Math.Max(a + 1, b), bot);
                            g.FillRectangle(fill, r);
                            g.DrawRectangle(edge, r.X, r.Y, r.Width, r.Height);
                        }
                    break;
                case SeriesKind.Points:
                    using (var b = new SolidBrush(s.Color))
                    {
                        int n = Math.Min(s.X.Length, s.Y.Length), sz = Math.Max(1, s.Size);
                        for (int i = 0; i < n; i++)
                            if (Ok(s.X[i]) && Ok(s.Y[i])) g.FillRectangle(b, MapX(s.X[i]) - sz / 2, MapY(s.Y[i]) - sz / 2, sz, sz);
                    }
                    break;
                case SeriesKind.Line:
                    {
                        var pts = new List<PointF>();
                        int n = Math.Min(s.X.Length, s.Y.Length);
                        for (int i = 0; i < n; i++) if (Ok(s.X[i]) && Ok(s.Y[i])) pts.Add(new PointF(MapX(s.X[i]), MapY(s.Y[i])));
                        if (pts.Count > 1)
                            using (var p = new Pen(s.Color, Math.Max(1, s.Size == 3 ? 1 : s.Size)) { DashStyle = s.Dashed ? DashStyle.Dash : DashStyle.Solid })
                                g.DrawLines(p, pts.ToArray());
                        break;
                    }
                case SeriesKind.VLine:
                    using (var p = new Pen(s.Color, 1) { DashStyle = s.Dashed ? DashStyle.Dash : DashStyle.Solid })
                        foreach (var v in s.X) if (Ok(v)) { float x = MapX(v); g.DrawLine(p, x, plot.Top, x, plot.Bottom); }
                    break;
                case SeriesKind.HLine:
                    using (var p = new Pen(s.Color, 1) { DashStyle = s.Dashed ? DashStyle.Dash : DashStyle.Solid })
                        foreach (var v in s.Y) if (Ok(v)) { float y = MapY(v); g.DrawLine(p, plot.Left, y, plot.Right, y); }
                    break;
                case SeriesKind.ErrorBars:
                    using (var p = new Pen(s.Color))
                    using (var b = new SolidBrush(s.Color))
                        for (int i = 0; i < s.X.Length; i++)
                        {
                            float x = MapX(s.X[i]);
                            if (s.Lo != null && s.Hi != null && Ok(s.Lo[i]) && Ok(s.Hi[i]))
                            {
                                float a = MapY(s.Lo[i]), c = MapY(s.Hi[i]);
                                g.DrawLine(p, x, a, x, c);
                                g.DrawLine(p, x - 5, a, x + 5, a);
                                g.DrawLine(p, x - 5, c, x + 5, c);
                            }
                            if (i < s.Y.Length && Ok(s.Y[i])) g.FillRectangle(b, x - 3, MapY(s.Y[i]) - 3, 7, 7);
                        }
                    break;
            }
        }

        void DrawLegend(Graphics g)
        {
            var items = Series.Where(s => s.InLegend && !string.IsNullOrEmpty(s.Label)).ToList();
            if (items.Count == 0) return;
            int w = items.Max(s => TextRenderer.MeasureText(s.Label, Font).Width) + 30, h = items.Count * 15 + 6;
            var r = new Rectangle(plot.Right - w - 6, plot.Top + 6, w, h);
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, r);
            using (var p = new Pen(Color.Black)) g.DrawRectangle(p, r);
            int y = r.Top + 4;
            foreach (var s in items)
            {
                var sw = new Rectangle(r.Left + 5, y + 2, 16, 9);
                using (var b = new SolidBrush(s.Color))
                using (var p = new Pen(s.Color) { DashStyle = s.Dashed ? DashStyle.Dash : DashStyle.Solid })
                {
                    if (s.Kind == SeriesKind.Bars) { g.FillRectangle(b, sw); g.DrawRectangle(Pens.Black, sw); }
                    else if (s.Kind == SeriesKind.Points || s.Kind == SeriesKind.ErrorBars) g.FillRectangle(b, sw.X + 6, sw.Y + 2, 5, 5);
                    else g.DrawLine(p, sw.Left, sw.Top + 4, sw.Right, sw.Top + 4);
                }
                TextRenderer.DrawText(g, s.Label, Font, new Point(r.Left + 25, y), Color.Black);
                y += 15;
            }
        }

        static IEnumerable<double> Ticks(double a, double b, int maxTicks)
        {
            double span = b - a;
            if (!(span > 0)) yield break;
            double raw = span / Math.Max(1, maxTicks);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * mag).First(st => st >= raw);
            double first = Math.Ceiling(a / step) * step;
            for (double v = first; v <= b + step * 1e-9; v += step) yield return Math.Abs(v) < step * 1e-9 ? 0 : v;
        }

        public static string Fmt(double v)
        {
            if (double.IsNaN(v)) return "-";
            double a = Math.Abs(v);
            if (a == 0) return "0";
            if (a >= 1e5 || a < 1e-3)
            {
                // engineering notation with SI prefix
                string[] pre = { "f", "p", "n", "u", "m", "", "k", "M", "G", "T" };
                int e3 = (int)Math.Floor(Math.Log10(a) / 3);
                e3 = Math.Max(-5, Math.Min(4, e3));
                return (v / Math.Pow(1000, e3)).ToString("0.###", CultureInfo.InvariantCulture) + pre[e3 + 5];
            }
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }
}
