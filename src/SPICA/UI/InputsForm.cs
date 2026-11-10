using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Spica.Core;

namespace Spica.UI
{
    /// <summary>Sensitivity > Inputs: which random parameters are the circuit's input (its excitation), not tolerances.</summary>
    public sealed class InputsForm : Win98Form
    {
        readonly Win98RadioButton rbAuto, rbChosen;
        readonly List<Win98CheckBox> boxes = new List<Win98CheckBox>();
        readonly List<string> names;

        /// <summary>The setup's list: null = SPICA decides.</summary>
        public List<string> Inputs { get; private set; }

        public InputsForm(ResultSet rs)
        {
            names = rs.Params.Select(Excitation.Label).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var found = Excitation.Columns(rs, out bool auto, out string why);
            var autoSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (auto) foreach (var c in found) autoSet.Add(Excitation.Label(c));
            else
            {
                var probe = rs.Setup.Inputs;
                rs.Setup.Inputs = null;
                foreach (var c in Excitation.Columns(rs, out _, out why)) autoSet.Add(Excitation.Label(c));
                rs.Setup.Inputs = probe;
            }

            Text = "Inputs";
            MinimizeBox = false; MaximizeBox = false; Resizable = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 400 + Padding.Vertical);
            int x = Padding.Left + 10, y = Padding.Top + 8;

            Controls.Add(new Label
            {
                Text = "The circuit's input (e.g. the input voltage sweeping its range) drives the outputs by design. " +
                       "Marked as an input it is set apart: the sensitivity shares what it leaves among the tolerances.",
                Location = new Point(x, y), Size = new Size(400, 44), UseMnemonic = false,
            });
            string autoText = autoSet.Count > 0 ? "Automatic: " + string.Join(", ", autoSet) : "Automatic: none found";
            rbAuto = new Win98RadioButton { Text = autoText, Location = new Point(x, y + 48), Width = 400, AutoEllipsis = true };
            rbChosen = new Win98RadioButton { Text = "These parameters:", Location = new Point(x, y + 92), Width = 400 };
            Controls.Add(rbAuto);
            Controls.Add(new Label { Text = why.Length > 0 ? "(" + why + ")" : "(the reference x of an error or of the transfer function, when it is a parameter or moves with one)",
                                     Location = new Point(x + 20, y + 70), Size = new Size(380, 18), ForeColor = Win98.Shadow, AutoEllipsis = true, UseMnemonic = false });
            Controls.Add(rbChosen);

            var list = new Panel { Location = new Point(x + 20, y + 116), Size = new Size(380, 220), AutoScroll = true, BackColor = Win98.Face };
            Controls.Add(list);
            var chosen = rs.Setup.Inputs ?? autoSet.ToList();
            for (int i = 0; i < names.Count; i++)
            {
                var c = new Win98CheckBox { Text = names[i], Location = new Point(2, 2 + 21 * i), Width = 350, AutoEllipsis = true,
                                            Checked = chosen.Contains(names[i], StringComparer.OrdinalIgnoreCase) };
                boxes.Add(c);
                list.Controls.Add(c);
            }
            if (names.Count == 0) list.Controls.Add(new Label { Text = "No random parameters: run with paired samples.", Location = new Point(2, 2), AutoSize = true, ForeColor = Win98.Shadow });

            int by = ClientSize.Height - Padding.Bottom - 33;
            var ok = new Win98Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(ClientSize.Width - Padding.Right - 176, by), Size = new Size(80, 23) };
            var cancel = new Win98Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(ClientSize.Width - Padding.Right - 88, by), Size = new Size(80, 23) };
            ok.Click += (s, e) => Inputs = rbAuto.Checked ? null : names.Where((n, i) => boxes[i].Checked).ToList();
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;

            rbAuto.CheckedChanged += (s, e) => list.Enabled = !rbAuto.Checked;
            (rs.Setup.Inputs == null ? rbAuto : rbChosen).Checked = true;
            list.Enabled = !rbAuto.Checked;
        }
    }
}
