using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Spica.UI
{
    // =====================================================================================
    //  Push button
    // =====================================================================================
    public class Win98Button : Button
    {
        bool mouseDown, mouseInside, spaceDown;

        public Win98Button()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Win98.Face;
            Font = Win98.Font;
            Size = new Size(75, 23);
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { mouseDown = true; mouseInside = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (mouseDown)
            {
                bool inside = ClientRectangle.Contains(e.Location);
                if (inside != mouseInside) { mouseInside = inside; Invalidate(); }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            mouseDown = false; Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { spaceDown = true; Invalidate(); }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { spaceDown = false; Invalidate(); }
            base.OnKeyUp(e);
        }

        protected override void OnLostFocus(EventArgs e) { mouseDown = spaceDown = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            g.Clear(Win98.Face);
            bool pressed = (mouseDown && mouseInside) || spaceDown;
            bool isDefault = IsDefault || Focused;

            var b = r;
            if (isDefault)
            {
                using (var p = new Pen(Color.Black)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                b.Inflate(-1, -1);
            }
            if (pressed)
            {
                if (isDefault) using (var p = new Pen(Win98.Shadow)) g.DrawRectangle(p, b.X, b.Y, b.Width - 1, b.Height - 1);
                else Win98.Pressed(g, b);
            }
            else Win98.Raised(g, b);

            var content = Rectangle.Inflate(b, -3, -3);
            if (pressed) content.Offset(1, 1);

            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            if (Image != null)
            {
                var img = Image;
                if (string.IsNullOrEmpty(Text))
                {
                    Win98.DrawIcon(g, img, content.X + (content.Width - img.Width) / 2, content.Y + (content.Height - img.Height) / 2, Enabled);
                }
                else
                {
                    var ts = TextRenderer.MeasureText(Text, Font);
                    int total = img.Width + 4 + ts.Width;
                    int x = content.X + Math.Max(0, (content.Width - total) / 2);
                    Win98.DrawIcon(g, img, x, content.Y + (content.Height - img.Height) / 2, Enabled);
                    var tr = new Rectangle(x + img.Width + 4, content.Y, content.Right - (x + img.Width + 4), content.Height);
                    Win98.DrawText(g, Text, Font, tr, Enabled, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
            }
            else
            {
                Win98.DrawText(g, Text, Font, content, Enabled, flags);
            }

            if (Focused && Enabled)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(b, -4, -4), Color.Black, Win98.Face);
        }
    }

    // =====================================================================================
    //  Check box
    // =====================================================================================
    public class Win98CheckBox : CheckBox
    {
        public Win98CheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Win98.Font;
            BackColor = Win98.Face;
            AutoSize = true;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var ts = TextRenderer.MeasureText(Text, Font);
            return new Size(13 + 6 + ts.Width + 2, Math.Max(17, ts.Height + 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var box = new Rectangle(0, (Height - 13) / 2, 13, 13);
            using (var bw = new SolidBrush(Enabled ? Win98.Window : Win98.Face)) g.FillRectangle(bw, box);
            Win98.Sunken(g, box);
            if (CheckState == CheckState.Checked)
                Win98.Glyph(g, Win98.CheckGlyph, box.X + 3, box.Y + 3, Enabled ? Color.Black : Win98.Shadow);
            else if (CheckState == CheckState.Indeterminate)
                Win98.Glyph(g, Win98.CheckGlyph, box.X + 3, box.Y + 3, Win98.Shadow);

            var ts = TextRenderer.MeasureText(Text, Font);
            var tr = new Rectangle(19, (Height - ts.Height) / 2, ts.Width, ts.Height);
            Win98.DrawText(g, Text, Font, tr, Enabled, TextFormatFlags.Left | TextFormatFlags.SingleLine);
            if (Focused) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(tr, 1, 0), Color.Black, BackColor);
        }

        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    // =====================================================================================
    //  Radio button
    // =====================================================================================
    public class Win98RadioButton : RadioButton
    {
        public Win98RadioButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Win98.Font;
            BackColor = Win98.Face;
            AutoSize = true;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var ts = TextRenderer.MeasureText(Text, Font);
            return new Size(12 + 7 + ts.Width + 2, Math.Max(17, ts.Height + 2));
        }

        // 12x12 classic radio: S = shadow, K = black, H = white, L = light, W = window, . = none
        static readonly string[] Map =
        {
            "....SSSS....",
            "..SSKKKKSS..",
            ".SKKWWWWKKH.",
            ".SKWWWWWWLH.",
            "SKWWWWWWWWLH",
            "SKWWWWWWWWLH",
            "SKWWWWWWWWLH",
            "SKWWWWWWWWLH",
            ".SKWWWWWWLH.",
            ".HLLWWWWLLH.",
            "..HHLLLLHH..",
            "....HHHH....",
        };

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int ox = 0, oy = (Height - 12) / 2;
            for (int y = 0; y < 12; y++)
                for (int x = 0; x < 12; x++)
                {
                    Color c;
                    switch (Map[y][x])
                    {
                        case 'S': c = Win98.Shadow; break;
                        case 'K': c = Color.Black; break;
                        case 'H': c = Color.White; break;
                        case 'L': c = Win98.Light; break;
                        case 'W': c = Enabled ? Color.White : Win98.Face; break;
                        default: continue;
                    }
                    using (var b = new SolidBrush(c)) g.FillRectangle(b, ox + x, oy + y, 1, 1);
                }
            if (Checked)
            {
                using (var b = new SolidBrush(Enabled ? Color.Black : Win98.Shadow))
                {
                    g.FillRectangle(b, ox + 5, oy + 4, 2, 1);
                    g.FillRectangle(b, ox + 4, oy + 5, 4, 2);
                    g.FillRectangle(b, ox + 5, oy + 7, 2, 1);
                }
            }
            var ts = TextRenderer.MeasureText(Text, Font);
            var tr = new Rectangle(18, (Height - ts.Height) / 2, ts.Width, ts.Height);
            Win98.DrawText(g, Text, Font, tr, Enabled, TextFormatFlags.Left | TextFormatFlags.SingleLine);
            if (Focused) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(tr, 1, 0), Color.Black, BackColor);
        }

        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    // =====================================================================================
    //  Group box (etched frame)
    // =====================================================================================
    public class Win98GroupBox : GroupBox
    {
        public Win98GroupBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Win98.Font;
            BackColor = Win98.Face;
            Padding = new Padding(8, 6, 8, 8);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var ts = TextRenderer.MeasureText(Text, Font);
            int top = ts.Height / 2;
            Win98.Etched(g, new Rectangle(0, top, Width, Height - top));
            if (!string.IsNullOrEmpty(Text))
            {
                var tr = new Rectangle(7, 0, ts.Width + 2, ts.Height);
                using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, tr);
                Win98.DrawText(g, Text, Font, new Rectangle(8, 0, ts.Width, ts.Height), Enabled, TextFormatFlags.Left | TextFormatFlags.SingleLine);
            }
        }
    }

    // =====================================================================================
    //  Sunken well that hosts one child (text box, list, ...)
    // =====================================================================================
    public class Win98Well : Panel
    {
        public Win98Well()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(2);
            BackColor = Win98.Window;
        }

        public Win98Well(Control child, Color? back = null) : this()
        {
            if (back.HasValue) BackColor = back.Value;
            child.Dock = DockStyle.Fill;
            Controls.Add(child);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            Win98.Sunken(e.Graphics, ClientRectangle);
        }
    }

    /// <summary>Single line text box inside a sunken well, the classic edit field look.</summary>
    public class Win98TextField : Win98Well
    {
        public readonly TextBox Box;

        public Win98TextField()
        {
            Box = new TextBox { BorderStyle = BorderStyle.None, Font = Win98.Font, BackColor = Win98.Window };
            Padding = new Padding(3, 3, 2, 1);
            Controls.Add(Box);
            Box.Dock = DockStyle.Fill;
            Height = 20;
        }

        public override string Text { get => Box.Text; set => Box.Text = value; }

        public bool ReadOnly
        {
            get => Box.ReadOnly;
            set { Box.ReadOnly = value; Box.BackColor = value ? Win98.Face : Win98.Window; BackColor = Box.BackColor; Invalidate(); }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            BackColor = Enabled && !ReadOnly ? Win98.Window : Win98.Face;
            Box.BackColor = BackColor;
        }
    }

    // =====================================================================================
    //  Progress bar (navy blocks)
    // =====================================================================================
    public class Win98ProgressBar : Control
    {
        int value, maximum = 100;

        public Win98ProgressBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Win98.Face;
            Height = 18;
        }

        public int Maximum { get => maximum; set { maximum = Math.Max(1, value); Invalidate(); } }
        public int Value { get => value; set { this.value = Math.Max(0, Math.Min(maximum, value)); Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Win98.Face);
            Win98.ThinSunken(g, ClientRectangle);
            var inner = Rectangle.Inflate(ClientRectangle, -3, -3);
            int fillW = (int)((long)inner.Width * value / maximum);
            const int block = 8, gap = 2;
            using (var b = new SolidBrush(Win98.Navy))
            {
                for (int x = 0; x < fillW; x += block + gap)
                    g.FillRectangle(b, inner.X + x, inner.Y, Math.Min(block, inner.Width - x), inner.Height);
            }
        }
    }

    // =====================================================================================
    //  Status bar with sunken panels and a size grip
    // =====================================================================================
    public class Win98StatusBar : Control
    {
        public class Panel98
        {
            public string Text = "";
            public int Width;          // 0 = spring
            public Image Icon;
        }

        public readonly List<Panel98> Panels = new List<Panel98>();

        public Win98StatusBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Bottom;
            Height = 22;
            BackColor = Win98.Face;
            Font = Win98.Font;
        }

        public Panel98 Add(string text, int width, Image icon = null)
        {
            var p = new Panel98 { Text = text, Width = width, Icon = icon };
            Panels.Add(p);
            return p;
        }

        public void SetText(int i, string text)
        {
            if (Panels[i].Text == text) return;
            Panels[i].Text = text;
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1;
            if (m.Msg == WM_NCHITTEST)
            {
                int lp = unchecked((int)(long)m.LParam);
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (p.X >= Width - 14 && p.Y >= Height - 14) { m.Result = (IntPtr)HTTRANSPARENT; return; }
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Win98.Face);
            int gripW = 14;
            int fixedW = 0, springs = 0;
            foreach (var p in Panels) { if (p.Width > 0) fixedW += p.Width + 2; else springs++; }
            int springW = springs > 0 ? Math.Max(20, (Width - gripW - 2 - fixedW - 2 * springs) / springs) : 0;
            int x = 2;
            foreach (var p in Panels)
            {
                int w = p.Width > 0 ? p.Width : springW;
                var r = new Rectangle(x, 3, w, Height - 5);
                Win98.ThinSunken(g, r);
                int tx = r.X + 3;
                if (p.Icon != null)
                {
                    Win98.DrawIcon(g, p.Icon, tx, r.Y + (r.Height - p.Icon.Height) / 2);
                    tx += p.Icon.Width + 3;
                }
                TextRenderer.DrawText(g, p.Text, Font, new Rectangle(tx, r.Y, r.Right - tx - 2, r.Height), Color.Black,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                x += w + 2;
            }
            // size grip: three diagonal white/gray stripes
            using (var pw = new Pen(Color.White))
            using (var ps = new Pen(Win98.Shadow))
            {
                int bx = Width - 2, by = Height - 2;
                for (int i = 0; i < 3; i++)
                {
                    int o = 3 + i * 4;
                    g.DrawLine(pw, bx - o - 1, by, bx, by - o - 1);
                    g.DrawLine(ps, bx - o, by, bx, by - o);
                    g.DrawLine(ps, bx - o + 1, by, bx, by - o + 1);
                }
            }
        }
    }

    // =====================================================================================
    //  Tab control (property-sheet style)
    // =====================================================================================
    public class Win98TabPage : Panel
    {
        public Image TabIcon;

        public Win98TabPage(string text, Image icon = null)
        {
            Text = text;
            TabIcon = icon;
            BackColor = Win98.Face;
            Padding = new Padding(6);
        }
    }

    public class Win98TabControl : Control
    {
        const int TabH = 20;
        readonly List<Win98TabPage> pages = new List<Win98TabPage>();
        int selected;

        public event EventHandler SelectedIndexChanged;

        public Win98TabControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Win98.Face;
            Font = Win98.Font;
            TabStop = true;
        }

        public IReadOnlyList<Win98TabPage> Pages => pages;

        public void AddPage(Win98TabPage p)
        {
            pages.Add(p);
            Controls.Add(p);
            p.Visible = pages.Count - 1 == selected;
            PerformLayout();
        }

        public int SelectedIndex
        {
            get => selected;
            set
            {
                if (value < 0 || value >= pages.Count || value == selected) return;
                selected = value;
                for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == selected;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        Rectangle Body => new Rectangle(0, TabH, Width, Height - TabH);

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            var b = Body;
            foreach (var p in pages) p.Bounds = new Rectangle(b.X + 3, b.Y + 3, b.Width - 6, b.Height - 6);
        }

        Rectangle TabRect(int i, Graphics g = null)
        {
            int x = 2;
            for (int k = 0; k < pages.Count; k++)
            {
                int w = TextRenderer.MeasureText(pages[k].Text, Font).Width + 14 + (pages[k].TabIcon != null ? 20 : 0);
                if (k == i) return new Rectangle(x, 2, w, TabH - 2);
                x += w;
            }
            return Rectangle.Empty;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            for (int i = 0; i < pages.Count; i++)
                if (TabRect(i).Contains(e.Location)) { SelectedIndex = i; Focus(); Invalidate(); return; }
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) SelectedIndex = Math.Max(0, selected - 1);
            if (e.KeyCode == Keys.Right) SelectedIndex = Math.Min(pages.Count - 1, selected + 1);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Tab)) { SelectedIndex = (selected + 1) % Math.Max(1, pages.Count); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.Tab)) { SelectedIndex = (selected + pages.Count - 1) % Math.Max(1, pages.Count); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Win98.Face);
            var body = Body;
            // body frame: white top/left, black + gray bottom/right
            Win98.Ring(g, body, Color.White, Color.Black);
            Win98.Ring(g, Rectangle.Inflate(body, -1, -1), Win98.Face, Win98.Shadow);

            for (int i = 0; i < pages.Count; i++)
                if (i != selected) PaintTab(g, i, false);
            if (pages.Count > 0) PaintTab(g, selected, true);
        }

        void PaintTab(Graphics g, int i, bool sel)
        {
            var t = TabRect(i);
            if (sel) t = new Rectangle(t.X - 2, 0, t.Width + 4, TabH + 1);
            using (var bf = new SolidBrush(Win98.Face)) g.FillRectangle(bf, t.X, t.Y, t.Width, t.Height);
            using (var w = new SolidBrush(Color.White))
            using (var k = new SolidBrush(Color.Black))
            using (var s = new SolidBrush(Win98.Shadow))
            {
                g.FillRectangle(w, t.X, t.Y + 2, 1, t.Height - 2);          // left
                g.FillRectangle(w, t.X + 1, t.Y + 1, 1, 1);                 // corner
                g.FillRectangle(w, t.X + 2, t.Y, t.Width - 4, 1);           // top
                g.FillRectangle(k, t.Right - 1, t.Y + 2, 1, t.Height - 2);  // right outer
                g.FillRectangle(k, t.Right - 2, t.Y + 1, 1, 1);             // corner
                g.FillRectangle(s, t.Right - 2, t.Y + 2, 1, t.Height - 2);  // right inner
            }
            var p = pages[i];
            int tx = t.X + 7;
            if (p.TabIcon != null)
            {
                Win98.DrawIcon(g, p.TabIcon, tx, t.Y + (t.Height - 16) / 2 + (sel ? 0 : 1));
                tx += 20;
            }
            var ts = TextRenderer.MeasureText(p.Text, Font);
            var tr = new Rectangle(tx, t.Y + (t.Height - ts.Height) / 2 + (sel ? 0 : 1), ts.Width, ts.Height);
            TextRenderer.DrawText(g, p.Text, Font, tr, Color.Black, TextFormatFlags.Left | TextFormatFlags.SingleLine);
            if (sel && Focused) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(tr, 2, 1), Color.Black, Win98.Face);
        }
    }

    // =====================================================================================
    //  Scrolling throughput graph (black/green, Task Manager style)
    // =====================================================================================
    public class Win98Graph : Control
    {
        readonly List<double> data = new List<double>();
        public int Capacity = 240;
        public string Caption = "";
        int scroll;

        public Win98Graph()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
        }

        public void Push(double v)
        {
            data.Add(v);
            if (data.Count > Capacity) data.RemoveAt(0);
            scroll = (scroll + 2) % 12;
            Invalidate();
        }

        public void Clear() { data.Clear(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.Black);
            var r = ClientRectangle;
            using (var grid = new Pen(Color.FromArgb(0, 128, 64)))
            {
                for (int y = r.Bottom - 1; y > 0; y -= 12) g.DrawLine(grid, 0, y, r.Width, y);
                for (int x = r.Width - 1 - scroll; x > 0; x -= 12) g.DrawLine(grid, x, 0, x, r.Height);
            }
            double max = 1e-9;
            foreach (var v in data) if (v > max) max = v;
            max *= 1.15;
            if (data.Count > 1)
            {
                var pts = new List<Point>();
                int n = data.Count;
                for (int i = 0; i < n; i++)
                {
                    int x = r.Width - 1 - (n - 1 - i) * 2;
                    if (x < 0) continue;
                    int y = r.Height - 2 - (int)((r.Height - 4) * data[i] / max);
                    pts.Add(new Point(x, y));
                }
                if (pts.Count > 1)
                    using (var pen = new Pen(Color.Lime)) g.DrawLines(pen, pts.ToArray());
            }
            if (!string.IsNullOrEmpty(Caption))
                TextRenderer.DrawText(g, Caption, Win98.Font, new Point(3, 2), Color.Lime);
        }
    }
}
