using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Forms;
using Spica.Core;

namespace Spica.UI
{
    /// <summary>
    /// The Results tab: Table | Statistics | Histogram | Scatter & TF | Temperature | Convergence,
    /// for the running campaign or any campaign folder opened from disk.
    /// </summary>
    public sealed class ResultsView : Panel
    {
        public event Action SetupChanged;
        public event Action OpenResultsRequested;
        public event Action OpenFolderRequested;

        ResultSet rs;
        bool live;
        string columnsKey = "";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly Label lblTitle;
        readonly Win98TabControl tabs;
        Win98Button btnReport, btnCsv, btnFolder;

        // table
        Win98ListView lvTable;
        ComboBox cbFilter;
        Label lblTable;
        List<ResultSet.Row> tableRows = new List<ResultSet.Row>();
        int sortCol = -1;
        bool sortDesc;

        // statistics
        ComboBox cbStatGroup, cbSpecMeas;
        Win98ListView lvStats;
        TextBox txtStat, txtLsl, txtUsl;
        List<MeasStats> statRows = new List<MeasStats>();

        // histogram
        ComboBox cbHistMeas, cbHistGroup;
        Win98CheckBox chkHistNormal;
        Win98Chart chHist;
        Label lblHist;

        // scatter & TF
        ComboBox cbTfX, cbTfY, cbTfGroup, cbTfUnit;
        TextBox txtTheory, txtBudget, txtK, txtTf;
        Win98Chart chTf, chTfErr;

        // temperature
        ComboBox cbTempMeas, cbTempRef;
        Win98Chart chTemp;
        Win98ListView lvTemp;
        TextBox txtTemp;
        Label lblTempMode;

        // convergence
        ComboBox cbConvMeas, cbConvGroup;
        Win98Chart chConvMean, chConvSigma;
        Label lblConv;

        bool filling;

        public ResultsView()
        {
            filling = true;   // no refreshes while the pages are being built
            BackColor = Win98.Face;
            Dock = DockStyle.Fill;

            var top = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Win98.Face };
            lblTitle = new Label { AutoSize = false, Location = new Point(0, 8), Height = 16, BackColor = Win98.Face, Text = "No results yet. Run a campaign, or open a campaign folder.", AutoEllipsis = true };
            var btnOpen = new Win98Button { Text = "&Open Results...", Image = Win98.Icon("open16"), Size = new Size(118, 25) };
            btnCsv = new Win98Button { Text = "Export &CSV...", Image = Win98.Icon("save16"), Size = new Size(108, 25) };
            btnReport = new Win98Button { Text = "&Report...", Image = Win98.Icon("doc16"), Size = new Size(96, 25) };
            btnFolder = new Win98Button { Text = "&Folder", Image = Win98.Icon("folder16"), Size = new Size(84, 25) };
            btnOpen.Click += (s, e) => OpenResultsRequested?.Invoke();
            btnCsv.Click += (s, e) => ExportCsv();
            btnReport.Click += (s, e) => ExportReport();
            btnFolder.Click += (s, e) => OpenFolderRequested?.Invoke();
            top.Controls.AddRange(new Control[] { lblTitle, btnOpen, btnCsv, btnReport, btnFolder });
            top.Resize += (s, e) =>
            {
                int x = top.Width;
                foreach (var b in new[] { btnFolder, btnReport, btnCsv, btnOpen }) { x -= b.Width; b.Location = new Point(x, 2); x -= 4; }
                lblTitle.Width = Math.Max(10, x - 8);
            };

            tabs = new Win98TabControl { Dock = DockStyle.Fill };
            tabs.AddPage(BuildTablePage());
            tabs.AddPage(BuildStatsPage());
            tabs.AddPage(BuildHistPage());
            tabs.AddPage(BuildTfPage());
            tabs.AddPage(BuildTempPage());
            tabs.AddPage(BuildConvPage());
            tabs.SelectedIndexChanged += (s, e) => RefreshPage();

            Controls.Add(tabs);
            Controls.Add(top);
            UpdateButtons();
            filling = false;
        }

        public ResultSet Data => rs;

        public int PageCount => tabs.Pages.Count;
        public void SelectPage(int i) => tabs.SelectedIndex = i;

        // =====================================================================================
        //  Data binding
        // =====================================================================================

        /// <summary>Shows a result set. <paramref name="isLive"/>: the campaign is still running.</summary>
        public void SetData(ResultSet set, bool isLive)
        {
            rs = set;
            live = isLive;
            lblTitle.Text = set == null ? "No results." :
                $"{set.Title}    {set.Rows.Count(r => r.Status == RunStatus.Ok)} of {set.Rows.Count} rows with data" +
                (set.Mode == TempMode.Corners ? $", {set.Corners.Count} temperatures" : set.Mode == TempMode.Random ? ", random temperature" : "") +
                (set.Paired ? ", paired samples" : "") + (isLive ? "   (running...)" : "");
            string key = set == null ? "" : string.Join("|", set.NumericColumns()) + "#" + string.Join(",", set.GroupNames());
            if (key != columnsKey)
            {
                columnsKey = key;
                FillChoices();
            }
            UpdateButtons();
            RefreshPage();
        }

        void UpdateButtons()
        {
            btnCsv.Enabled = btnReport.Enabled = rs != null && rs.Rows.Count > 0;
            btnFolder.Enabled = rs != null && Directory.Exists(rs.Folder ?? "");
        }

        void FillChoices()
        {
            filling = true;
            var meas = rs?.Meas ?? new List<string>();
            var cols = rs?.NumericColumns() ?? new List<string>();
            var groups = rs?.GroupNames() ?? new List<string> { "All runs" };
            Fill(cbHistMeas, meas); Fill(cbConvMeas, meas); Fill(cbTempMeas, meas); Fill(cbSpecMeas, meas);
            Fill(cbStatGroup, groups); Fill(cbHistGroup, groups); Fill(cbTfGroup, groups); Fill(cbConvGroup, groups);
            Fill(cbTfX, cols); Fill(cbTfY, cols);
            var tf = rs?.Setup.Tf;
            if (tf != null)
            {
                Select(cbTfX, tf.X.Length > 0 ? tf.X : (meas.Count > 1 ? meas[0] : null));
                Select(cbTfY, tf.Y.Length > 0 ? tf.Y : (meas.Count > 1 ? meas[1] : meas.FirstOrDefault()));
                txtTheory.Text = tf.Theory;
                txtBudget.Text = double.IsNaN(tf.Budget) ? "" : tf.Budget.ToString("R", Inv);
                cbTfUnit.SelectedIndex = tf.BudgetPercent ? 0 : 1;
                txtK.Text = tf.K.ToString("0.###", Inv);
            }
            cbTempRef.Items.Clear();
            if (rs != null && rs.Mode == TempMode.Corners)
            {
                foreach (var t in rs.Corners) cbTempRef.Items.Add(ResultSet.TempLabel(t));
                cbTempRef.SelectedIndex = Math.Max(0, rs.Corners.IndexOf(TempAnalysis.ReferenceCorner(rs.Corners)));
            }
            SetupTableColumns();
            filling = false;
        }

        static void Fill(ComboBox cb, IList<string> items)
        {
            string keep = cb.SelectedItem as string;
            cb.BeginUpdate();
            cb.Items.Clear();
            foreach (var i in items) cb.Items.Add(i);
            cb.EndUpdate();
            if (keep != null && items.Contains(keep)) cb.SelectedItem = keep;
            else if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        }

        static void Select(ComboBox cb, string item)
        {
            if (item == null) return;
            for (int i = 0; i < cb.Items.Count; i++)
                if (string.Equals((string)cb.Items[i], item, StringComparison.OrdinalIgnoreCase)) { cb.SelectedIndex = i; return; }
        }

        void RefreshPage()
        {
            if (filling || tabs == null) return;
            switch (tabs.SelectedIndex)
            {
                case 0: RefreshTable(); break;
                case 1: RefreshStats(); break;
                case 2: RefreshHist(); break;
                case 3: RefreshTf(); break;
                case 4: RefreshTemp(); break;
                case 5: RefreshConv(); break;
            }
        }

        void NotifySetup()
        {
            if (rs == null) return;
            if (!live) rs.SaveSetup();      // a campaign folder opened from disk keeps its own analysis setup
            SetupChanged?.Invoke();
        }

        // =====================================================================================
        //  Layout helpers
        // =====================================================================================

        static Label Lbl(string text, int x, int y) => new Label { Text = text, Location = new Point(x, y + 3), AutoSize = true, BackColor = Win98.Face };

        ComboBox Combo(int x, int y, int w)
        {
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x, y), Width = w, Font = Win98.Font, FlatStyle = FlatStyle.Standard };
            cb.SelectedIndexChanged += (s, e) => RefreshPage();
            return cb;
        }

        static TextBox Field(int x, int y, int w) => new TextBox { Location = new Point(x, y), Width = w, Font = Win98.Font, BorderStyle = BorderStyle.Fixed3D };

        static TextBox Memo() => new TextBox
        {
            Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Win98.Window,
            ScrollBars = ScrollBars.Both, Font = Win98.MonoFont, WordWrap = false,
        };

        static Panel Bar(int h) => new Panel { Dock = DockStyle.Top, Height = h, BackColor = Win98.Face };
        static Panel Gap(DockStyle d, int size = 6) => new Panel { Dock = d, Height = size, Width = size, BackColor = Win98.Face };

        static Win98Well ChartWell(Win98Chart c) => new Win98Well(c, Color.White) { Dock = DockStyle.Fill };

        static string F(double v) => MeasStats.F(v);

        static double ParseField(TextBox t)
        {
            var s = t.Text.Trim();
            if (s.Length == 0) return double.NaN;
            if (Expr.TryParse(s, out var e, out _)) return e.Eval(0);   // accepts SPICE suffixes: 10m, 2.2k ...
            return double.NaN;
        }

        // =====================================================================================
        //  Table
        // =====================================================================================

        Win98TabPage BuildTablePage()
        {
            var p = new Win98TabPage("Table", Win98.Icon("doc16"));
            var bar = Bar(30);
            cbFilter = Combo(40, 2, 170);
            cbFilter.Items.AddRange(new object[] { "All rows", "Out of spec only", "Failed / no data only" });
            cbFilter.SelectedIndex = 0;
            lblTable = new Label { AutoSize = true, Location = new Point(222, 7), BackColor = Win98.Face };
            bar.Controls.AddRange(new Control[] { Lbl("Show:", 0, 2), cbFilter, lblTable });

            lvTable = new Win98ListView(true);
            lvTable.RetrieveVirtualItem += (s, e) => e.Item = TableItem(e.ItemIndex);
            lvTable.CellColor = TableCellColor;
            lvTable.ColumnClick += (s, e) =>
            {
                if (sortCol == e.Column) sortDesc = !sortDesc; else { sortCol = e.Column; sortDesc = false; }
                RefreshTable();
            };
            p.Controls.Add(new Win98Well(lvTable) { Dock = DockStyle.Fill });
            p.Controls.Add(Gap(DockStyle.Top));
            p.Controls.Add(bar);
            return p;
        }

        // column layout: Run, [Temp], Status, meas..., [Pass], Note
        int ColTemp => rs != null && rs.HasTemp ? 1 : -1;
        int ColStatus => rs != null && rs.HasTemp ? 2 : 1;
        int ColMeas0 => ColStatus + 1;
        int ColPass => rs != null && rs.AnySpec ? ColMeas0 + rs.Meas.Count : -1;

        void SetupTableColumns()
        {
            lvTable.BeginUpdate();
            lvTable.VirtualListSize = 0;
            lvTable.Columns.Clear();
            lvTable.Columns.Add("Run", 56, HorizontalAlignment.Right);
            if (rs != null && rs.HasTemp) lvTable.Columns.Add("Temp °C", 64, HorizontalAlignment.Right);
            lvTable.Columns.Add("Status", 60);
            if (rs != null) foreach (var m in rs.Meas) lvTable.Columns.Add(m, 110, HorizontalAlignment.Right);
            lvTable.Columns.Add("Spec", 50);
            lvTable.Columns.Add("Note", 220);
            lvTable.EndUpdate();
            sortCol = -1;
        }

        void RefreshTable()
        {
            if (rs == null) { lvTable.VirtualListSize = 0; lblTable.Text = ""; return; }
            // the Spec column exists only when specs are set; rebuild when that changes
            bool wantPass = rs.AnySpec;
            bool hasPassCol = lvTable.Columns.Count == ColMeas0 + rs.Meas.Count + 2;
            if (wantPass != hasPassCol) SetupTableColumnsKeepSort(wantPass);

            IEnumerable<ResultSet.Row> q = rs.Rows;
            if (cbFilter.SelectedIndex == 1) q = q.Where(r => rs.RowPasses(r) == false);
            else if (cbFilter.SelectedIndex == 2) q = q.Where(r => r.Status != RunStatus.Ok);
            var list = q.ToList();
            if (sortCol >= 0)
            {
                Func<ResultSet.Row, IComparable> key = r => r.Run;
                if (sortCol == ColTemp) key = r => r.Temp;
                else if (sortCol == ColStatus) key = r => r.Status.ToString();
                else if (sortCol >= ColMeas0 && sortCol < ColMeas0 + rs.Meas.Count) { int c = sortCol - ColMeas0; key = r => double.IsNaN(r.V[c]) ? double.MaxValue : r.V[c]; }
                else if (sortCol == ColPass) key = r => rs.RowPasses(r) == false ? 0 : 1;
                else if (sortCol > 0) key = r => r.Note;
                list = sortDesc ? list.OrderByDescending(key).ToList() : list.OrderBy(key).ToList();
            }
            tableRows = list;
            lvTable.VirtualListSize = tableRows.Count;
            lvTable.Invalidate();
            int fails = rs.AnySpec ? rs.Rows.Count(r => rs.RowPasses(r) == false) : 0;
            lblTable.Text = $"{tableRows.Count} rows shown" + (rs.AnySpec ? $";  {fails} rows out of spec" : ";  no spec limits set (Statistics tab)");
        }

        void SetupTableColumnsKeepSort(bool withPass)
        {
            int keep = sortCol; bool desc = sortDesc;
            lvTable.BeginUpdate();
            lvTable.VirtualListSize = 0;
            lvTable.Columns.Clear();
            lvTable.Columns.Add("Run", 56, HorizontalAlignment.Right);
            if (rs.HasTemp) lvTable.Columns.Add("Temp °C", 64, HorizontalAlignment.Right);
            lvTable.Columns.Add("Status", 60);
            foreach (var m in rs.Meas) lvTable.Columns.Add(m, 110, HorizontalAlignment.Right);
            if (withPass) lvTable.Columns.Add("Spec", 50);
            lvTable.Columns.Add("Note", 220);
            lvTable.EndUpdate();
            sortCol = keep; sortDesc = desc;
        }

        ListViewItem TableItem(int i)
        {
            var r = i < tableRows.Count ? tableRows[i] : null;
            var it = new ListViewItem(r?.Run.ToString(Inv) ?? "");
            if (r == null) return it;
            if (rs.HasTemp) it.SubItems.Add(double.IsNaN(r.Temp) ? "" : r.Temp.ToString("0.##", Inv));
            it.SubItems.Add(r.Status.ToString());
            for (int c = 0; c < rs.Meas.Count; c++) it.SubItems.Add(r.Text[c]);
            if (rs.AnySpec) { var p = rs.RowPasses(r); it.SubItems.Add(p == null ? "" : p.Value ? "pass" : "FAIL"); }
            it.SubItems.Add(r.Note);
            return it;
        }

        Color? TableCellColor(int i, int col)
        {
            if (rs == null || i >= tableRows.Count) return null;
            var r = tableRows[i];
            if (r.Status != RunStatus.Ok) return Win98.Shadow;
            if (col >= ColMeas0 && col < ColMeas0 + rs.Meas.Count)
            {
                var s = rs.Setup.Spec(rs.Meas[col - ColMeas0]);
                if (s != null && s.IsSet && !s.Passes(r.V[col - ColMeas0])) return Win98Chart.Red;
            }
            if (col == ColPass && rs.RowPasses(r) == false) return Win98Chart.Red;
            return null;
        }

        // =====================================================================================
        //  Statistics
        // =====================================================================================

        Win98TabPage BuildStatsPage()
        {
            var p = new Win98TabPage("Statistics", Win98.Icon("results16"));
            var bar = Bar(30);
            cbStatGroup = Combo(44, 2, 160);
            bar.Controls.AddRange(new Control[] { Lbl("Group:", 0, 2), cbStatGroup,
                new Label { AutoSize = true, Location = new Point(216, 7), BackColor = Win98.Face, ForeColor = Win98.Shadow,
                            Text = "Select a row for its sigma analysis. Yield CI: exact binomial 95 %. Normal: Anderson-Darling." } });

            lvStats = new Win98ListView();
            foreach (var (t, w) in new[] { ("Measurement", 120), ("n", 50), ("Mean", 84), ("Std dev", 84), ("Min", 84), ("Max", 84), ("Median", 84),
                                           ("P0.135%", 84), ("P99.865%", 84), ("Skew", 56), ("Ex.kurt", 56), ("Normal p", 66),
                                           ("LSL", 70), ("USL", 70), ("Yield", 66), ("Yield 95% CI", 120), ("Cpk", 56), ("Pred. ppm", 76) })
                lvStats.Columns.Add(t, w, t == "Measurement" ? HorizontalAlignment.Left : HorizontalAlignment.Right);
            lvStats.CellColor = (i, c) =>
            {
                if (i >= statRows.Count) return null;
                var s = statRows[i];
                if (c == 11 && !double.IsNaN(s.AdP)) return s.NormalOk ? Win98Chart.Green : Win98Chart.Maroon;
                if (c == 14 && s.Cap.N > 0 && s.Cap.Pass < s.Cap.N) return Win98Chart.Red;
                if (c == 16 && !double.IsNaN(s.Cap.Cpk)) return s.Cap.Cpk >= 1.33 ? Win98Chart.Green : s.Cap.Cpk >= 1 ? Win98Chart.Olive : Win98Chart.Red;
                return null;
            };
            lvStats.SelectedIndexChanged += (s, e) => ShowStatDetail();
            var listWell = new Win98Well(lvStats) { Dock = DockStyle.Fill };

            // bottom: spec editor + narrative
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 236, BackColor = Win98.Face };
            var grpSpec = new Win98GroupBox { Text = "Spec limits", Dock = DockStyle.Left, Width = 236 };
            cbSpecMeas = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(92, 22), Width = 132, Font = Win98.Font };
            cbSpecMeas.SelectedIndexChanged += (s, e) => LoadSpecFields();
            txtLsl = Field(92, 52, 132);
            txtUsl = Field(92, 80, 132);
            var btnApply = new Win98Button { Text = "&Apply", Location = new Point(92, 112), Size = new Size(64, 24) };
            var btnClear = new Win98Button { Text = "C&lear", Location = new Point(160, 112), Size = new Size(64, 24) };
            btnApply.Click += (s, e) => ApplySpec(false);
            btnClear.Click += (s, e) => ApplySpec(true);
            txtLsl.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ApplySpec(false); e.SuppressKeyPress = true; } };
            txtUsl.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ApplySpec(false); e.SuppressKeyPress = true; } };
            var hint = new Label { Location = new Point(10, 146), Size = new Size(218, 80), BackColor = Win98.Face, ForeColor = Win98.Shadow,
                Text = "Leave a limit empty for a one-sided spec. SPICE suffixes work (10m, 2.2k).\nLimits are saved in the setup (.spica) and in the campaign folder." };
            grpSpec.Controls.AddRange(new Control[] { Lbl("Measurement:", 10, 22), cbSpecMeas, Lbl("Lower (LSL):", 10, 52), txtLsl, Lbl("Upper (USL):", 10, 80), txtUsl, btnApply, btnClear, hint });
            txtStat = Memo();
            bottom.Controls.Add(new Win98Well(txtStat) { Dock = DockStyle.Fill });
            bottom.Controls.Add(Gap(DockStyle.Left));
            bottom.Controls.Add(grpSpec);

            p.Controls.Add(listWell);
            p.Controls.Add(Gap(DockStyle.Top));
            p.Controls.Add(bar);
            p.Controls.Add(Gap(DockStyle.Bottom));
            p.Controls.Add(bottom);
            bottom.SendToBack();
            return p;
        }

        void RefreshStats()
        {
            int keep = lvStats.SelectedIndices.Count > 0 ? lvStats.SelectedIndices[0] : 0;
            lvStats.BeginUpdate();
            lvStats.Items.Clear();
            statRows.Clear();
            if (rs != null)
            {
                int g = Math.Max(0, cbStatGroup.SelectedIndex);
                string gname = rs.GroupNames()[Math.Min(g, rs.GroupNames().Count - 1)];
                foreach (var m in rs.Meas)
                {
                    var s = MeasStats.Of(m, gname, rs.Values(m, g), rs.Setup.Spec(m));
                    statRows.Add(s);
                    var d = s.D;
                    var c = s.Cap;
                    var it = new ListViewItem(m);
                    string[] cols =
                    {
                        d.N.ToString(Inv), F(d.Mean), F(d.Std), F(d.Min), F(d.Max), F(d.Median), F(s.P00135), F(s.P99865),
                        Num(d.Skew, "0.000"), Num(d.ExKurt, "0.000"), MeasStats.P(s.AdP),
                        s.Spec != null ? F(s.Spec.Lsl) : "", s.Spec != null ? F(s.Spec.Usl) : "",
                        c.N > 0 && s.Spec?.IsSet == true ? MeasStats.Pct(c.Yield) : "",
                        c.N > 0 && s.Spec?.IsSet == true ? $"{MeasStats.Pct(c.YieldLo)} .. {MeasStats.Pct(c.YieldHi)}" : "",
                        Num(c.Cpk, "0.000"), s.NormalOk ? (double.IsNaN(c.PredictedPpm) ? "" : MeasStats.Ppm(c.PredictedPpm)) : (s.Spec?.IsSet == true ? "(not normal)" : ""),
                    };
                    foreach (var x in cols) it.SubItems.Add(x);
                    lvStats.Items.Add(it);
                }
            }
            lvStats.EndUpdate();
            if (lvStats.Items.Count > 0) lvStats.Items[Math.Min(keep, lvStats.Items.Count - 1)].Selected = true;
            ShowStatDetail();
        }

        static string Num(double v, string fmt) => double.IsNaN(v) ? "" : v.ToString(fmt, Inv);

        void ShowStatDetail()
        {
            if (lvStats.SelectedIndices.Count == 0 || lvStats.SelectedIndices[0] >= statRows.Count) { txtStat.Text = rs == null ? "" : "Select a measurement."; return; }
            var s = statRows[lvStats.SelectedIndices[0]];
            txtStat.Text = s.Narrative().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
            if (!filling) { Select(cbSpecMeas, s.Meas); }
        }

        void LoadSpecFields()
        {
            var sp = rs?.Setup.Spec(cbSpecMeas.SelectedItem as string);
            txtLsl.Text = sp == null || double.IsNaN(sp.Lsl) ? "" : sp.Lsl.ToString("G9", Inv);
            txtUsl.Text = sp == null || double.IsNaN(sp.Usl) ? "" : sp.Usl.ToString("G9", Inv);
        }

        void ApplySpec(bool clear)
        {
            if (rs == null || !(cbSpecMeas.SelectedItem is string m)) return;
            double lo = clear ? double.NaN : ParseField(txtLsl), hi = clear ? double.NaN : ParseField(txtUsl);
            if (!clear && ((txtLsl.Text.Trim().Length > 0 && double.IsNaN(lo)) || (txtUsl.Text.Trim().Length > 0 && double.IsNaN(hi))))
            {
                Win98MessageBox.Show(FindForm(), "Enter numbers for the limits (e.g. 2.5, 10m, 1.2k).", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                return;
            }
            if (!double.IsNaN(lo) && !double.IsNaN(hi) && lo > hi) { var t = lo; lo = hi; hi = t; }
            rs.Setup.SetSpec(m, lo, hi);
            LoadSpecFields();
            NotifySetup();
            RefreshStats();
        }

        // =====================================================================================
        //  Histogram
        // =====================================================================================

        Win98TabPage BuildHistPage()
        {
            var p = new Win98TabPage("Histogram", Win98.Icon("chart16"));
            var bar = Bar(30);
            cbHistMeas = Combo(82, 2, 150);
            cbHistGroup = Combo(290, 2, 150);
            chkHistNormal = new Win98CheckBox { Text = "Normal fit && 3 sigma", Location = new Point(456, 5), Checked = true };
            chkHistNormal.CheckedChanged += (s, e) => RefreshHist();
            bar.Controls.AddRange(new Control[] { Lbl("Measurement:", 0, 2), cbHistMeas, Lbl("Group:", 246, 2), cbHistGroup, chkHistNormal });
            chHist = new Win98Chart();
            lblHist = new Label { Dock = DockStyle.Bottom, Height = 18, BackColor = Win98.Face, TextAlign = ContentAlignment.MiddleLeft };
            p.Controls.Add(ChartWell(chHist));
            p.Controls.Add(Gap(DockStyle.Top));
            p.Controls.Add(bar);
            p.Controls.Add(lblHist);
            return p;
        }

        void RefreshHist()
        {
            if (rs == null || !(cbHistMeas.SelectedItem is string m)) { chHist.Clear(); lblHist.Text = ""; return; }
            int g = Math.Max(0, cbHistGroup.SelectedIndex);
            var s = BuildHistogram(chHist, m, g, chkHistNormal.Checked);
            lblHist.Text = s == null ? "" :
                $"n = {s.D.N}   mean = {F(s.D.Mean)}   sigma = {F(s.D.Std)}   normal p = {MeasStats.P(s.AdP)}" +
                (s.Cap.N > 0 && s.Spec?.IsSet == true ? $"   yield = {MeasStats.Pct(s.Cap.Yield)}   Cpk = {Num(s.Cap.Cpk, "0.000")}" : "");
        }

        MeasStats BuildHistogram(Win98Chart ch, string meas, int group, bool normal)
        {
            ch.Clear();
            var groups = rs.GroupNames();
            var s = MeasStats.Of(meas, groups[Math.Min(group, groups.Count - 1)], rs.Values(meas, group), rs.Setup.Spec(meas));
            ch.Title = $"{meas}  -  {s.Group}";
            ch.XLabel = meas;
            ch.YLabel = "runs";
            ch.YFromZero = true;
            if (s.D.N == 0) return s;
            var edges = Stats.HistogramEdges(s.D);
            var counts = Stats.Histogram(s.D.Sorted, edges);
            ch.Add(new ChartSeries { Kind = SeriesKind.Bars, Edges = edges, Y = counts.Select(c => (double)c).ToArray(), X = edges, Color = Win98Chart.Navy, Label = "runs", InLegend = false });
            if (normal && s.D.Std > 0)
            {
                double w = edges[1] - edges[0];
                double lo = Math.Min(edges[0], s.D.Mean - 4 * s.D.Std), hi = Math.Max(edges[edges.Length - 1], s.D.Mean + 4 * s.D.Std);
                var xs = Enumerable.Range(0, 161).Select(i => lo + (hi - lo) * i / 160).ToArray();
                ch.Add(new ChartSeries { Kind = SeriesKind.Line, X = xs, Y = xs.Select(x => s.D.N * w * Stats.NormalPdf((x - s.D.Mean) / s.D.Std) / s.D.Std).ToArray(), Color = Win98Chart.Teal, Size = 2, Label = "normal fit" });
                ch.Add(new ChartSeries { Kind = SeriesKind.VLine, X = new[] { s.D.Mean }, Color = Win98Chart.Green, Label = "mean" });
                ch.Add(new ChartSeries { Kind = SeriesKind.VLine, X = new[] { s.D.Mean - 3 * s.D.Std, s.D.Mean + 3 * s.D.Std }, Color = Win98Chart.Purple, Dashed = true, Label = "mean +/- 3 sigma" });
            }
            if (s.Spec != null && s.Spec.IsSet)
                ch.Add(new ChartSeries { Kind = SeriesKind.VLine, X = new[] { s.Spec.Lsl, s.Spec.Usl }.Where(v => !double.IsNaN(v)).ToArray(), Color = Win98Chart.Red, Label = "spec limits" });
            return s;
        }

        // =====================================================================================
        //  Scatter & transfer function
        // =====================================================================================

        Win98TabPage BuildTfPage()
        {
            var p = new Win98TabPage("Scatter && TF", Win98.Icon("chart16"));
            var bar = Bar(60);
            cbTfX = Combo(20, 2, 130);
            cbTfY = Combo(176, 2, 130);
            cbTfGroup = Combo(358, 2, 140);
            txtTheory = Field(132, 32, 200);
            txtBudget = Field(410, 32, 60);
            cbTfUnit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(474, 32), Width = 54, Font = Win98.Font };
            cbTfUnit.Items.AddRange(new object[] { "%", "abs" });
            cbTfUnit.SelectedIndex = 0;
            txtK = Field(578, 32, 36);
            var btn = new Win98Button { Text = "A&nalyze", Location = new Point(624, 30), Size = new Size(80, 24) };
            btn.Click += (s, e) => ApplyTf();
            foreach (var t in new[] { txtTheory, txtBudget, txtK }) t.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ApplyTf(); e.SuppressKeyPress = true; } };
            bar.Controls.AddRange(new Control[] { Lbl("X:", 0, 2), cbTfX, Lbl("Y:", 158, 2), cbTfY, Lbl("Group:", 316, 2), cbTfGroup,
                Lbl("Theoretical TF  y =", 0, 32), txtTheory, Lbl("Budget +/-", 344, 32), txtBudget, cbTfUnit, Lbl("k sigma", 534, 32), txtK, btn });

            chTf = new Win98Chart();
            chTfErr = new Win98Chart();
            txtTf = Memo();
            var right = new Win98Well(txtTf) { Dock = DockStyle.Right, Width = 470 };
            var charts = new Panel { Dock = DockStyle.Fill, BackColor = Win98.Face };
            var errWell = new Win98Well(chTfErr, Color.White) { Dock = DockStyle.Bottom, Height = 200 };
            charts.Controls.Add(ChartWell(chTf));
            charts.Controls.Add(Gap(DockStyle.Bottom));
            charts.Controls.Add(errWell);
            charts.Resize += (s, e) => errWell.Height = Math.Max(120, charts.Height * 2 / 5);

            p.Controls.Add(charts);
            p.Controls.Add(Gap(DockStyle.Right));
            p.Controls.Add(right);
            p.Controls.Add(Gap(DockStyle.Top));
            p.Controls.Add(bar);
            p.Resize += (s, e) => right.Width = Math.Max(360, Math.Min(560, p.Width * 2 / 5));
            return p;
        }

        void ApplyTf()
        {
            if (rs == null) return;
            var tf = rs.Setup.Tf;
            string theory = txtTheory.Text.Trim();
            if (theory.Length > 0 && !Expr.TryParse(theory, out _, out var err))
            {
                Win98MessageBox.Show(FindForm(), "The theoretical TF is not a valid expression:\n" + err + "\n\nUse x for the X value (and T for temperature), e.g.  1.93*x  or  2*x + 10m.",
                    "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                return;
            }
            tf.X = cbTfX.SelectedItem as string ?? "";
            tf.Y = cbTfY.SelectedItem as string ?? "";
            tf.Theory = theory;
            tf.Budget = ParseField(txtBudget);
            tf.BudgetPercent = cbTfUnit.SelectedIndex == 0;
            var k = ParseField(txtK);
            tf.K = k > 0 ? k : 3;
            rs.Setup.Dirty = true;
            NotifySetup();
            RefreshTf();
        }

        void RefreshTf()
        {
            if (rs == null) { chTf.Clear(); chTfErr.Clear(); txtTf.Text = ""; return; }
            // the combos drive the plot immediately; the theory/budget fields apply on Analyze
            var tf = new TfSetup
            {
                X = cbTfX.SelectedItem as string ?? "", Y = cbTfY.SelectedItem as string ?? "",
                Theory = rs.Setup.Tf.Theory, Budget = rs.Setup.Tf.Budget, BudgetPercent = rs.Setup.Tf.BudgetPercent, K = rs.Setup.Tf.K,
            };
            var rep = BuildTf(chTf, chTfErr, tf, Math.Max(0, cbTfGroup.SelectedIndex));
            txtTf.Text = rep.Narrative().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
        }

        TfReport BuildTf(Win98Chart top, Win98Chart bottom, TfSetup tf, int group)
        {
            var rep = TfReport.Compute(rs, tf, group);
            top.Clear(); bottom.Clear();
            top.Title = $"{tf.Y} vs {tf.X}";
            top.XLabel = tf.X; top.YLabel = tf.Y;
            if (rep.Error != null || rep.N == 0) { top.EmptyText = rep.Error ?? "No data."; bottom.EmptyText = ""; return rep; }
            top.Add(new ChartSeries { Kind = SeriesKind.Points, X = rep.X, Y = rep.Y, Color = Win98Chart.Navy, Label = "runs", Size = 3 });
            double xa = rep.X.Min(), xb = rep.X.Max();
            var lx = new[] { xa, xb };
            top.Add(new ChartSeries { Kind = SeriesKind.Line, X = lx, Y = lx.Select(rep.Fit.Predict).ToArray(), Color = Win98Chart.Green, Size = 2, Label = "best line a*x+b" });
            if (rep.Theory != null)
            {
                var xs = Enumerable.Range(0, 41).Select(i => xa + (xb - xa) * i / 40).ToArray();
                top.Add(new ChartSeries { Kind = SeriesKind.Line, X = xs, Y = xs.Select(x => rep.Theory.Eval(x, double.NaN)).ToArray(), Color = Win98Chart.Red, Dashed = true, Label = "theoretical TF" });
            }

            // error plot: vs theory when given, else residuals around the best line
            double[] err;
            string unit = tf.BudgetPercent ? "%" : "";
            if (rep.ErrTheory != null) { err = rep.ErrTheory; bottom.Title = $"Error vs theoretical TF ({(tf.BudgetPercent ? "% of theory" : "units of Y")})"; }
            else
            {
                err = rep.X.Select((x, i) => tf.BudgetPercent ? (rep.Y[i] - rep.Fit.Predict(x)) / Math.Abs(rep.Fit.Predict(x)) * 100 : rep.Y[i] - rep.Fit.Predict(x)).ToArray();
                bottom.Title = $"Residuals around the best line ({(tf.BudgetPercent ? "%" : "units of Y")})";
            }
            bottom.XLabel = tf.X; bottom.YLabel = "error " + unit;
            bottom.Add(new ChartSeries { Kind = SeriesKind.Points, X = rep.X, Y = err, Color = Win98Chart.Navy, Size = 3, InLegend = false });
            bottom.Add(new ChartSeries { Kind = SeriesKind.HLine, Y = new[] { 0.0 }, Color = Color.Black, InLegend = false });
            if (!double.IsNaN(tf.Budget))
                bottom.Add(new ChartSeries { Kind = SeriesKind.HLine, Y = new[] { -tf.Budget, tf.Budget }, Color = Win98Chart.Red, Label = "budget" });
            var de = Descriptive.Of(err);
            if (de.N > 1)
                bottom.Add(new ChartSeries { Kind = SeriesKind.HLine, Y = new[] { de.Mean - tf.K * de.Std, de.Mean + tf.K * de.Std }, Color = Win98Chart.Purple, Dashed = true, Label = $"mean +/- {tf.K:0.#} sigma" });
            return rep;
        }

        // =====================================================================================
        //  Temperature
        // =====================================================================================

        Win98TabPage BuildTempPage()
        {
            var p = new Win98TabPage("Temperature", Win98.Icon("clock16"));
            var bar = Bar(30);
            cbTempMeas = Combo(82, 2, 150);
            cbTempRef = Combo(330, 2, 90);
            lblTempMode = new Label { AutoSize = true, Location = new Point(432, 7), BackColor = Win98.Face, ForeColor = Win98.Shadow };
            bar.Controls.AddRange(new Control[] { Lbl("Measurement:", 0, 2), cbTempMeas, Lbl("Drift reference:", 244, 2), cbTempRef, lblTempMode });

            chTemp = new Win98Chart();
            txtTemp = Memo();
            lvTemp = new Win98ListView();
            var right = new Win98Well(txtTemp) { Dock = DockStyle.Right, Width = 420 };
            var listWell = new Win98Well(lvTemp) { Dock = DockStyle.Bottom, Height = 150 };
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Win98.Face };
            body.Controls.Add(ChartWell(chTemp));
            body.Controls.Add(Gap(DockStyle.Right));
            body.Controls.Add(right);
            p.Controls.Add(body);
            p.Controls.Add(Gap(DockStyle.Top));
            p.Controls.Add(bar);
            p.Controls.Add(Gap(DockStyle.Bottom));
            p.Controls.Add(listWell);
            listWell.SendToBack();
            return p;
        }

        void RefreshTemp()
        {
            chTemp.Clear();
            lvTemp.Items.Clear();
            txtTemp.Text = "";
            if (rs == null) return;
            cbTempRef.Enabled = rs.Mode == TempMode.Corners && rs.Paired;
            if (rs.Mode == TempMode.Default)
            {
                lblTempMode.Text = "";
                chTemp.EmptyText = "This campaign ran at the schematic's own temperature.\n\nChoose temperature corners or a random temperature range on the Setup tab to analyse temperature effects.";
                chTemp.Invalidate();
                return;
            }
            if (!(cbTempMeas.SelectedItem is string m)) return;
            var text = rs.Mode == TempMode.Corners ? BuildTempCorners(chTemp, lvTemp, m) : BuildTempRandom(chTemp, lvTemp, m);
            txtTemp.Text = text.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
        }

        double SelectedRef() => rs.Corners.Count == 0 ? double.NaN :
            rs.Corners[Math.Max(0, Math.Min(rs.Corners.Count - 1, cbTempRef.SelectedIndex))];

        string BuildTempCorners(Win98Chart ch, ListView lv, string m)
        {
            ch.Clear();
            lblTempMode.Text = rs.Paired ? "Paired samples: the same units at every temperature." : "Independent samples per temperature (LTspice mc()): no per-unit drift.";
            double tref = SelectedRef();
            var rows = TempAnalysis.Corners(rs, m, tref);
            ch.Title = $"{m} vs temperature";
            ch.XLabel = "temperature (°C)"; ch.YLabel = m;
            int col = rs.Col(m);
            if (rs.Paired && col >= 0)
            {
                // a sample of individual units, each one line across the corners
                var byRun = rs.Rows.Where(r => r.Status == RunStatus.Ok && !double.IsNaN(r.V[col])).GroupBy(r => r.Run).Where(g => g.Count() == rs.Corners.Count).Take(60);
                bool first = true;
                foreach (var g in byRun)
                {
                    var pts = g.OrderBy(r => r.Temp).ToList();
                    ch.Add(new ChartSeries { Kind = SeriesKind.Line, X = pts.Select(r => r.Temp).ToArray(), Y = pts.Select(r => r.V[col]).ToArray(), Color = Win98Chart.Silver, Label = first ? "units (60 shown)" : null, InLegend = first });
                    first = false;
                }
            }
            ch.Add(new ChartSeries
            {
                Kind = SeriesKind.ErrorBars, X = rows.Select(r => r.Temp).ToArray(), Y = rows.Select(r => r.S.D.Mean).ToArray(),
                Lo = rows.Select(r => r.S.D.Mean - 3 * r.S.D.Std).ToArray(), Hi = rows.Select(r => r.S.D.Mean + 3 * r.S.D.Std).ToArray(),
                Color = Win98Chart.Navy, Label = "mean +/- 3 sigma",
            });
            ch.Add(new ChartSeries { Kind = SeriesKind.Line, X = rows.Select(r => r.Temp).ToArray(), Y = rows.Select(r => r.S.D.Min).ToArray(), Color = Win98Chart.Teal, Dashed = true, Label = "min / max" });
            ch.Add(new ChartSeries { Kind = SeriesKind.Line, X = rows.Select(r => r.Temp).ToArray(), Y = rows.Select(r => r.S.D.Max).ToArray(), Color = Win98Chart.Teal, Dashed = true, InLegend = false });
            var spec = rs.Setup.Spec(m);
            if (spec != null && spec.IsSet) ch.Add(new ChartSeries { Kind = SeriesKind.HLine, Y = new[] { spec.Lsl, spec.Usl }.Where(v => !double.IsNaN(v)).ToArray(), Color = Win98Chart.Red, Label = "spec limits" });

            lv.BeginUpdate();
            lv.Columns.Clear();
            foreach (var (t, w) in new[] { ("Temp °C", 64), ("n", 50), ("Mean", 84), ("Std dev", 84), ("Min", 84), ("Max", 84), ("Yield", 70), ("Cpk", 56),
                                           ("Mean drift", 84), ("Drift sigma", 84), ("Max |drift|", 84), ("Tempco ppm/°C", 96), ("Tempco sigma", 90) })
                lv.Columns.Add(t, w, HorizontalAlignment.Right);
            foreach (var r in rows)
            {
                var it = new ListViewItem(r.Temp.ToString("0.###", Inv));
                bool sp = r.S.Spec?.IsSet == true;
                var d = r.Drift;
                bool hasDrift = d != null && d.N > 0;
                foreach (var x in new[]
                {
                    r.S.D.N.ToString(Inv), F(r.S.D.Mean), F(r.S.D.Std), F(r.S.D.Min), F(r.S.D.Max),
                    sp ? MeasStats.Pct(r.S.Cap.Yield) : "", Num(r.S.Cap.Cpk, "0.000"),
                    hasDrift ? F(d.Mean) : "", hasDrift ? F(d.Std) : "", hasDrift ? F(Math.Max(Math.Abs(d.Min), Math.Abs(d.Max))) : "",
                    r.Tempco != null && r.Tempco.N > 0 ? r.Tempco.Mean.ToString("0.###", Inv) : "", r.Tempco != null && r.Tempco.N > 1 ? r.Tempco.Std.ToString("0.###", Inv) : "",
                }) it.SubItems.Add(x);
                lv.Items.Add(it);
            }
            lv.EndUpdate();

            var sb = new StringBuilder();
            sb.AppendLine($"{m}: {rs.Corners.Count} temperature corners, {rows.Sum(r => r.S.D.N)} values.");
            if (rows.Count > 1)
            {
                var hot = rows.OrderBy(r => r.Temp).Last(); var cold = rows.OrderBy(r => r.Temp).First();
                sb.AppendLine($"Mean shift {cold.Temp:0.#} -> {hot.Temp:0.#} °C: {F(hot.S.D.Mean - cold.S.D.Mean)} ({MeasStats.Pct((hot.S.D.Mean - cold.S.D.Mean) / Math.Abs(cold.S.D.Mean))})");
                var worstSigma = rows.OrderByDescending(r => r.S.D.Std).First();
                sb.AppendLine($"Widest spread at {worstSigma.Temp:0.#} °C (sigma {F(worstSigma.S.D.Std)}).");
            }
            if (rs.Paired)
            {
                sb.AppendLine();
                sb.AppendLine($"Per-unit drift relative to {tref:0.#} °C (same unit at each temperature):");
                foreach (var r in rows.Where(r => r.Temp != tref && r.Drift != null && r.Drift.N > 1))
                    sb.AppendLine($"  {r.Temp,6:0.#} °C: drift {F(r.Drift.Mean)} +/- {F(3 * r.Drift.Std)} (3 sigma), tempco {r.Tempco.Mean:0.##} +/- {3 * r.Tempco.Std:0.##} ppm/°C");
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("Samples were independent at each temperature, so the per-corner");
                sb.AppendLine("statistics include both temperature and tolerance effects; per-unit");
                sb.AppendLine("drift needs the paired generator (Setup tab).");
            }
            if (rs.AnySpec)
            {
                sb.AppendLine();
                if (rs.Paired)
                {
                    TempAnalysis.PassAllTemps(rs, out int pass, out int units);
                    Stats.ClopperPearson(pass, units, 0.95, out var lo, out var hi);
                    sb.AppendLine($"Units passing every spec at ALL temperatures: {pass}/{units} = {MeasStats.Pct(units > 0 ? (double)pass / units : double.NaN)}");
                    sb.AppendLine($"  (95 % CI {MeasStats.Pct(lo)} .. {MeasStats.Pct(hi)})");
                }
                foreach (var r in rows)
                    if (r.S.Spec?.IsSet == true) sb.AppendLine($"  {m} yield at {r.Temp,6:0.#} °C: {MeasStats.Pct(r.S.Cap.Yield)}");
            }
            return sb.ToString();
        }

        string BuildTempRandom(Win98Chart ch, ListView lv, string m)
        {
            ch.Clear();
            lblTempMode.Text = "Random temperature per run: regression on temperature.";
            var rep = TempAnalysis.Random(rs, m);
            rs.Pairs(ResultSet.TempColumn, m, 0, out var t, out var y, out _);
            ch.Title = $"{m} vs temperature";
            ch.XLabel = "temperature (°C)"; ch.YLabel = m;
            ch.Add(new ChartSeries { Kind = SeriesKind.Points, X = t, Y = y, Color = Win98Chart.Navy, Size = 3, Label = "runs" });
            if (t.Length > 2)
            {
                var lx = new[] { t.Min(), t.Max() };
                ch.Add(new ChartSeries { Kind = SeriesKind.Line, X = lx, Y = lx.Select(rep.Fit.Predict).ToArray(), Color = Win98Chart.Green, Size = 2, Label = "linear fit" });
                ch.Add(new ChartSeries { Kind = SeriesKind.Line, X = lx, Y = lx.Select(x => rep.Fit.Predict(x) + 3 * rep.SigmaResidual).ToArray(), Color = Win98Chart.Purple, Dashed = true, Label = "fit +/- 3 sigma residual" });
                ch.Add(new ChartSeries { Kind = SeriesKind.Line, X = lx, Y = lx.Select(x => rep.Fit.Predict(x) - 3 * rep.SigmaResidual).ToArray(), Color = Win98Chart.Purple, Dashed = true, InLegend = false });
            }
            var spec = rs.Setup.Spec(m);
            if (spec != null && spec.IsSet) ch.Add(new ChartSeries { Kind = SeriesKind.HLine, Y = new[] { spec.Lsl, spec.Usl }.Where(v => !double.IsNaN(v)).ToArray(), Color = Win98Chart.Red, Label = "spec limits" });

            lv.BeginUpdate();
            lv.Columns.Clear();
            foreach (var (h, w) in new[] { ("Band °C", 110), ("n", 50), ("Mean", 84), ("Std dev", 84), ("Min", 84), ("Max", 84), ("Yield", 70), ("Cpk", 56) })
                lv.Columns.Add(h, w, HorizontalAlignment.Right);
            foreach (var b in rep.Bands)
            {
                var s = b.Item3;
                var it = new ListViewItem($"{b.Item1:0.#} .. {b.Item2:0.#}");
                foreach (var x in new[] { s.D.N.ToString(Inv), F(s.D.Mean), F(s.D.Std), F(s.D.Min), F(s.D.Max), s.Spec?.IsSet == true ? MeasStats.Pct(s.Cap.Yield) : "", Num(s.Cap.Cpk, "0.000") })
                    it.SubItems.Add(x);
                lv.Items.Add(it);
            }
            lv.EndUpdate();

            var sb = new StringBuilder();
            if (t.Length < 3) return "Not enough data.";
            var f = rep.Fit;
            sb.AppendLine($"{m} vs temperature, n = {t.Length}, T = {F(t.Min())} .. {F(t.Max())} °C");
            sb.AppendLine();
            sb.AppendLine($"Slope: {F(f.Slope)} per °C  (+/- {F(1.96 * f.SlopeSe)}, 95 %)");
            sb.AppendLine($"Tempco: {rep.TempcoPpm:0.###} ppm/°C  (+/- {rep.TempcoPpmCi:0.###})");
            sb.AppendLine($"R2 = {f.R2:0.0000}: temperature explains {MeasStats.Pct(f.R2)} of the variance.");
            sb.AppendLine();
            sb.AppendLine("Where the spread comes from (RSS):");
            sb.AppendLine($"  total sigma        {F(rep.StdY)}");
            sb.AppendLine($"  from temperature   {F(rep.SigmaTemp)}   (= |slope| x sigma_T, sigma_T = {F(rep.StdT)} °C)");
            sb.AppendLine($"  from tolerances    {F(rep.SigmaResidual)}   (scatter around the fit)");
            sb.AppendLine($"  check: sqrt(temp^2 + tol^2) = {F(Math.Sqrt(rep.SigmaTemp * rep.SigmaTemp + rep.SigmaResidual * rep.SigmaResidual))}");
            sb.AppendLine();
            double tmin = t.Min(), tmax = t.Max();
            sb.AppendLine("Extrapolated band at the range ends (fit +/- 3 sigma tolerance):");
            sb.AppendLine($"  {tmin,7:0.#} °C: {F(f.Predict(tmin) - 3 * rep.SigmaResidual)} .. {F(f.Predict(tmin) + 3 * rep.SigmaResidual)}");
            sb.AppendLine($"  {tmax,7:0.#} °C: {F(f.Predict(tmax) - 3 * rep.SigmaResidual)} .. {F(f.Predict(tmax) + 3 * rep.SigmaResidual)}");
            sb.AppendLine("Few runs fall at the very ends of a flat range: use corners to verify them.");
            return sb.ToString();
        }

        // =====================================================================================
        //  Convergence
        // =====================================================================================

        Win98TabPage BuildConvPage()
        {
            var p = new Win98TabPage("Convergence", Win98.Icon("chart16"));
            var bar = Bar(30);
            cbConvMeas = Combo(82, 2, 150);
            cbConvGroup = Combo(290, 2, 150);
            bar.Controls.AddRange(new Control[] { Lbl("Measurement:", 0, 2), cbConvMeas, Lbl("Group:", 246, 2), cbConvGroup });
            chConvMean = new Win98Chart();
            chConvSigma = new Win98Chart();
            lblConv = new Label { Dock = DockStyle.Bottom, Height = 34, BackColor = Win98.Face };
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Win98.Face };
            var sigmaWell = new Win98Well(chConvSigma, Color.White) { Dock = DockStyle.Bottom, Height = 200 };
            body.Controls.Add(ChartWell(chConvMean));
            body.Controls.Add(Gap(DockStyle.Bottom));
            body.Controls.Add(sigmaWell);
            body.Resize += (s, e) => sigmaWell.Height = Math.Max(120, body.Height / 2 - 3);
            p.Controls.Add(body);
            p.Controls.Add(Gap(DockStyle.Top));
            p.Controls.Add(bar);
            p.Controls.Add(lblConv);
            return p;
        }

        void RefreshConv()
        {
            if (rs == null || !(cbConvMeas.SelectedItem is string m)) { chConvMean.Clear(); chConvSigma.Clear(); lblConv.Text = ""; return; }
            lblConv.Text = BuildConvergence(chConvMean, chConvSigma, m, Math.Max(0, cbConvGroup.SelectedIndex));
        }

        string BuildConvergence(Win98Chart chMean, Win98Chart chSigma, string m, int group)
        {
            chMean.Clear(); chSigma.Clear();
            int col = rs.Col(m);
            // in run order: what the campaign would have shown after n runs
            var vals = rs.InGroup(group).Where(r => r.Status == RunStatus.Ok && col >= 0 && !double.IsNaN(r.V[col])).OrderBy(r => r.Run).ThenBy(r => r.Corner).Select(r => r.V[col]).ToArray();
            chMean.Title = $"Running mean of {m} (95 % confidence band)";
            chSigma.Title = $"Running sigma of {m}" + (rs.Setup.Spec(m)?.IsSet == true ? " and yield" : "");
            chMean.XLabel = chSigma.XLabel = "runs";
            if (vals.Length < 3) return "";
            int n = vals.Length;
            int step = Math.Max(1, n / 400);
            var xs = new List<double>(); var mean = new List<double>(); var lo = new List<double>(); var hi = new List<double>(); var sd = new List<double>(); var yld = new List<double>();
            var spec = rs.Setup.Spec(m);
            double s1 = 0, s2 = 0; int pass = 0;
            for (int i = 0; i < n; i++)
            {
                s1 += vals[i]; s2 += vals[i] * vals[i];
                if (spec != null && spec.IsSet && spec.Passes(vals[i])) pass++;
                int k = i + 1;
                if (k < 3 || (k % step != 0 && k != n)) continue;
                double mu = s1 / k, var = Math.Max(0, (s2 - k * mu * mu) / (k - 1)), sg = Math.Sqrt(var), h = 1.96 * sg / Math.Sqrt(k);
                xs.Add(k); mean.Add(mu); lo.Add(mu - h); hi.Add(mu + h); sd.Add(sg); yld.Add((double)pass / k);
            }
            chMean.Add(new ChartSeries { Kind = SeriesKind.Line, X = xs.ToArray(), Y = mean.ToArray(), Color = Win98Chart.Navy, Size = 2, Label = "mean" });
            chMean.Add(new ChartSeries { Kind = SeriesKind.Line, X = xs.ToArray(), Y = lo.ToArray(), Color = Win98Chart.Teal, Dashed = true, Label = "95 % band" });
            chMean.Add(new ChartSeries { Kind = SeriesKind.Line, X = xs.ToArray(), Y = hi.ToArray(), Color = Win98Chart.Teal, Dashed = true, InLegend = false });
            chSigma.Add(new ChartSeries { Kind = SeriesKind.Line, X = xs.ToArray(), Y = sd.ToArray(), Color = Win98Chart.Maroon, Size = 2, Label = "sigma" });
            if (spec != null && spec.IsSet)
            {
                // yield drawn on the sigma chart's scale would be meaningless; give it its own axis by scaling to sigma's range
                double smin = sd.Min(), smax = sd.Max();
                double ymin = yld.Min(), ymax = yld.Max();
                if (ymax > ymin && smax > smin)
                    chSigma.Add(new ChartSeries { Kind = SeriesKind.Line, X = xs.ToArray(), Y = yld.Select(v => smin + (v - ymin) / (ymax - ymin) * (smax - smin)).ToArray(), Color = Win98Chart.Green, Label = $"yield (scaled {MeasStats.Pct(ymin)}..{MeasStats.Pct(ymax)})" });
            }
            var d = Descriptive.Of(vals);
            double half = 1.96 * d.Std / Math.Sqrt(n);
            double rel = Math.Abs(d.Mean) > 0 ? half / Math.Abs(d.Mean) : double.NaN;
            return $"After {n} runs the mean is known to +/-{F(half)} ({MeasStats.Pct(rel)}) and sigma to +/-{MeasStats.Pct(1.96 / Math.Sqrt(2.0 * (n - 1)))} (95 %).\r\n" +
                   $"Halving these intervals needs about {4 * n} runs; the band narrows as 1/sqrt(n).";
        }

        // =====================================================================================
        //  Export
        // =====================================================================================

        public void ExportCsv()
        {
            if (rs == null) return;
            using (var d = new SaveFileDialog { Title = "Export Results", Filter = "CSV files (*.csv)|*.csv", FileName = "results.csv", InitialDirectory = rs.Folder ?? "", AutoUpgradeEnabled = false })
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                var sb = new StringBuilder("run,temp,status");
                foreach (var m in rs.Meas) sb.Append(',').Append(CampaignEngine.Csv(m));
                if (rs.AnySpec) sb.Append(",spec");
                sb.Append(",note\r\n");
                foreach (var r in rs.Rows)
                {
                    sb.Append(r.Run.ToString(Inv)).Append(',').Append(double.IsNaN(r.Temp) ? "" : r.Temp.ToString("R", Inv)).Append(',').Append(r.Status);
                    foreach (var t in r.Text) sb.Append(',').Append(CampaignEngine.Csv(t));
                    if (rs.AnySpec) { var p = rs.RowPasses(r); sb.Append(',').Append(p == null ? "" : p.Value ? "pass" : "fail"); }
                    sb.Append(',').Append(CampaignEngine.Csv(r.Note)).Append("\r\n");
                }
                File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(false));
            }
        }

        void ExportReport()
        {
            if (rs == null) return;
            using (var d = new SaveFileDialog { Title = "Save Analysis Report", Filter = "HTML report (*.html)|*.html", FileName = "SPICA_report.html", InitialDirectory = rs.Folder ?? "", AutoUpgradeEnabled = false })
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                Cursor = Cursors.WaitCursor;
                try
                {
                    File.WriteAllText(d.FileName, BuildReport(), new UTF8Encoding(false));
                    System.Diagnostics.Process.Start(d.FileName);
                }
                catch (Exception ex) { Win98MessageBox.Show(FindForm(), "Could not write the report:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); }
                finally { Cursor = Cursors.Default; }
            }
        }

        /// <summary>Writes the HTML report without dialogs (also used by tests).</summary>
        public void WriteReport(string path) => File.WriteAllText(path, BuildReport(), new UTF8Encoding(false));

        string BuildReport()
        {
            string H(string s) => WebUtility.HtmlEncode(s ?? "");
            string Img(Win98Chart c, int w = 760, int h = 340)
            {
                using (var bmp = c.ToBitmap(w, h))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return $"<img src=\"data:image/png;base64,{Convert.ToBase64String(ms.ToArray())}\" width=\"{w}\" height=\"{h}\">";
                }
            }
            string Pre(string s) => "<pre>" + H(s) + "</pre>";

            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>SPICA report</title><style>")
              .Append("body{background:#c0c0c0;font:12px 'Microsoft Sans Serif',Tahoma,Arial,sans-serif;margin:16px;color:#000}")
              .Append("h1{background:linear-gradient(90deg,#000080,#1084d0);color:#fff;font-size:14px;padding:4px 8px;margin:0 0 12px}")
              .Append("h2{font-size:13px;border-bottom:1px solid #808080;box-shadow:0 1px 0 #fff;padding-bottom:2px;margin-top:22px}")
              .Append("table{border-collapse:collapse;background:#fff;border:2px inset #fff}td,th{border:1px solid #c0c0c0;padding:2px 6px;text-align:right;white-space:nowrap}")
              .Append("th{background:#c0c0c0;border:2px outset #fff}td:first-child{text-align:left}pre{background:#fff;border:2px inset #fff;padding:6px;font:11px 'Lucida Console',monospace;overflow:auto}")
              .Append("img{border:2px inset #fff;background:#fff;display:block;margin:6px 0}.box{max-width:100%;overflow-x:auto}</style></head><body>");
            sb.Append($"<h1>SPICA analysis report</h1>");
            var st = rs.Setup;
            sb.Append("<table>");
            void Row(string k, string v) => sb.Append($"<tr><td>{H(k)}</td><td style=\"text-align:left\">{H(v)}</td></tr>");
            Row("Campaign", rs.Title);
            Row("Folder", rs.Folder);
            Row("Schematic", st.Schematic);
            Row("Rows", $"{rs.Rows.Count} ({rs.Rows.Count(r => r.Status == RunStatus.Ok)} with data)");
            Row("Temperature", rs.Mode == TempMode.Corners ? "corners " + string.Join(", ", rs.Corners.Select(t => t.ToString("0.###", Inv))) + " °C" : rs.Mode == TempMode.Random ? "random per run" : "schematic default");
            Row("Sampling", rs.Paired ? $"SPICA paired generator, seed {rs.Seed}" : "LTspice mc()");
            Row("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv));
            sb.Append("</table>");

            var groups = rs.GroupNames();
            for (int g = 0; g < groups.Count; g++)
            {
                sb.Append($"<h2>Statistics - {H(groups[g])}</h2><div class=\"box\"><table><tr>");
                foreach (var h in new[] { "Measurement", "n", "Mean", "Std dev", "Min", "Max", "Median", "P0.135%", "P99.865%", "Skew", "Ex.kurt", "Normal p", "LSL", "USL", "Yield", "Yield 95% CI", "Cpk", "Pred. ppm" })
                    sb.Append($"<th>{H(h)}</th>");
                sb.Append("</tr>");
                foreach (var m in rs.Meas)
                {
                    var s = MeasStats.Of(m, groups[g], rs.Values(m, g), st.Spec(m));
                    bool sp = s.Spec?.IsSet == true;
                    var cells = new[] { m, s.D.N.ToString(Inv), F(s.D.Mean), F(s.D.Std), F(s.D.Min), F(s.D.Max), F(s.D.Median), F(s.P00135), F(s.P99865),
                        Num(s.D.Skew, "0.000"), Num(s.D.ExKurt, "0.000"), MeasStats.P(s.AdP), sp ? F(s.Spec.Lsl) : "", sp ? F(s.Spec.Usl) : "",
                        sp ? MeasStats.Pct(s.Cap.Yield) : "", sp ? $"{MeasStats.Pct(s.Cap.YieldLo)} .. {MeasStats.Pct(s.Cap.YieldHi)}" : "", Num(s.Cap.Cpk, "0.000"),
                        sp && s.NormalOk ? MeasStats.Ppm(s.Cap.PredictedPpm) : "" };
                    sb.Append("<tr>").Append(string.Concat(cells.Select(c => $"<td>{H(c)}</td>"))).Append("</tr>");
                }
                sb.Append("</table></div>");
                if (rs.Mode != TempMode.Corners) break;   // other modes have a single group
            }

            using (var c1 = new Win98Chart())
            using (var c2 = new Win98Chart())
            {
                foreach (var m in rs.Meas)
                {
                    sb.Append($"<h2>{H(m)}</h2>");
                    var s = BuildHistogram(c1, m, 0, true);
                    sb.Append(Img(c1));
                    sb.Append(Pre(s.Narrative()));
                    sb.Append(Pre(BuildConvergence(c1, c2, m, 0)));
                    sb.Append(Img(c1, 760, 240));
                    if (rs.Mode == TempMode.Corners) { var lv = new ListView(); sb.Append(Pre(BuildTempCorners(c1, lv, m))); sb.Append(Img(c1)); }
                    else if (rs.Mode == TempMode.Random) { var lv = new ListView(); sb.Append(Pre(BuildTempRandom(c1, lv, m))); sb.Append(Img(c1)); }
                }
                var tf = st.Tf;
                if (tf.X.Length > 0 && tf.Y.Length > 0 && rs.Col(tf.Y) >= 0)
                {
                    sb.Append($"<h2>Transfer function: {H(tf.Y)} vs {H(tf.X)}</h2>");
                    for (int g = 0; g < groups.Count; g++)
                    {
                        var rep = BuildTf(c1, c2, tf, g);
                        if (groups.Count > 1) sb.Append($"<p><b>{H(groups[g])}</b></p>");
                        sb.Append(Img(c1)).Append(Img(c2, 760, 240)).Append(Pre(rep.Narrative()));
                        if (rs.Mode != TempMode.Corners) break;
                    }
                }
            }
            sb.Append("<p style=\"color:#808080\">Generated by SPICA - Spice Parallel Instancer Circuit Analysis.</p></body></html>");
            return sb.ToString();
        }
    }
}
