using System;
using System.Drawing;
using System.Windows.Forms;

namespace Spica.UI
{
    /// <summary>A flat button showing only a small icon: raised under the pointer, sunken while pressed (a close cross...).</summary>
    public sealed class FlatGlyphButton : Control
    {
        readonly Image image;
        bool hover, down;

        public FlatGlyphButton(Image image)
        {
            this.image = image;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Win98.Face;
            Cursor = Cursors.Default;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var r = new Rectangle(0, 0, Width, Height);
            if (hover && down) Win98.ThinSunken(g, r);
            else if (hover) Win98.ThinRaised(g, r);
            int off = hover && down ? 1 : 0;
            g.DrawImage(image, (Width - image.Width) / 2 + off, (Height - image.Height) / 2 + off, image.Width, image.Height);
        }
    }
}
