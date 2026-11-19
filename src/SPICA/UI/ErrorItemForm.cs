using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Spica.Core;

namespace Spica.UI
{
    /// <summary>
    /// Workbench > Error Budget > New / Edit: what one error is and its boundaries, as the Error Budget page defines it,
    /// with the measurements of the schematic (its .meas, before any campaign has run).
    /// </summary>
    public sealed class ErrorItemForm : Win98Form
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        readonly Win98TextField txtName, txtGain, txtOff, txtTf, txtSym, txtLo, txtHi, txtFs;
        readonly Win98RadioButton rbMeas, rbCmp, rbLin, rbExpr;
        readonly ComboBox cbY, cbX, cbUnit;
        readonly Win98CheckBox chkRefer, chkSym;
        readonly Label lblY, lblX, lblFormula, lblError;
        readonly Panel pnlIdeal;
        readonly HashSet<string> taken;

        public ErrorItem Result { get; private set; }

        public ErrorItemForm(ErrorItem it, IList<string> measurements, IEnumerable<string> otherNames)
        {
            taken = new HashSet<string>(otherNames, StringComparer.OrdinalIgnoreCase);
            Text = it == null ? "New Error" : "Edit Error";
            MinimizeBox = false; MaximizeBox = false; Resizable = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(470, 470 + Padding.Vertical);
            int x = Padding.Left + 12, y = Padding.Top + 10, fx = x + 82, fw = 250;
            Label L(string t, int lx, int ly, Font f = null) { var l = new Label { Text = t, Location = new Point(lx, ly + 3), AutoSize = true, Font = f ?? Win98.Font }; Controls.Add(l); return l; }
            Win98TextField F(int fxx, int fy, int w) { var t = new Win98TextField { Location = new Point(fxx, fy), Width = w }; Controls.Add(t); return t; }

            L("&Name:", x, y);
            txtName = F(fx, y, fw);
            y += 30;
            rbMeas = new Win98RadioButton { Text = "The error is already a .meas", Location = new Point(x, y), Width = 400, Checked = true };
            rbCmp = new Win98RadioButton { Text = "Compare an output with a reference (error = output - ideal)", Location = new Point(x, y + 21), Width = 420 };
            Controls.Add(rbMeas); Controls.Add(rbCmp);
            y += 50;
            lblY = L("Error:", x, y);
            cbY = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(fx, y), Width = fw, Font = Win98.Font };
            cbY.Items.AddRange(measurements.Cast<object>().ToArray());
            Controls.Add(cbY);
            y += 28;
            lblX = L("Reference:", x, y);
            cbX = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(fx, y), Width = fw, Font = Win98.Font };
            cbX.Items.Add("(none)");
            cbX.Items.AddRange(measurements.Cast<object>().ToArray());
            Controls.Add(cbX);
            y += 30;

            pnlIdeal = new Panel { Location = new Point(x - 4, y), Size = new Size(440, 98), BackColor = Win98.Face };
            rbLin = new Win98RadioButton { Text = "Ideal = gain", Location = new Point(4, 2), Width = 86, Checked = true };
            txtGain = new Win98TextField { Location = new Point(92, 0), Width = 60 };
            var lblOff = new Label { Text = "x ref +", Location = new Point(158, 3), AutoSize = true };
            txtOff = new Win98TextField { Location = new Point(204, 0), Width = 80 };
            rbExpr = new Win98RadioButton { Text = "Ideal y =", Location = new Point(4, 26), Width = 86 };
            txtTf = new Win98TextField { Location = new Point(92, 24), Width = 250 };
            chkRefer = new Win98CheckBox { Text = "Refer to the input (divide by the gain)", Location = new Point(4, 50), Width = 300, Checked = true };
            lblFormula = new Label { Location = new Point(4, 76), Size = new Size(430, 18), ForeColor = Win98Chart.Navy, UseMnemonic = false, AutoEllipsis = true };
            pnlIdeal.Controls.AddRange(new Control[] { rbLin, txtGain, lblOff, txtOff, rbExpr, txtTf, chkRefer, lblFormula });
            Controls.Add(pnlIdeal);
            y += 104;

            L("Boundaries", x, y, Win98.BoldFont);
            y += 22;
            chkSym = new Win98CheckBox { Text = "Symmetric   ±", Location = new Point(x, y), Width = 104, Checked = true };
            Controls.Add(chkSym);
            txtSym = F(x + 108, y, 80);
            y += 28;
            L("Lower:", x, y); txtLo = F(x + 48, y, 80);
            L("Upper:", x + 150, y); txtHi = F(x + 196, y, 80);
            y += 28;
            L("Unit:", x, y);
            cbUnit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x + 48, y), Width = 130, Font = Win98.Font };
            cbUnit.Items.AddRange(new object[] { "absolute", "% of reading", "% of full scale" });
            cbUnit.SelectedIndex = 0;
            Controls.Add(cbUnit);
            L("FS:", x + 196, y); txtFs = F(x + 222, y, 60);
            y += 32;
            Controls.Add(new Label
            {
                Location = new Point(x, y), Size = new Size(440, 46), ForeColor = Win98.Shadow,
                Text = "Output vs reference: run by run, error = output - ideal. The reference is a measurement (e.g. vin, or a .meas " +
                       "holding the expected value: gain 1) or a number. Leave a boundary empty for a one-sided limit.",
            });
            lblError = new Label { Location = new Point(x, y + 48), Size = new Size(440, 18), ForeColor = Color.FromArgb(150, 0, 0), UseMnemonic = false };
            Controls.Add(lblError);

            var tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400 };
            tip.SetToolTip(cbX, "Output vs reference: the measurement the ideal output is computed from (e.g. vin), or a fixed number.\n" +
                                "A .meas error with a % of reading boundary: the measurement the % is taken of.\n" +
                                "A .meas error otherwise: the input it is swept over (optional), so the error is judged across it, not pooled.");
            tip.SetToolTip(txtTf, "Any expression of x (the reference) and T (temperature in °C), e.g. 2*x + 10m, or 2*x*(1 + 50u*(T-25)).");
            tip.SetToolTip(chkRefer, "Divide the error by the ideal gain (d ideal / dx): the error as seen at the input.");
            tip.SetToolTip(chkSym, "Ticked: one value, +/- around zero. Clear it to enter a lower and an upper boundary.");

            int by = ClientSize.Height - Padding.Bottom - 33;
            var ok = new Win98Button { Text = "OK", Location = new Point(ClientSize.Width - Padding.Right - 176, by), Size = new Size(80, 23) };
            var cancel = new Win98Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(ClientSize.Width - Padding.Right - 88, by), Size = new Size(80, 23) };
            ok.Click += (s, e) =>
            {
                string why = Check(Read());
                if (why != null) { lblError.Text = why; return; }
                Result = Read();
                DialogResult = DialogResult.OK;
            };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;

            LoadItem(it ?? new ErrorItem());
            foreach (var rb in new[] { rbMeas, rbCmp, rbLin, rbExpr }) rb.CheckedChanged += (s, e) => Sync();
            foreach (var t in new[] { txtGain, txtOff, txtTf, txtSym, txtLo, txtHi, txtFs, txtName }) t.Box.TextChanged += (s, e) => Sync();
            chkRefer.CheckedChanged += (s, e) => Sync();
            chkSym.CheckedChanged += (s, e) => { CarrySymmetric(); Sync(); };
            cbUnit.SelectedIndexChanged += (s, e) => Sync();
            cbY.TextChanged += (s, e) => Sync();
            cbX.TextChanged += (s, e) => Sync();
            Sync();
        }

        static double Num(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return double.NaN;
            return Expr.TryParse(s, out var e, out _, true) ? e.Eval(new Dictionary<string, double>()) : double.NaN;
        }

        void LoadItem(ErrorItem it)
        {
            txtName.Text = it.Name;
            cbY.Text = it.Y;
            cbX.Text = it.X.Length == 0 ? "(none)" : it.X;
            rbCmp.Checked = it.UsesTf; rbMeas.Checked = !it.UsesTf;
            rbLin.Checked = it.Ideal == IdealKind.Linear; rbExpr.Checked = it.Ideal == IdealKind.Expression;
            txtGain.Text = it.Gain.ToString("G9", Inv);
            txtOff.Text = it.Offset.ToString("G9", Inv);
            txtTf.Text = it.Ideal == IdealKind.Expression ? it.Tf : "";
            chkRefer.Checked = it.ReferToInput;
            chkSym.Checked = it.Symmetric;
            txtSym.Text = it.Symmetric && !double.IsNaN(it.Upper) ? it.Upper.ToString("G9", Inv) : "";
            txtLo.Text = double.IsNaN(it.Lower) ? "" : it.Lower.ToString("G9", Inv);
            txtHi.Text = double.IsNaN(it.Upper) ? "" : it.Upper.ToString("G9", Inv);
            cbUnit.SelectedIndex = (int)it.Unit;
            txtFs.Text = double.IsNaN(it.FullScale) ? "" : it.FullScale.ToString("G9", Inv);
        }

        ErrorItem Read()
        {
            string x = cbX.Text.Trim();
            var it = new ErrorItem
            {
                Name = txtName.Text.Trim(), Y = cbY.Text.Trim(), X = x == "(none)" ? "" : x,
                ReferToInput = chkRefer.Checked, Unit = (BudgetUnit)Math.Max(0, cbUnit.SelectedIndex),
                FullScale = Num(txtFs.Text), Symmetric = chkSym.Checked,
            };
            if (rbCmp.Checked)
            {
                if (rbLin.Checked)
                {
                    double g = Num(txtGain.Text), o = Num(txtOff.Text);
                    it.Ideal = IdealKind.Linear;
                    it.Gain = double.IsNaN(g) ? 1 : g;
                    it.Offset = double.IsNaN(o) ? 0 : o;
                    it.Tf = ErrorItem.LinearTf(it.Gain, it.Offset);
                }
                else { it.Ideal = IdealKind.Expression; it.Tf = txtTf.Text.Trim(); }
            }
            else it.Tf = "";
            if (it.Symmetric) { double b = Math.Abs(Num(txtSym.Text)); it.Lower = -b; it.Upper = b; }
            else { it.Lower = Num(txtLo.Text); it.Upper = Num(txtHi.Text); }
            if (it.Name.Length == 0) it.Name = it.Y;
            return it;
        }

        string Check(ErrorItem it)
        {
            if (it.Name.Length == 0) return "Give the error a name (or choose its measurement).";
            if (taken.Contains(it.Name)) return $"There is already an error named {it.Name}.";
            if (it.Y.Length == 0) return rbCmp.Checked ? "Choose the output measurement." : "Choose the .meas that is the error.";
            if (rbCmp.Checked && !it.UsesTf) return "Enter the ideal output: a gain (and offset), or an expression of x.";
            if (rbCmp.Checked && it.X.Length == 0 && it.Ideal == IdealKind.Linear) return "Choose the reference (a measurement or a number).";
            if (double.IsNaN(it.Lower) && double.IsNaN(it.Upper)) return "Enter at least one boundary.";
            if (it.Unit == BudgetUnit.PctFs && !(it.FullScale > 0)) return "Enter the full scale for a % of full scale boundary.";
            return null;
        }

        void CarrySymmetric()
        {
            if (chkSym.Checked)
            {
                double lo = Num(txtLo.Text), hi = Num(txtHi.Text);
                double b = Math.Max(double.IsNaN(lo) ? 0 : Math.Abs(lo), double.IsNaN(hi) ? 0 : Math.Abs(hi));
                if (double.IsNaN(Num(txtSym.Text)) && b > 0) txtSym.Text = b.ToString("G9", Inv);
            }
            else
            {
                double b = Num(txtSym.Text);
                if (!double.IsNaN(b) && txtLo.Text.Trim().Length == 0 && txtHi.Text.Trim().Length == 0)
                {
                    txtLo.Text = (-Math.Abs(b)).ToString("G9", Inv);
                    txtHi.Text = Math.Abs(b).ToString("G9", Inv);
                }
            }
        }

        void Sync()
        {
            bool cmp = rbCmp.Checked, pctRdg = cbUnit.SelectedIndex == 1;
            lblY.Text = cmp ? "Output:" : "Error:";
            lblX.Text = cmp ? "Reference:" : pctRdg ? "Reading:" : "Input:";
            pnlIdeal.Enabled = cmp;
            txtGain.Enabled = txtOff.Enabled = cmp && rbLin.Checked;
            txtTf.Enabled = cmp && rbExpr.Checked;
            txtSym.Enabled = chkSym.Checked;
            txtLo.Enabled = txtHi.Enabled = !chkSym.Checked;
            txtFs.Enabled = cbUnit.SelectedIndex == 2;
            var it = Read();
            lblFormula.Text = cmp && it.UsesTf ? "error = " + it.Definition() : "";
            lblError.Text = "";
        }
    }
}
