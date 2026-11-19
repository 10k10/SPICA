using System.Drawing;
using System.Drawing.Drawing2D;

namespace Spica.UI
{
    /// <summary>Small 16 px icons drawn in code, in the style of the app's icon set, where the set has none.</summary>
    public static class Glyphs
    {
        static Bitmap New16(out Graphics g)
        {
            var bmp = new Bitmap(16, 16);
            g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            return bmp;
        }

        /// <summary>A magnifying glass (Fit).</summary>
        public static Image Magnifier()
        {
            var bmp = New16(out var g);
            using (g)
            {
                using (var handle = new Pen(Color.FromArgb(96, 64, 32), 3f)) g.DrawLine(handle, 10, 10, 14, 14);
                using (var glass = new SolidBrush(Color.FromArgb(200, 230, 255))) g.FillEllipse(glass, 1, 1, 10, 10);
                using (var shine = new SolidBrush(Color.White)) g.FillEllipse(shine, 3, 3, 4, 3);
                using (var rim = new Pen(Color.Black, 1.5f)) g.DrawEllipse(rim, 1, 1, 10, 10);
            }
            return bmp;
        }

        /// <summary>A green play triangle over queue lines (Add to Queue).</summary>
        public static Image PlayQueue()
        {
            var bmp = New16(out var g);
            using (g)
            {
                using (var line = new SolidBrush(Color.FromArgb(0, 0, 128)))
                    for (int i = 0; i < 3; i++) g.FillRectangle(line, 9, 4 + i * 4, 6, 2);
                var tri = new[] { new PointF(1, 1.5f), new PointF(10, 8), new PointF(1, 14.5f) };
                using (var fill = new SolidBrush(Color.FromArgb(0, 168, 0))) g.FillPolygon(fill, tri);
                using (var hl = new SolidBrush(Color.FromArgb(150, 240, 150))) g.FillPolygon(hl, new[] { new PointF(2.5f, 4), new PointF(6, 7), new PointF(2.5f, 7) });
                using (var edge = new Pen(Color.FromArgb(0, 80, 0), 1.2f)) g.DrawPolygon(edge, tri);
            }
            return bmp;
        }

        /// <summary>A pencil (Rename).</summary>
        public static Image Pencil()
        {
            var bmp = New16(out var g);
            using (g)
            {
                var body = new[] { new PointF(3, 10), new PointF(11, 2), new PointF(14, 5), new PointF(6, 13) };
                using (var wood = new SolidBrush(Color.FromArgb(240, 200, 60))) g.FillPolygon(wood, body);
                using (var eraser = new SolidBrush(Color.FromArgb(230, 120, 140))) g.FillPolygon(eraser, new[] { new PointF(11, 2), new PointF(12.5f, 0.5f), new PointF(15.5f, 3.5f), new PointF(14, 5) });
                using (var tip = new SolidBrush(Color.FromArgb(250, 230, 190))) g.FillPolygon(tip, new[] { new PointF(3, 10), new PointF(6, 13), new PointF(1, 15) });
                using (var lead = new SolidBrush(Color.Black)) g.FillPolygon(lead, new[] { new PointF(1.8f, 12.8f), new PointF(3.2f, 14.2f), new PointF(1, 15) });
                using (var edge = new Pen(Color.FromArgb(90, 60, 0), 1f)) g.DrawPolygon(edge, body);
            }
            return bmp;
        }

        /// <summary>A red disc with "!": something is missing.</summary>
        public static Image Bang()
        {
            var bmp = New16(out var g);
            using (g)
            {
                using (var red = new SolidBrush(Color.FromArgb(210, 0, 0))) g.FillEllipse(red, 1, 1, 14, 14);
                using (var rim = new Pen(Color.FromArgb(120, 0, 0))) g.DrawEllipse(rim, 1, 1, 14, 14);
                using (var w = new SolidBrush(Color.White)) { g.FillRectangle(w, 7, 3.5f, 2.2f, 6.5f); g.FillEllipse(w, 6.9f, 11, 2.4f, 2.4f); }
            }
            return bmp;
        }

        /// <summary>A yellow lightning bolt: out of date, it runs again.</summary>
        public static Image Bolt()
        {
            var bmp = New16(out var g);
            using (g)
            {
                var pts = new[] { new PointF(9, 0.5f), new PointF(3, 9), new PointF(7.5f, 9), new PointF(5.5f, 15.5f), new PointF(13, 6), new PointF(8.5f, 6), new PointF(11, 0.5f) };
                using (var y = new SolidBrush(Color.FromArgb(255, 210, 0))) g.FillPolygon(y, pts);
                using (var e = new Pen(Color.FromArgb(150, 100, 0))) g.DrawPolygon(e, pts);
            }
            return bmp;
        }

        /// <summary>A grey ring: set up, not run yet.</summary>
        public static Image Ring()
        {
            var bmp = New16(out var g);
            using (g)
            using (var p = new Pen(Color.FromArgb(128, 128, 128), 2f)) g.DrawEllipse(p, 3, 3, 10, 10);
            return bmp;
        }
    }
}
