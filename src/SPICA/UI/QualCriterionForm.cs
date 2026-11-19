using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Spica.Core;

namespace Spica.UI
{
    /// <summary>Qualification > Pass / Fail > Add or Edit: one rule on one signal, inside a window of each test.</summary>
    public sealed class QualCriterionForm : Win98Form
    {
        readonly Win98TextField txtName, txtLo, txtHi, txtFrom, txtTo, txtTests;
        readonly ComboBox cbSignal, cbRule, cbFrom, cbTo;
        readonly Label lblPreview;
        readonly HashSet<string> taken;

        public QualCriterion Result { get; private set; }

        static readonly string[] Rules = { "stays above (minimum ≥ low)", "stays below (maximum ≤ high)", "stays between low and high" };
        static readonly string[] Anchors = { "test start", "event start (supply leaves its level)", "event end (supply is back)", "test end" };

        public QualCriterionForm(QualCriterion c, IList<string> signals, IEnumerable<string> otherNames)
        {
            taken = new HashSet<string>(otherNames, StringComparer.OrdinalIgnoreCase);
            Text = c == null ? "Add Criterion" : "Edit Criterion";
            MinimizeBox = false; MaximizeBox = false; Resizable = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(470, 318 + Padding.Vertical);
            int x = Padding.Left + 10, y = Padding.Top + 10, fx = x + 96;

            Label L(string t, int yy) { var l = new Label { Text = t, Location = new Point(x, yy + 3), AutoSize = true }; Controls.Add(l); return l; }

            L("&Name:", y);
            txtName = new Win98TextField { Location = new Point(fx, y), Width = 160 };
            L("&Signal:", y + 28);
            cbSignal = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(fx, y + 28), Width = 200, Font = Win98.Font };
            foreach (var s in signals) cbSignal.Items.Add(s);
            Controls.Add(new Label { Text = "e.g. V(pgood), V(out), I(R1)", Location = new Point(fx + 208, y + 31), AutoSize = true, ForeColor = Win98.Shadow });

            L("&Rule:", y + 62);
            cbRule = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(fx, y + 62), Width = 250, Font = Win98.Font };
            cbRule.Items.AddRange(Rules);
            L("&Low:", y + 90);
            txtLo = new Win98TextField { Location = new Point(fx, y + 90), Width = 80 };
            var lblHi = new Label { Text = "&High:", Location = new Point(fx + 100, y + 93), AutoSize = true };
            txtHi = new Win98TextField { Location = new Point(fx + 140, y + 90), Width = 80 };
            Controls.Add(lblHi);

            L("&From:", y + 124);
            cbFrom = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(fx, y + 124), Width = 230, Font = Win98.Font };
            cbFrom.Items.AddRange(Anchors);
            Controls.Add(new Label { Text = "+", Location = new Point(fx + 236, y + 127), AutoSize = true });
            txtFrom = new Win98TextField { Location = new Point(fx + 250, y + 124), Width = 70 };
            Controls.Add(new Label { Text = "s", Location = new Point(fx + 324, y + 127), AutoSize = true });
            L("&To:", y + 152);
            cbTo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(fx, y + 152), Width = 230, Font = Win98.Font };
            cbTo.Items.AddRange(Anchors);
            Controls.Add(new Label { Text = "+", Location = new Point(fx + 236, y + 155), AutoSize = true });
            txtTo = new Win98TextField { Location = new Point(fx + 250, y + 152), Width = 70 };
            Controls.Add(new Label { Text = "s", Location = new Point(fx + 324, y + 155), AutoSize = true });

            L("T&ests:", y + 186);
            txtTests = new Win98TextField { Location = new Point(fx, y + 186), Width = 250 };
            Controls.Add(new Label { Text = "empty = every test; else words in the test names, separated by |  (e.g. interruption | surge)",
                                     Location = new Point(fx, y + 210), Size = new Size(350, 30), ForeColor = Win98.Shadow });

            lblPreview = new Label { Location = new Point(x, y + 244), Size = new Size(450, 30), UseMnemonic = false, Font = Win98.BoldFont };
            Controls.AddRange(new Control[] { txtName, cbSignal, cbRule, txtLo, txtHi, cbFrom, txtFrom, cbTo, txtTo, txtTests, lblPreview });

            int by = ClientSize.Height - Padding.Bottom - 33;
            var ok = new Win98Button { Text = "OK", Location = new Point(ClientSize.Width - Padding.Right - 176, by), Size = new Size(80, 23) };
            var cancel = new Win98Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(ClientSize.Width - Padding.Right - 88, by), Size = new Size(80, 23) };
            ok.Click += (s, e) => { if (Read(out var r, out var err)) { Result = r; DialogResult = DialogResult.OK; } else Win98MessageBox.Show(this, err, "SPICA", MessageBoxButtons.OK, Win98Icon.Warning); };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;

            // a recovery check is the usual start: after the supply is back, to the end
            c = c ?? new QualCriterion { Name = NewName(), From = QualAnchor.EventEnd, FromOffset = 0.1, To = QualAnchor.TestEnd };
            txtName.Text = c.Name;
            cbSignal.Text = c.Signal;
            cbRule.SelectedIndex = !double.IsNaN(c.Lo) && !double.IsNaN(c.Hi) ? 2 : !double.IsNaN(c.Hi) ? 1 : 0;
            txtLo.Text = Eng(c.Lo);
            txtHi.Text = Eng(c.Hi);
            cbFrom.SelectedIndex = (int)c.From;
            cbTo.SelectedIndex = (int)c.To;
            txtFrom.Text = Eng(c.FromOffset == 0 ? 0 : c.FromOffset);
            txtTo.Text = Eng(c.ToOffset == 0 ? 0 : c.ToOffset);
            txtTests.Text = c.Tests;

            void Sync()
            {
                txtLo.Enabled = cbRule.SelectedIndex != 1;
                txtHi.Enabled = cbRule.SelectedIndex != 0;
                lblPreview.Text = Read(out var r, out var err) ? $"{r.RuleText()}\nfrom {r.WindowText()}" : err;
                lblPreview.ForeColor = err == null ? Win98.Text : Color.FromArgb(128, 0, 0);
            }
            foreach (var t in new[] { txtName, txtLo, txtHi, txtFrom, txtTo, txtTests }) t.Box.TextChanged += (s, e) => Sync();
            foreach (var cb in new[] { cbSignal, cbRule, cbFrom, cbTo }) { cb.SelectedIndexChanged += (s, e) => Sync(); cb.TextChanged += (s, e) => Sync(); }
            Sync();
        }

        string NewName()
        {
            for (int i = 1; ; i++) if (!taken.Contains("crit" + i)) return "crit" + i;
        }

        static string Eng(double v) => double.IsNaN(v) ? "" : v == 0 ? "0" : ErrorBudget.Eng(v).Replace("µ", "u");

        static double Num(string s)
        {
            s = (s ?? "").Trim();
            return s.Length > 0 && Expr.TryParse(s, out var e, out _, true) ? e.Eval(new Dictionary<string, double>()) : double.NaN;
        }

        bool Read(out QualCriterion r, out string error)
        {
            r = null; error = null;
            string name = txtName.Text.Trim();
            if (name.Length == 0) { error = "Give the criterion a name."; return false; }
            if (taken.Contains(name)) { error = $"There is already a criterion named {name}."; return false; }
            string sig = cbSignal.Text.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(sig, @"^[VI][a-z]{0,2}\(.+\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            { error = "Signal: a voltage or current as LTspice writes it, e.g. V(pgood) or I(R1)."; return false; }
            double lo = cbRule.SelectedIndex == 1 ? double.NaN : Num(txtLo.Text), hi = cbRule.SelectedIndex == 0 ? double.NaN : Num(txtHi.Text);
            if (cbRule.SelectedIndex != 1 && double.IsNaN(lo)) { error = "Enter the low limit (SPICE suffixes work: 2.5, 500m)."; return false; }
            if (cbRule.SelectedIndex != 0 && double.IsNaN(hi)) { error = "Enter the high limit."; return false; }
            if (!double.IsNaN(lo) && !double.IsNaN(hi) && !(hi > lo)) { error = "The high limit must be above the low one."; return false; }
            double fo = txtFrom.Text.Trim().Length == 0 ? 0 : Num(txtFrom.Text), to = txtTo.Text.Trim().Length == 0 ? 0 : Num(txtTo.Text);
            if (double.IsNaN(fo) || double.IsNaN(to)) { error = "The window offsets are times in seconds (e.g. 100m)."; return false; }
            r = new QualCriterion
            {
                Name = name, Signal = sig, Lo = lo, Hi = hi,
                From = (QualAnchor)Math.Max(0, cbFrom.SelectedIndex), FromOffset = fo,
                To = (QualAnchor)Math.Max(0, cbTo.SelectedIndex), ToOffset = to,
                Tests = txtTests.Text.Trim(),
            };
            return true;
        }
    }
}
