using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Spica.UI
{
    /// <summary>
    /// Borderless form that paints a Windows 98 frame and caption (navy gradient, bold title,
    /// classic caption buttons). Dragging, snapping, resizing and the system menu are delegated
    /// to Windows through WM_NCHITTEST, so the window still behaves natively.
    /// </summary>
    public class Win98Form : Form
    {
        public const int FrameSize = 4;
        public const int CaptionHeight = 18;
        const int CaptionTop = 3;

        bool active;
        int pressedButton = -1;
        bool pressedInside;

        bool resizable = true;
        /// <summary>The window can be resized; its frame is then a little wider inside, so the edges are easy to grab.</summary>
        public bool Resizable
        {
            get => resizable;
            set { resizable = value; Padding = FramePadding; }
        }

        /// <summary>How far in from each edge the window can be grabbed to resize it, and the length of a corner along each side.</summary>
        const int Grip = 7, CornerGrip = 18;

        Padding FramePadding => new Padding(resizable ? Grip - 1 : FrameSize, CaptionTop + CaptionHeight + 1, resizable ? Grip - 1 : FrameSize, resizable ? Grip - 1 : FrameSize);
        public Image CaptionIcon { get; set; }

        public Win98Form()
        {
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Win98.Face;
            Font = Win98.Font;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Padding = FramePadding;
            CaptionIcon = Win98.Icon("app16");
            var ico = Win98.AppIcon();
            if (ico != null) Icon = ico;
        }

        // ------------------------------------------------------------ native plumbing

        const int WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
        const int WM_NCHITTEST = 0x84, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONDBLCLK = 0xA3;
        DateTime lastCaptionClick = DateTime.MinValue;
        Point lastCaptionAt;
        const int HTCLIENT = 1, HTCAPTION = 2, HTSYSMENU = 3, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12,
                  HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= WS_SYSMENU | WS_MINIMIZEBOX;
                if (MaximizeBox && Resizable) cp.Style |= WS_MAXIMIZEBOX;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                int lp = unchecked((int)(long)m.LParam);
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                m.Result = (IntPtr)HitTest(p);
                return;
            }
            if (m.Msg == WM_NCLBUTTONDBLCLK && (int)m.WParam == HTCAPTION)
            {
                if (MaximizeBox && Resizable) ToggleMaximize();
                return;
            }
            // A borderless window starts moving on the first click of the caption, and that move loop swallows the
            // second click: the double-click is told apart here, by time and place, before the move begins.
            if (m.Msg == WM_NCLBUTTONDOWN && (int)m.WParam == HTCAPTION)
            {
                int lp = unchecked((int)(long)m.LParam);
                var at = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                var size = SystemInformation.DoubleClickSize;
                bool again = (DateTime.UtcNow - lastCaptionClick).TotalMilliseconds <= SystemInformation.DoubleClickTime &&
                             Math.Abs(at.X - lastCaptionAt.X) <= size.Width && Math.Abs(at.Y - lastCaptionAt.Y) <= size.Height;
                lastCaptionClick = again ? DateTime.MinValue : DateTime.UtcNow;
                lastCaptionAt = at;
                if (again)
                {
                    if (MaximizeBox && Resizable) ToggleMaximize();
                    return;
                }
            }
            base.WndProc(ref m);
        }

        int HitTest(Point p)
        {
            if (Resizable && WindowState == FormWindowState.Normal)
            {
                const int g = Grip, c = CornerGrip;
                bool l = p.X < g, r = p.X >= Width - g, t = p.Y < g, b = p.Y >= Height - g;
                // a corner answers along a stretch of both its sides, not only in its few pixels
                bool nl = p.X < c, nr = p.X >= Width - c, nt = p.Y < c, nb = p.Y >= Height - c;
                if ((t && nl) || (l && nt)) return HTTOPLEFT;
                if ((t && nr) || (r && nt)) return HTTOPRIGHT;
                if ((b && nl) || (l && nb)) return HTBOTTOMLEFT;
                if ((b && nr) || (r && nb)) return HTBOTTOMRIGHT;
                if (l) return HTLEFT;
                if (r) return HTRIGHT;
                if (t) return HTTOP;
                if (b) return HTBOTTOM;
                // status-bar size grip (child returns HTTRANSPARENT there)
                if (p.X >= Width - 16 && p.Y >= Height - 16) return HTBOTTOMRIGHT;
            }
            var cap = CaptionRect;
            if (cap.Contains(p))
            {
                if (ButtonAt(p) >= 0) return HTCLIENT;
                if (IconRect.Contains(p)) return HTSYSMENU;
                return HTCAPTION;
            }
            return HTCLIENT;
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            UpdateMaximizedBounds();
        }

        void UpdateMaximizedBounds()
        {
            var scr = Screen.FromHandle(Handle);
            var wa = scr.WorkingArea;
            MaximizedBounds = new Rectangle(wa.X - scr.Bounds.X, wa.Y - scr.Bounds.Y, wa.Width, wa.Height);
        }

        public void ToggleMaximize()
        {
            UpdateMaximizedBounds();
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        protected override void OnActivated(EventArgs e) { active = true; Invalidate(new Rectangle(0, 0, Width, CaptionTop + CaptionHeight + 1)); base.OnActivated(e); }
        protected override void OnDeactivate(EventArgs e) { active = false; Invalidate(new Rectangle(0, 0, Width, CaptionTop + CaptionHeight + 1)); base.OnDeactivate(e); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(CaptionRect); }

        // ------------------------------------------------------------ geometry

        Rectangle CaptionRect => new Rectangle(CaptionTop, CaptionTop, Width - 2 * CaptionTop, CaptionHeight);
        Rectangle IconRect { get { var c = CaptionRect; return new Rectangle(c.X + 1, c.Y + 1, 16, 16); } }

        bool HasMinMax => MinimizeBox || MaximizeBox;

        /// <summary>0 = close, 1 = maximize/restore, 2 = minimize.</summary>
        Rectangle ButtonRect(int i)
        {
            var c = CaptionRect;
            int y = c.Y + 2;
            int closeX = c.Right - 2 - 16;
            if (i == 0) return new Rectangle(closeX, y, 16, 14);
            if (i == 1) return new Rectangle(closeX - 2 - 16, y, 16, 14);
            return new Rectangle(closeX - 2 - 32, y, 16, 14);
        }

        bool ButtonVisible(int i) => i == 0 || HasMinMax;
        bool ButtonEnabled(int i) => i == 0 ? ControlBox : i == 1 ? (MaximizeBox && Resizable) : MinimizeBox;

        int ButtonAt(Point p)
        {
            for (int i = 0; i < 3; i++)
                if (ButtonVisible(i) && ButtonRect(i).Contains(p)) return i;
            return -1;
        }

        // ------------------------------------------------------------ mouse on caption buttons

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int b = ButtonAt(e.Location);
            if (b >= 0 && ButtonEnabled(b))
            {
                pressedButton = b;
                pressedInside = true;
                Invalidate(CaptionRect);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (pressedButton < 0) return;
            bool inside = ButtonRect(pressedButton).Contains(e.Location);
            if (inside != pressedInside) { pressedInside = inside; Invalidate(CaptionRect); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (pressedButton < 0) return;
            int b = pressedButton;
            bool fire = ButtonRect(b).Contains(e.Location);
            pressedButton = -1;
            Invalidate(CaptionRect);
            if (!fire) return;
            if (b == 0) Close();
            else if (b == 1) ToggleMaximize();
            else WindowState = FormWindowState.Minimized;
        }

        // ------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            Win98.RaisedWindow(g, new Rectangle(0, 0, Width, Height));

            var cap = CaptionRect;
            Color a = active ? Win98.TitleActiveA : Win98.TitleInactiveA;
            Color b = active ? Win98.TitleActiveB : Win98.TitleInactiveB;
            using (var br = new LinearGradientBrush(new Rectangle(cap.X - 1, cap.Y, cap.Width + 2, cap.Height), a, b, LinearGradientMode.Horizontal))
                g.FillRectangle(br, cap);

            int textX = cap.X + 3;
            if (CaptionIcon != null)
            {
                Win98.DrawIcon(g, CaptionIcon, IconRect.X, IconRect.Y);
                textX = IconRect.Right + 3;
            }
            int textRight = (HasMinMax ? ButtonRect(2).X : ButtonRect(0).X) - 4;
            var tr = new Rectangle(textX, cap.Y, Math.Max(0, textRight - textX), cap.Height);
            TextRenderer.DrawText(g, Text, Win98.BoldFont, tr, active ? Color.White : Win98.Face,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            for (int i = 0; i < 3; i++)
                if (ButtonVisible(i)) PaintCaptionButton(g, i);
        }

        void PaintCaptionButton(Graphics g, int i)
        {
            var r = ButtonRect(i);
            bool pressed = pressedButton == i && pressedInside;
            using (var bf = new SolidBrush(Win98.Face)) g.FillRectangle(bf, r);
            if (pressed) Win98.Pressed(g, r); else Win98.Raised(g, r);

            string[] glyph = i == 0 ? Win98.CloseGlyph
                : i == 1 ? (WindowState == FormWindowState.Maximized ? Win98.RestoreGlyph : Win98.MaximizeGlyph)
                : Win98.MinimizeGlyph;
            int gw = glyph[0].Length, gh = glyph.Length;
            int gx = r.X + (r.Width - gw) / 2;
            int gy = i == 2 ? r.Bottom - 5 : r.Y + (r.Height - gh) / 2;
            if (pressed) { gx++; gy++; }
            if (ButtonEnabled(i))
                Win98.Glyph(g, glyph, gx, gy, Color.Black);
            else
            {
                Win98.Glyph(g, glyph, gx + 1, gy + 1, Color.White);
                Win98.Glyph(g, glyph, gx, gy, Win98.Shadow);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

    }
}
