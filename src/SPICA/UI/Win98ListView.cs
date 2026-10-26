using System;
using System.Drawing;
using System.Windows.Forms;

namespace Spica.UI
{
    /// <summary>
    /// Details-view list with Win98 column headers and fully owner-drawn rows (navy selection,
    /// per-cell colours through <see cref="CellColor"/>). Works in virtual mode too.
    /// </summary>
    public class Win98ListView : ListView
    {
        /// <summary>Optional text colour per (row, column); null = default.</summary>
        public Func<int, int, Color?> CellColor;
        public Func<int, Color?> RowBack;

        public Win98ListView(bool virtualMode = false)
        {
            View = View.Details;
            FullRowSelect = true;
            BorderStyle = BorderStyle.None;
            HideSelection = false;
            Font = Win98.Font;
            OwnerDraw = true;
            VirtualMode = virtualMode;
            GridLines = false;
            DoubleBuffered = true;
        }

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
            Color back = sel ? (Focused ? Win98.Navy : Win98.Face) : (RowBack?.Invoke(e.ItemIndex) ?? Color.White);
            Color fore = sel ? (Focused ? Color.White : Color.Black) : (CellColor?.Invoke(e.ItemIndex, e.ColumnIndex) ?? Color.Black);
            using (var b = new SolidBrush(back)) g.FillRectangle(b, e.Bounds);
            var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height);
            var align = e.Header != null && e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
            TextRenderer.DrawText(g, e.SubItem?.Text ?? "", Font, r, fore,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
