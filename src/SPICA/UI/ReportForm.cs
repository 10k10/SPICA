using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Spica.Core;

namespace Spica.UI
{
    /// <summary>Results > Report: what goes into the HTML report (the whole analysis, or only some errors).</summary>
    public sealed class ReportForm : Win98Form
    {
        readonly Win98RadioButton rbConcise, rbFull;
        readonly List<Win98CheckBox> errBoxes = new List<Win98CheckBox>(), measBoxes = new List<Win98CheckBox>();
        readonly Win98CheckBox chkBudget, chkSummary, chkHist, chkSens, chkConv, chkTemp, chkTf;
        readonly Win98GroupBox grpErr, grpMeas, grpSec;
        readonly Label lblHint;
        readonly Win98Button btnOk;
        readonly Win98TextField txtTitle, txtNotes;

        public ReportOptions Options { get; private set; }

        public ReportForm(ResultSet rs)
        {
            var o = rs.Setup.Report.Clone();
            var errors = rs.Setup.Errors.Select(e => e.Name).ToList();
            Text = "Report";
            MinimizeBox = false;
            MaximizeBox = false;
            Resizable = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 548 + Padding.Vertical);
            int x = Padding.Left + 10, y = Padding.Top + 8;

            string folderName = string.IsNullOrEmpty(rs.Folder) ? "" : System.IO.Path.GetFileName(rs.Folder.TrimEnd('\\', '/'));
            Controls.Add(new Label { Text = "&Title:", Location = new Point(x, y + 3), AutoSize = true });
            txtTitle = new Win98TextField { Location = new Point(x + 44, y), Width = 556, Text = o.Title };
            Controls.Add(txtTitle);
            Controls.Add(new Label { Text = o.Title.Length == 0 && folderName.Length > 0 ? $"Empty: the campaign folder's name ({folderName})." : "The report's heading; the schematic is listed below it.",
                                     Location = new Point(x + 44, y + 22), AutoSize = true, ForeColor = Win98.Shadow, UseMnemonic = false });
            y += 46;

            rbConcise = new Win98RadioButton { Text = "Error budget only: one page per error, for checking a requirement", Location = new Point(x, y), Width = 590 };
            rbFull = new Win98RadioButton { Text = "Full analysis: every measurement and analysis chosen below", Location = new Point(x, y + 22), Width = 590 };
            Controls.Add(rbConcise);
            Controls.Add(rbFull);

            // errors (both kinds), measurements and sections (full only)
            grpErr = new Win98GroupBox { Text = "Errors", Location = new Point(x, y + 52), Size = new Size(196, 290) };
            grpMeas = new Win98GroupBox { Text = "Measurements", Location = new Point(x + 204, y + 52), Size = new Size(196, 290) };
            grpSec = new Win98GroupBox { Text = "Sections", Location = new Point(x + 408, y + 52), Size = new Size(192, 290) };
            Controls.Add(grpErr); Controls.Add(grpMeas); Controls.Add(grpSec);
            FillList(grpErr, errBoxes, errors, o.HasError, errors.Count == 0 ? "No error defined: add one on the Error Budget tab." : null);
            FillList(grpMeas, measBoxes, rs.Meas, o.HasMeas, rs.Meas.Count == 0 ? "No measurements." : null);

            int sy = 22;
            Win98CheckBox Sec(string t, bool on) { var c = new Win98CheckBox { Text = t, Location = new Point(10, sy), Checked = on }; sy += 24; grpSec.Controls.Add(c); return c; }
            chkBudget = Sec("Error budget", o.Budget);
            chkSummary = Sec("Summary tables", o.Summary);
            sy += 8;
            grpSec.Controls.Add(new Label { Text = "For each measurement:", Location = new Point(10, sy), AutoSize = true });
            sy += 20;
            chkHist = Sec("Histogram", o.Histogram);
            chkSens = Sec("Sensitivity", o.Sensitivity);
            chkTemp = Sec("Temperature", o.Temperature);
            chkConv = Sec("Convergence", o.Convergence);
            sy += 8;
            chkTf = Sec("Transfer function", o.Tf);
            chkTemp.Enabled = rs.Mode != TempMode.Default;
            chkTf.Enabled = rs.Setup.Tf.X.Length > 0 && rs.Setup.Tf.Y.Length > 0;

            Controls.Add(new Label { Text = "&Notes (free text, at the top of the report):", Location = new Point(x, y + 350), AutoSize = true });
            txtNotes = new Win98TextField { Location = new Point(x, y + 368), Width = 600, Text = o.Notes ?? "" };
            txtNotes.Box.Multiline = true;
            txtNotes.Box.AcceptsReturn = true;
            txtNotes.Box.ScrollBars = ScrollBars.Vertical;
            txtNotes.Height = 64;
            Controls.Add(txtNotes);

            lblHint = new Label { Location = new Point(x, y + 440), Size = new Size(410, 32), ForeColor = Win98.Shadow, UseMnemonic = false };
            Controls.Add(lblHint);

            int by = ClientSize.Height - Padding.Bottom - 33;
            btnOk = new Win98Button { Text = "&Save...", DialogResult = DialogResult.OK, Location = new Point(ClientSize.Width - Padding.Right - 176, by), Size = new Size(80, 23) };
            var btnCancel = new Win98Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(ClientSize.Width - Padding.Right - 88, by), Size = new Size(80, 23) };
            btnOk.Click += (s, e) => Options = Read(o, errors, rs.Meas);
            Controls.Add(btnOk); Controls.Add(btnCancel);
            AcceptButton = btnOk; CancelButton = btnCancel;

            rbConcise.CheckedChanged += (s, e) => UpdateState();
            foreach (var c in errBoxes) c.CheckedChanged += (s, e) => UpdateState();
            (o.Kind == ReportKind.Concise && errors.Count > 0 ? rbConcise : rbFull).Checked = true;
            rbConcise.Enabled = errors.Count > 0;
            UpdateState();
        }

        void FillList(Win98GroupBox grp, List<Win98CheckBox> boxes, List<string> names, Func<string, bool> on, string empty)
        {
            var list = new Panel { Location = new Point(8, 20), Size = new Size(grp.Width - 16, grp.Height - 56), AutoScroll = true, BackColor = Win98.Face };
            grp.Controls.Add(list);
            if (empty != null) list.Controls.Add(new Label { Text = empty, Location = new Point(2, 2), Size = new Size(list.Width - 6, 48), ForeColor = Win98.Shadow });
            for (int i = 0; i < names.Count; i++)
            {
                var c = new Win98CheckBox { Text = names[i], Location = new Point(2, 2 + 21 * i), Checked = on(names[i]), AutoEllipsis = true };
                boxes.Add(c);
                list.Controls.Add(c);
            }
            var all = new Win98Button { Text = "All", Location = new Point(8, grp.Height - 32), Size = new Size(56, 22) };
            var none = new Win98Button { Text = "None", Location = new Point(70, grp.Height - 32), Size = new Size(56, 22) };
            all.Click += (s, e) => boxes.ForEach(c => c.Checked = true);
            none.Click += (s, e) => boxes.ForEach(c => c.Checked = false);
            all.Enabled = none.Enabled = names.Count > 0;
            grp.Controls.Add(all); grp.Controls.Add(none);
        }

        void UpdateState()
        {
            bool concise = rbConcise.Checked;
            grpMeas.Enabled = grpSec.Enabled = !concise;
            int n = errBoxes.Count(c => c.Checked);
            lblHint.Text = concise
                ? "Per error: the requirement as entered, verdict, mean, sigma, shape,\nhistogram, error vs input and temperature, drivers, every condition."
                : "The errors ticked go in the Error budget section; untick that\nsection to leave them all out.";
            btnOk.Enabled = !concise || n > 0;
        }

        ReportOptions Read(ReportOptions o, List<string> errors, List<string> meas)
        {
            o.Kind = rbConcise.Checked ? ReportKind.Concise : ReportKind.Full;
            o.Title = txtTitle.Text.Trim();
            o.Notes = txtNotes.Text.TrimEnd();
            o.ErrorsOff = errors.Where((e, i) => !errBoxes[i].Checked).ToList();
            o.MeasOff = meas.Where((m, i) => !measBoxes[i].Checked).ToList();
            o.Budget = chkBudget.Checked; o.Summary = chkSummary.Checked; o.Histogram = chkHist.Checked;
            o.Sensitivity = chkSens.Checked; o.Convergence = chkConv.Checked; o.Temperature = chkTemp.Checked; o.Tf = chkTf.Checked;
            return o;
        }
    }
}
