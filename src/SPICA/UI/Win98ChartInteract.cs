using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Spica.UI
{
    /// <summary>
    /// Mouse interaction of the chart: drag a box to zoom (a flat box zooms X only, a narrow one Y only),
    /// wheel to zoom around the pointer (Shift: X only, Ctrl: Y only), middle-drag to pan, double-click
    /// to reset, click a legend entry to hide or show that series, right-click for the menu.
    /// The zoom and the hidden series survive data refreshes (a running campaign); <see cref="ResetView"/>
    /// clears them when the chart starts showing something else.
    /// </summary>
    public partial class Win98Chart
    {
        struct View { public double X0, X1, Y0, Y1; }

        View? view;
        readonly HashSet<string> hiddenLabels = new HashSet<string>();
        readonly List<KeyValuePair<Rectangle, string>> legendHits = new List<KeyValuePair<Rectangle, string>>();
        Point? dragFrom, panFrom;
        Rectangle dragBox;
        View panStart;
        ContextMenuStrip menu;

        public bool IsZoomed => view.HasValue;

        /// <summary>Back to the full data range with every series shown.</summary>
        public void ResetView()
        {
            view = null;
            hiddenLabels.Clear();
            Invalidate();
        }

        /// <summary>A series is hidden with its legend entry; unlabeled series that follow a labeled one
        /// (e.g. the second line of a "min / max" pair) belong to it.</summary>
        bool IsHidden(int i)
        {
            if (hiddenLabels.Count == 0) return false;
            for (int k = i; k >= 0; k--)
                if (!string.IsNullOrEmpty(Series[k].Label)) return hiddenLabels.Contains(Series[k].Label);
            return false;
        }

        void SetView(double xa, double xb, double ya, double yb)
        {
            // y may run downwards (category rows), so only a collapsed or broken range is refused
            if (!(xb > xa) || !(Math.Abs(yb - ya) > 0) || double.IsInfinity(xb - xa) || double.IsInfinity(yb - ya)) return;
            view = new View { X0 = xa, X1 = xb, Y0 = ya, Y1 = yb };
            Invalidate();
        }

        // ------------------------------------------------------------------ mouse

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            // the wheel goes to the focused control: take focus unless the user is typing somewhere
            var f = FindForm();
            if (f != null && !(f.ActiveControl is TextBoxBase) && !(f.ActiveControl is ComboBox && ((ComboBox)f.ActiveControl).DroppedDown)) Focus();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                foreach (var h in legendHits)
                    if (h.Key.Contains(e.Location))
                    {
                        if (!hiddenLabels.Remove(h.Value)) hiddenLabels.Add(h.Value);
                        Invalidate();
                        return;
                    }
                if (plot.Contains(e.Location)) { dragFrom = e.Location; dragBox = Rectangle.Empty; }
            }
            else if (e.Button == MouseButtons.Middle && plot.Contains(e.Location))
            {
                panFrom = e.Location;
                panStart = new View { X0 = x0, X1 = x1, Y0 = y0, Y1 = y1 };
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            if (dragFrom.HasValue)
            {
                var a = dragFrom.Value;
                int bx = Math.Max(plot.Left, Math.Min(plot.Right, e.X)), by = Math.Max(plot.Top, Math.Min(plot.Bottom, e.Y));
                dragBox = Rectangle.FromLTRB(Math.Min(a.X, bx), Math.Min(a.Y, by), Math.Max(a.X, bx), Math.Max(a.Y, by));
            }
            else if (panFrom.HasValue && plot.Width > 0 && plot.Height > 0)
            {
                double dx = (e.X - panFrom.Value.X) * (panStart.X1 - panStart.X0) / plot.Width;
                double dy = (e.Y - panFrom.Value.Y) * (panStart.Y1 - panStart.Y0) / plot.Height;
                SetView(panStart.X0 - dx, panStart.X1 - dx, panStart.Y0 + dy, panStart.Y1 + dy);
            }
            Cursor = legendHits.Any(h => h.Key.Contains(e.Location)) ? Cursors.Hand : panFrom.HasValue ? Cursors.SizeAll : plot.Contains(e.Location) ? Cursors.Cross : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Middle) { panFrom = null; Cursor = Cursors.Default; return; }
            if (e.Button == MouseButtons.Right) { ShowMenu(e.Location); return; }
            if (!dragFrom.HasValue) return;
            var b = dragBox;
            dragFrom = null; dragBox = Rectangle.Empty;
            bool zx = b.Width >= 6, zy = b.Height >= 6;
            if (zx || zy)
            {
                // a flat box zooms X only, a narrow one Y only
                if (zx && b.Height < 12) zy = false;
                if (zy && b.Width < 12) zx = false;
                SetView(zx ? InvX(b.Left) : x0, zx ? InvX(b.Right) : x1, zy ? InvY(b.Bottom) : y0, zy ? InvY(b.Top) : y1);
            }
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!plot.Contains(e.Location) || Series.Count == 0) return;
            double f = Math.Pow(0.8, e.Delta / 120.0);              // wheel up: zoom in
            double cx = InvX(e.X), cy = InvY(e.Y);
            bool onlyX = (ModifierKeys & Keys.Shift) != 0, onlyY = (ModifierKeys & Keys.Control) != 0;
            double nx0 = onlyY ? x0 : cx - (cx - x0) * f, nx1 = onlyY ? x1 : cx + (x1 - cx) * f;
            double ny0 = onlyX ? y0 : cy - (cy - y0) * f, ny1 = onlyX ? y1 : cy + (y1 - cy) * f;
            SetView(nx0, nx1, ny0, ny1);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && !legendHits.Any(h => h.Key.Contains(e.Location))) { view = null; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            mouse = null;
            Invalidate();
        }

        void DrawInteraction(Graphics g)
        {
            if (dragFrom.HasValue && dragBox.Width > 0 && dragBox.Height >= 0)
                using (var p = new Pen(Color.Black) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(p, dragBox.X, dragBox.Y, dragBox.Width, Math.Max(1, dragBox.Height));
            if (view.HasValue || hiddenLabels.Count > 0)
            {
                string t = view.HasValue ? "zoomed - double-click to reset" : "series hidden - click the legend";
                var sz = TextRenderer.MeasureText(t, Font);
                TextRenderer.DrawText(g, t, Font, new Point(plot.Right - sz.Width - 4, plot.Bottom - sz.Height - 3), Win98.Shadow);
            }
        }

        // ------------------------------------------------------------------ menu

        void ShowMenu(Point at)
        {
            if (menu == null)
            {
                menu = new ContextMenuStrip();
                menu.Items.Add("&Reset zoom", null, (s, e) => { view = null; Invalidate(); });
                menu.Items.Add("&Show all series", null, (s, e) => { hiddenLabels.Clear(); Invalidate(); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("&Copy image", null, (s, e) => { using (var b = ToBitmap(Math.Max(200, Width), Math.Max(150, Height))) Clipboard.SetImage(b); });
                menu.Items.Add("Save image as &PNG...", null, (s, e) => SaveImage(false));
                menu.Items.Add("Save image as S&VG...", null, (s, e) => SaveImage(true));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("Drag: zoom   Wheel: zoom (Shift X, Ctrl Y)   Middle-drag: pan") { Enabled = false });
                Win98Renderer.Apply(menu);
                Win98Renderer.StyleDropDowns(menu.Items);
            }
            menu.Items[0].Enabled = view.HasValue;
            menu.Items[1].Enabled = hiddenLabels.Count > 0;
            menu.Show(this, at);
        }

        void SaveImage(bool svg)
        {
            string name = new string((Title.Length > 0 ? Title : "chart").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray()).Trim('_');
            using (var d = new SaveFileDialog { Title = "Save Chart", Filter = svg ? "SVG image (*.svg)|*.svg" : "PNG image (*.png)|*.png", FileName = name + (svg ? ".svg" : ".png"), AutoUpgradeEnabled = false })
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    int w = Math.Max(400, Width), h = Math.Max(250, Height);
                    if (svg) File.WriteAllText(d.FileName, ToSvg(w, h), new UTF8Encoding(false));
                    else using (var b = ToBitmap(w, h)) b.Save(d.FileName, ImageFormat.Png);
                }
                catch (Exception ex) { Win98MessageBox.Show(FindForm(), "Could not save the image:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) menu?.Dispose();
            base.Dispose(disposing);
        }
    }
}
