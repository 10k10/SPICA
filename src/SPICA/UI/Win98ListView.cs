using System;
using System.Drawing;
using System.Runtime.InteropServices;
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

        /// <summary>Optional hover text for a column header (what the column means); null/empty = none.</summary>
        public Func<int, string> HeaderTip;
        /// <summary>Optional hover text for a cell (row, column): what this value means.</summary>
        public Func<int, int, string> CellTip;

        readonly ToolTip tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 0, ReshowDelay = 0, UseAnimation = false, UseFading = false };
        readonly Timer hoverTimer = new Timer { Interval = 450 };
        HeaderHook header;
        string hoverKey, hoverText;
        Point hoverAt;

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
            if (CellTip == null || VirtualMode) return;
            var hit = HitTest(e.Location);
            if (hit.Item == null || hit.SubItem == null) { Hover(null, null, e.Location); return; }
            int row = hit.Item.Index, col = hit.Item.SubItems.IndexOf(hit.SubItem);
            Hover($"c{row}:{col}", () => CellTip(row, col), e.Location);
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); EndHover(); }
        protected override void OnMouseDown(MouseEventArgs e) { EndHover(); base.OnMouseDown(e); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var h = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (h != IntPtr.Zero) header = new HeaderHook(this, h);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            header?.ReleaseHandle();
            header = null;
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { hoverTimer.Dispose(); tip.Dispose(); }
            base.Dispose(disposing);
        }

        /// <summary>The header is a child window of its own: watch its mouse moves for the column tips.</summary>
        sealed class HeaderHook : NativeWindow
        {
            readonly Win98ListView lv;
            public HeaderHook(Win98ListView lv, IntPtr h) { this.lv = lv; AssignHandle(h); }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_MOUSEMOVE && lv.HeaderTip != null)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT)), dwFlags = TME_LEAVE, hwndTrack = Handle };
                    TrackMouseEvent(ref tme);
                    int x = (short)((long)m.LParam & 0xFFFF), y = (short)(((long)m.LParam >> 16) & 0xFFFF);
                    int col = ColumnAt(x);
                    var pt = lv.PointToClient(PointToScreenOf(Handle, new Point(x, y)));
                    if (col < 0) lv.Hover(null, null, pt);
                    else lv.Hover("h" + col, () => lv.HeaderTip(col), pt);
                }
                else if (m.Msg == WM_MOUSELEAVE || m.Msg == WM_LBUTTONDOWN) lv.EndHover();
                base.WndProc(ref m);
            }

            int ColumnAt(int x)
            {
                for (int i = 0; i < lv.Columns.Count; i++)
                {
                    var r = new RECT();
                    SendMessage(Handle, HDM_GETITEMRECT, (IntPtr)i, ref r);
                    if (x >= r.Left && x < r.Right) return i;
                }
                return -1;
            }

            static Point PointToScreenOf(IntPtr h, Point p) { var q = new POINT { X = p.X, Y = p.Y }; ClientToScreen(h, ref q); return new Point(q.X, q.Y); }
        }

        const int LVM_GETHEADER = 0x101F, HDM_GETITEMRECT = 0x1207;
        const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_MOUSELEAVE = 0x02A3, TME_LEAVE = 0x2;

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public int cbSize, dwFlags; public IntPtr hwndTrack; public int dwHoverTime; }

        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref RECT l);
        [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);

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
