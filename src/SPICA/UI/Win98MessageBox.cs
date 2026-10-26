using System;
using System.Drawing;
using System.Linq;
using System.Media;
using System.Windows.Forms;

namespace Spica.UI
{
    public enum Win98Icon { None, Information, Warning, Error, Question }

    /// <summary>Classic message box drawn with the Win98 frame.</summary>
    public class Win98MessageBox : Win98Form
    {
        Win98MessageBox(string text, string caption, MessageBoxButtons buttons, Win98Icon icon)
            : this(text, caption, Labels(buttons), Results(buttons), icon) { }

        static string[] Labels(MessageBoxButtons b)
        {
            switch (b)
            {
                case MessageBoxButtons.OKCancel: return new[] { "OK", "Cancel" };
                case MessageBoxButtons.YesNo: return new[] { "&Yes", "&No" };
                case MessageBoxButtons.YesNoCancel: return new[] { "&Yes", "&No", "Cancel" };
                default: return new[] { "OK" };
            }
        }

        static DialogResult[] Results(MessageBoxButtons b)
        {
            switch (b)
            {
                case MessageBoxButtons.OKCancel: return new[] { DialogResult.OK, DialogResult.Cancel };
                case MessageBoxButtons.YesNo: return new[] { DialogResult.Yes, DialogResult.No };
                case MessageBoxButtons.YesNoCancel: return new[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel };
                default: return new[] { DialogResult.OK };
            }
        }

        Win98MessageBox(string text, string caption, string[] labels, DialogResult[] results, Win98Icon icon)
        {
            Text = caption;
            Resizable = false;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;

            Image img = null;
            switch (icon)
            {
                case Win98Icon.Information: img = Win98.Icon("info32"); break;
                case Win98Icon.Warning: img = Win98.Icon("warning32"); break;
                case Win98Icon.Error: img = Win98.Icon("error32"); break;
                case Win98Icon.Question: img = Win98.Icon("question32"); break;
            }

            int maxTextW = 480;
            var ts = TextRenderer.MeasureText(text, Font, new Size(maxTextW, 2000), TextFormatFlags.WordBreak);
            int left = Padding.Left + 12;
            int iconW = img != null ? 32 + 14 : 0;

            var pic = new PictureBox { Image = img, Size = new Size(32, 32), Location = new Point(left, Padding.Top + 12), Visible = img != null };
            var lbl = new Label
            {
                Text = text,
                AutoSize = false,
                Size = new Size(ts.Width + 4, ts.Height),
                Location = new Point(left + iconW, Padding.Top + 12 + Math.Max(0, (32 - ts.Height) / 2)),
                UseMnemonic = false,
            };
            Controls.Add(pic);
            Controls.Add(lbl);
            // the label wraps a little differently than MeasureText: take its own wrapped height
            lbl.Height = Math.Max(lbl.Height, lbl.GetPreferredSize(new Size(lbl.Width, 0)).Height);

            // classic 75 px buttons, wider when a label needs it
            var widths = new int[labels.Length];
            for (int i = 0; i < labels.Length; i++)
                widths[i] = Math.Max(75, TextRenderer.MeasureText(labels[i].Replace("&", ""), Font).Width + 20);
            int buttonsW = widths.Sum() + (labels.Length - 1) * 6;
            int contentW = Math.Max(iconW + ts.Width + 4, buttonsW);
            int btnTop = Math.Max(pic.Bottom, lbl.Bottom) + 16;
            int width = contentW + 24 + Padding.Horizontal;
            int bx = (width - buttonsW) / 2;
            for (int i = 0; i < labels.Length; i++)
            {
                var b = new Win98Button { Text = labels[i], DialogResult = results[i], Location = new Point(bx, btnTop), Size = new Size(widths[i], 23) };
                bx += widths[i] + 6;
                Controls.Add(b);
                if (i == 0) AcceptButton = b;
                if (results[i] == DialogResult.Cancel || results[i] == DialogResult.No || labels.Length == 1) CancelButton = b;
            }
            ClientSize = new Size(width, btnTop + 23 + 12 + Padding.Bottom);

            switch (icon)
            {
                case Win98Icon.Error: SystemSounds.Hand.Play(); break;
                case Win98Icon.Warning: SystemSounds.Exclamation.Play(); break;
                case Win98Icon.Question: SystemSounds.Question.Play(); break;
                case Win98Icon.Information: SystemSounds.Asterisk.Play(); break;
            }
        }

        public static DialogResult Show(IWin32Window owner, string text, string caption = "SPICA",
            MessageBoxButtons buttons = MessageBoxButtons.OK, Win98Icon icon = Win98Icon.Information)
        {
            using (var f = new Win98MessageBox(text, caption, buttons, icon))
            {
                if (owner == null) f.StartPosition = FormStartPosition.CenterScreen;
                return f.ShowDialog(owner);
            }
        }

        /// <summary>A message box with custom buttons; returns the index of the button, or -1 when closed.
        /// The last button acts as Cancel (Esc).</summary>
        public static int Choose(IWin32Window owner, string text, string caption, Win98Icon icon, params string[] labels)
        {
            var codes = new[] { DialogResult.Yes, DialogResult.No, DialogResult.Retry, DialogResult.Ignore, DialogResult.Abort, DialogResult.OK };
            var results = new DialogResult[labels.Length];
            for (int i = 0; i < labels.Length; i++) results[i] = i == labels.Length - 1 ? DialogResult.Cancel : codes[i];
            using (var f = new Win98MessageBox(text, caption, labels, results, icon))
            {
                if (owner == null) f.StartPosition = FormStartPosition.CenterScreen;
                var r = f.ShowDialog(owner);
                int idx = Array.IndexOf(results, r);
                return idx;
            }
        }
    }
}
