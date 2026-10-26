using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Spica.UI
{
    /// <summary>Windows 98 "Windows Standard" colour scheme, fonts and bevel painting primitives.</summary>
    static class Win98
    {
        public static readonly Color Face = Color.FromArgb(192, 192, 192);
        public static readonly Color Light = Color.FromArgb(223, 223, 223);
        public static readonly Color Highlight = Color.White;
        public static readonly Color Shadow = Color.FromArgb(128, 128, 128);
        public static readonly Color DarkShadow = Color.Black;
        public static readonly Color Navy = Color.FromArgb(0, 0, 128);
        public static readonly Color TitleActiveA = Color.FromArgb(0, 0, 128);
        public static readonly Color TitleActiveB = Color.FromArgb(16, 132, 208);
        public static readonly Color TitleInactiveA = Color.FromArgb(128, 128, 128);
        public static readonly Color TitleInactiveB = Color.FromArgb(181, 181, 181);
        public static readonly Color Window = Color.White;
        public static readonly Color Text = Color.Black;
        public static readonly Color InfoBack = Color.FromArgb(255, 255, 225);

        public static readonly Font Font = new Font("Microsoft Sans Serif", 8.25f);
        public static readonly Font BoldFont = new Font("Microsoft Sans Serif", 8.25f, FontStyle.Bold);
        public static readonly Font MonoFont = new Font("Lucida Console", 8f);

        // ---------------------------------------------------------------- bevels

        /// <summary>One 1-px ring: top/left in <paramref name="tl"/>, bottom/right in <paramref name="br"/>.</summary>
        public static void Ring(Graphics g, Rectangle r, Color tl, Color br)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var bt = new SolidBrush(tl))
            using (var bb = new SolidBrush(br))
            {
                g.FillRectangle(bt, r.X, r.Y, r.Width - 1, 1);
                g.FillRectangle(bt, r.X, r.Y, 1, r.Height - 1);
                g.FillRectangle(bb, r.X, r.Bottom - 1, r.Width, 1);
                g.FillRectangle(bb, r.Right - 1, r.Y, 1, r.Height);
            }
        }

        static Rectangle Shrink(Rectangle r, int n) => new Rectangle(r.X + n, r.Y + n, r.Width - 2 * n, r.Height - 2 * n);

        /// <summary>Push button, normal state.</summary>
        public static void Raised(Graphics g, Rectangle r)
        {
            Ring(g, r, Highlight, DarkShadow);
            Ring(g, Shrink(r, 1), Light, Shadow);
        }

        /// <summary>Window frame / dropdown frame.</summary>
        public static void RaisedWindow(Graphics g, Rectangle r)
        {
            Ring(g, r, Light, DarkShadow);
            Ring(g, Shrink(r, 1), Highlight, Shadow);
        }

        /// <summary>Push button, pressed.</summary>
        public static void Pressed(Graphics g, Rectangle r)
        {
            Ring(g, r, DarkShadow, Highlight);
            Ring(g, Shrink(r, 1), Shadow, Light);
        }

        /// <summary>Text field / list well.</summary>
        public static void Sunken(Graphics g, Rectangle r)
        {
            Ring(g, r, Shadow, Highlight);
            Ring(g, Shrink(r, 1), DarkShadow, Light);
        }

        public static void ThinRaised(Graphics g, Rectangle r) => Ring(g, r, Highlight, Shadow);
        public static void ThinSunken(Graphics g, Rectangle r) => Ring(g, r, Shadow, Highlight);

        /// <summary>Etched (group box) rectangle.</summary>
        public static void Etched(Graphics g, Rectangle r)
        {
            using (var pw = new Pen(Highlight))
            using (var ps = new Pen(Shadow))
            {
                g.DrawRectangle(pw, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
                g.DrawRectangle(ps, r.X, r.Y, r.Width - 2, r.Height - 2);
            }
        }

        public static void EtchedHLine(Graphics g, int x1, int x2, int y)
        {
            using (var bs = new SolidBrush(Shadow)) g.FillRectangle(bs, x1, y, x2 - x1, 1);
            using (var bh = new SolidBrush(Highlight)) g.FillRectangle(bh, x1, y + 1, x2 - x1, 1);
        }

        public static void EtchedVLine(Graphics g, int x, int y1, int y2)
        {
            using (var bs = new SolidBrush(Shadow)) g.FillRectangle(bs, x, y1, 1, y2 - y1);
            using (var bh = new SolidBrush(Highlight)) g.FillRectangle(bh, x + 1, y1, 1, y2 - y1);
        }

        // ---------------------------------------------------------------- text

        public static void DrawText(Graphics g, string text, Font font, Rectangle r, bool enabled, TextFormatFlags flags, Color? color = null)
        {
            if (enabled)
            {
                TextRenderer.DrawText(g, text, font, r, color ?? Text, flags);
            }
            else
            {
                var r1 = r; r1.Offset(1, 1);
                TextRenderer.DrawText(g, text, font, r1, Highlight, flags);
                TextRenderer.DrawText(g, text, font, r, Shadow, flags);
            }
        }

        // ---------------------------------------------------------------- pixel glyphs

        /// <summary>Draws a pixel pattern ('X' = set) at (x, y).</summary>
        public static void Glyph(Graphics g, string[] rows, int x, int y, Color c)
        {
            using (var b = new SolidBrush(c))
            {
                for (int j = 0; j < rows.Length; j++)
                {
                    string row = rows[j];
                    int i = 0;
                    while (i < row.Length)
                    {
                        if (row[i] != 'X') { i++; continue; }
                        int start = i;
                        while (i < row.Length && row[i] == 'X') i++;
                        g.FillRectangle(b, x + start, y + j, i - start, 1);
                    }
                }
            }
        }

        public static readonly string[] CheckGlyph =
        {
            "......X",
            ".....XX",
            "X...XXX",
            "XX.XXX.",
            "XXXXX..",
            ".XXX...",
            "..X....",
        };

        public static readonly string[] CloseGlyph =
        {
            "XX....XX",
            ".XX..XX.",
            "..XXXX..",
            "...XX...",
            "..XXXX..",
            ".XX..XX.",
            "XX....XX",
        };

        public static readonly string[] MinimizeGlyph =
        {
            "XXXXXX",
            "XXXXXX",
        };

        public static readonly string[] MaximizeGlyph =
        {
            "XXXXXXXXX",
            "XXXXXXXXX",
            "X.......X",
            "X.......X",
            "X.......X",
            "X.......X",
            "X.......X",
            "X.......X",
            "XXXXXXXXX",
        };

        public static readonly string[] RestoreGlyph =
        {
            "..XXXXXX",
            "..XXXXXX",
            "..X....X",
            "XXXXXX.X",
            "XXXXXX.X",
            "X....XXX",
            "X....X..",
            "X....X..",
            "XXXXXX..",
        };

        public static readonly string[] ArrowDownGlyph =
        {
            "XXXXXXX",
            ".XXXXX.",
            "..XXX..",
            "...X...",
        };

        public static readonly string[] ArrowRightGlyph =
        {
            "X...",
            "XX..",
            "XXX.",
            "XXXX",
            "XXX.",
            "XX..",
            "X...",
        };

        // ---------------------------------------------------------------- icons

        static readonly Dictionary<string, Image> iconCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Loads an embedded icon PNG from Resources/Icons (e.g. "run32").</summary>
        public static Image Icon(string name)
        {
            lock (iconCache)
            {
                if (iconCache.TryGetValue(name, out var img)) return img;
                var asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("Spica.Resources.Icons." + name + ".png"))
                {
                    img = s == null ? new Bitmap(16, 16) : new Bitmap(Image.FromStream(s));
                }
                iconCache[name] = img;
                return img;
            }
        }

        public static System.Drawing.Icon AppIcon()
        {
            try { return System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return null; }
        }

        public static void DrawIcon(Graphics g, Image img, int x, int y, bool enabled = true)
        {
            var old = g.InterpolationMode;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            if (enabled)
                g.DrawImage(img, new Rectangle(x, y, img.Width, img.Height));
            else
                ControlPaint.DrawImageDisabled(g, img, x, y, Face);
            g.InterpolationMode = old;
        }
    }
}
