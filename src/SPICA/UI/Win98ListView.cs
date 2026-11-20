using System;
using System.Drawing;
using System.Windows.Forms;

namespace Spica.UI
{
    /// <summary>
    /// Details-view list with Win98 column headers and fully owner-drawn rows (navy selection, per-cell colours through
    /// <see cref="CellColor"/>, a hover text per cell through <see cref="CellTip"/>).
    /// </summary>
    public class Win98ListView : ListView
    {
        /// <summary>Optional text colour per (row, column); null = default.</summary>
        public Func<int, int, Color?> CellColor;
        /// <summary>Optional hover text for a cell (row, column): what this value means.</summary>
        public Func<int, int, string> CellTip;

        readonly ToolTip tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 0, ReshowDelay = 0, UseAnimation = false, UseFading = false };
        readonly Timer hoverTimer = new Timer { Interval = 450 };
        string hoverKey, hoverText;
        Point hoverAt;

        public Win98ListView()
        {
            View = View.Details;
            FullRowSelect = true;
            BorderStyle = BorderStyle.None;
            HideSelection = false;
            Font = Win98.Font;
            OwnerDraw = true;
            GridLines = false;
            DoubleBuffered = true;
            hoverTimer.Tick += (s, e) =>
            {
                hoverTimer.Stop();
                if (!string.IsNullOrEmpty(hoverText)) tip.Show(hoverText, this, hoverAt.X, hoverAt.Y, 30000);
            };
        }

        // ------------------------------------------------------------------ hover tips

        /// <summary>Shows <paramref name="text"/> after a short hover; the same key keeps the tip that is up.</summary>
        void Hover(string key, Func<string> text, Point clientPos)
        {
            if (key == hoverKey) return;
            hoverKey = key;
            hoverTimer.Stop();
            tip.Hide(this);
            hoverText = key == null ? null : text();
            if (string.IsNullOrEmpty(hoverText)) return;
            hoverAt = new Point(clientPos.X + 12, clientPos.Y + 18);
            hoverTimer.Start();
        }

        void EndHover() { hoverKey = null; hoverTimer.Stop(); tip.Hide(this); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (CellTip == null) return;
            var hit = HitTest(e.Location);
            if (hit.Item == null || hit.SubItem == null) { Hover(null, null, e.Location); return; }
            int row = hit.Item.Index, col = hit.Item.SubItems.IndexOf(hit.SubItem);
            Hover($"c{row}:{col}", () => CellTip(row, col), e.Location);
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); EndHover(); }
        protected override void OnMouseDown(MouseEventArgs e) { EndHover(); base.OnMouseDown(e); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { hoverTimer.Dispose(); tip.Dispose(); }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ drawing

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Win98.Face)) g.FillRectangle(b, e.Bounds);
            Win98.Ring(g, e.Bounds, Color.White, Color.Black);
            Win98.Ring(g, Rectangle.Inflate(e.Bounds, -1, -1), Win98.Face, Win98.Shadow);
            var r = Rectangle.Inflate(e.Bounds, -4, 0);
            var align = e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
            TextRenderer.DrawText(g, e.Header.Text, Win98.Font, r, Color.Black,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e) { }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            var g = e.Graphics;
            bool sel = e.Item.Selected;
            Color back = sel ? (Focused ? Win98.Navy : Win98.Face) : Color.White;
            Color fore = sel ? (Focused ? Color.White : Color.Black) : (CellColor?.Invoke(e.ItemIndex, e.ColumnIndex) ?? Color.Black);
            using (var b = new SolidBrush(back)) g.FillRectangle(b, e.Bounds);
            var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height);
            var align = e.Header != null && e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
            TextRenderer.DrawText(g, e.SubItem?.Text ?? "", Font, r, fore,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
