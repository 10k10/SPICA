using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Spica.Core;

namespace Spica.UI
{
    /// <summary>
    /// The listener: SPICA watches a folder and runs every campaign put there, one after another, writing the results
    /// to another folder. This window chooses the two folders and shows what ran and what did not.
    /// </summary>
    public sealed class ListenerForm : Win98Form
    {
        readonly Listener listener;
        readonly AppSettings settings;
        readonly Action<ListenerEntry> openResults;
        readonly Win98TextField txtIn, txtOut;
        readonly Win98Button btnIn, btnOut, btnToggle, btnOpen, btnAgain, btnInFolder, btnOutFolder;
        readonly Label lblState, lblDetail;
        readonly Win98ListView lv;
        readonly Timer timer = new Timer { Interval = 1000 };
        bool outEdited, settingOut;

        static readonly Color Good = Color.FromArgb(0, 110, 0), Bad = Color.FromArgb(170, 0, 0);

        public ListenerForm(Listener listener, AppSettings settings, Action<ListenerEntry> openResults)
        {
            this.listener = listener;
            this.settings = settings;
            this.openResults = openResults;
            Text = "Listener";
            MaximizeBox = false;
            Resizable = false;
            StartPosition = FormStartPosition.CenterParent;
            CaptionIcon = Win98.Icon("listener16");
            const int W = 800;
            ClientSize = new Size(W + 20 + Padding.Horizontal, 520 + Padding.Vertical);
            int x = Padding.Left + 10, y = Padding.Top + 10;

            Controls.Add(new PictureBox { Image = Win98.Icon("listener32"), Location = new Point(x, y + 2), Size = new Size(32, 32), BackColor = Win98.Face });
            Controls.Add(new Label
            {
                Location = new Point(x + 44, y), Size = new Size(W - 44, 44), BackColor = Win98.Face, UseMnemonic = false,
                Text = "While it listens, SPICA runs every campaign that appears in the watched folder, one after another, and writes the results to the results folder.\n" +
                       "A campaign is a setup (.spica) with its schematic beside it, or a bundle (.zip) sent from another PC with Run Remote.\n" +
                       "Each file runs once; to run it again, change it or use Run Again.",
            });
            y += 54;

            var grp = new Win98GroupBox { Text = "Folders", Location = new Point(x, y), Size = new Size(W, 110) };
            txtIn = new Win98TextField { Location = new Point(100, 20), Width = W - 100 - 10 - 81 };
            btnIn = new Win98Button { Text = "&Browse...", Location = new Point(W - 10 - 75, 18) };
            txtOut = new Win98TextField { Location = new Point(100, 48), Width = W - 100 - 10 - 81 };
            btnOut = new Win98Button { Text = "B&rowse...", Location = new Point(W - 10 - 75, 46) };
            btnIn.Click += (s, e) => Browse(txtIn, "Folder to watch for campaigns (.spica with its schematic, or .zip bundles):");
            btnOut.Click += (s, e) => Browse(txtOut, "Folder where the results go (one sub-folder per campaign):");
            btnToggle = new Win98Button { Text = "&Start Listening", Size = new Size(120, 23), Location = new Point(W - 10 - 120, 78) };
            btnToggle.Click += (s, e) => Toggle();
            lblState = new Label { Location = new Point(10, 76), Size = new Size(W - 150, 15), Font = Win98.BoldFont, BackColor = Win98.Face, UseMnemonic = false };
            lblDetail = new Label { Location = new Point(10, 91), Size = new Size(W - 150, 15), ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false, AutoEllipsis = true };
            grp.Controls.AddRange(new Control[] { Lbl("Watch folder:", 10, 20), txtIn, btnIn, Lbl("Results folder:", 10, 48), txtOut, btnOut, lblState, lblDetail, btnToggle });
            Controls.Add(grp);
            y += 118;

            Controls.Add(new Label { Text = "Campaigns found in the watched folder:", Location = new Point(x, y), AutoSize = true, BackColor = Win98.Face });
            y += 18;
            lv = new Win98ListView();
            foreach (var (t, w) in new[] { ("File", 200), ("Status", 220), ("Received", 96), ("Ended", 96), ("Results", 180) })
                lv.Columns.Add(t, w);
            lv.CellColor = (i, c) =>
            {
                if (c != 1 || i < 0 || i >= lv.Items.Count || !(lv.Items[i].Tag is ListenerEntry en)) return null;
                switch (en.Status)
                {
                    case ListenerStatus.Running: return Win98.Navy;
                    case ListenerStatus.Done: return Good;
                    case ListenerStatus.Failed: return Bad;
                    default: return null;
                }
            };
            lv.CellTip = (i, c) => i >= 0 && i < lv.Items.Count && c < lv.Items[i].SubItems.Count ? lv.Items[i].SubItems[c].Text : null;
            lv.MultiSelect = false;
            lv.SelectedIndexChanged += (s, e) => UpdateButtons();
            lv.DoubleClick += (s, e) => OpenSelected();
            int listH = ClientSize.Height - Padding.Bottom - 43 - y;
            Controls.Add(new Win98Well(lv) { Location = new Point(x, y), Size = new Size(W, listH) });

            int by = ClientSize.Height - Padding.Bottom - 33;
            btnOpen = new Win98Button { Text = "&Open Results", Size = new Size(100, 23), Location = new Point(x, by) };
            btnAgain = new Win98Button { Text = "Run &Again", Size = new Size(84, 23), Location = new Point(x + 106, by) };
            btnInFolder = new Win98Button { Text = "Open &Watch Folder", Size = new Size(120, 23), Location = new Point(x + 210, by) };
            btnOutFolder = new Win98Button { Text = "Open Results &Folder", Size = new Size(126, 23), Location = new Point(x + 336, by) };
            var btnClose = new Win98Button { Text = "Close", Location = new Point(x + W - 75, by) };
            btnOpen.Click += (s, e) => OpenSelected();
            btnAgain.Click += (s, e) => { if (Selected() is ListenerEntry en) { listener.RunAgain(en); Refill(); } };
            btnInFolder.Click += (s, e) => OpenFolder(listener.Active ? listener.Input : txtIn.Text.Trim());
            btnOutFolder.Click += (s, e) => OpenFolder(listener.Active ? listener.Output : txtOut.Text.Trim());
            btnClose.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { btnOpen, btnAgain, btnInFolder, btnOutFolder, btnClose });
            CancelButton = btnClose;

            settingOut = true;
            txtIn.Text = listener.Active ? listener.Input : settings.ListenerInput;
            txtOut.Text = listener.Active ? listener.Output : settings.ListenerOutput;
            settingOut = false;
            outEdited = txtOut.Text.Trim().Length > 0 && !SamePath(txtOut.Text, Listener.SuggestOutput(txtIn.Text));
            txtIn.Box.TextChanged += (s, e) =>
            {
                if (outEdited || listener.Active) return;
                settingOut = true;
                txtOut.Text = Listener.SuggestOutput(txtIn.Text);
                settingOut = false;
            };
            txtOut.Box.TextChanged += (s, e) => { if (!settingOut) outEdited = txtOut.Text.Trim().Length > 0; };

            listener.Changed += OnChanged;
            timer.Tick += (s, e) => Refill();
            timer.Start();
            Refill();
        }

        static Label Lbl(string text, int x, int y) => new Label { Text = text, Location = new Point(x, y + 3), AutoSize = true, BackColor = Win98.Face };

        static bool SamePath(string a, string b) =>
            string.Equals((a ?? "").Trim().TrimEnd('\\', '/'), (b ?? "").Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        void OnChanged() { if (!IsDisposed) Refill(); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            listener.Changed -= OnChanged;
            timer.Stop();
            timer.Dispose();
            base.OnFormClosed(e);
        }

        void Browse(Win98TextField field, string what)
        {
            using (var d = new FolderBrowserDialog { Description = what, ShowNewFolderButton = true })
            {
                string cur = field.Text.Trim();
                if (cur.Length > 0 && Directory.Exists(cur)) d.SelectedPath = cur;
                if (d.ShowDialog(this) == DialogResult.OK) field.Text = d.SelectedPath;
            }
        }

        void Toggle()
        {
            if (listener.Active)
            {
                listener.Stop();
                settings.ListenerEnabled = false;
                settings.Save();
                Refill();
                return;
            }
            string input = txtIn.Text.Trim(), output = txtOut.Text.Trim();
            if (input.Length == 0)
            {
                Win98MessageBox.Show(this, "Choose the folder to watch.", "Listener", MessageBoxButtons.OK, Win98Icon.Information);
                return;
            }
            if (output.Length == 0) { output = Listener.SuggestOutput(input); txtOut.Text = output; }
            try
            {
                if (!Directory.Exists(input))
                {
                    if (Win98MessageBox.Show(this, "The folder to watch does not exist:\n" + input + "\n\nCreate it?", "Listener", MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;
                    Directory.CreateDirectory(input);
                }
                listener.Start(input, output);
            }
            catch (Exception ex)
            {
                Win98MessageBox.Show(this, ex.Message, "Listener", MessageBoxButtons.OK, Win98Icon.Warning);
                return;
            }
            settings.ListenerInput = listener.Input;
            settings.ListenerOutput = listener.Output;
            settings.ListenerEnabled = true;
            settings.Save();
            Refill();
        }

        ListenerEntry Selected() => lv.SelectedItems.Count > 0 ? lv.SelectedItems[0].Tag as ListenerEntry : null;

        void OpenSelected()
        {
            var en = Selected();
            if (en != null && CanOpen(en)) openResults(en);
        }

        static bool CanOpen(ListenerEntry en) =>
            en.Status == ListenerStatus.Running || (en.Results.Length > 0 && File.Exists(Path.Combine(en.Results, "results.csv")));

        void OpenFolder(string dir)
        {
            if (dir.Length > 0 && Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
            else Win98MessageBox.Show(this, "That folder does not exist yet.", "Listener", MessageBoxButtons.OK, Win98Icon.Information);
        }

        void UpdateButtons()
        {
            var en = Selected();
            btnOpen.Enabled = en != null && CanOpen(en);
            btnAgain.Enabled = listener.Active && en != null && en.Job == null &&
                               (en.Status == ListenerStatus.Done || en.Status == ListenerStatus.Stopped || en.Status == ListenerStatus.Failed);
        }

        void Refill()
        {
            bool on = listener.Active;
            btnToggle.Text = on ? "&Stop Listening" : "&Start Listening";
            txtIn.ReadOnly = txtOut.ReadOnly = on;
            btnIn.Enabled = btnOut.Enabled = !on;
            if (on)
            {
                int pending = listener.PendingCount;
                lblState.ForeColor = listener.Problem.Length > 0 ? Bad : Good;
                lblState.Text = listener.Problem.Length > 0 ? "Listening, but the watched folder cannot be read" :
                                "Listening" + (pending > 0 ? $": {pending} campaign{(pending == 1 ? "" : "s")} to do" : ": nothing to do");
                lblDetail.Text = listener.Problem.Length > 0 ? listener.Problem :
                                 (listener.LastScan.HasValue ? "Last look at the folder: " + listener.LastScan.Value.ToString("HH:mm:ss") + ".  " : "") +
                                 "Others can see this in " + Listener.StatusFile + " there.";
            }
            else
            {
                lblState.ForeColor = Color.Black;
                lblState.Text = "Not listening";
                lblDetail.Text = "Choose the folders and press Start Listening. SPICA starts listening again by itself the next time it opens.";
            }

            var list = listener.Entries.OrderByDescending(e => e.Received).ToList();
            var sel = Selected();
            lv.BeginUpdate();
            while (lv.Items.Count > list.Count) lv.Items.RemoveAt(lv.Items.Count - 1);
            for (int i = 0; i < list.Count; i++)
            {
                var en = list[i];
                string[] cols =
                {
                    en.File, listener.Describe(en), When(en.Received), en.Ended.HasValue ? When(en.Ended.Value) : "",
                    en.Message.Length > 0 && en.Status != ListenerStatus.Done ? en.Message : en.Results.Length > 0 ? en.Results + (en.Message.Length > 0 ? "  (" + en.Message + ")" : "") : en.Message,
                };
                ListViewItem it;
                if (i >= lv.Items.Count) { it = new ListViewItem(cols); lv.Items.Add(it); }
                else
                {
                    it = lv.Items[i];
                    for (int c = 0; c < cols.Length; c++)
                    {
                        if (c < it.SubItems.Count) { if (it.SubItems[c].Text != cols[c]) it.SubItems[c].Text = cols[c]; }
                        else it.SubItems.Add(cols[c]);
                    }
                }
                it.Tag = en;
                bool want = ReferenceEquals(en, sel);
                if (it.Selected != want) it.Selected = want;
            }
            // the last column takes the rest of the width
            int used = 0;
            for (int c = 0; c < lv.Columns.Count - 1; c++) used += lv.Columns[c].Width;
            int rest = Math.Max(120, lv.ClientSize.Width - used);
            if (lv.Columns[lv.Columns.Count - 1].Width != rest) lv.Columns[lv.Columns.Count - 1].Width = rest;
            lv.EndUpdate();
            UpdateButtons();
        }

        static string When(DateTime t) => t.Date == DateTime.Today ? t.ToString("HH:mm:ss") : t.ToString("dd MMM HH:mm");
    }
}
