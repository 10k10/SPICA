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
    /// The Results tab: Table | Statistics | Histogram | Scatter & TF | Sensitivity | Temperature | Convergence | Worst Case,
    /// for the running campaign (refreshed while it runs, unless paused) or any campaign folder opened from disk.
    /// </summary>
    public sealed class ResultsView : Panel
    {
        public event Action SetupChanged;
        /// <summary>The result set changed: the Results menu enables its commands from <see cref="CanExport"/> / <see cref="CanOpenFolder"/>.</summary>
        public event Action DataChanged;

        ResultSet rs;
        bool live;
        string columnsKey = "";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly Label lblTitle;
        readonly Win98TabControl tabs;
        Win98CheckBox chkLive;
        Panel topBar;
        string titleText = "";
        readonly List<Win98Chart> charts = new List<Win98Chart>();

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
        ComboBox cbHistMeas, cbHistGroup, cbHistView, cbHistBins;
        Win98CheckBox chkHistNormal;
        Win98Chart chHist;
        Label lblHist;

        // scatter & TF
        ComboBox cbTfX, cbTfY, cbTfGroup;
        TextBox txtTheory;
        Win98Chart chTf, chTfErr;

        // sensitivity
        ComboBox cbSensMeas, cbSensGroup, cbSensInputs, cbSensShow;
        Win98Chart chSens;
        Win98ListView lvSens;
        TextBox txtSens;
        SensitivityReport sensRep;

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
            lblTitle = new Label { AutoSize = false, Location = new Point(2, 8), Height = 16, BackColor = Win98.Face, Text = "No results yet. Run a campaign, or open a campaign folder (Results > Open Results).", AutoEllipsis = true };
            chkLive = new Win98CheckBox { Text = "&Live update", Checked = true, Visible = false, Width = 96 };
            chkLive.CheckedChanged += (s, e) => UpdateTitle();
            top.Controls.AddRange(new Control[] { lblTitle, chkLive });
            topBar = top;
            top.Resize += (s, e) => LayoutTop();

            tabs = new Win98TabControl { Dock = DockStyle.Fill };
            tabs.AddPage(pgBudget = BuildBudgetPage());
            tabs.AddPage(pgTable = BuildTablePage());
            tabs.AddPage(pgStats = BuildStatsPage());
            tabs.AddPage(pgHist = BuildHistPage());
            tabs.AddPage(pgTf = BuildTfPage());
            tabs.AddPage(pgSens = BuildSensPage());
            tabs.AddPage(pgTemp = BuildTempPage());
            tabs.AddPage(pgConv = BuildConvPage());
            tabs.AddPage(pgWc = BuildWcPage());
            tabs.SelectedIndexChanged += (s, e) => RefreshPage();

            Controls.Add(tabs);
            Controls.Add(top);
            filling = false;
        }

        public ResultSet Data => rs;

        /// <summary>The user froze the live view: building new result sets would be wasted work.</summary>
        public bool LivePaused => live && !chkLive.Checked;

        public int PageCount => tabs.Pages.Count;
        public void SelectPage(int i) => tabs.SelectedIndex = i;

        // =====================================================================================
        //  Data binding
        // =====================================================================================

        /// <summary>Shows a result set. <paramref name="isLive"/>: the campaign is still running.</summary>
        public void SetData(ResultSet set, bool isLive)
        {
            // paused: keep analysing the snapshot on screen while the campaign goes on
            if (isLive && live && rs != null && set != null && !chkLive.Checked) return;
            rs = set;
            live = isLive;
            if (chkLive.Visible != isLive) { chkLive.Visible = isLive; chkLive.Checked = true; LayoutTop(); }
            titleText = set == null ? "No results." :
                $"{set.Title}    {set.Rows.Count(r => r.Status == RunStatus.Ok)} of {set.Rows.Count} rows with data" +
                (set.Mode == TempMode.Corners ? $", {set.Corners.Count} temperatures" : set.Mode == TempMode.Random ? ", random temperature" : "") +
                (set.Paired ? $", paired samples ({set.Params.Count} random parameters)" : "");
            UpdateTitle();
            string key = set == null ? "" : string.Join("|", set.AxisColumns()) + "#" + string.Join(",", set.GroupNames());
            if (key != columnsKey)
            {
                columnsKey = key;
                FillChoices();
            }
            DataChanged?.Invoke();
            // a worst-case campaign opens on its own page (once, so the user can move away)
            bool isWc = set != null && set.Setup.WorstCase != WcMethod.Off;
            if (set != null) ErrorBudget.Register(set, set.Setup.Errors);
            if (isWc && !shownWc) { shownWc = true; if (!Showing(pgWc)) { Show(pgWc); return; } }
            if (!isWc) shownWc = false;
            RefreshPage();
        }

        bool shownWc;

        void LayoutTop()
        {
            int x = topBar.Width - 4;
            if (chkLive.Visible) { x -= chkLive.Width; chkLive.Location = new Point(x, 6); x -= 8; }
            lblTitle.Width = Math.Max(10, x - 4);
        }

        void UpdateTitle()
        {
            lblTitle.Text = titleText + (!live ? "" : chkLive.Checked ? "   (running: updates every second)" : "   (running: paused - tick Live update to resume)");
        }

        public bool CanExport => rs != null && rs.Rows.Count > 0;
        public bool CanOpenFolder => rs != null && Directory.Exists(rs.Folder ?? "");

        void FillChoices()
        {
            filling = true;
            var meas = rs?.Meas ?? new List<string>();
            var cols = rs?.NumericColumns() ?? new List<string>();
            var groups = rs?.GroupNames() ?? new List<string> { "All runs" };
            Fill(cbHistMeas, meas); Fill(cbConvMeas, meas); Fill(cbTempMeas, meas); Fill(cbSpecMeas, meas); Fill(cbSensMeas, meas);
            Fill(cbBudY, meas); Fill(cbBudGroup, groups);
            Fill(cbBudX, new[] { "(none)" }.Concat(meas).ToList());
            Fill(cbStatGroup, groups); Fill(cbHistGroup, groups); Fill(cbTfGroup, groups); Fill(cbConvGroup, groups); Fill(cbSensGroup, groups);
            var axes = rs?.AxisColumns() ?? new List<string>();
            Fill(cbTfX, axes); Fill(cbTfY, axes);
            if (rs != null && cbSensInputs.Items.Count > 0) cbSensInputs.SelectedIndex = rs.Params.Count > 0 || rs.Mode == TempMode.Random ? 0 : 1;
            var tf = rs?.Setup.Tf;
            if (tf != null)
            {
                Select(cbTfX, tf.X.Length > 0 ? tf.X : (meas.Count > 1 ? meas[0] : null));
                Select(cbTfY, tf.Y.Length > 0 ? tf.Y : (meas.Count > 1 ? meas[1] : meas.FirstOrDefault()));
                txtTheory.Text = tf.Theory;
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
            if (filling || tabs == null || tabs.SelectedIndex < 0) return;
            var p = tabs.Pages[tabs.SelectedIndex];
            if (p == pgBudget) RefreshBudget();
            else if (p == pgTable) RefreshTable();
            else if (p == pgStats) RefreshStats();
            else if (p == pgHist) RefreshHist();
            else if (p == pgTf) RefreshTf();
            else if (p == pgSens) RefreshSens();
            else if (p == pgTemp) RefreshTemp();
            else if (p == pgConv) RefreshConv();
            else if (p == pgWc) RefreshWc();
        }

        Win98TabPage pgBudget, pgTable, pgStats, pgHist, pgTf, pgSens, pgTemp, pgConv, pgWc;

        void Show(Win98TabPage p)
        {
            for (int i = 0; i < tabs.Pages.Count; i++) if (tabs.Pages[i] == p) { tabs.SelectedIndex = i; return; }
        }

        bool Showing(Win98TabPage p) => tabs.SelectedIndex >= 0 && tabs.Pages[tabs.SelectedIndex] == p;

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
            cb.SelectedIndexChanged += (s, e) => { if (!filling) foreach (var c in charts) c.ResetView(); RefreshPage(); };
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

        Win98Well ChartWell(Win98Chart c) => new Win98Well(Track(c), Color.White) { Dock = DockStyle.Fill };

        /// <summary>Charts on the pages: their zoom is reset when the selection changes.</summary>
        Win98Chart Track(Win98Chart c) { charts.Add(c); return c; }

        /// <summary>Grey hint text on a toolbar row.</summary>
        static Label Hint(string text, int x, int y) => new Label { Text = text, Location = new Point(x, y + 3), AutoSize = true, BackColor = Win98.Face, ForeColor = Win98.Shadow };

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
            var tableHint = Hint("Click a column to sort. Values in engineering notation; Export CSV keeps full precision.", 0, 2);
            bar.Controls.Add(tableHint);
            bar.Resize += (s, e) => tableHint.Left = Math.Max(lblTable.Right + 24, bar.Width - tableHint.Width - 4);

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
            for (int c = 0; c < rs.Meas.Count; c++) it.SubItems.Add(double.IsNaN(r.V[c]) ? r.Text[c] : F(r.V[c]));
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
        //  Error budget
        // =====================================================================================

        Win98ListView lvBud, lvBudCond;
        TextBox txtBudSigma, txtBudName, txtBudTf, txtBudGain, txtBudOff, txtBudSym, txtBudLo, txtBudHi, txtBudFs;
        ComboBox cbBudGroup, cbBudChart, cbBudY, cbBudX, cbBudUnit;
        Win98CheckBox chkBudPess, chkBudRefer, chkBudSym;
        Win98RadioButton rbBudMeas, rbBudCmp, rbBudLin, rbBudExpr;
        Label lblBudY, lblBudX, lblBudFormula, lblBudVerdict, lblBudNotes;
        Panel pnlBudIdeal;
        Win98Chart chBud, chBudSens;
        Win98Tile[] budTiles;
        readonly List<ErrorResult> budRows = new List<ErrorResult>();
        ErrorResult budShown;
        ErrorSlice budSlice;         // the condition in the tiles and the chart (the worst one unless picked in the table)
        bool loadingBud;

        static readonly Color PassBack = Color.FromArgb(222, 242, 222), FailBack = Color.FromArgb(250, 222, 222), CheckBack = Color.FromArgb(246, 238, 204);

        Win98TabPage BuildBudgetPage()
        {
            var p = new Win98TabPage("Error Budget", Win98.Icon("results16"));
            var bar = Bar(30);
            txtBudSigma = Field(84, 2, 36);
            txtBudSigma.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ApplyBudgetSigma(); e.SuppressKeyPress = true; } };
            txtBudSigma.Leave += (s, e) => ApplyBudgetSigma();
            chkBudPess = new Win98CheckBox { Text = "Pessimistic sigma (95 % upper bound)", Location = new Point(132, 4), Width = 230 };
            chkBudPess.CheckedChanged += (s, e) => { if (filling || rs == null) return; rs.Setup.PessimisticSigma = chkBudPess.Checked; rs.Setup.Dirty = true; NotifySetup(); RefreshBudget(); };
            cbBudGroup = Combo(414, 2, 140);
            bar.Controls.AddRange(new Control[] { Lbl("Design sigma:", 0, 2), txtBudSigma, chkBudPess, Lbl("Group:", 370, 2), cbBudGroup,
                Hint("PASS: mean +/- k sigma inside the boundaries in every condition, and the error bell-shaped.", 566, 2) });

            // ---------------------------------------------------------------- left: the definition
            var grp = new Win98GroupBox { Text = "Error definition", Dock = DockStyle.Left, Width = 292 };
            const int L = 10, FX = 84, FW = 196;
            txtBudName = Field(FX, 20, FW);

            var style = new Panel { Location = new Point(6, 46), Size = new Size(280, 42), BackColor = Win98.Face };
            rbBudMeas = new Win98RadioButton { Text = "The error is already a .meas", Location = new Point(4, 0), Width = 270, Checked = true };
            rbBudCmp = new Win98RadioButton { Text = "Compare an output with a reference", Location = new Point(4, 21), Width = 270 };
            style.Controls.AddRange(new Control[] { rbBudMeas, rbBudCmp });

            lblBudY = Lbl("Error:", L, 92);
            cbBudY = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(FX, 92), Width = FW, Font = Win98.Font };
            lblBudX = Lbl("Reference:", L, 120);
            cbBudX = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(FX, 120), Width = FW, Font = Win98.Font };

            pnlBudIdeal = new Panel { Location = new Point(6, 146), Size = new Size(280, 96), BackColor = Win98.Face };
            rbBudLin = new Win98RadioButton { Text = "Ideal = gain", Location = new Point(4, 2), Width = 86, Checked = true };
            txtBudGain = Field(92, 0, 52);
            var lblOff = new Label { Text = "x ref +", Location = new Point(148, 3), AutoSize = true, BackColor = Win98.Face };
            txtBudOff = Field(196, 0, 78);
            rbBudExpr = new Win98RadioButton { Text = "Ideal y =", Location = new Point(4, 26), Width = 86 };
            txtBudTf = Field(92, 24, 182);
            chkBudRefer = new Win98CheckBox { Text = "Refer to the input (divide by the gain)", Location = new Point(4, 50), Width = 270, Checked = true };
            lblBudFormula = new Label { Location = new Point(4, 74), Size = new Size(274, 18), BackColor = Win98.Face, ForeColor = Win98Chart.Navy, UseMnemonic = false, AutoEllipsis = true };
            pnlBudIdeal.Controls.AddRange(new Control[] { rbBudLin, txtBudGain, lblOff, txtBudOff, rbBudExpr, txtBudTf, chkBudRefer, lblBudFormula });

            var lblBounds = new Label { Text = "Boundaries", Location = new Point(L, 250), AutoSize = true, BackColor = Win98.Face, Font = new Font(Win98.Font, FontStyle.Bold) };
            chkBudSym = new Win98CheckBox { Text = "Symmetric   ±", Location = new Point(L, 270), Width = 100, Checked = true };
            txtBudSym = Field(FX + 30, 270, 80);
            txtBudLo = Field(FX - 30, 298, 70);
            txtBudHi = Field(FX + 110, 298, 70);
            cbBudUnit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(FX - 30, 326), Width = 120, Font = Win98.Font };
            cbBudUnit.Items.AddRange(new object[] { "absolute", "% of reading", "% of full scale" });
            cbBudUnit.SelectedIndex = 0;
            txtBudFs = Field(FX + 154, 326, 44);

            var btnAdd = new Win98Button { Text = "&Add", Location = new Point(L, 362), Size = new Size(84, 24) };
            var btnUpd = new Win98Button { Text = "&Update", Location = new Point(L + 92, 362), Size = new Size(84, 24) };
            var btnDel = new Win98Button { Text = "&Delete", Location = new Point(L + 184, 362), Size = new Size(84, 24) };
            btnAdd.Click += (s, e) => SaveBudgetItem(true);
            btnUpd.Click += (s, e) => SaveBudgetItem(false);
            btnDel.Click += (s, e) => DeleteBudgetItem();
            var hint = new Label
            {
                Location = new Point(L, 396), Size = new Size(272, 120), BackColor = Win98.Face, ForeColor = Win98.Shadow,
                Text = "Output vs reference: run by run, error = output - ideal. The reference is a measurement (e.g. vin, or a .meas " +
                       "holding the expected value: gain 1) or a number.\n\nLeave a boundary empty for a one-sided limit.",
            };

            grp.Controls.AddRange(new Control[] { Lbl("Name:", L, 20), txtBudName, style, lblBudY, cbBudY, lblBudX, cbBudX, pnlBudIdeal,
                lblBounds, chkBudSym, txtBudSym, Lbl("Lower:", L, 298), txtBudLo, Lbl("Upper:", FX + 66, 298), txtBudHi,
                Lbl("Unit:", L, 326), cbBudUnit, Lbl("FS:", FX + 132, 326), txtBudFs, btnAdd, btnUpd, btnDel, hint });

            EventHandler form = (s, e) => { if (!loadingBud) UpdateBudForm(); };
            foreach (var rb in new[] { rbBudMeas, rbBudCmp, rbBudLin, rbBudExpr }) rb.CheckedChanged += form;
            chkBudRefer.CheckedChanged += form;
            chkBudSym.CheckedChanged += (s, e) => { if (!loadingBud) SwapSymmetric(); };
            cbBudUnit.SelectedIndexChanged += form;
            cbBudX.SelectedIndexChanged += form;
            cbBudX.TextChanged += form;
            cbBudY.SelectedIndexChanged += (s, e) => { if (!loadingBud) { FillTfFromScatter(); UpdateBudForm(); } };
            foreach (var t in new[] { txtBudGain, txtBudOff, txtBudTf }) t.TextChanged += form;

            var tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400 };
            tip.SetToolTip(txtBudSigma, "The error must stay inside the boundaries at mean +/- this many sigma (6 = very conservative, 3 = 99.73 %).");
            tip.SetToolTip(chkBudPess, "Sigma from n runs is itself uncertain. Ticked: use its 95 % upper bound (about +12 % at 200 runs, +4 % at 2000).");
            tip.SetToolTip(cbBudX, "Output vs reference: the measurement the ideal output is computed from (e.g. vin), or a fixed number.\n" +
                                   "A .meas error with a % of reading boundary: the measurement the % is taken of.");
            tip.SetToolTip(txtBudTf, "Any expression of x (the reference) and T (temperature in °C), e.g. 2*x + 10m, or 2*x*(1 + 50u*(T-25)).");
            tip.SetToolTip(chkBudRefer, "Divide the error by the ideal gain (d ideal / dx): the error as seen at the input.");
            tip.SetToolTip(chkBudSym, "Ticked: one value, +/- around zero. Clear it to enter a lower and an upper boundary.");

            // ---------------------------------------------------------------- right: the verdict
            lvBud = new Win98ListView();
            foreach (var (t, w) in new[] { ("Error", 150), ("Verdict", 60), ("Margin", 60), ("Mean ± 6 sigma", 150), ("Boundaries", 110),
                                           ("Worst condition", 180), ("Definition", 300) })
                lvBud.Columns.Add(t, w, t == "Margin" ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            lvBud.CellColor = (i, c) =>
            {
                if (i >= budRows.Count) return null;
                var r = budRows[i];
                if (c == 1) return VerdictColor(r.Verdict);
                if (c == 2 && r.Margin < 0) return Win98Chart.Red;
                return null;
            };
            lvBud.HeaderTip = c => BudTips[c];
            lvBud.SelectedIndexChanged += (s, e) => ShowBudgetDetail();

            budTiles = new[] { "Verdict", "Mean", "Sigma", "Mean ± 6 sigma", "Low side", "High side", "Bell-shaped" }
                .Select(c => new Win98Tile { Caption = c }).ToArray();
            var tiles = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Win98.Face };
            tiles.Controls.AddRange(budTiles);
            tiles.Resize += (s, e) =>
            {
                // the range and the two sides hold longer text: wider boxes
                double[] weight = { 1, 0.9, 0.9, 1.6, 1.25, 1.25, 1 };
                int n = budTiles.Length, gap = 6, x = 0;
                double unit = (tiles.Width - gap * (n - 1)) / weight.Sum();
                for (int i = 0; i < n; i++)
                {
                    int w = i == n - 1 ? tiles.Width - x : (int)(weight[i] * unit);
                    budTiles[i].SetBounds(x, 0, w, 58);
                    x += w + gap;
                }
            };
            foreach (var (t, i) in new[] {
                ("PASS: mean ± k sigma inside the boundaries in every condition, and the error bell-shaped (or with shorter tails).\nFAIL: outside at k sigma, or units already outside.\nCHECK: not bell-shaped, the k-sigma figure cannot decide.", 0),
                ("Average error in this condition: the same in every unit, so a better TF or a calibration can remove it.", 1),
                ("Unit-to-unit spread in this condition (its 95 % upper bound when Pessimistic sigma is ticked).", 2),
                ("The extrapolated range of a production unit.", 3),
                ("How many sigma the lower boundary is from the mean (needed: k), and the room left at k sigma.", 4),
                ("How many sigma the upper boundary is from the mean (needed: k), and the room left at k sigma.", 5),
                ("Is the error a normal (bell) curve? Anderson-Darling test. Only then is mean ± k sigma a valid extrapolation\n(or when the tails are shorter than a normal's, as with flat tolerances adding up: then it is a safe over-estimate).", 6) })
                tip.SetToolTip(budTiles[i], t);

            lblBudVerdict = new Label { Dock = DockStyle.Top, Height = 22, BackColor = Win98.Face, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, AutoEllipsis = true };

            chBud = Track(new Win98Chart());
            chBudSens = Track(new Win98Chart());
            cbBudChart = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(0, 0), Width = 150, Font = Win98.Font };
            cbBudChart.Items.AddRange(new object[] { "Histogram", "Error vs input" });
            cbBudChart.SelectedIndex = 0;
            cbBudChart.SelectedIndexChanged += (s, e) => ShowBudgetSlice();

            var chartPanel = new Panel { Dock = DockStyle.Fill, BackColor = Win98.Face };
            var chartBar = Bar(26);
            chartBar.Controls.Add(cbBudChart);
            chartPanel.Controls.Add(ChartWell(chBud));
            chartPanel.Controls.Add(chartBar);
            var sensWell = new Win98Well(chBudSens, Color.White) { Dock = DockStyle.Right, Width = 330 };
            var middle = new Panel { Dock = DockStyle.Fill, BackColor = Win98.Face };
            middle.Controls.Add(chartPanel);
            middle.Controls.Add(Gap(DockStyle.Right));
            middle.Controls.Add(sensWell);
            middle.Resize += (s, e) => sensWell.Width = Math.Max(240, Math.Min(380, middle.Width / 3));

            lvBudCond = new Win98ListView();
            foreach (var (t, w) in new[] { ("Condition", 190), ("n", 50), ("Mean", 80), ("Sigma", 80), ("Mean ± 6 sigma", 150), ("Low side", 76), ("High side", 76),
                                           ("Margin", 60), ("Bell-shaped", 90), ("Verdict", 60) })
                lvBudCond.Columns.Add(t, w, t == "Condition" || t == "Bell-shaped" || t == "Verdict" ? HorizontalAlignment.Left : HorizontalAlignment.Right);
            lvBudCond.CellColor = (i, c) =>
            {
                if (budShown == null || i >= budShown.Slices.Count) return null;
                var s = budShown.Slices[i];
                if (c == 9) return VerdictColor(s.Verdict);
                if (c == 5) return SideColor(s.SigmasLo, s.MarginLo);
                if (c == 6) return SideColor(s.SigmasHi, s.MarginHi);
                if (c == 7 && s.Margin < 0) return Win98Chart.Red;
                if (c == 8) return s.Normal ? Win98Chart.Green : s.TailsOk ? Win98Chart.Olive : Win98Chart.Maroon;
                return null;
            };
            lvBudCond.HeaderTip = c => c == 0 ? "Each error is judged per temperature and per band of the input range: units in the same condition differ only by their tolerances. Click a row to see it above."
                                     : c == 5 || c == 6 ? "Distance of the boundary from the mean, in sigma (needed: k)." : c == 7 ? "Room left at k sigma, as a share of half the allowed range." : null;
            lvBudCond.SelectedIndexChanged += (s, e) =>
            {
                if (budShown == null || lvBudCond.SelectedIndices.Count == 0) return;
                int i = lvBudCond.SelectedIndices[0];
                if (i < budShown.Slices.Count && budShown.Slices[i] != budSlice) { budSlice = budShown.Slices[i]; ShowBudgetSlice(); }
            };
            var condWell = new Win98Well(lvBudCond) { Dock = DockStyle.Bottom, Height = 150 };
            lblBudNotes = new Label { Dock = DockStyle.Bottom, Height = 40, BackColor = Win98.Face, ForeColor = Win98.Shadow, UseMnemonic = false };

            var right = new Panel { Dock = DockStyle.Fill, BackColor = Win98.Face };
            var listWell = new Win98Well(lvBud) { Dock = DockStyle.Top, Height = 104 };
            right.Controls.Add(middle);
            right.Controls.Add(Gap(DockStyle.Top));
            right.Controls.Add(lblBudVerdict);
            right.Controls.Add(tiles);
            right.Controls.Add(Gap(DockStyle.Top));
            right.Controls.Add(listWell);
            right.Controls.Add(lblBudNotes);
            right.Controls.Add(condWell);
            middle.BringToFront();

            p.Controls.Add(right);
            p.Controls.Add(Gap(DockStyle.Left));
            p.Controls.Add(grp);
            p.Controls.Add(Gap(DockStyle.Top));
            p.Controls.Add(bar);
            UpdateBudForm();
            return p;
        }

        static Color? VerdictColor(string v) => v == "PASS" ? Win98Chart.Green : v == "FAIL" ? Win98Chart.Red : v == "CHECK" ? Win98Chart.Olive : (Color?)null;

        Color? SideColor(double sigmas, double margin)
        {
            if (double.IsNaN(margin)) return null;
            return margin < 0 ? Win98Chart.Red : Win98Chart.Green;
        }

        static readonly string[] BudTips =
        {
            "Your name for this error.",
            "PASS: mean +/- k sigma inside the boundaries in every condition, and the error is bell-shaped (or has shorter tails, so the bound is safe).\n" +
            "FAIL: outside the boundaries at k sigma, or simulated units already outside.\n" +
            "CHECK: the k-sigma extrapolation cannot decide (tails longer than normal, or flat-shaped and over on paper): run a worst case or many more runs.",
            "Room left at k sigma in the worst condition, as a share of half the allowed range. Negative = outside.",
            "The extrapolated error range of a production unit: mean +/- k sigma, in the worst condition.",
            "The allowed error.",
            "Each error is judged per temperature and per band of the input range. This is the condition with the least margin.",
            "How the error is obtained: a .meas that already is the error, or output - ideal(reference), optionally referred to the input.",
        };

        // ---------------------------------------------------------------- the definition form

        /// <summary>Enables what the chosen style needs and shows the resulting formula.</summary>
        void UpdateBudForm()
        {
            bool cmp = rbBudCmp.Checked, pctRdg = cbBudUnit.SelectedIndex == 1;
            lblBudY.Text = cmp ? "Output:" : "Error:";
            lblBudX.Text = cmp ? "Reference:" : "Reading:";
            lblBudX.Enabled = cbBudX.Enabled = cmp || pctRdg;
            pnlBudIdeal.Enabled = cmp;
            txtBudGain.Enabled = txtBudOff.Enabled = cmp && rbBudLin.Checked;
            txtBudTf.Enabled = cmp && rbBudExpr.Checked;
            txtBudSym.Enabled = chkBudSym.Checked;
            txtBudLo.Enabled = txtBudHi.Enabled = !chkBudSym.Checked;
            txtBudFs.Enabled = cbBudUnit.SelectedIndex == 2;
            var it = ReadBudgetForm();
            lblBudFormula.Text = cmp && it.UsesTf ? "error = " + it.Definition() : "";
        }

        /// <summary>Symmetric ticked or cleared: carry the values over (+/-b <-> lower/upper).</summary>
        void SwapSymmetric()
        {
            if (chkBudSym.Checked)
            {
                double lo = ParseField(txtBudLo), hi = ParseField(txtBudHi);
                double b = Math.Max(double.IsNaN(lo) ? 0 : Math.Abs(lo), double.IsNaN(hi) ? 0 : Math.Abs(hi));
                if (double.IsNaN(ParseField(txtBudSym)) && b > 0) txtBudSym.Text = b.ToString("G9", Inv);
            }
            else
            {
                double b = ParseField(txtBudSym);
                if (!double.IsNaN(b) && txtBudLo.Text.Trim().Length == 0 && txtBudHi.Text.Trim().Length == 0)
                {
                    txtBudLo.Text = (-Math.Abs(b)).ToString("G9", Inv);
                    txtBudHi.Text = Math.Abs(b).ToString("G9", Inv);
                }
            }
            UpdateBudForm();
        }

        ErrorItem ReadBudgetForm()
        {
            string x = cbBudX.Text.Trim();
            var it = new ErrorItem
            {
                Name = txtBudName.Text.Trim(), Y = cbBudY.SelectedItem as string ?? "", X = x == "(none)" ? "" : x,
                ReferToInput = chkBudRefer.Checked, Unit = (BudgetUnit)Math.Max(0, cbBudUnit.SelectedIndex),
                FullScale = ParseField(txtBudFs), Symmetric = chkBudSym.Checked,
            };
            if (rbBudCmp.Checked)
            {
                if (rbBudLin.Checked)
                {
                    double g = ParseField(txtBudGain), o = ParseField(txtBudOff);
                    it.Ideal = IdealKind.Linear;
                    it.Gain = double.IsNaN(g) ? 1 : g;
                    it.Offset = double.IsNaN(o) ? 0 : o;
                    it.Tf = ErrorItem.LinearTf(it.Gain, it.Offset);
                }
                else { it.Ideal = IdealKind.Expression; it.Tf = txtBudTf.Text.Trim(); }
            }
            else
            {
                it.Tf = "";
                if (it.Unit != BudgetUnit.PctReading) it.X = "";
            }
            if (it.Symmetric)
            {
                double b = Math.Abs(ParseField(txtBudSym));
                it.Lower = -b; it.Upper = b;
            }
            else { it.Lower = ParseField(txtBudLo); it.Upper = ParseField(txtBudHi); }
            return it;
        }

        void LoadBudgetFields(ErrorItem it)
        {
            loadingBud = true;
            txtBudName.Text = it.Name;
            Select(cbBudY, it.Y);
            cbBudX.Text = it.X;
            rbBudCmp.Checked = it.UsesTf;
            rbBudMeas.Checked = !it.UsesTf;
            rbBudLin.Checked = it.Ideal == IdealKind.Linear;
            rbBudExpr.Checked = it.Ideal == IdealKind.Expression;
            txtBudGain.Text = it.Gain.ToString("G9", Inv);
            txtBudOff.Text = it.Offset.ToString("G9", Inv);
            txtBudTf.Text = it.Ideal == IdealKind.Expression ? it.Tf : "";
            chkBudRefer.Checked = it.ReferToInput;
            chkBudSym.Checked = it.Symmetric;
            txtBudSym.Text = it.Symmetric && !double.IsNaN(it.Upper) ? it.Upper.ToString("G9", Inv) : "";
            txtBudLo.Text = double.IsNaN(it.Lower) ? "" : it.Lower.ToString("G9", Inv);
            txtBudHi.Text = double.IsNaN(it.Upper) ? "" : it.Upper.ToString("G9", Inv);
            cbBudUnit.SelectedIndex = (int)it.Unit;
            txtBudFs.Text = double.IsNaN(it.FullScale) ? "" : it.FullScale.ToString("G9", Inv);
            loadingBud = false;
            UpdateBudForm();
        }

        /// <summary>The Scatter & TF tab has an ideal TF for this output: its input and TF.</summary>
        bool ScatterTf(string y, out string x, out string theory)
        {
            var tf = rs.Setup.Tf;
            x = tf.X; theory = tf.Theory;
            return !string.IsNullOrWhiteSpace(tf.Theory) && !string.IsNullOrWhiteSpace(tf.X) && string.Equals(tf.Y, y, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Puts an ideal into the form: gain and offset when it is a straight line, else the expression.</summary>
        void SetBudgetIdeal(string x, string theory)
        {
            loadingBud = true;
            rbBudCmp.Checked = true;
            cbBudX.Text = x;
            if (ErrorItem.TryLinear(theory, out var g, out var o))
            {
                rbBudLin.Checked = true;
                txtBudGain.Text = g.ToString("G9", Inv); txtBudOff.Text = o.ToString("G9", Inv); txtBudTf.Text = "";
            }
            else { rbBudExpr.Checked = true; txtBudTf.Text = theory; }
            loadingBud = false;
            UpdateBudForm();
        }

        /// <summary>Output picked with no ideal yet: take the reference and TF the Scatter & TF tab already has for it.</summary>
        void FillTfFromScatter()
        {
            if (rs == null || !ScatterTf(cbBudY.SelectedItem as string, out var x, out var theory)) return;
            double g = ParseField(txtBudGain), o = ParseField(txtBudOff);
            bool untouched = rbBudMeas.Checked
                          || rbBudLin.Checked && (double.IsNaN(g) || g == 1) && (double.IsNaN(o) || o == 0)
                          || rbBudExpr.Checked && txtBudTf.Text.Trim().Length == 0;
            if (untouched) SetBudgetIdeal(x, theory);
        }

        /// <summary>
        /// A .meas error is compared with the boundaries as it is, so it must already be an error (near zero).
        /// A signal there (e.g. vout = 2 V against +/-15 mV) fails every unit: say so before saving.
        /// </summary>
        string SignalWarning(ErrorItem it)
        {
            if (it.UsesTf || it.Unit != BudgetUnit.Abs) return null;
            var d = Descriptive.Of(rs.Values(it.Y, 0));
            if (d.N < 3) return null;
            double typ = Math.Abs(d.Median);
            bool signal = it.HasBounds && it.Span > 0 ? typ > 5 * it.Span : d.Std > 0 && typ > 20 * d.Std;
            if (!signal) return null;
            return $"{it.Y} is about {ErrorBudget.Eng(d.Median)} in every run" +
                   (it.HasBounds ? $", and the boundaries are {it.BoundsText()}." : $" and varies only by {ErrorBudget.Eng(d.Std)} (sigma).") + "\n" +
                   $"It looks like the signal, not its error: as a .meas error, {it.Y} itself is compared with the boundaries.\n\n" +
                   (ScatterTf(it.Y, out var x, out var theory)
                        ? $"Compare it with the TF of the Scatter & TF tab instead (reference {x}, ideal y = {theory})?"
                        : "Choose \"Compare an output with a reference\": the error is then output - (gain x reference + offset).\n" +
                          "Or choose a .meas that already computes the error.");
        }

        void SaveBudgetItem(bool add)
        {
            if (rs == null) return;
            var it = ReadBudgetForm();
            if (rbBudCmp.Checked && !it.UsesTf)
            {
                Win98MessageBox.Show(FindForm(), "Enter the ideal output: a gain (and offset), or an expression of x.", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                return;
            }
            var list = rs.Setup.Errors;
            int sel = lvBud.SelectedIndices.Count > 0 ? lvBud.SelectedIndices[0] : -1;
            if (it.Name.Length == 0) it.Name = it.Y.Length > 0 ? it.Y : "error";
            if (add || sel < 0 || sel >= list.Count)
            {
                string bas = it.Name;
                for (int n = 2; list.Any(e => e.Name.Equals(it.Name, StringComparison.OrdinalIgnoreCase)); n++) it.Name = $"{bas} {n}";
            }
            string bad = ErrorBudget.Check(rs, it);
            if (bad != null) { Win98MessageBox.Show(FindForm(), bad, "SPICA", MessageBoxButtons.OK, Win98Icon.Warning); return; }
            string warn = SignalWarning(it);
            if (warn != null)
            {
                if (ScatterTf(it.Y, out var x, out var theory))
                {
                    int c = Win98MessageBox.Choose(FindForm(), warn, "Error Budget", Win98Icon.Question, "&Use that TF", "&Save as it is", "Cancel");
                    if (c == 0)
                    {
                        SetBudgetIdeal(x, theory);
                        var keepName = it.Name;
                        it = ReadBudgetForm();
                        it.Name = keepName;
                    }
                    else if (c != 1) return;
                }
                else if (Win98MessageBox.Choose(FindForm(), warn, "Error Budget", Win98Icon.Warning, "&Save as it is", "Cancel") != 0) return;
            }
            if (add || sel < 0 || sel >= list.Count) { list.Add(it); sel = list.Count - 1; }
            else list[sel] = it;
            rs.Setup.Dirty = true;
            ErrorBudget.Register(rs, list);
            NotifySetup();
            RefreshBudget();
            if (sel < lvBud.Items.Count) lvBud.Items[sel].Selected = true;
        }

        void DeleteBudgetItem()
        {
            if (rs == null || lvBud.SelectedIndices.Count == 0) return;
            int sel = lvBud.SelectedIndices[0];
            if (sel >= rs.Setup.Errors.Count) return;
            rs.Setup.Errors.RemoveAt(sel);
            rs.Setup.Dirty = true;
            ErrorBudget.Register(rs, rs.Setup.Errors);
            NotifySetup();
            RefreshBudget();
        }

        /// <summary>An error made elsewhere (Scatter & TF): add it and show it.</summary>
        void AddBudgetItem(ErrorItem it)
        {
            var list = rs.Setup.Errors;
            string bas = it.Name;
            for (int n = 2; list.Any(e => e.Name.Equals(it.Name, StringComparison.OrdinalIgnoreCase)); n++) it.Name = $"{bas} {n}";
            list.Add(it);
            rs.Setup.Dirty = true;
            ErrorBudget.Register(rs, list);
            NotifySetup();
            Show(pgBudget);
            RefreshBudget();
            if (lvBud.Items.Count == list.Count) lvBud.Items[list.Count - 1].Selected = true;
        }

        // ---------------------------------------------------------------- the results

        void ApplyBudgetSigma()
        {
            if (filling || rs == null) return;
            var k = ParseField(txtBudSigma);
            if (!(k > 0) || k > 20) { txtBudSigma.Text = rs.Setup.DesignSigma.ToString("0.##", Inv); return; }
            if (k == rs.Setup.DesignSigma) return;
            rs.Setup.DesignSigma = k;
            rs.Setup.Dirty = true;
            NotifySetup();
            RefreshBudget();
        }

        void RefreshBudget()
        {
            if (rs == null) { lvBud.Items.Clear(); budRows.Clear(); budShown = null; ShowBudgetDetail(); return; }
            filling = true;
            var st = rs.Setup;
            txtBudSigma.Text = st.DesignSigma.ToString("0.##", Inv);
            chkBudPess.Checked = st.PessimisticSigma;
            filling = false;
            string k = st.DesignSigma.ToString("0.#", Inv);
            lvBud.Columns[3].Text = $"Mean ± {k} sigma";
            lvBudCond.Columns[4].Text = $"Mean ± {k} sigma";
            budTiles[3].Caption = $"Mean ± {k} sigma";
            budTiles[4].Caption = $"Low side (need {k} sigma)";
            budTiles[5].Caption = $"High side (need {k} sigma)";
            int keep = lvBud.SelectedIndices.Count > 0 ? lvBud.SelectedIndices[0] : 0;
            int g = Math.Max(0, cbBudGroup.SelectedIndex);
            budRows.Clear();
            lvBud.BeginUpdate();
            lvBud.Items.Clear();
            foreach (var it in st.Errors)
            {
                var r = ErrorBudget.Compute(rs, it, g, st.DesignSigma, st.PessimisticSigma);
                budRows.Add(r);
                string u(double v) => ErrorBudget.Fmt(v, it);
                var li = new ListViewItem(it.Name);
                string[] cols = r.Error != null ? new[] { "ERROR", "", r.Error, it.BoundsText(), "", it.Definition() } : new[]
                {
                    r.Verdict, r.HasBudget ? r.MarginPct.ToString("0", Inv) + " %" : "", $"{u(r.Lo)} .. {u(r.Hi)}", it.BoundsText(),
                    r.Slices.Count > 1 ? r.Worst.Label : "all runs", it.Definition(),
                };
                foreach (var c in cols) li.SubItems.Add(c);
                lvBud.Items.Add(li);
            }
            lvBud.EndUpdate();
            if (lvBud.Items.Count > 0) lvBud.Items[Math.Min(keep, lvBud.Items.Count - 1)].Selected = true;
            ShowBudgetDetail();
        }

        void ShowBudgetDetail()
        {
            int i = lvBud.SelectedIndices.Count > 0 ? lvBud.SelectedIndices[0] : -1;
            budShown = i >= 0 && i < budRows.Count ? budRows[i] : null;
            budSlice = budShown?.Worst;
            if (budShown != null) LoadBudgetFields(budShown.Item);

            lvBudCond.BeginUpdate();
            lvBudCond.Items.Clear();
            if (budShown != null && budShown.Error == null)
            {
                var r = budShown;
                string u(double v) => ErrorBudget.Fmt(v, r.Item);
                string side(double sig, double m) => double.IsNaN(m) ? "open" : double.IsNaN(sig) ? "-" : sig.ToString("0.0", Inv) + " sigma";
                foreach (var s in r.Slices)
                    lvBudCond.Items.Add(new ListViewItem(new[]
                    {
                        s.Label, s.D.N.ToString(Inv), u(s.D.Mean), u(s.Sigma), $"{u(s.Lo)} .. {u(s.Hi)}", side(s.SigmasLo, s.MarginLo), side(s.SigmasHi, s.MarginHi),
                        r.HasBudget ? r.SlicePct(s).ToString("0", Inv) + " %" : "", s.Normal ? "yes" : s.TailsOk ? "shorter tails" : "no", s.Verdict,
                    }));
                int w = r.Slices.IndexOf(r.Worst);
                if (w >= 0) { lvBudCond.Items[w].Selected = true; lvBudCond.Items[w].Focused = true; }
            }
            lvBudCond.EndUpdate();
            ShowBudgetSlice();
        }

        /// <summary>Tiles, chart and drivers for the condition picked (the worst one by default).</summary>
        void ShowBudgetSlice()
        {
            var r = budShown;
            if (r == null || r.Error != null || budSlice == null)
            {
                foreach (var t in budTiles) t.Set("");
                chBud.Clear(); chBudSens.Clear();
                chBudSens.Title = "What drives it";
                chBud.EmptyText = r?.Error ?? "";
                lblBudNotes.Text = "";
                lblBudVerdict.ForeColor = Color.Black;
                lblBudVerdict.Text = rs == null ? "" : r != null ? "  " + r.Item.Name + ": " + r.Error
                    : rs.Setup.Errors.Count > 0 ? "  Select an error."
                    : "  No error defined yet: define one on the left (a .meas that is the error, or an output against a reference) and press Add.";
                return;
            }
            var s = budSlice;
            string k = r.K.ToString("0.#", Inv);
            string u(double v) => ErrorBudget.Fmt(v, r.Item);
            var vc = VerdictColor(r.Verdict);
            budTiles[0].Set(r.Verdict == "-" ? "no boundaries" : r.Verdict, r.Verdict == "-" ? "" : $"at {k} sigma" + (r.Slices.Count > 1 ? ", all conditions" : ""),
                vc, r.Verdict == "PASS" ? PassBack : r.Verdict == "FAIL" ? FailBack : r.Verdict == "CHECK" ? CheckBack : (Color?)null);
            budTiles[1].Set(u(s.D.Mean), "same in every unit");
            budTiles[2].Set(u(s.Sigma), r.Pessimistic ? "95 % upper bound" : "sample");
            budTiles[3].Set($"{u(s.Lo)} .. {u(s.Hi)}", $"seen {u(s.D.Min)} .. {u(s.D.Max)}");
            void Side(Win98Tile t, double sig, double m)
            {
                if (!r.HasBudget || double.IsNaN(m)) { t.Set("open", r.HasBudget ? "no boundary" : "no boundaries"); return; }
                string note = m >= 0 ? $"{u(m)} to spare" : $"short by {u(-m)}";
                t.Set(double.IsNaN(sig) ? "-" : $"{sig.ToString("0.0", Inv)} sigma", note, m >= 0 ? Win98Chart.Green : Win98Chart.Red);
            }
            Side(budTiles[4], s.SigmasLo, s.MarginLo);
            Side(budTiles[5], s.SigmasHi, s.MarginHi);
            budTiles[6].Set(s.Normal ? "yes" : s.TailsOk ? "shorter tails" : "no", "p = " + MeasStats.P(s.AdP),
                s.Normal ? Win98Chart.Green : s.TailsOk ? Win98Chart.Olive : Win98Chart.Maroon);

            lblBudVerdict.ForeColor = vc ?? Color.Black;
            lblBudVerdict.Text = $"  {(s == r.Worst ? (r.Slices.Count > 1 ? "Worst condition" : "All runs") : "Condition")}: {s.Label}.   {(r.Verdict == "-" ? "" : r.Verdict + ": ")}{r.VerdictText()}";
            lblBudNotes.Text = string.Join("\n", r.Notes());
            BuildBudgetChart(chBud, r, cbBudChart.SelectedIndex, s);

            // what drives the spread in this condition
            BuildBudgetSens(chBudSens, r, s, 8);
            chBudSens.Invalidate();
        }

        /// <summary>Share of the spread of the error in one condition explained by each tolerance; the number of bars drawn.</summary>
        int BuildBudgetSens(Win98Chart ch, ErrorResult r, ErrorSlice s, int top)
        {
            ch.Clear();
            ch.Title = "What drives it";
            if (rs.Params.Count == 0 && rs.Mode != TempMode.Random) { ch.EmptyText = "No tolerances recorded for this campaign."; return 0; }
            var set = new HashSet<ResultSet.Row>(s.Rows);
            var rep = s == r.Worst && r.Sens != null ? r.Sens : SensitivityReport.Parameters(rs, ErrorBudget.Column(r.Item), 0, row => set.Contains(row));
            var items = rep.Items.Where(x => !double.IsNaN(x.Share)).Take(top).ToList();
            if (items.Count == 0) { ch.EmptyText = rep.Error ?? "Nothing to show."; return 0; }
            ch.YCategories = items.Select(x => Short(x.Name)).ToArray();
            ch.XLabel = "share of the spread (%)";
            ch.Add(new ChartSeries { Kind = SeriesKind.HBars, X = items.Select(x => 100 * x.Share).ToArray(), Color = Win98Chart.Navy, InLegend = false });
            return items.Count;
        }

        /// <summary>
        /// The error against temperature: per corner the mean and the widest mean +/- k sigma of its input bands, the
        /// extremes seen and the boundaries; with a random temperature, every run. False when there is nothing to show.
        /// </summary>
        bool BuildBudgetTempChart(Win98Chart ch, ErrorResult r)
        {
            ch.Clear();
            if (r.Error != null || r.Slices.Count == 0 || rs.Mode == TempMode.Default) return false;
            string unit = r.Item.Suffix().Trim();
            ch.Title = r.Item.Name + " vs temperature";
            ch.XLabel = "temperature (°C)"; ch.YLabel = "error" + (unit.Length > 0 ? " (%)" : "");
            ch.YFromZero = false;
            string k = r.K.ToString("0.#", Inv);
            if (rs.Mode == TempMode.Corners)
            {
                var cs = r.Slices.Where(x => x.Corner >= 0 && x.Corner < rs.Corners.Count).GroupBy(x => x.Corner).OrderBy(g => rs.Corners[g.Key]).ToList();
                if (cs.Count < 2) return false;
                var t = cs.Select(g => rs.Corners[g.Key]).ToArray();
                var all = cs.Select(g => g.SelectMany(x => x.E).ToArray()).ToArray();
                ch.Add(new ChartSeries
                {
                    Kind = SeriesKind.ErrorBars, X = t, Y = all.Select(e => e.Average()).ToArray(),
                    Lo = cs.Select(g => g.Min(x => x.Lo)).ToArray(), Hi = cs.Select(g => g.Max(x => x.Hi)).ToArray(),
                    Color = Win98Chart.Navy, Label = $"mean, and mean +/- {k} sigma" + (cs.Any(g => g.Count() > 1) ? " (widest input band)" : ""),
                });
                ch.Add(new ChartSeries { Kind = SeriesKind.Line, X = t, Y = all.Select(e => e.Min()).ToArray(), Color = Win98Chart.Teal, Dashed = true, Label = "min / max seen" });
                ch.Add(new ChartSeries { Kind = SeriesKind.Line, X = t, Y = all.Select(e => e.Max()).ToArray(), Color = Win98Chart.Teal, Dashed = true, InLegend = false });
            }
            else
            {
                var tx = r.Slices.SelectMany(x => x.Rows.Select(row => row.Temp)).ToArray();
                var ey = r.Slices.SelectMany(x => x.E).ToArray();
                if (tx.Length < 3 || tx.Max() <= tx.Min()) return false;
                ch.Add(new ChartSeries { Kind = SeriesKind.Points, X = tx, Y = ey, Color = Win98Chart.Navy, Size = 2, Label = "runs" });
            }
            var bounds = new[] { r.Item.Lower, r.Item.Upper }.Where(v => !double.IsNaN(v)).ToArray();
            if (bounds.Length > 0) ch.Add(new ChartSeries { Kind = SeriesKind.HLine, Y = bounds, Color = Win98Chart.Red, Size = 2, Label = "boundaries" });
            return true;
        }

        void BuildBudgetChart(Win98Chart ch, ErrorResult r, int view, ErrorSlice slice = null)
        {
            ch.Clear();
            ch.Title = r.Item.Name + (r.Error != null ? "" : $"  -  {r.Verdict}");
            if (r.Error != null || r.D.N < 3) { ch.EmptyText = r.Error ?? "No data."; return; }
            var s = slice ?? r.Worst;
            string unit = r.Item.Suffix().Trim();
            double k = r.K;
            var bounds = new[] { r.Item.Lower, r.Item.Upper }.Where(v => !double.IsNaN(v)).ToArray();
            if (view == 1 && r.InputVaries)
            {
                ch.YFromZero = false;
                ch.XLabel = r.Item.X; ch.YLabel = "error" + (unit.Length > 0 ? " (%)" : "");
                ch.Add(new ChartSeries { Kind = SeriesKind.Points, X = r.X, Y = r.E, Color = Win98Chart.Navy, Size = 2, Label = "runs" });
                // the +/- k sigma band of every input band, at the temperature of the condition shown
                bool first = true;
                foreach (var b in r.Slices.Where(x => x.Corner == s.Corner && !double.IsNaN(x.XLo)))
                {
                    foreach (var v in new[] { b.Lo, b.Hi })
                    {
                        ch.Add(new ChartSeries { Kind = SeriesKind.Line, X = new[] { b.XLo, b.XHi }, Y = new[] { v, v }, Color = Win98Chart.Purple, Size = 2,
                                                 Label = $"mean +/- {k:0.#} sigma per band" + (s.Corner >= 0 && rs != null ? $" ({ResultSet.TempLabel(rs.Corners[s.Corner])})" : ""), InLegend = first });
                        first = false;
                    }
                }
                if (first) ch.Add(new ChartSeries { Kind = SeriesKind.HLine, Y = new[] { s.Lo, s.Hi }, Color = Win98Chart.Purple, Dashed = true, Label = $"mean +/- {k:0.#} sigma" });
                if (bounds.Length > 0)
                {
                    double pad = 0.12 * Math.Max(r.Item.Span, 1e-30);
                    ch.Add(new ChartSeries { Kind = SeriesKind.HLine, Y = bounds.Select(v => v + Math.Sign(v == 0 ? 1 : v) * pad).ToArray(), Color = Color.White, InLegend = false });
                    ch.Add(new ChartSeries { Kind = SeriesKind.HLine, Y = bounds, Color = Win98Chart.Red, Size = 2, Label = "boundaries" });
                }
                return;
            }
            if (view == 1) { ch.EmptyText = "The input does not vary between runs: see the histogram."; return; }
            ch.YFromZero = true;
            if (r.Slices.Count > 1) ch.Title += $"  -  {s.Label}";
            ch.XLabel = "error" + (unit.Length > 0 ? " (%)" : ""); ch.YLabel = "runs";
            var edges = Stats.HistogramEdges(s.D);
            var counts = Stats.Histogram(s.D.Sorted, edges);
            ch.Add(new ChartSeries { Kind = SeriesKind.Bars, Edges = edges, Y = counts.Select(c => (double)c).ToArray(), X = edges, Color = Win98Chart.Navy, Label = "runs", InLegend = false });
            if (s.D.Std > 0)
            {
                double w = edges[1] - edges[0];
                double lo = Math.Min(s.Lo, edges[0]), hi = Math.Max(s.Hi, edges[edges.Length - 1]);
                foreach (var b in bounds) { lo = Math.Min(lo, b); hi = Math.Max(hi, b); }
                var xs = Enumerable.Range(0, 201).Select(j => lo + (hi - lo) * j / 200).ToArray();
                ch.Add(new ChartSeries { Kind = SeriesKind.Line, X = xs, Y = xs.Select(x => s.D.N * w * Stats.NormalPdf((x - s.D.Mean) / s.D.Std) / s.D.Std).ToArray(), Color = Win98Chart.Teal, Size = 2, Label = "normal fit" });
            }
            ch.Add(new ChartSeries { Kind = SeriesKind.VLine, X = new[] { s.Lo, s.Hi }, Color = Win98Chart.Purple, Dashed = true, Label = $"mean +/- {k:0.#} sigma" });
            if (bounds.Length > 0)
            {
                // white lines just outside, so the boundary lines do not sit on the frame
                double reach = Math.Max(bounds.Max(v => Math.Abs(v)), Math.Max(Math.Abs(s.Lo), Math.Abs(s.Hi)));
                ch.Add(new ChartSeries { Kind = SeriesKind.VLine, X = new[] { Math.Min(s.Lo, bounds.Min()) - 0.12 * reach, Math.Max(s.Hi, bounds.Max()) + 0.12 * reach }, Color = Color.White, InLegend = false });
                ch.Add(new ChartSeries { Kind = SeriesKind.VLine, X = bounds, Color = Win98Chart.Red, Size = 2, Label = "boundaries" });
            }
        }

        // =====================================================================================
        //  Worst case
        // =====================================================================================

        Win98ListView lvWc;
        TextBox txtWc;
        WcReport wcRep;

        Win98TabPage BuildWcPage()
        {
            var p = new Win98TabPage("Worst Case", Win98.Icon("results16"));
            var bar = Bar(24);
            bar.Controls.Add(Hint("Every tolerance at an edge of its range: the guaranteed limits, not a statistic. Select a row for its causes below.", 0, 2));
            lvWc = new Win98ListView();
            foreach (var (t, w) in new[] { ("Measurement", 120), ("Verdict", 80), ("Nominal", 150), ("Worst low", 90), ("Worst high", 90), ("LSL", 70), ("USL", 70),
                                           ("Low margin", 90), ("High margin", 90), ("Main cause", 200) })
                lvWc.Columns.Add(t, w, t == "Measurement" || t == "Verdict" || t == "Main cause" ? HorizontalAlignment.Left : HorizontalAlignment.Right);
            lvWc.CellColor = (i, c) =>
            {
                if (wcRep == null || i >= wcRep.Items.Count) return null;
                var w = wcRep.Items[i];
                if (c == 1) return w.Pass == true ? Win98Chart.Green : w.Pass == false ? Win98Chart.Red : (Color?)null;
                if (c == 7 && w.LowMargin < 0 || c == 8 && w.HighMargin < 0) return Win98Chart.Red;
                return null;
            };
            lvWc.HeaderTip = c => WcTips[c];
            lvWc.SelectedIndexChanged += (s, e) => ShowWcDetail();
            txtWc = Memo();
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 280, BackColor = Win98.Face };
            bottom.Controls.Add(new Win98Well(txtWc) { Dock = DockStyle.Fill });
            p.Controls.Add(new Win98Well(lvWc) { Dock = DockStyle.Fill });
            p.Controls.Add(Gap(DockStyle.Top));
            p.Controls.Add(bar);
            p.Controls.Add(Gap(DockStyle.Bottom));
            p.Controls.Add(bottom);
            bottom.SendToBack();
            return p;
        }

        static readonly string[] WcTips =
        {
            "The .meas result.",
            "Worst case against the spec limits: green = even the most extreme unit passes, red = some combination of tolerances fails.\nSet limits on the Statistics tab.",
            "The nominal unit: every tolerance at its centre (a range when it changes with temperature).",
            "The lowest value of all the worst-case runs (and temperatures).",
            "The highest value of all the worst-case runs (and temperatures).",
            "Lower spec limit.",
            "Upper spec limit.",
            "Worst low minus LSL: how much room is left below. Negative = fails.",
            "USL minus worst high: how much room is left above. Negative = fails.",
            "The tolerance that moves this measurement most, with its share of the total swing. Tighten it first.",
        };

        void RefreshWc()
        {
            int keep = lvWc.SelectedIndices.Count > 0 ? lvWc.SelectedIndices[0] : 0;
            wcRep = rs == null ? null : WcReport.Of(rs);
            lvWc.BeginUpdate();
            lvWc.Items.Clear();
            if (wcRep != null)
                foreach (var w in wcRep.Items)
                {
                    var it = new ListViewItem(w.Meas);
                    var top = w.Top.FirstOrDefault();
                    string[] cols =
                    {
                        w.Pass == true ? "PASS" : w.Pass == false ? "FAIL" : "(no limits)",
                        double.IsNaN(w.NomLo) ? "" : w.NomLo == w.NomHi ? F(w.NomLo) : $"{F(w.NomLo)} .. {F(w.NomHi)}",
                        F(w.Min), F(w.Max),
                        w.Spec != null ? F(w.Spec.Lsl) : "", w.Spec != null ? F(w.Spec.Usl) : "",
                        F(w.LowMargin), F(w.HighMargin),
                        top == null ? "" : $"{Short(top.Param)} ({top.Share * 100:0} %)",
                    };
                    foreach (var x in cols) it.SubItems.Add(x);
                    lvWc.Items.Add(it);
                }
            lvWc.EndUpdate();
            if (lvWc.Items.Count > 0) lvWc.Items[Math.Min(keep, lvWc.Items.Count - 1)].Selected = true;
            ShowWcDetail();
        }

        static string Short(string p) { int i = p.LastIndexOf(" ["); return i > 0 ? p.Substring(0, i) : p; }

        void ShowWcDetail()
        {
            if (rs == null) { txtWc.Text = ""; return; }
            if (wcRep == null)
            {
                txtWc.Text = ("This campaign is a Monte Carlo (random units), not a worst case.\n\n" +
                              "Worst case sets every tolerance to an edge of its range and reports the guaranteed limits.\n" +
                              "To run one: Setup > Analysis > Worst case, then Start.").Replace("\n", "\r\n");
                return;
            }
            if (lvWc.SelectedIndices.Count == 0 || lvWc.SelectedIndices[0] >= wcRep.Items.Count) { txtWc.Text = "Select a measurement."; return; }
            txtWc.Text = wcRep.Summary(wcRep.Items[lvWc.SelectedIndices[0]], rs).Replace("\r\n", "\n").Replace("\n", "\r\n");
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
                Hint("Select a row for its full analysis below. Yield CI: exact binomial 95 %. Normal p: Anderson-Darling.", 216, 2) });

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
            lvStats.HeaderTip = c => Glossary.Column(lvStats.Columns[c].Text);
            lvStats.CellTip = (i, c) => i < statRows.Count ? Glossary.StatCell(statRows[i], c) : null;
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
            string text = s.Summary() + "\n" + new string('-', 72) + "\nStatistical details\n\n" + s.Narrative();
            txtStat.Text = text.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
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
            cbHistView = Combo(490, 2, 150);
            cbHistView.Items.AddRange(new object[] { "Histogram", "Cumulative (CDF)", "Normal probability" });
            cbHistView.SelectedIndex = 0;
            cbHistBins = Combo(682, 2, 64);
            cbHistBins.Items.AddRange(new object[] { "Auto", "10", "20", "30", "50", "100" });
            cbHistBins.SelectedIndex = 0;
            chkHistNormal = new Win98CheckBox { Text = "Normal fit && 3 sigma", Location = new Point(762, 5), Checked = true };
            chkHistNormal.CheckedChanged += (s, e) => RefreshHist();
            bar.Controls.AddRange(new Control[] { Lbl("Measurement:", 0, 2), cbHistMeas, Lbl("Group:", 246, 2), cbHistGroup,
                Lbl("View:", 454, 2), cbHistView, Lbl("Bins:", 650, 2), cbHistBins, chkHistNormal });
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
            int bins = cbHistBins.SelectedIndex > 0 ? int.Parse((string)cbHistBins.SelectedItem, Inv) : 0;
            cbHistBins.Enabled = cbHistView.SelectedIndex == 0;
            var s = BuildHistogram(chHist, m, g, chkHistNormal.Checked, cbHistView.SelectedIndex, bins);
            lblHist.Text = s == null ? "" :
                $"n = {s.D.N}   mean = {F(s.D.Mean)}   sigma = {F(s.D.Std)}   normal p = {MeasStats.P(s.AdP)}" +
                (s.Cap.N > 0 && s.Spec?.IsSet == true ? $"   yield = {MeasStats.Pct(s.Cap.Yield)}   Cpk = {Num(s.Cap.Cpk, "0.000")}" : "");
        }

        /// <param name="view">0 histogram, 1 cumulative distribution, 2 normal probability plot</param>
        /// <param name="bins">histogram bins, 0 = automatic</param>
        MeasStats BuildHistogram(Win98Chart ch, string meas, int group, bool normal, int view = 0, int bins = 0)
        {
            ch.Clear();
            var groups = rs.GroupNames();
            var s = MeasStats.Of(meas, groups[Math.Min(group, groups.Count - 1)], rs.Values(meas, group), rs.Setup.Spec(meas));
            ch.Title = $"{meas}  -  {s.Group}";
            ch.XLabel = meas;
            ch.YLabel = "runs";
            ch.YFromZero = true;
            if (s.D.N == 0) return s;
            if (view == 1 || view == 2) { BuildDistribution(ch, s, normal, view == 2); return s; }
            var edges = bins > 0 && s.D.Max > s.D.Min
                ? Enumerable.Range(0, bins + 1).Select(i => s.D.Min + (s.D.Max - s.D.Min) * i / bins).ToArray()
                : Stats.HistogramEdges(s.D);
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

        /// <summary>Cumulative distribution, or normal probability plot (a straight line when the data are normal).</summary>
        void BuildDistribution(Win98Chart ch, MeasStats s, bool normal, bool probit)
        {
            var a = s.D.Sorted;
            int n = a.Length;
            ch.YFromZero = false;
            bool fit = normal && s.D.Std > 0;
            if (probit)
            {
                ch.Title = $"{s.Meas}  -  {s.Group}: normal probability plot";
                ch.YLabel = "normal quantile (sigma)";
                ch.Add(new ChartSeries { Kind = SeriesKind.Points, X = a, Y = Enumerable.Range(0, n).Select(i => Stats.NormalInv((i + 0.5) / n)).ToArray(), Color = Win98Chart.Navy, Size = 3, Label = "runs" });
                if (fit)
                {
                    var lx = new[] { a[0], a[n - 1] };
                    ch.Add(new ChartSeries { Kind = SeriesKind.Line, X = lx, Y = lx.Select(x => (x - s.D.Mean) / s.D.Std).ToArray(), Color = Win98Chart.Teal, Size = 2, Label = "normal (same mean, sigma)" });
                }
            }
            else
            {
                ch.Title = $"{s.Meas}  -  {s.Group}: cumulative distribution";
                ch.YLabel = "% of runs below";
                ch.Add(new ChartSeries { Kind = SeriesKind.Line, X = a, Y = Enumerable.Range(0, n).Select(i => 100.0 * (i + 1) / n).ToArray(), Color = Win98Chart.Navy, Size = 2, Label = "runs" });
                if (fit)
                {
                    double lo = Math.Min(a[0], s.D.Mean - 4 * s.D.Std), hi = Math.Max(a[n - 1], s.D.Mean + 4 * s.D.Std);
                    var xs = Enumerable.Range(0, 161).Select(i => lo + (hi - lo) * i / 160).ToArray();
                    ch.Add(new ChartSeries { Kind = SeriesKind.Line, X = xs, Y = xs.Select(x => 100 * Stats.NormalCdf((x - s.D.Mean) / s.D.Std)).ToArray(), Color = Win98Chart.Teal, Dashed = true, Label = "normal fit" });
                }
            }
            if (s.Spec != null && s.Spec.IsSet)
                ch.Add(new ChartSeries { Kind = SeriesKind.VLine, X = new[] { s.Spec.Lsl, s.Spec.Usl }.Where(v => !double.IsNaN(v)).ToArray(), Color = Win98Chart.Red, Label = "spec limits" });
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
            txtTheory = Field(84, 32, 200);
            var btn = new Win98Button { Text = "&Apply", Location = new Point(292, 30), Size = new Size(70, 24) };
            btn.Click += (s, e) => ApplyTf();
            var btnIdeal = new Win98Button { Text = "Use as ideal in Error &Budget", Location = new Point(376, 30), Size = new Size(190, 24) };
            btnIdeal.Click += (s, e) => TfToBudget(false);
            var btnFit = new Win98Button { Text = "Use the &fitted line", Location = new Point(572, 30), Size = new Size(140, 24) };
            btnFit.Click += (s, e) => TfToBudget(true);
            txtTheory.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ApplyTf(); e.SuppressKeyPress = true; } };
            bar.Controls.AddRange(new Control[] { Lbl("X:", 0, 2), cbTfX, Lbl("Y:", 158, 2), cbTfY, Lbl("Group:", 316, 2), cbTfGroup,
                Hint("X and Y can be measurements, TEMP, a random parameter or the run number.", 512, 2),
                Lbl("Ideal  y =", 0, 32), txtTheory, btn, btnIdeal, btnFit,
                Hint("Find the TF here; boundaries and the verdict are on Error Budget.", 722, 32) });
            var tfTip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400 };
            tfTip.SetToolTip(txtTheory, "The ideal output as an expression of x (and T, temperature in °C), e.g. 2*x, or 2*x + 10m. Optional.");
            tfTip.SetToolTip(btnIdeal, "Adds an error to the Error Budget tab: Y compared with this ideal, run by run, referred to the input.");
            tfTip.SetToolTip(btnFit, "Adds an error to the Error Budget tab with the fitted line as the ideal: what is left after a gain and offset calibration.");

            chTf = new Win98Chart();
            chTfErr = Track(new Win98Chart());

            // figures in boxes, two columns
            tfTiles = new[] { "Fitted gain", "Fitted offset", "Ideal gain", "Ideal offset", "Gain error", "Offset error", "Scatter around the line", "Linearity (R²)" }
                .Select(c => new Win98Tile { Caption = c }).ToArray();
            var tilePanel = new Panel { Dock = DockStyle.Top, Height = 4 * 64, BackColor = Win98.Face };
            tilePanel.Controls.AddRange(tfTiles);
            tilePanel.Resize += (s, e) =>
            {
                int w = (tilePanel.Width - 6) / 2;
                for (int i = 0; i < tfTiles.Length; i++) tfTiles[i].SetBounds((i % 2) * (w + 6), (i / 2) * 64, i % 2 == 1 ? tilePanel.Width - w - 6 : w, 58);
            };
            foreach (var (t, i) in new[] {
                ("Slope of the least-squares line through all runs, with its 95 % interval.", 0),
                ("Intercept of the least-squares line (Y at X = 0), with its 95 % interval.", 1),
                ("Slope of the ideal over the range of X.", 2),
                ("The ideal at X = 0.", 3),
                ("Fitted gain against the ideal gain: systematic, the same in every unit.", 4),
                ("Fitted offset against the ideal offset: systematic, the same in every unit.", 5),
                ("Unit-to-unit spread of Y around the fitted line (sigma): what a gain and offset calibration cannot remove.", 6),
                ("How straight Y is against X: 1 = a perfect line.", 7) })
                tfTip.SetToolTip(tfTiles[i], t);
            lblTfNote = new Label { Dock = DockStyle.Fill, BackColor = Win98.Face, ForeColor = Win98.Shadow, UseMnemonic = false, Padding = new Padding(2, 8, 2, 0) };
            var right = new Panel { Dock = DockStyle.Right, Width = 420, BackColor = Win98.Face };
            right.Controls.Add(lblTfNote);
            right.Controls.Add(tilePanel);

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Win98.Face };
            var errWell = new Win98Well(chTfErr, Color.White) { Dock = DockStyle.Bottom, Height = 200 };
            body.Controls.Add(ChartWell(chTf));
            body.Controls.Add(Gap(DockStyle.Bottom));
            body.Controls.Add(errWell);
            body.Resize += (s, e) => errWell.Height = Math.Max(120, body.Height * 2 / 5);

            p.Controls.Add(body);
            p.Controls.Add(Gap(DockStyle.Right));
            p.Controls.Add(right);
            p.Controls.Add(Gap(DockStyle.Top));
            p.Controls.Add(bar);
            p.Resize += (s, e) => right.Width = Math.Max(340, Math.Min(480, p.Width / 3));
            return p;
        }

        Win98Tile[] tfTiles;
        Label lblTfNote;
        TfReport tfShown;

        void ApplyTf()
        {
            if (rs == null) return;
            var tf = rs.Setup.Tf;
            string theory = txtTheory.Text.Trim();
            if (theory.Length > 0 && !Expr.TryParse(theory, out _, out var err))
            {
                Win98MessageBox.Show(FindForm(), "The ideal is not a valid expression:\n" + err + "\n\nUse x for the X value (and T for temperature), e.g.  2*x  or  2*x + 10m.",
                    "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                return;
            }
            tf.X = cbTfX.SelectedItem as string ?? "";
            tf.Y = cbTfY.SelectedItem as string ?? "";
            tf.Theory = theory;
            rs.Setup.Dirty = true;
            NotifySetup();
            RefreshTf();
        }

        /// <summary>
        /// This tab's Y against its ideal (or the fitted line) as an Error Budget item: compared run by run, referred to
        /// the input. The boundaries come from another error of the same output, if there is one.
        /// </summary>
        void TfToBudget(bool fitted)
        {
            if (rs == null) return;
            ApplyTf();
            var tf = rs.Setup.Tf;
            if (string.IsNullOrWhiteSpace(tf.X) || string.IsNullOrWhiteSpace(tf.Y) || !fitted && string.IsNullOrWhiteSpace(tf.Theory))
            {
                Win98MessageBox.Show(FindForm(), fitted ? "Choose X and Y first." : "Choose X and Y and enter the ideal first (e.g. 2*x).", "SPICA", MessageBoxButtons.OK, Win98Icon.Information);
                return;
            }
            var it = new ErrorItem { Name = tf.Y + (fitted ? " vs fitted line" : " vs ideal"), Y = tf.Y, X = tf.X, ReferToInput = true };
            if (fitted)
            {
                var rep = TfReport.Compute(rs, tf, Math.Max(0, cbTfGroup.SelectedIndex));
                if (rep.Error != null) { Win98MessageBox.Show(FindForm(), rep.Error, "SPICA", MessageBoxButtons.OK, Win98Icon.Warning); return; }
                it.Ideal = IdealKind.Linear; it.Gain = rep.Fit.Slope; it.Offset = rep.Fit.Intercept;
                it.Tf = ErrorItem.LinearTf(it.Gain, it.Offset);
            }
            else if (ErrorItem.TryLinear(tf.Theory, out var g, out var o)) { it.Ideal = IdealKind.Linear; it.Gain = g; it.Offset = o; it.Tf = ErrorItem.LinearTf(g, o); }
            else { it.Ideal = IdealKind.Expression; it.Tf = tf.Theory; }
            var like = rs.Setup.Errors.FirstOrDefault(e => e.HasBounds && e.Y.Equals(tf.Y, StringComparison.OrdinalIgnoreCase) && e.UsesTf);
            if (like != null) { it.Symmetric = like.Symmetric; it.Lower = like.Lower; it.Upper = like.Upper; it.Unit = like.Unit; it.FullScale = like.FullScale; it.ReferToInput = like.ReferToInput; }
            string bad = ErrorBudget.Check(rs, it);
            if (bad != null) { Win98MessageBox.Show(FindForm(), bad, "SPICA", MessageBoxButtons.OK, Win98Icon.Warning); return; }
            AddBudgetItem(it);
        }

        void RefreshTf()
        {
            if (rs == null) { chTf.Clear(); chTfErr.Clear(); foreach (var t in tfTiles) t.Set(""); lblTfNote.Text = ""; return; }
            // the combos drive the plot immediately; the ideal applies with Apply / Enter
            var tf = new TfSetup { X = cbTfX.SelectedItem as string ?? "", Y = cbTfY.SelectedItem as string ?? "", Theory = rs.Setup.Tf.Theory };
            var rep = tfShown = BuildTf(chTf, chTfErr, tf, Math.Max(0, cbTfGroup.SelectedIndex));
            if (rep.Error != null || rep.N == 0)
            {
                foreach (var t in tfTiles) t.Set("");
                lblTfNote.Text = rep.Error ?? "";
                return;
            }
            var f = rep.Fit;
            tfTiles[0].Set(F(f.Slope), "± " + F(1.96 * f.SlopeSe) + " (95 %)");
            tfTiles[1].Set(ErrorBudget.Eng(f.Intercept), "± " + ErrorBudget.Eng(1.96 * f.InterceptSe) + (Math.Abs(f.Intercept) < 1.96 * f.InterceptSe ? ", not different from 0" : ""));
            bool th = rep.Theory != null;
            tfTiles[2].Set(th ? F(rep.ThGain) : "-", th ? (rep.ThLinR2 < 0.999999 ? "the ideal is not a line" : "") : "enter an ideal");
            tfTiles[3].Set(th ? F(rep.ThOffset) : "-", th ? "" : "enter an ideal");
            tfTiles[4].Set(th ? rep.GainErrPct.ToString("+0.0000;-0.0000", Inv) + " %" : "-", th ? "fitted vs ideal gain" : "");
            tfTiles[5].Set(th ? ErrorBudget.Eng(rep.OffsetErr) : "-", th ? "units of " + tf.Y : "");
            tfTiles[6].Set(ErrorBudget.Eng(f.ResidualStd), "sigma, units of " + tf.Y);
            tfTiles[7].Set(f.R2.ToString("0.000000", Inv), $"n = {rep.N}");
            lblTfNote.Text = $"Through the origin: y = {F(f.OriginSlope)}·x, scatter sigma {ErrorBudget.Eng(f.OriginResidualStd)}.\n\n" +
                             "The lower chart is Y - ideal (or the residual around the fitted line) in units of " + tf.Y + ", all runs together.\n" +
                             "To judge it against boundaries, per temperature and input band, use one of the buttons above: " +
                             "the error goes to Error Budget, referred to the input.";
        }

        TfReport BuildTf(Win98Chart top, Win98Chart bottom, TfSetup tf, int group)
        {
            tf.Budget = double.NaN;
            tf.BudgetPercent = false;
            var rep = TfReport.Compute(rs, tf, group);
            top.Clear(); bottom.Clear();
            top.Title = $"{tf.Y} vs {tf.X}";
            top.XLabel = tf.X; top.YLabel = tf.Y;
            if (rep.Error != null || rep.N == 0) { top.EmptyText = rep.Error ?? "No data."; bottom.EmptyText = ""; return rep; }
            top.Add(new ChartSeries { Kind = SeriesKind.Points, X = rep.X, Y = rep.Y, Color = Win98Chart.Navy, Label = "runs", Size = 3 });
            double xa = rep.X.Min(), xb = rep.X.Max();
            var lx = new[] { xa, xb };
            top.Add(new ChartSeries { Kind = SeriesKind.Line, X = lx, Y = lx.Select(rep.Fit.Predict).ToArray(), Color = Win98Chart.Green, Size = 2, Label = "fitted line a*x+b" });
            if (rep.Theory != null)
            {
                var xs = Enumerable.Range(0, 41).Select(i => xa + (xb - xa) * i / 40).ToArray();
                top.Add(new ChartSeries { Kind = SeriesKind.Line, X = xs, Y = xs.Select(x => rep.Theory.Eval(x, double.NaN)).ToArray(), Color = Win98Chart.Red, Dashed = true, Label = "ideal" });
            }

            // error plot: vs the ideal when given, else residuals around the fitted line
            double[] err;
            if (rep.ErrTheory != null) { err = rep.ErrTheory; bottom.Title = $"{tf.Y} - ideal (units of {tf.Y})"; }
            else
            {
                err = rep.X.Select((x, i) => rep.Y[i] - rep.Fit.Predict(x)).ToArray();
                bottom.Title = $"{tf.Y} - fitted line (units of {tf.Y})";
            }
            bottom.XLabel = tf.X; bottom.YLabel = "error";
            bottom.Add(new ChartSeries { Kind = SeriesKind.Points, X = rep.X, Y = err, Color = Win98Chart.Navy, Size = 3, InLegend = false });
            bottom.Add(new ChartSeries { Kind = SeriesKind.HLine, Y = new[] { 0.0 }, Color = Color.Black, InLegend = false });
            return rep;
        }

        // =====================================================================================
        //  Sensitivity
        // =====================================================================================

        Win98TabPage BuildSensPage()
        {
            var p = new Win98TabPage("Sensitivity", Win98.Icon("chart16"));
            var bar = Bar(30);
            cbSensMeas = Combo(82, 2, 150);
            cbSensGroup = Combo(290, 2, 150);
            cbSensInputs = Combo(500, 2, 150);
            cbSensInputs.Items.AddRange(new object[] { "Random parameters", "Other measurements" });
            cbSensInputs.SelectedIndex = 0;
            cbSensShow = Combo(704, 2, 150);
            cbSensShow.Items.AddRange(new object[] { "Share of variance", "Signed effect" });
            cbSensShow.SelectedIndex = 0;
            bar.Controls.AddRange(new Control[] { Lbl("Measurement:", 0, 2), cbSensMeas, Lbl("Group:", 246, 2), cbSensGroup,
                Lbl("Inputs:", 454, 2), cbSensInputs, Lbl("Show:", 664, 2), cbSensShow, Hint("Double-click a row to plot it.", 866, 2) });

            chSens = new Win98Chart();
            txtSens = Memo();
            lvSens = new Win98ListView();
            lvSens.HeaderTip = c => Glossary.Column(lvSens.Columns[c].Text);
            foreach (var (t, w) in new[] { ("#", 34), ("Input", 220), ("Share of variance", 110), ("Signed effect (SRC)", 116), ("Correlation r", 90), ("Slope (per unit)", 100), ("Shift at limit", 100), ("Limit", 150) })
                lvSens.Columns.Add(t, w, t == "Input" || t == "Limit" ? HorizontalAlignment.Left : HorizontalAlignment.Right);
            lvSens.DoubleClick += (s, e) =>
            {
                if (sensRep == null || lvSens.SelectedIndices.Count == 0 || lvSens.SelectedIndices[0] >= sensRep.Items.Count) return;
                ShowScatter(sensRep.Items[lvSens.SelectedIndices[0]].Name, sensRep.Meas);
            };
            var right = new Win98Well(txtSens) { Dock = DockStyle.Right, Width = 440 };
            var listWell = new Win98Well(lvSens) { Dock = DockStyle.Bottom, Height = 170 };
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Win98.Face };
            body.Controls.Add(ChartWell(chSens));
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

        /// <summary>Opens the Scatter tab with this X and Y.</summary>
        void ShowScatter(string x, string y)
        {
            filling = true;
            Select(cbTfX, x);
            Select(cbTfY, y);
            cbTfGroup.SelectedIndex = Math.Min(Math.Max(0, cbSensGroup.SelectedIndex), cbTfGroup.Items.Count - 1);
            filling = false;
            chTf.ResetView(); chTfErr.ResetView();
            Show(pgTf);
            RefreshTf();
        }

        void RefreshSens()
        {
            if (rs == null || !(cbSensMeas.SelectedItem is string m)) { chSens.Clear(); lvSens.Items.Clear(); txtSens.Text = ""; return; }
            sensRep = BuildSensitivity(chSens, m, Math.Max(0, cbSensGroup.SelectedIndex), cbSensInputs.SelectedIndex == 0, cbSensShow.SelectedIndex == 1);
            txtSens.Text = sensRep.Narrative().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
            lvSens.BeginUpdate();
            lvSens.Items.Clear();
            int k = 1;
            foreach (var it in sensRep.Items)
            {
                var row = new ListViewItem((k++).ToString(Inv));
                foreach (var x in new[] { it.Name, double.IsNaN(it.Share) ? "" : (100 * it.Share).ToString("0.0", Inv) + " %", Num(it.Src, "+0.000;-0.000"),
                                          Num(it.R, "+0.000;-0.000"), double.IsNaN(it.Slope) ? "" : F(it.Slope), double.IsNaN(it.AtLimit) ? "" : F(it.AtLimit), it.LimitText })
                    row.SubItems.Add(x);
                lvSens.Items.Add(row);
            }
            lvSens.EndUpdate();
        }

        SensitivityReport BuildSensitivity(Win98Chart ch, string m, int group, bool onParams, bool signed, int top = 20)
        {
            ch.Clear();
            var rep = onParams ? SensitivityReport.Parameters(rs, m, group) : SensitivityReport.Measurements(rs, m, group);
            var items = rep.Items.Where(i => !double.IsNaN(i.Share)).Take(top).ToList();
            ch.Title = onParams ? $"What drives {m}" : $"Correlation of {m} with the other measurements";
            if (items.Count == 0) { ch.EmptyText = rep.Error ?? "Nothing to show."; ch.Invalidate(); return rep; }
            ch.YCategories = items.Select(i => i.Name).ToArray();
            if (signed)
            {
                ch.XLabel = onParams ? $"standardized effect: sigmas of {m} per sigma of the input" : "correlation r";
                ch.Add(new ChartSeries { Kind = SeriesKind.HBars, X = items.Select(i => i.Src).ToArray(), Color = Win98Chart.Navy, NegColor = Win98Chart.Maroon, InLegend = false });
            }
            else
            {
                ch.XLabel = onParams ? $"share of the variance of {m} (%)" : "r squared (%)";
                ch.Add(new ChartSeries { Kind = SeriesKind.HBars, X = items.Select(i => 100 * i.Share).ToArray(), Color = Win98Chart.Navy, InLegend = false });
            }
            ch.Invalidate();
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
            lvTemp.HeaderTip = c => Glossary.Column(lvTemp.Columns[c].Text);
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
                txtTemp.Text = "No temperature data in this campaign.";
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
            chConvSigma = Track(new Win98Chart());
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

        public void ExportReport()
        {
            if (rs == null) return;
            ReportOptions o;
            using (var f = new ReportForm(rs))
            {
                if (f.ShowDialog(FindForm()) != DialogResult.OK || f.Options == null) return;
                o = f.Options;
            }
            rs.Setup.Report = o;      // kept with the setup (and the campaign folder), so the next report and the auto report match
            NotifySetup();
            string path = ReportPath(o);
            using (var d = new SaveFileDialog { Title = "Save Analysis Report", Filter = "HTML report (*.html)|*.html", FileName = Path.GetFileName(path), InitialDirectory = Path.GetDirectoryName(path), AutoUpgradeEnabled = false })
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                Cursor = Cursors.WaitCursor;
                try
                {
                    File.WriteAllText(d.FileName, BuildReport(o), new UTF8Encoding(false));
                    System.Diagnostics.Process.Start(d.FileName);
                }
                catch (Exception ex) { Win98MessageBox.Show(FindForm(), "Could not write the report:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); }
                finally { Cursor = Cursors.Default; }
            }
        }

        /// <summary>Writes the report as set in the setup into the campaign folder; returns its path.</summary>
        public string AutoReport()
        {
            string path = ReportPath(rs.Setup.Report);
            WriteReport(path);
            return path;
        }

        /// <summary>Writes the HTML report without dialogs (also used by tests).</summary>
        public void WriteReport(string path, ReportOptions o = null) => File.WriteAllText(path, BuildReport(o ?? rs.Setup.Report), new UTF8Encoding(false));

        /// <summary>
        /// "&lt;simulation&gt;_report.html" (or _error_budget for the short report) in the campaign folder; when that file
        /// already exists, a timestamp is added so no report is overwritten.
        /// </summary>
        string ReportPath(ReportOptions o)
        {
            string name = !string.IsNullOrEmpty(rs.Setup.Schematic) ? Path.GetFileNameWithoutExtension(rs.Setup.Schematic) : rs.Title;
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            string dir = !string.IsNullOrEmpty(rs.Folder) && Directory.Exists(rs.Folder) ? rs.Folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string stem = name + (o.Kind == ReportKind.Concise ? "_error_budget" : "_report");
            string path = Path.Combine(dir, stem + ".html");
            if (File.Exists(path)) path = Path.Combine(dir, stem + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv) + ".html");
            return path;
        }

        string BuildReport(ReportOptions o)
        {
            string H(string s) => WebUtility.HtmlEncode(s ?? "");
            string Fig(Win98Chart c, string caption, int w = 760, int h = 340) =>
                $"<figure>{c.ToSvg(w, h)}" + (caption != null ? $"<figcaption>{H(caption)}</figcaption>" : "") + "</figure>";
            string Pre(string s) => string.IsNullOrWhiteSpace(s) ? "" : "<pre>" + H(s.TrimEnd()) + "</pre>";
            string Card(string label, string value, string sub = null, string color = null, string back = null) =>
                $"<div class=\"card\"{(back != null ? $" style=\"background:{back}\"" : "")}><span>{H(label)}</span><b{(color != null ? $" style=\"color:{color}\"" : "")}>{H(value)}</b>" +
                (string.IsNullOrEmpty(sub) ? "" : $"<i>{H(sub)}</i>") + "</div>";

            var st = rs.Setup;
            bool concise = o.Kind == ReportKind.Concise;
            var groups = rs.GroupNames();
            var meas = concise ? new List<string>() : rs.Meas.Where(o.HasMeas).ToList();
            var errItems = st.Errors.Where(e => o.HasError(e.Name)).ToList();
            bool hasBudget = errItems.Count > 0 && (concise || o.Budget);
            bool showSummary = !concise && o.Summary && meas.Count > 0;
            bool anySpec = meas.Any(m => st.Spec(m)?.IsSet == true);
            var tf = st.Tf;
            bool hasTf = !concise && o.Tf && tf.X.Length > 0 && tf.Y.Length > 0 && rs.Col(tf.Y) >= 0;
            string schematic = string.IsNullOrEmpty(st.Schematic) ? "" : Path.GetFileName(st.Schematic);
            string kk = st.DesignSigma.ToString("0.#", Inv);

            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
              .Append($"<title>SPICA report - {H(rs.Title)}</title><style>")
              .Append(":root{--ink:#1d1d1f;--muted:#5f5f5f;--line:#dedede;--accent:#000080;--soft:#f5f5f7}")
              .Append("*{box-sizing:border-box}body{margin:0;background:#e9e9e6;color:var(--ink);font:13px/1.5 'Segoe UI',Tahoma,Arial,sans-serif}")
              .Append(".page{max-width:1020px;margin:24px auto;background:#fff;padding:36px 44px 28px;box-shadow:0 1px 4px rgba(0,0,0,.18)}")
              .Append("header{border-bottom:3px solid var(--accent);padding-bottom:14px;margin-bottom:8px}")
              .Append("header .brand{font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:var(--accent);font-weight:600}")
              .Append("h1{margin:4px 0 2px;font-size:24px;font-weight:600}header .sub{color:var(--muted)}")
              .Append("h2{font-size:18px;font-weight:600;margin:36px 0 10px;padding-top:14px;border-top:1px solid var(--line)}")
              .Append("h3{font-size:14px;font-weight:600;margin:22px 0 6px}")
              .Append("table{border-collapse:collapse;width:100%;font-size:12px;font-variant-numeric:tabular-nums;margin:6px 0 4px}")
              .Append("th,td{padding:5px 9px;border-bottom:1px solid var(--line);text-align:right;white-space:nowrap}")
              .Append("th{background:var(--soft);font-weight:600;border-bottom:2px solid #c4c4c4}td:first-child,th:first-child{text-align:left}")
              .Append("table.kv{width:auto}table.kv td{text-align:left;border:none;padding:2px 14px 2px 0}table.kv td:first-child{color:var(--muted)}")
              .Append(".req{border:1px solid var(--line);border-left:4px solid var(--accent);background:var(--soft);padding:8px 14px;margin:8px 0 12px}")
              .Append(".req .t{font-size:11px;letter-spacing:.06em;text-transform:uppercase;color:var(--accent);font-weight:600;margin-bottom:2px}")
              .Append(".req table.kv td{white-space:normal}.req table.kv td:last-child{font-weight:600}")
              .Append(".verdict{font-size:14px;font-weight:600;margin:6px 0 2px}")
              .Append(".wide{overflow-x:auto}.note{color:var(--muted);font-size:12px;margin:2px 0 10px}ul.notes{margin:6px 0 10px;padding-left:20px;color:#333}")
              .Append("figure{margin:12px 0 6px}figure svg{display:block;width:100%;height:auto;border:1px solid var(--line)}")
              .Append("figcaption{color:var(--muted);font-size:12px;margin-top:5px}")
              .Append("pre{background:var(--soft);border-left:3px solid var(--accent);padding:9px 13px;margin:8px 0;font:12px/1.45 Consolas,'Lucida Console',monospace;white-space:pre-wrap}")
              .Append("nav{background:var(--soft);padding:10px 16px;margin:18px 0}nav b{font-size:12px}nav ol{columns:2;margin:4px 0 0;padding-left:20px}nav a{color:var(--accent);text-decoration:none}")
              .Append(".cards{display:flex;flex-wrap:wrap;gap:10px;margin:8px 0 4px}.card{border:1px solid var(--line);border-top:3px solid var(--accent);padding:6px 12px;min-width:120px}")
              .Append(".card span{display:block;color:var(--muted);font-size:11px}.card b{font-size:15px;font-weight:600;font-variant-numeric:tabular-nums}")
              .Append(".card i{display:block;color:var(--muted);font-size:11px;font-style:normal}")
              .Append("footer{margin-top:36px;padding-top:8px;border-top:1px solid var(--line);color:var(--muted);font-size:11px}")
              .Append("@media print{body{background:#fff}.page{box-shadow:none;margin:0;max-width:none;padding:0}nav{display:none}")
              .Append("section.meas" + (concise ? ",section.err+section.err" : "") + "{break-before:page}figure,pre,table,.cards,.req{break-inside:avoid}}")
              .Append("</style></head><body><div class=\"page\">");

            // ---------------------------------------------------------------- header + campaign
            sb.Append("<header><div class=\"brand\">SPICA - Spice Parallel Instancer Circuit Analysis</div>")
              .Append($"<h1>{(concise ? "Error budget" : "Monte Carlo report")}: {H(schematic.Length > 0 ? schematic : rs.Title)}</h1>")
              .Append($"<div class=\"sub\">Campaign {H(rs.Title)} &middot; generated {DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv)} by SPICA {H(MainForm.AppVersion)}</div></header>");

            sb.Append("<table class=\"kv\">");
            void Row(string k, string v) => sb.Append($"<tr><td>{H(k)}</td><td>{H(v)}</td></tr>");
            Row("Schematic", st.Schematic);
            Row("Results folder", rs.Folder);
            Row("Runs", $"{rs.Rows.Count} ({rs.Rows.Count(r => r.Status == RunStatus.Ok)} with data)");
            Row("Temperature", rs.Mode == TempMode.Corners ? "corners " + string.Join(", ", rs.Corners.Select(t => t.ToString("0.###", Inv))) + " °C"
                              : rs.Mode == TempMode.Random ? "random per run" : "schematic default");
            Row("Sampling", rs.Paired ? $"SPICA paired generator, seed {rs.Seed}" : "LTspice mc()");
            if (rs.Params.Count > 0) Row("Tolerances", $"{rs.Params.Count} random parameters");
            sb.Append("</table>");

            ErrorBudget.Register(rs, st.Errors);
            var res = hasBudget ? errItems.Select(e => ErrorBudget.Compute(rs, e, 0, st.DesignSigma, st.PessimisticSigma)).ToList() : new List<ErrorResult>();

            if (!concise || res.Count > 1)
            {
                sb.Append("<nav><b>Contents</b><ol>");
                if (hasBudget) sb.Append("<li><a href=\"#budget\">Error budget</a></li>");
                for (int j = 0; j < res.Count; j++) sb.Append($"<li><a href=\"#e{j}\">{(concise ? "" : "&nbsp;&nbsp;")}{H(res[j].Item.Name)}</a></li>");
                if (showSummary) sb.Append("<li><a href=\"#summary\">Summary</a></li>");
                foreach (var m in meas) sb.Append($"<li><a href=\"#m{rs.Meas.IndexOf(m)}\">{H(m)}</a></li>");
                if (hasTf) sb.Append("<li><a href=\"#tf\">Transfer function</a></li>");
                sb.Append("</ol></nav>");
            }

            // ---------------------------------------------------------------- error budget
            if (hasBudget)
            {
                int pass = res.Count(r => r.Verdict == "PASS"), fail = res.Count(r => r.Verdict == "FAIL"), check = res.Count(r => r.Verdict == "CHECK");
                sb.Append("<h2 id=\"budget\">Error budget</h2>");
                sb.Append("<div class=\"cards\">").Append(Card("Criterion", $"mean ± {kk} σ inside the boundaries", "in every temperature and input band"))
                  .Append(Card("Pass", pass.ToString(Inv), null, pass > 0 ? "#007a00" : null)).Append(Card("Fail", fail.ToString(Inv), null, fail > 0 ? "#c00000" : null))
                  .Append(Card("Check (not normal)", check.ToString(Inv), null, check > 0 ? "#8a6d00" : null)).Append("</div>");
                if (res.Count > 1 || !concise)
                {
                    sb.Append("<div class=\"wide\"><table><tr>");
                    foreach (var h in new[] { "Error", "Verdict", "Margin", $"Mean ± {kk}σ", "Boundaries", "Mean", "Sigma", "Normality p", "Worst condition", "n" })
                        sb.Append($"<th>{H(h)}</th>");
                    sb.Append("</tr>");
                    foreach (var r in res)
                    {
                        string u(double v) => ErrorBudget.Fmt(v, r.Item);
                        var cells = r.Error != null ? new[] { r.Item.Name, "ERROR", "", r.Error, "", "", "", "", "", "" } : new[]
                        {
                            r.Item.Name, r.Verdict, r.HasBudget ? r.MarginPct.ToString("0", Inv) + " %" : "", $"{u(r.Lo)} .. {u(r.Hi)}", r.Item.BoundsText(),
                            u(r.D.Mean), u(r.Sigma), MeasStats.P(r.AdP), r.Slices.Count > 1 ? r.Worst.Label : "", r.N.ToString(Inv),
                        };
                        sb.Append("<tr>").Append(string.Concat(cells.Select((c, j) => j == 1 ? $"<td style=\"color:{HtmlVerdict(r.Verdict)};font-weight:600;text-align:left\">{H(c)}</td>" : $"<td>{H(c)}</td>"))).Append("</tr>");
                    }
                    sb.Append("</table></div>");
                }
                sb.Append($"<p class=\"note\">PASS: mean ± {kk} σ inside the boundaries in every condition, the error bell-shaped (Anderson-Darling p ≥ {MeasStats.NormalAlpha.ToString("0.###", Inv)}) " +
                          $"or with tails no longer than a normal's. FAIL: a simulated unit is outside, or mean ± {kk} σ crosses a boundary. " +
                          $"CHECK: not bell-shaped, so the {kk} σ extrapolation cannot decide. Sigma: " +
                          (st.PessimisticSigma ? "95 % upper confidence bound." : "sample standard deviation.") + "</p>");

                using (var cb = new Win98Chart())
                using (var cs = new Win98Chart())
                    for (int j = 0; j < res.Count; j++)
                        BudgetBlock(sb, res[j], j, cb, cs, concise, H, Fig, Card);
            }

            // ---------------------------------------------------------------- summary tables
            if (showSummary)
            {
                sb.Append("<h2 id=\"summary\">Summary</h2>");
                for (int g = 0; g < groups.Count; g++)
                {
                    if (groups.Count > 1) sb.Append($"<h3>{H(groups[g])}</h3>");
                    var stats = meas.Select(m => MeasStats.Of(m, groups[g], rs.Values(m, g), st.Spec(m))).ToList();

                    sb.Append("<div class=\"wide\"><table><tr>");
                    foreach (var h in new[] { "Measurement", "n", "Mean", "Std dev", "Min", "Max", "Median", "P0.135 %", "P99.865 %", "Skew", "Ex. kurt", "Normality p" })
                        sb.Append($"<th>{H(h)}</th>");
                    sb.Append("</tr>");
                    foreach (var s in stats)
                    {
                        var cells = new[] { s.Meas, s.D.N.ToString(Inv), F(s.D.Mean), F(s.D.Std), F(s.D.Min), F(s.D.Max), F(s.D.Median), F(s.P00135), F(s.P99865),
                            Num(s.D.Skew, "0.000"), Num(s.D.ExKurt, "0.000"), MeasStats.P(s.AdP) };
                        sb.Append("<tr>").Append(string.Concat(cells.Select(c => $"<td>{H(c)}</td>"))).Append("</tr>");
                    }
                    sb.Append("</table></div>");
                    sb.Append("<p class=\"note\">P0.135 % and P99.865 % bound the central 99.73 % (&plusmn;3&sigma; for a normal distribution). Normality p is the Anderson-Darling test.</p>");

                    if (anySpec)
                    {
                        sb.Append("<div class=\"wide\"><table><tr>");
                        foreach (var h in new[] { "Measurement", "LSL", "USL", "Yield", "Yield 95 % CI", "Cpk", "Predicted fails" })
                            sb.Append($"<th>{H(h)}</th>");
                        sb.Append("</tr>");
                        foreach (var s in stats.Where(x => x.Spec?.IsSet == true))
                        {
                            var cells = new[] { s.Meas, F(s.Spec.Lsl), F(s.Spec.Usl), MeasStats.Pct(s.Cap.Yield), $"{MeasStats.Pct(s.Cap.YieldLo)} .. {MeasStats.Pct(s.Cap.YieldHi)}",
                                Num(s.Cap.Cpk, "0.000"), s.NormalOk ? MeasStats.Ppm(s.Cap.PredictedPpm) : "not normal" };
                            sb.Append("<tr>").Append(string.Concat(cells.Select(c => $"<td>{H(c)}</td>"))).Append("</tr>");
                        }
                        sb.Append("</table></div>");
                    }
                    if (rs.Mode != TempMode.Corners) break;   // other modes have a single group
                }
            }

            // ---------------------------------------------------------------- one section per measurement
            using (var c1 = new Win98Chart())
            using (var c2 = new Win98Chart())
            {
                foreach (var m in meas)
                {
                    sb.Append($"<section class=\"meas\"><h2 id=\"m{rs.Meas.IndexOf(m)}\">{H(m)}</h2>");
                    var s = BuildHistogram(c1, m, 0, true);
                    sb.Append("<div class=\"cards\">").Append(Card("Runs", s.D.N.ToString(Inv))).Append(Card("Mean", Win98Chart.Fmt(s.D.Mean))).Append(Card("Std dev", Win98Chart.Fmt(s.D.Std)))
                      .Append(Card("Bell-shaped", s.NormalOk ? "yes" : "no", "p = " + MeasStats.P(s.AdP)));
                    if (s.Spec?.IsSet == true) sb.Append(Card("Yield", MeasStats.Pct(s.Cap.Yield))).Append(Card("Cpk", Num(s.Cap.Cpk, "0.000")));
                    sb.Append("</div>");
                    if (o.Histogram)
                    {
                        sb.Append(Fig(c1, $"Distribution of {m} over {s.D.N} runs" + (s.Spec?.IsSet == true ? ", with the specification limits." : ".")));
                        sb.Append(Pre(s.Narrative()));
                    }

                    if (o.Sensitivity && rs.Params.Count > 0)
                    {
                        // what drives the spread: share of variance per random parameter
                        var sr = BuildSensitivity(c1, m, 0, true, false, 12);
                        var top = sr.Items.Where(x => !double.IsNaN(x.Share)).Take(12).ToList();
                        if (top.Count > 0)
                        {
                            sb.Append("<h3>Sensitivity</h3>");
                            sb.Append(Fig(c1, $"Share of the variance of {m} explained by each random parameter (linear model, R² = {(100 * sr.R2).ToString("0.0", Inv)} %).", 760, 70 + 24 * top.Count));
                            sb.Append("<div class=\"wide\"><table><tr><th>Input</th><th>Share of variance</th><th>Signed effect (SRC)</th><th>Shift at limit</th><th style=\"text-align:left\">Limit</th></tr>");
                            foreach (var it in top)
                                sb.Append($"<tr><td>{H(it.Name)}</td><td>{(100 * it.Share).ToString("0.0", Inv)} %</td><td>{Num(it.Src, "+0.000;-0.000")}</td><td>{H(F(it.AtLimit))}</td><td style=\"text-align:left\">{H(it.LimitText)}</td></tr>");
                            sb.Append("</table></div>");
                        }
                    }

                    if (o.Convergence)
                    {
                        sb.Append("<h3>Convergence</h3>");
                        string conv = BuildConvergence(c1, c2, m, 0);
                        sb.Append(Fig(c1, null, 760, 240)).Append(Fig(c2, null, 760, 240));
                        sb.Append(Pre(conv));
                    }

                    if (o.Temperature && rs.Mode == TempMode.Corners)
                    {
                        string t = BuildTempCorners(c1, new ListView(), m);
                        sb.Append("<h3>Temperature</h3>").Append(Fig(c1, $"{m} at each temperature corner.")).Append(Pre(t));
                    }
                    else if (o.Temperature && rs.Mode == TempMode.Random)
                    {
                        string t = BuildTempRandom(c1, new ListView(), m);
                        sb.Append("<h3>Temperature</h3>").Append(Fig(c1, $"{m} against the random temperature of each run.")).Append(Pre(t));
                    }
                    sb.Append("</section>");
                }

                if (hasTf)
                {
                    sb.Append($"<section class=\"meas\"><h2 id=\"tf\">Transfer function: {H(tf.Y)} vs {H(tf.X)}</h2>");
                    for (int g = 0; g < groups.Count; g++)
                    {
                        var rep = BuildTf(c1, c2, tf, g);
                        if (groups.Count > 1) sb.Append($"<h3>{H(groups[g])}</h3>");
                        sb.Append(Fig(c1, $"{tf.Y} against {tf.X}.")).Append(Fig(c2, null, 760, 240)).Append(Pre(rep.Narrative()));
                        if (rs.Mode != TempMode.Corners) break;
                    }
                    sb.Append("</section>");
                }
            }
            sb.Append($"<footer>Generated by SPICA {H(MainForm.AppVersion)} - Spice Parallel Instancer Circuit Analysis. ")
              .Append("Figures are vector graphics (SVG): they stay sharp when zoomed or printed.</footer></div></body></html>");
            return sb.ToString();
        }

        static string HtmlVerdict(string v) => v == "PASS" ? "#007a00" : v == "FAIL" ? "#c00000" : v == "CHECK" ? "#8a6d00" : "inherit";

        /// <summary>
        /// One error in the report: the requirement as entered (for a reviewer to check), the figures calculated from
        /// it, the histogram, the error across the input and temperature, what drives it and every condition.
        /// </summary>
        void BudgetBlock(StringBuilder sb, ErrorResult r, int j, Win98Chart cb, Win98Chart cs, bool concise,
                         Func<string, string> H, Func<Win98Chart, string, int, int, string> Fig, Func<string, string, string, string, string, string> Card)
        {
            var it = r.Item;
            string k = r.K.ToString("0.#", Inv);
            string u(double v) => ErrorBudget.Fmt(v, it);
            string abs(double v) => ErrorBudget.Eng(it.ToAbs(v));
            bool pctFs = it.Unit == BudgetUnit.PctFs;
            string hx = concise ? "h2" : "h3";

            sb.Append($"<section class=\"err\"><{hx} id=\"e{j}\">{H(it.Name)}</{hx}>");

            // the requirement, as entered
            sb.Append("<div class=\"req\"><div class=\"t\">Requirement (as entered)</div><table class=\"kv\">");
            void Req(string a, string b) => sb.Append($"<tr><td>{H(a)}</td><td>{H(b)}</td></tr>");
            Req("Error", it.UsesTf ? "error = " + it.Definition() : $"{it.Y}, computed by a .meas in LTspice");
            if (it.UsesTf) Req("Referred to", it.ReferToInput ? $"the input: divided by the ideal gain, in units of {it.X}" : $"the output, in units of {it.Y}");
            Req("Boundaries", it.RequirementText());
            if (pctFs && it.HasBounds)
            {
                string b = it.Symmetric && !double.IsNaN(it.Upper) ? "±" + abs(it.Upper)
                         : double.IsNaN(it.Lower) ? "≤ " + abs(it.Upper) : double.IsNaN(it.Upper) ? "≥ " + abs(it.Lower) : abs(it.Lower) + " .. " + abs(it.Upper);
                Req("In absolute terms", $"{b} ({it.AbsUnits()})");
            }
            else if (it.Unit == BudgetUnit.PctReading) Req("In absolute terms", "depends on the reading of each run");
            Req("Criterion", $"mean ± {k} σ inside the boundaries in every condition ({(r.Slices.Count > 1 ? r.Slices.Count + " conditions: temperature × input band" : "one condition")}); " +
                             (r.Pessimistic ? "σ = 95 % upper confidence bound" : "σ = sample standard deviation"));
            Req("Shape test", $"Anderson-Darling, bell-shaped when p ≥ {MeasStats.NormalAlpha.ToString("0.###", Inv)}");
            sb.Append("</table></div>");

            if (r.Error != null)
            {
                sb.Append($"<p class=\"verdict\" style=\"color:#c00000\">Cannot be evaluated: {H(r.Error)}</p></section>");
                return;
            }

            // what was calculated
            var w = r.Worst;
            string vc = HtmlVerdict(r.Verdict);
            string vb = r.Verdict == "PASS" ? "#eef8ee" : r.Verdict == "FAIL" ? "#fbeeee" : r.Verdict == "CHECK" ? "#fbf6e6" : null;
            string Side(double sig, double m) => double.IsNaN(m) ? "open" : double.IsNaN(sig) ? "-" : sig.ToString("0.0", Inv) + " σ";
            string SideSub(double m) => double.IsNaN(m) ? "no boundary" : m >= 0 ? $"needs {k} σ: {u(m)} to spare" : $"needs {k} σ: short by {u(-m)}";
            string SideColor(double m) => double.IsNaN(m) ? null : m >= 0 ? "#007a00" : "#c00000";
            sb.Append("<div class=\"cards\">")
              .Append(Card("Verdict", r.Verdict == "-" ? "no boundaries" : r.Verdict, r.Verdict == "-" ? null : $"at {k} σ" + (r.Slices.Count > 1 ? ", all conditions" : ""), vc, vb))
              .Append(Card("Margin", r.HasBudget ? r.MarginPct.ToString("0", Inv) + " %" : "-", "of half the allowed range", null, null))
              .Append(Card("Mean", u(w.D.Mean), pctFs ? "= " + abs(w.D.Mean) : null, null, null))
              .Append(Card("Sigma", u(w.Sigma), r.Pessimistic ? "95 % upper bound" : "sample", null, null))
              .Append(Card($"Mean ± {k} σ", $"{u(w.Lo)} .. {u(w.Hi)}", pctFs ? $"= {abs(w.Lo)} .. {abs(w.Hi)}" : null, null, null))
              .Append(Card("Low side", Side(w.SigmasLo, w.MarginLo), SideSub(w.MarginLo), SideColor(w.MarginLo), null))
              .Append(Card("High side", Side(w.SigmasHi, w.MarginHi), SideSub(w.MarginHi), SideColor(w.MarginHi), null))
              .Append(Card("Bell-shaped", w.Normal ? "yes" : w.TailsOk ? "shorter tails" : "no", "p = " + MeasStats.P(w.AdP), w.Normal ? "#007a00" : w.TailsOk ? "#6b6b00" : "#8a0000", null))
              .Append(Card("Seen", $"{u(w.D.Min)} .. {u(w.D.Max)}", $"{r.Outside} of {r.N} units outside", r.Outside > 0 ? "#c00000" : null, null))
              .Append(Card("Runs", r.N.ToString(Inv), r.Slices.Count > 1 ? $"worst: {w.Label} ({w.D.N})" : null, null, null))
              .Append("</div>");
            sb.Append($"<p class=\"verdict\" style=\"color:{vc}\">{H((r.Verdict == "-" ? "" : r.Verdict + ": ") + r.VerdictText())}</p>");

            // figures
            BuildBudgetChart(cb, r, 0);
            sb.Append(Fig(cb, $"Error of {it.Name}" + (r.Slices.Count > 1 ? $" in the worst condition, {w.Label} ({w.D.N} runs)" : $" ({w.D.N} runs)") +
                              $", with the normal fit, mean ± {k} σ and the boundaries.", 760, 300));
            if (r.InputVaries) { BuildBudgetChart(cb, r, 1); sb.Append(Fig(cb, $"Error across the input range ({it.X}), with mean ± {k} σ per input band.", 760, 260)); }
            if (BuildBudgetTempChart(cb, r))
                sb.Append(Fig(cb, rs.Mode == TempMode.Corners ? $"Error at each temperature corner: mean, mean ± {k} σ and the extremes seen." : "Error against the random temperature of each run.", 760, 260));
            int bars = BuildBudgetSens(cs, r, w, 10);
            if (bars > 0)
                sb.Append(Fig(cs, $"What drives the error{(r.Slices.Count > 1 ? " in the worst condition" : "")}: share of its variance explained by each tolerance" +
                                  (r.Sens != null && r.Sens.Error == null ? $" (linear model, R² = {(100 * r.Sens.R2).ToString("0.0", Inv)} %)." : "."), 760, 70 + 24 * bars));

            // every condition
            if (r.Slices.Count > 1)
            {
                sb.Append("<h3>Every condition</h3><div class=\"wide\"><table><tr>");
                foreach (var h in new[] { "Condition", "n", "Mean", "Sigma", $"Mean ± {k}σ", "Low side", "High side", "Margin", "Bell-shaped", "Verdict" })
                    sb.Append($"<th>{H(h)}</th>");
                sb.Append("</tr>");
                foreach (var s in r.Slices)
                {
                    var cells = new[]
                    {
                        s.Label, s.D.N.ToString(Inv), u(s.D.Mean), u(s.Sigma), $"{u(s.Lo)} .. {u(s.Hi)}", Side(s.SigmasLo, s.MarginLo), Side(s.SigmasHi, s.MarginHi),
                        r.HasBudget ? r.SlicePct(s).ToString("0", Inv) + " %" : "", s.Normal ? "yes" : s.TailsOk ? "shorter tails" : "no", s.Verdict,
                    };
                    sb.Append(s == w ? "<tr style=\"background:#fff8d8\">" : "<tr>")
                      .Append(string.Concat(cells.Select((c, i) => i == 9 ? $"<td style=\"color:{HtmlVerdict(c)};font-weight:600\">{H(c)}</td>" : $"<td>{H(c)}</td>"))).Append("</tr>");
                }
                sb.Append("</table></div><p class=\"note\">Highlighted: the condition the figures above refer to. Low / high side: how many σ the boundary is from the mean.</p>");
            }

            var notes = r.Notes();
            if (notes.Count > 0) sb.Append("<ul class=\"notes\">").Append(string.Concat(notes.Select(n => $"<li>{H(n)}</li>"))).Append("</ul>");
            sb.Append("</section>");
        }
    }
}
