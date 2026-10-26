using System.Drawing;
using System.Windows.Forms;

namespace Spica.UI
{
    /// <summary>Paints MenuStrip / ToolStrip / ContextMenuStrip in the Windows 98 style.</summary>
    public class Win98Renderer : ToolStripRenderer
    {
        public static void Apply(ToolStrip ts)
        {
            ts.Renderer = new Win98Renderer();
            ts.Font = Win98.Font;
            ts.BackColor = Win98.Face;
            ts.GripStyle = ToolStripGripStyle.Hidden;
        }

        /// <summary>Recursively styles drop-downs (no drop shadow, classic metrics).</summary>
        public static void StyleDropDowns(ToolStripItemCollection items)
        {
            foreach (ToolStripItem it in items)
            {
                if (it is ToolStripDropDownItem dd && dd.HasDropDownItems)
                {
                    if (dd.DropDown is ToolStripDropDownMenu m)
                    {
                        m.DropShadowEnabled = false;
                        m.ShowImageMargin = true;
                        m.Font = Win98.Font;
                        m.Renderer = new Win98Renderer();
                    }
                    StyleDropDowns(dd.DropDownItems);
                }
            }
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Win98.Face)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var ts = e.ToolStrip;
            if (ts is ToolStripDropDown)
                Win98.RaisedWindow(e.Graphics, new Rectangle(0, 0, ts.Width, ts.Height));
            else if (!(ts is MenuStrip) && !(ts is StatusStrip))
                Win98.ThinRaised(e.Graphics, new Rectangle(0, 0, ts.Width, ts.Height));
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Win98.Face)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var it = e.Item;
            var r = new Rectangle(Point.Empty, it.Size);
            var g = e.Graphics;
            if (it.IsOnDropDown)
            {
                r = new Rectangle(2, 0, it.Width - 4, it.Height);
                using (var b = new SolidBrush(it.Selected && it.Enabled ? Win98.Navy : Win98.Face)) g.FillRectangle(b, r);
                if (it.Selected && !it.Enabled)
                    using (var b = new SolidBrush(Win98.Navy)) g.FillRectangle(b, r);
            }
            else
            {
                using (var b = new SolidBrush(Win98.Face)) g.FillRectangle(b, r);
                if (it.Pressed) Win98.ThinSunken(g, r);
                else if (it.Selected) Win98.ThinRaised(g, r);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var it = e.Item;
            bool highlight = it.IsOnDropDown && it.Selected;
            var r = e.TextRectangle;
            if (!it.IsOnDropDown && it.Pressed) r.Offset(1, 1);
            if (!it.Enabled)
            {
                if (!highlight)
                {
                    var r1 = r; r1.Offset(1, 1);
                    TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, r1, Color.White, e.TextFormat);
                }
                TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, r, Win98.Shadow, e.TextFormat);
            }
            else
            {
                TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, r, highlight ? Color.White : Color.Black, e.TextFormat);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var it = e.Item;
            if (e.Vertical)
                Win98.EtchedVLine(e.Graphics, it.Width / 2 - 1, 2, it.Height - 2);
            else
                Win98.EtchedHLine(e.Graphics, 3, it.Width - 3, it.Height / 2 - 1);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var it = e.Item;
            var r = new Rectangle(Point.Empty, it.Size);
            var g = e.Graphics;
            using (var b = new SolidBrush(Win98.Face)) g.FillRectangle(b, r);
            var btn = it as ToolStripButton;
            if (!it.Enabled) return;
            if (it.Pressed || (btn != null && btn.Checked)) Win98.ThinSunken(g, r);
            else if (it.Selected) Win98.ThinRaised(g, r);
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Image == null) return;
            var r = e.ImageRectangle;
            if (!e.Item.IsOnDropDown && e.Item.Pressed) r.Offset(1, 1);
            var g = e.Graphics;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            if (e.Item.Enabled) g.DrawImage(e.Image, r);
            else ControlPaint.DrawImageDisabled(g, e.Image, r.X, r.Y, Win98.Face);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            var it = e.Item;
            var c = it != null && it.IsOnDropDown && it.Selected ? Color.White : (it == null || it.Enabled ? Color.Black : Win98.Shadow);
            var r = e.ArrowRectangle;
            if (e.Direction == ArrowDirection.Right)
                Win98.Glyph(e.Graphics, Win98.ArrowRightGlyph, r.X + (r.Width - 4) / 2, r.Y + (r.Height - 7) / 2, c);
            else
                Win98.Glyph(e.Graphics, Win98.ArrowDownGlyph, r.X + (r.Width - 7) / 2, r.Y + (r.Height - 4) / 2, c);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            var c = e.Item.Selected ? Color.White : Color.Black;
            Win98.Glyph(e.Graphics, Win98.CheckGlyph, r.X + (r.Width - 7) / 2, r.Y + (r.Height - 7) / 2, c);
        }

        protected override void OnRenderGrip(ToolStripGripRenderEventArgs e) { }
    }
}
