using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace Spica.UI
{
    /// <summary>
    /// Vector (SVG) rendering of a chart for the HTML report: same ranges, ticks and series as the
    /// on-screen chart, drawn in a quieter document style (light grid, grey axes, sans-serif text).
    /// </summary>
    public partial class Win98Chart
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const string Ink = "#1d1d1f", Muted = "#5f5f5f", GridColor = "#e3e3e3", AxisColor = "#8c8c8c";

        static string N(double v) => v.ToString("0.#", Inv);
        static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        static string Esc(string s) => WebUtility.HtmlEncode(s ?? "");

        /// <summary>Rough text width at 11 px (layout only: legend box, label clearance).</summary>
        static double TextWidth(string s, double px = 11) => (s ?? "").Length * px * 0.56;

        public string ToSvg(int w, int h)
        {
            var sb = new StringBuilder();
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w} {h}\" width=\"{w}\" height=\"{h}\" ")
              .Append("font-family=\"Segoe UI, Tahoma, Arial, sans-serif\" font-size=\"11\" role=\"img\">");
            if (Title.Length > 0) sb.Append($"<title>{Esc(Title)}</title>");
            sb.Append($"<rect width=\"{w}\" height=\"{h}\" fill=\"#fff\"/>");

            bool any = Series.Any(s => s.X.Length > 0 || (s.Edges != null && s.Edges.Length > 0));
            if (!any)
            {
                sb.Append($"<text x=\"{w / 2}\" y=\"{h / 2}\" text-anchor=\"middle\" fill=\"{Muted}\">{Esc(EmptyText)}</text></svg>");
                return sb.ToString();
            }

            ComputeRange();
            int top = Title.Length > 0 ? 30 : 12;
            int left = YCategories != null ? (int)Math.Min(w / 3.0, YCategories.Select(c => TextWidth(c)).DefaultIfEmpty(0).Max() + 16) : 66;
            int right = w - 16, bottom = h - (XLabel.Length > 0 ? 44 : 28);
            plot = Rectangle.FromLTRB(left, top, Math.Max(left + 10, right), Math.Max(top + 10, bottom));

            if (Title.Length > 0)
                sb.Append($"<text x=\"{left}\" y=\"18\" font-size=\"12.5\" font-weight=\"600\" fill=\"{Ink}\">{Esc(Title)}</text>");

            // grid, ticks and tick labels
            sb.Append($"<g stroke=\"{GridColor}\" stroke-width=\"1\">");
            var xt = Ticks(x0, x1, Math.Max(2, plot.Width / 90)).ToList();
            var yt = YCategories != null ? new List<double>() : Ticks(y0, y1, Math.Max(2, plot.Height / 45)).ToList();
            foreach (var v in xt) { var x = N(MapX(v)); sb.Append($"<line x1=\"{x}\" y1=\"{plot.Top}\" x2=\"{x}\" y2=\"{plot.Bottom}\"/>"); }
            foreach (var v in yt) { var y = N(MapY(v)); sb.Append($"<line x1=\"{plot.Left}\" y1=\"{y}\" x2=\"{plot.Right}\" y2=\"{y}\"/>"); }
            sb.Append("</g>");
            sb.Append($"<g fill=\"{Muted}\">");
            foreach (var v in xt) sb.Append($"<text x=\"{N(MapX(v))}\" y=\"{plot.Bottom + 16}\" text-anchor=\"middle\">{Esc(Fmt(v))}</text>");
            foreach (var v in yt) sb.Append($"<text x=\"{plot.Left - 7}\" y=\"{N(MapY(v) + 4)}\" text-anchor=\"end\">{Esc(Fmt(v))}</text>");
            if (YCategories != null)
                for (int i = 0; i < YCategories.Length; i++)
                    sb.Append($"<text x=\"{plot.Left - 7}\" y=\"{N(MapY(i) + 4)}\" text-anchor=\"end\" fill=\"{Ink}\">{Esc(YCategories[i])}</text>");
            sb.Append("</g>");
            if (XLabel.Length > 0)
                sb.Append($"<text x=\"{N(plot.Left + plot.Width / 2.0)}\" y=\"{plot.Bottom + 34}\" text-anchor=\"middle\" fill=\"{Ink}\">{Esc(XLabel)}</text>");
            if (YLabel.Length > 0)
                sb.Append($"<text transform=\"translate(14 {N(plot.Top + plot.Height / 2.0)}) rotate(-90)\" text-anchor=\"middle\" fill=\"{Ink}\">{Esc(YLabel)}</text>");

            // series, clipped to the plot
            string clip = "c" + Guid.NewGuid().ToString("N").Substring(0, 8);
            sb.Append($"<clipPath id=\"{clip}\"><rect x=\"{plot.Left}\" y=\"{plot.Top}\" width=\"{plot.Width}\" height=\"{plot.Height}\"/></clipPath>");
            sb.Append($"<g clip-path=\"url(#{clip})\">");
            for (int i = 0; i < Series.Count; i++) if (!IsHidden(i)) SvgSeries(sb, Series[i]);
            sb.Append("</g>");
            sb.Append($"<path d=\"M{plot.Left} {plot.Top}V{plot.Bottom}H{plot.Right}\" fill=\"none\" stroke=\"{AxisColor}\"/>");
            SvgLegend(sb);
            sb.Append("</svg>");
            return sb.ToString();
        }

        static string Dash(ChartSeries s) => s.Dashed ? " stroke-dasharray=\"6 4\"" : "";

        void SvgSeries(StringBuilder sb, ChartSeries s)
        {
            string col = Hex(s.Color);
            switch (s.Kind)
            {
                case SeriesKind.Bars:
                    sb.Append($"<g fill=\"{col}\" fill-opacity=\"0.85\" stroke=\"#fff\" stroke-width=\"1\">");
                    for (int i = 0; i < s.Y.Length && s.Edges != null && i + 1 < s.Edges.Length; i++)
                    {
                        if (!(s.Y[i] > 0)) continue;
                        double a = MapX(s.Edges[i]), b = MapX(s.Edges[i + 1]), t = MapY(s.Y[i]), bot = MapY(Math.Max(0, y0));
                        sb.Append($"<rect x=\"{N(a)}\" y=\"{N(t)}\" width=\"{N(Math.Max(1, b - a))}\" height=\"{N(Math.Max(0, bot - t))}\"/>");
                    }
                    sb.Append("</g>");
                    break;
                case SeriesKind.HBars:
                    for (int i = 0; i < s.X.Length; i++)
                    {
                        if (!Ok(s.X[i])) continue;
                        double a = MapX(Math.Min(0, s.X[i])), b = MapX(Math.Max(0, s.X[i])), yt = MapY(i - 0.32), yb = MapY(i + 0.32);
                        sb.Append($"<rect x=\"{N(a)}\" y=\"{N(Math.Min(yt, yb))}\" width=\"{N(Math.Max(1, b - a))}\" height=\"{N(Math.Abs(yb - yt))}\" fill=\"{Hex(s.X[i] < 0 ? s.NegColor : s.Color)}\" fill-opacity=\"0.85\"/>");
                    }
                    break;
                case SeriesKind.Points:
                    {
                        // one path of small squares keeps thousands of points compact
                        int n = Math.Min(s.X.Length, s.Y.Length);
                        double sz = Math.Max(1.5, s.Size), hs = sz / 2;
                        var d = new StringBuilder();
                        for (int i = 0; i < n; i++)
                            if (Ok(s.X[i]) && Ok(s.Y[i]))
                                d.Append($"M{N(MapX(s.X[i]) - hs)} {N(MapY(s.Y[i]) - hs)}h{N(sz)}v{N(sz)}h-{N(sz)}z");
                        if (d.Length > 0) sb.Append($"<path d=\"{d}\" fill=\"{col}\" fill-opacity=\"0.7\"/>");
                        break;
                    }
                case SeriesKind.Line:
                    {
                        int n = Math.Min(s.X.Length, s.Y.Length);
                        var pts = new StringBuilder();
                        for (int i = 0; i < n; i++)
                            if (Ok(s.X[i]) && Ok(s.Y[i])) pts.Append(N(MapX(s.X[i]))).Append(',').Append(N(MapY(s.Y[i]))).Append(' ');
                        double lw = s.Size == 3 ? 1.5 : Math.Max(1, s.Size);
                        if (pts.Length > 0)
                            sb.Append($"<polyline points=\"{pts.ToString().TrimEnd()}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"{N(lw)}\" stroke-linejoin=\"round\"{Dash(s)}/>");
                        break;
                    }
                case SeriesKind.VLine:
                    foreach (var v in s.X)
                        if (Ok(v)) { var x = N(MapX(v)); sb.Append($"<line x1=\"{x}\" y1=\"{plot.Top}\" x2=\"{x}\" y2=\"{plot.Bottom}\" stroke=\"{col}\" stroke-width=\"1.5\"{Dash(s)}/>"); }
                    break;
                case SeriesKind.HLine:
                    foreach (var v in s.Y)
                        if (Ok(v)) { var y = N(MapY(v)); sb.Append($"<line x1=\"{plot.Left}\" y1=\"{y}\" x2=\"{plot.Right}\" y2=\"{y}\" stroke=\"{col}\" stroke-width=\"1.5\"{Dash(s)}/>"); }
                    break;
                case SeriesKind.ErrorBars:
                    {
                        var d = new StringBuilder();
                        for (int i = 0; i < s.X.Length; i++)
                        {
                            double x = MapX(s.X[i]);
                            if (s.Lo != null && s.Hi != null && Ok(s.Lo[i]) && Ok(s.Hi[i]))
                            {
                                double a = MapY(s.Lo[i]), c = MapY(s.Hi[i]);
                                d.Append($"M{N(x)} {N(a)}V{N(c)}M{N(x - 5)} {N(a)}h10M{N(x - 5)} {N(c)}h10");
                            }
                        }
                        if (d.Length > 0) sb.Append($"<path d=\"{d}\" stroke=\"{col}\" stroke-width=\"1.2\" fill=\"none\"/>");
                        for (int i = 0; i < s.X.Length && i < s.Y.Length; i++)
                            if (Ok(s.Y[i])) sb.Append($"<circle cx=\"{N(MapX(s.X[i]))}\" cy=\"{N(MapY(s.Y[i]))}\" r=\"3.5\" fill=\"{col}\" stroke=\"#fff\" stroke-width=\"1\"/>");
                        break;
                    }
            }
        }

        void SvgLegend(StringBuilder sb)
        {
            var items = Series.Where(s => s.InLegend && !string.IsNullOrEmpty(s.Label)).ToList();
            if (items.Count == 0) return;
            double w = items.Max(s => TextWidth(s.Label)) + 36, h = items.Count * 16 + 8;
            double x = plot.Right - w - 8, y = plot.Top + 8;
            sb.Append($"<g><rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" fill=\"#fff\" fill-opacity=\"0.92\" stroke=\"#cfcfcf\"/>");
            double ly = y + 6;
            foreach (var s in items)
            {
                string col = Hex(s.Color);
                double sx = x + 8, cy = ly + 6;
                if (s.Kind == SeriesKind.Bars) sb.Append($"<rect x=\"{N(sx)}\" y=\"{N(cy - 5)}\" width=\"16\" height=\"10\" fill=\"{col}\" fill-opacity=\"0.85\"/>");
                else if (s.Kind == SeriesKind.Points || s.Kind == SeriesKind.ErrorBars) sb.Append($"<circle cx=\"{N(sx + 8)}\" cy=\"{N(cy)}\" r=\"3.5\" fill=\"{col}\"/>");
                else sb.Append($"<line x1=\"{N(sx)}\" y1=\"{N(cy)}\" x2=\"{N(sx + 16)}\" y2=\"{N(cy)}\" stroke=\"{col}\" stroke-width=\"2\"{Dash(s)}/>");
                sb.Append($"<text x=\"{N(sx + 22)}\" y=\"{N(cy + 4)}\" fill=\"{Ink}\">{Esc(s.Label)}</text>");
                ly += 16;
            }
            sb.Append("</g>");
        }
    }
}
