using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Spica.Core;

namespace Spica.UI
{
    /// <summary>Tools > Op-amp Models: the parts SPICA models, their tolerances, and how to use them.</summary>
    public sealed class OpAmpForm : Win98Form
    {
        readonly ComboBox cbPart;
        readonly Win98ListView lv;
        readonly Label lblInfo, lblUse;
        readonly string folder;

        public OpAmpForm(string schematicFolder)
        {
            folder = schematicFolder;
            Text = "Op-amp Models";
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(860, 560 + Padding.Vertical);
            int x = Padding.Left + 10, y = Padding.Top + 10;

            Controls.Add(new Label { Text = "Part:", Location = new Point(x, y + 3), AutoSize = true });
            cbPart = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x + 40, y), Width = 420, Font = Win98.Font };
            foreach (var p in OpAmps.Parts) cbPart.Items.Add($"{p.Model}  -  {p.Title}");
            cbPart.SelectedIndexChanged += (s, e) => ShowPart();
            Controls.Add(cbPart);

            lblInfo = new Label { Location = new Point(x, y + 30), Size = new Size(830, 32), UseMnemonic = false };
            Controls.Add(lblInfo);

            lv = new Win98ListView { Location = new Point(x, y + 64), Size = new Size(836, 352), View = View.Details, FullRowSelect = true };
            foreach (var (t, w) in new[] { ("Parameter", 70), ("SPICA uses", 150), ("Basis", 175), ("Datasheet", 420) })
                lv.Columns.Add(t, w);
            lv.CellColor = (i, c) => c == 1 && i < lv.Items.Count && lv.Items[i].Tag is OpParam q && q.Varies ? Color.FromArgb(0, 0, 160) : (Color?)null;
            Controls.Add(lv);

            lblUse = new Label { Location = new Point(x, y + 426), Size = new Size(836, 100), UseMnemonic = false };
            Controls.Add(lblUse);

            int by = ClientSize.Height - Padding.Bottom - 33;
            var btnSave = new Win98Button { Text = "&Save Library...", Location = new Point(x, by), Size = new Size(110, 23) };
            btnSave.Click += (s, e) => SaveLibrary();
            var btnClose = new Win98Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(ClientSize.Width - Padding.Right - 85, by), Size = new Size(75, 23) };
            Controls.Add(btnSave);
            Controls.Add(btnClose);
            CancelButton = btnClose;

            cbPart.SelectedIndex = 0;
        }

        void ShowPart()
        {
            var p = OpAmps.Parts[cbPart.SelectedIndex];
            lblInfo.Text = $"{p.Source}: {p.Conditions}.\nModel {p.Core}, pins {p.Pins}. Blue values vary from unit to unit; the others are fixed.";
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var q in p.Params)
            {
                var it = new ListViewItem(new[] { q.Name, q.RangeText(), OpParam.BasisText(q.Basis), q.Note ?? "" }) { Tag = q };
                lv.Items.Add(it);
            }
            lv.EndUpdate();
            var vary = p.Params.Where(q => q.Varies).Select(q => q.Name).ToList();
            lblUse.Text =
                $"Use it: place {p.Symbol} and set its value to {p.Model}. SPICA adds the tolerances when it runs\n" +
                $"the campaign ({string.Join(", ", vary)}), each one paired per unit and named in the analyses (\"U1.Vos\").\n\n" +
                "Change a tolerance on the symbol (right-click > SpiceLine), e.g. Vos={gauss(277u)} for a normal with 3 sigma = 830 uV,\n" +
                "or Vos=0 to hold it. VsRef=<nominal supply> turns on the supply sensitivity (Kps) for supply tolerances.\n" +
                "To run the schematic in LTspice too, save the library next to it and add the directive .lib " + OpAmps.LibraryFile + ".\n" +
                "Limits are the 25 C ones; temperature acts through TcVos (offset drift) and IbT (bias current doubling).";
        }

        void SaveLibrary()
        {
            using (var d = new SaveFileDialog { Title = "Save op-amp library", FileName = OpAmps.LibraryFile, Filter = "LTspice library (*.lib)|*.lib", AutoUpgradeEnabled = false })
            {
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) d.InitialDirectory = folder;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(d.FileName, OpAmps.LibraryText(), new UTF8Encoding(false));
                    Win98MessageBox.Show(this, $"Saved {Path.GetFileName(d.FileName)}.\nAdd the directive .lib {Path.GetFileName(d.FileName)} to the schematic to run it in LTspice.", "Op-amp Models");
                }
                catch (Exception ex) { Win98MessageBox.Show(this, ex.Message, "Op-amp Models", MessageBoxButtons.OK, Win98Icon.Error); }
            }
        }
    }
}
