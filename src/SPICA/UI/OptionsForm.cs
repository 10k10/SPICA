using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Spica.Core;

namespace Spica.UI
{
    /// <summary>Tools > Options: what belongs to this PC and not to a campaign (where LTspice is, how many instances run).</summary>
    public sealed class OptionsForm : Win98Form
    {
        readonly AppSettings settings;
        readonly HardwareInfo hw;
        readonly Win98TextField txtLtspice, txtInstances, txtThreads;
        readonly Win98RadioButton rbAuto, rbManual;
        readonly Label lblVersion;

        public OptionsForm(AppSettings settings, HardwareInfo hw)
        {
            this.settings = settings;
            this.hw = hw;
            Text = "Options";
            MinimizeBox = MaximizeBox = false;
            Resizable = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            CaptionIcon = Win98.Icon("options16");
            const int W = 540;
            ClientSize = new Size(W + 20 + Padding.Horizontal, 270 + Padding.Vertical);
            int x = Padding.Left + 10, y = Padding.Top + 10;

            var grpLt = new Win98GroupBox { Text = "LTspice", Location = new Point(x, y), Size = new Size(W, 74) };
            txtLtspice = new Win98TextField { Location = new Point(70, 20), Width = W - 70 - 10 - 81 };
            var btnLt = new Win98Button { Text = "&Browse...", Location = new Point(W - 10 - 75, 18) };
            btnLt.Click += (s, e) => BrowseLtspice();
            lblVersion = new Label { Location = new Point(70, 46), Size = new Size(W - 80, 16), ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false };
            grpLt.Controls.AddRange(new Control[] { Lbl("Program:", 10, 20), txtLtspice, btnLt, lblVersion });
            txtLtspice.Box.TextChanged += (s, e) => ShowVersion();

            var grpPar = new Win98GroupBox { Text = "Parallel instances", Location = new Point(x, y + 82), Size = new Size(W, 132) };
            rbAuto = new Win98RadioButton { Text = "&Automatic: one LTspice per processor core, in chunks of about 2 minutes (recommended)", Location = new Point(10, 20) };
            rbManual = new Win98RadioButton { Text = "&Manual:", Location = new Point(10, 46) };
            txtInstances = new Win98TextField { Location = new Point(84, 44), Width = 40 };
            txtThreads = new Win98TextField { Location = new Point(204, 44), Width = 40 };
            foreach (var t in new[] { txtInstances, txtThreads })
                t.Box.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            rbAuto.CheckedChanged += (s, e) => { txtInstances.Enabled = txtThreads.Enabled = rbManual.Checked; };
            var lblHw = new Label
            {
                Location = new Point(28, 74), Size = new Size(W - 40, 32), ForeColor = Win98.Shadow, BackColor = Win98.Face,
                Text = $"This PC: {hw.PhysicalCores} cores / {hw.LogicalProcessors} threads" + (hw.IsHybrid ? $" ({hw.PerformanceCores}P+{hw.EfficiencyCores}E)" : "") +
                       $", {hw.TotalMemory / (double)(1L << 30):0.0} GB RAM.\nAutomatic uses fewer instances when free RAM is short.",
            };
            var lblWhen = new Label
            {
                Location = new Point(28, 108), Size = new Size(W - 40, 16), ForeColor = Win98.Shadow, BackColor = Win98.Face,
                Text = "Used by the campaigns added to the queue from now on; those already there keep theirs.",
            };
            grpPar.Controls.AddRange(new Control[] { rbAuto, rbManual, txtInstances, Lbl("instances  x", 130, 44), txtThreads, Lbl("threads each", 250, 44), lblHw, lblWhen });

            int by = ClientSize.Height - Padding.Bottom - 33;
            var btnOk = new Win98Button { Text = "OK", Location = new Point(ClientSize.Width - Padding.Right - 10 - 75 - 6 - 75, by) };
            btnOk.Click += (s, e) => Accept();
            var btnCancel = new Win98Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(ClientSize.Width - Padding.Right - 10 - 75, by) };
            Controls.AddRange(new Control[] { grpLt, grpPar, btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            txtLtspice.Text = settings.LtspicePath;
            rbAuto.Checked = settings.AutoParallel;
            rbManual.Checked = !settings.AutoParallel;
            txtInstances.Text = (settings.ManualInstances > 0 ? settings.ManualInstances : hw.PhysicalCores).ToString(CultureInfo.InvariantCulture);
            txtThreads.Text = Math.Max(1, settings.ManualThreads).ToString(CultureInfo.InvariantCulture);
            txtInstances.Enabled = txtThreads.Enabled = rbManual.Checked;
            ShowVersion();
        }

        static Label Lbl(string text, int x, int y) => new Label { Text = text, Location = new Point(x, y + 3), AutoSize = true, BackColor = Win98.Face };

        void ShowVersion()
        {
            string lt = txtLtspice.Text.Trim();
            bool ok = lt.Length > 0 && File.Exists(lt);
            lblVersion.ForeColor = ok ? Win98.Shadow : Color.FromArgb(160, 0, 0);
            string v = "";
            if (ok) { try { v = Ltspice.Version(lt); } catch { v = ""; } }
            lblVersion.Text = ok ? "Found: LTspice " + v : lt.Length == 0 ? "LTspice was not found in the usual places: point SPICA at LTspice.exe." : "This file does not exist.";
        }

        void BrowseLtspice()
        {
            using (var d = new OpenFileDialog { Title = "Locate LTspice", Filter = "LTspice (LTspice.exe;XVIIx64.exe)|LTspice.exe;XVIIx64.exe|Programs (*.exe)|*.exe", AutoUpgradeEnabled = false })
            {
                string cur = txtLtspice.Text.Trim();
                try { if (cur.Length > 0 && Directory.Exists(Path.GetDirectoryName(cur))) d.InitialDirectory = Path.GetDirectoryName(cur); } catch { }
                if (d.ShowDialog(this) == DialogResult.OK) txtLtspice.Text = d.FileName;
            }
        }

        void Accept()
        {
            string lt = txtLtspice.Text.Trim();
            if (lt.Length > 0 && !File.Exists(lt))
            {
                Win98MessageBox.Show(this, "LTspice.exe was not found at:\n" + lt, "Options", MessageBoxButtons.OK, Win98Icon.Warning);
                return;
            }
            int inst = 0, thr = 1;
            if (rbManual.Checked && (!int.TryParse(txtInstances.Text, out inst) || inst < 1 || inst > 256 || !int.TryParse(txtThreads.Text, out thr) || thr < 1 || thr > 256))
            {
                Win98MessageBox.Show(this, "Enter the number of instances and threads per instance (1 or more).", "Options", MessageBoxButtons.OK, Win98Icon.Warning);
                return;
            }
            settings.LtspicePath = lt;
            settings.AutoParallel = rbAuto.Checked;
            if (rbManual.Checked) { settings.ManualInstances = inst; settings.ManualThreads = thr; }
            settings.Save();
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
