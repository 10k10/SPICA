using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Spica.Core;

namespace Spica.UI
{
    // The Setup tab: what to simulate. Left, the campaign in the order it is decided (analysis, temperature, random
    // values, output); right, what the schematic offers (signals, circuit); at the bottom, what will run and the button.
    public partial class MainForm
    {
        Win98TextField txtAsc, txtRuns, txtOut, txtFilter, txtSeed;
        Win98TextField txtCorners, txtTempMin, txtTempMax;
        Win98CheckBox chkReseed, chkKeepRaw, chkPaired, chkAutoReport;
        Win98RadioButton rbSaveSel, rbSaveAll, rbTempDefault, rbTempCorners, rbTempRandom;
        CheckedListBox lstSignals;
        Label lblSignalCount, lblPcRuns, lblPcLtspice, lblPlan, lblPlan2;
        RichTextBox txtCircuit;
        Win98Button btnAdd, btnReload;

        // analysis: Monte Carlo (with optional early stop) or worst case
        Win98RadioButton rbMc, rbWc;
        Win98CheckBox chkStop;
        Win98TextField txtSpread, txtYield, txtMinRuns, txtWatch, txtWcSigma;
        ComboBox cbWc;
        Label lblAnalysisHint;
        int siteCount;                 // tolerance sites SPICA can drive in the loaded schematic

        string addedNote = "";         // what happened to the campaign just added, shown for a few seconds
        DateTime addedUntil;
        bool addingLocal;              // AddToQueue is putting the user's own campaign in the queue

        sealed class SignalItem
        {
            public string Signal;
            public bool Locked;
            public override string ToString() => Locked ? $"{Signal}    (used by .meas)" : Signal;
        }
        readonly List<SignalItem> allSignals = new List<SignalItem>();
        readonly HashSet<string> checkedSignals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool fillingList;

        const int LeftWidth = 478;     // the column of campaign groups

        void BuildSetupPage()
        {
            var p = pageSetup;
            var tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400 };
            void Digits(Win98TextField t) => t.Box.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            Label Grey(string text, int x, int y) => new Label { Text = text, Location = new Point(x, y + 3), AutoSize = true, ForeColor = Win98.Shadow, BackColor = Win98.Face };

            // --- the schematic -------------------------------------------------------------
            var grpFiles = new Win98GroupBox { Text = "Schematic", Dock = DockStyle.Top, Height = 48 };
            txtAsc = new Win98TextField { Location = new Point(44, 18), Width = 400, ReadOnly = true };
            var btnAsc = new Win98Button { Text = "&Browse..." };
            btnAsc.Click += (s, e) => BrowseSchematic();
            btnReload = new Win98Button { Text = "Re&load", Enabled = false };
            btnReload.Click += (s, e) => { if (ascPath != null) _ = LoadSchematic(ascPath, true); };
            tip.SetToolTip(btnReload, "Read the schematic again after editing it in LTspice (the setup is kept)");
            grpFiles.Controls.AddRange(new Control[] { Lbl("File:", 10, 18), txtAsc, btnAsc, btnReload });
            grpFiles.Resize += (s, e) =>
            {
                btnReload.Location = new Point(grpFiles.Width - 10 - btnReload.Width, 16);
                btnAsc.Location = new Point(btnReload.Left - 6 - btnAsc.Width, 16);
                txtAsc.Width = btnAsc.Left - 6 - txtAsc.Left;
            };

            // --- left column: the campaign ---------------------------------------------------
            var left = new Panel { Dock = DockStyle.Left, Width = LeftWidth + 6, BackColor = Win98.Face, Padding = new Padding(0, 0, 6, 0) };

            var grpAn = new Win98GroupBox { Text = "Analysis", Dock = DockStyle.Top, Height = 154 };
            rbMc = new Win98RadioButton { Text = "&Monte Carlo (random units)", Location = new Point(10, 18) };
            txtRuns = new Win98TextField { Location = new Point(216, 18), Width = 64 };
            txtSeed = new Win98TextField { Location = new Point(326, 18), Width = 70 };
            Digits(txtRuns);
            Digits(txtSeed);
            chkStop = new Win98CheckBox { Text = "Stop early when the answer is known (Runs is then the maximum)", Location = new Point(28, 42) };
            txtSpread = new Win98TextField { Location = new Point(94, 66), Width = 34 };
            txtYield = new Win98TextField { Location = new Point(190, 66), Width = 44 };
            txtMinRuns = new Win98TextField { Location = new Point(276, 66), Width = 44 };
            txtWatch = new Win98TextField { Location = new Point(368, 66), Width = LeftWidth - 10 - 368 };
            rbWc = new Win98RadioButton { Text = "&Worst case:", Location = new Point(10, 94) };
            cbWc = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(100, 93), Width = 224, Font = Win98.Font };
            cbWc.Items.AddRange(new object[] { "Auto (recommended)", "Sensitivity: n+1 runs, then verify", "All corners: 2^n runs (exact)" });
            cbWc.SelectedIndex = 0;
            txtWcSigma = new Win98TextField { Location = new Point(410, 94), Width = 34 };
            lblAnalysisHint = new Label { AutoSize = false, Location = new Point(10, 120), Size = new Size(LeftWidth - 20, 28), ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false };
            grpAn.Controls.AddRange(new Control[] { rbMc, Lbl("Runs:", 182, 18), txtRuns, Lbl("Seed:", 292, 18), txtSeed, Grey("(0 = new)", 400, 18), chkStop,
                Lbl("Spread ±", 46, 66), txtSpread, Lbl("%  Yield ≥", 132, 66), txtYield, Lbl("%  Min", 238, 66), txtMinRuns, Lbl("Watch:", 328, 66), txtWatch,
                rbWc, cbWc, Lbl("Gaussian at ±", 336, 94), txtWcSigma, Lbl("σ", 448, 94), lblAnalysisHint });

            var grpTemp = new Win98GroupBox { Text = "Temperature", Dock = DockStyle.Top, Height = 94 };
            rbTempDefault = new Win98RadioButton { Text = "Schematic &default (no .temp)", Location = new Point(10, 18) };
            rbTempCorners = new Win98RadioButton { Text = "&Corners, °C:", Location = new Point(10, 42) };
            txtCorners = new Win98TextField { Location = new Point(118, 42), Width = 206 };
            rbTempRandom = new Win98RadioButton { Text = "&Random, flat:", Location = new Point(10, 66) };
            txtTempMin = new Win98TextField { Location = new Point(118, 66), Width = 56 };
            txtTempMax = new Win98TextField { Location = new Point(204, 66), Width = 56 };
            foreach (var rb in new[] { rbTempDefault, rbTempCorners, rbTempRandom })
                rb.CheckedChanged += (s, e) => OnTempModeChanged((Win98RadioButton)s);
            grpTemp.Controls.AddRange(new Control[] { rbTempDefault, rbTempCorners, txtCorners, Grey("every run at each of them", 332, 42),
                rbTempRandom, txtTempMin, Lbl("to", 182, 66), txtTempMax, Lbl("°C", 266, 66), Grey("one temperature per run", 332, 66) });

            var grpRnd = new Win98GroupBox { Text = "Random values", Dock = DockStyle.Top, Height = 64 };
            chkPaired = new Win98CheckBox { Text = "&Paired samples: same unit at every temperature (SPICA generator)", Location = new Point(10, 18) };
            chkReseed = new Win98CheckBox { Text = "Reseed LTspice's mc() from the clock (recommended)", Location = new Point(10, 40) };
            grpRnd.Controls.AddRange(new Control[] { chkPaired, chkReseed });

            var grpOut = new Win98GroupBox { Text = "Output", Dock = DockStyle.Top, Height = 70 };
            txtOut = new Win98TextField { Location = new Point(56, 18), Width = LeftWidth - 56 - 10 - 36 };
            var btnOut = new Win98Button { Text = "...", Size = new Size(30, 23), Location = new Point(LeftWidth - 10 - 30, 16) };
            btnOut.Click += (s, e) => BrowseOutput();
            tip.SetToolTip(btnOut, "Choose the folder; every campaign gets its own dated sub-folder there");
            chkKeepRaw = new Win98CheckBox { Text = "&Keep .raw waveform files", Location = new Point(10, 44) };
            chkAutoReport = new Win98CheckBox { Text = "Write the report when it &ends", Location = new Point(200, 44) };
            grpOut.Controls.AddRange(new Control[] { Lbl("Folder:", 10, 18), txtOut, btnOut, chkKeepRaw, chkAutoReport });

            // what the options say (Tools > Options): shown where the campaign is set up, changed there
            var grpPc = new Win98GroupBox { Text = "This PC", Dock = DockStyle.Top, Height = 72 };
            var lblPcHw = new Label
            {
                Location = new Point(10, 18), Size = new Size(LeftWidth - 20 - 81, 15), BackColor = Win98.Face, UseMnemonic = false, AutoEllipsis = true,
                Text = $"{hw.PhysicalCores} cores / {hw.LogicalProcessors} threads" + (hw.IsHybrid ? $" ({hw.PerformanceCores}P+{hw.EfficiencyCores}E)" : "") +
                       $", {hw.TotalMemory / (double)(1L << 30):0.0} GB RAM",
            };
            lblPcRuns = new Label { Location = new Point(10, 34), Size = new Size(LeftWidth - 20 - 81, 15), BackColor = Win98.Face, UseMnemonic = false, AutoEllipsis = true };
            lblPcLtspice = new Label { Location = new Point(10, 50), Size = new Size(LeftWidth - 20, 15), ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false, AutoEllipsis = true };
            var btnOptions = new Win98Button { Text = "Optio&ns...", Location = new Point(LeftWidth - 10 - 75, 20) };
            btnOptions.Click += (s, e) => ShowOptions();
            grpPc.Controls.AddRange(new Control[] { lblPcHw, lblPcRuns, lblPcLtspice, btnOptions });

            // docked from the top in reading order (the last one added docks first)
            foreach (var c in new Control[] { grpPc, Gap(DockStyle.Top), grpOut, Gap(DockStyle.Top), grpRnd, Gap(DockStyle.Top), grpTemp, Gap(DockStyle.Top), grpAn })
                left.Controls.Add(c);
            int withPc = grpAn.Height + grpTemp.Height + grpRnd.Height + grpOut.Height + grpPc.Height + 4 * 6;
            left.Resize += (s, e) => grpPc.Visible = left.Height >= withPc;      // a small window keeps the campaign groups

            // --- middle column: the signals to save -------------------------------------------
            var grpSave = new Win98GroupBox { Text = "Signals saved in the .raw file", Dock = DockStyle.Left, Width = 250 };
            rbSaveSel = new Win98RadioButton { Text = "Only the &selected (smaller, faster .raw)", Location = new Point(10, 18) };
            rbSaveAll = new Win98RadioButton { Text = "E&verything (largest .raw)", Location = new Point(10, 40) };
            rbSaveSel.CheckedChanged += (s, e) => { lstSignals.Enabled = txtFilter.Enabled = rbSaveSel.Checked; UpdateSignalCount(); };
            txtFilter = new Win98TextField { Location = new Point(48, 66), Width = 160 };
            txtFilter.Box.TextChanged += (s, e) => FillSignalList();
            var btnAll = new Win98Button { Text = "Select &All" };
            var btnNone = new Win98Button { Text = "Clea&r" };
            btnAll.Click += (s, e) => { foreach (var it in VisibleSignals()) checkedSignals.Add(it.Signal); FillSignalList(); MarkDirty(); };
            btnNone.Click += (s, e) => { foreach (var it in VisibleSignals()) if (!it.Locked) checkedSignals.Remove(it.Signal); FillSignalList(); MarkDirty(); };
            lstSignals = new CheckedListBox { BorderStyle = BorderStyle.None, CheckOnClick = true, IntegralHeight = false, Font = Win98.Font };
            lstSignals.ItemCheck += OnSignalCheck;
            var wellSignals = new Win98Well(lstSignals) { Location = new Point(10, 92) };
            lblSignalCount = new Label { AutoSize = false, Height = 28, ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false };
            grpSave.Controls.AddRange(new Control[] { rbSaveSel, rbSaveAll, Lbl("Filter:", 10, 66), txtFilter, wellSignals, btnAll, btnNone, lblSignalCount });
            grpSave.Resize += (s, e) =>
            {
                int w = grpSave.Width - 20, h = grpSave.Height;
                txtFilter.Width = grpSave.Width - 10 - txtFilter.Left;
                lblSignalCount.SetBounds(10, h - 8 - 28, w, 28);
                btnAll.Location = new Point(10, lblSignalCount.Top - 6 - 23);
                btnNone.Location = new Point(10 + 75 + 6, btnAll.Top);
                wellSignals.Size = new Size(w, Math.Max(20, btnAll.Top - 6 - wellSignals.Top));
            };

            // --- right column: what SPICA found in the schematic -------------------------------
            var grpCircuit = new Win98GroupBox { Text = "Circuit", Dock = DockStyle.Fill, Padding = new Padding(10, 20, 10, 10) };
            txtCircuit = new RichTextBox
            {
                ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Win98.Window, Font = Win98.Font, WordWrap = false,
                DetectUrls = false, TabStop = false, ScrollBars = RichTextBoxScrollBars.Both,
            };
            grpCircuit.Controls.Add(new Win98Well(txtCircuit) { Dock = DockStyle.Fill });

            var mid = new Panel { Dock = DockStyle.Fill, BackColor = Win98.Face };
            mid.Controls.Add(grpCircuit);
            mid.Controls.Add(Gap(DockStyle.Left));
            mid.Controls.Add(grpSave);
            mid.Controls.Add(left);
            mid.Resize += (s, e) => grpSave.Width = Math.Max(236, (mid.Width - left.Width - 6) * 45 / 100);

            // --- bottom: what will run, and the button that runs it ----------------------------
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Win98.Face };
            bar.Paint += (s, e) => Win98.EtchedHLine(e.Graphics, 0, bar.Width, 5);
            lblPlan = new Label { Location = new Point(2, 14), Height = 15, Font = Win98.BoldFont, BackColor = Win98.Face, UseMnemonic = false, AutoEllipsis = true };
            lblPlan2 = new Label { Location = new Point(2, 32), Height = 15, BackColor = Win98.Face, UseMnemonic = false, AutoEllipsis = true };
            btnAdd = new Win98Button { Text = "&Open Schematic...", Image = Win98.Icon("open16"), Size = new Size(150, 30) };
            btnAdd.Click += (s, e) => AddToQueue();
            tip.SetToolTip(btnAdd, "Put this campaign in the queue (F5). It starts at once when nothing is running;\nthe Setup tab stays free to prepare the next one.");
            bar.Controls.AddRange(new Control[] { lblPlan, lblPlan2, btnAdd });
            bar.Resize += (s, e) =>
            {
                btnAdd.Location = new Point(bar.Width - btnAdd.Width, 15);
                lblPlan.Width = lblPlan2.Width = Math.Max(50, btnAdd.Left - 12);
                bar.Invalidate();
            };

            p.Controls.Add(mid);
            p.Controls.Add(Gap(DockStyle.Top));
            p.Controls.Add(bar);
            p.Controls.Add(grpFiles);

            tip.SetToolTip(chkAutoReport, "When the campaign ends, save the HTML report in the campaign folder, with the contents chosen in Results > Report...");
            tip.SetToolTip(chkStop, "The campaign ends as soon as the result is known well enough, at most after 'Runs'.\n" +
                "Measurements with spec limits: stop when the yield is proven at least the required yield (or proven below it), 95 % confidence.\n" +
                "Measurements without limits: stop when their spread (sigma) is known within the given percentage.");
            tip.SetToolTip(txtSeed.Box, "0 = a new seed for every campaign (recorded with the results).\nAny other number repeats the same random units.");
            tip.SetToolTip(txtSpread.Box, "Measurements without spec limits: stop when sigma is known within ± this % (95 % confidence).\n10 % takes ~200 runs, 5 % ~770, 3 % ~2100 (for bell-shaped results).");
            tip.SetToolTip(txtYield.Box, "Measurements with spec limits: the yield to prove (or disprove).\nWith no failures: 99 % takes ~300 runs, 99.73 % ~1100, 99.9 % ~3000.");
            tip.SetToolTip(txtMinRuns.Box, "Never stop before this many runs (per temperature).");
            tip.SetToolTip(txtWatch.Box, "Measurements that decide the stop, separated by commas. Empty = all of them.");
            tip.SetToolTip(cbWc, "Every tolerance is set to an edge of its range instead of a random value.\n" +
                "Sensitivity: nominal + one run per tolerance finds which way each one pushes every measurement,\nthen 2 runs per measurement (and temperature) with all of them pushed the same way.\n" +
                "All corners: every combination of edges, 2^n runs; exact, for up to " + WorstCase.MaxCornerSites + " tolerances.\nAuto: all corners up to " + WorstCase.AutoCornerSites + " tolerances, sensitivity above.");
            tip.SetToolTip(txtWcSigma.Box, "gauss() tolerances have no edge: they are pushed to ± this many sigma (3 = 99.73 % of the parts).");
            tip.SetToolTip(chkPaired, "SPICA draws the random values itself, so run 17 is the same unit at every temperature\n(per-unit drift and 'passes at all temperatures' need this) and a seed repeats the campaign exactly.");
            tip.SetToolTip(chkReseed, "Without it every LTspice instance repeats the same random sequence.\nNot used when SPICA draws the values (paired samples).");

            foreach (var c in new Control[] { rbMc, rbWc, chkStop, cbWc })
            {
                if (c is RadioButton r) r.CheckedChanged += (s, e) => UpdateAnalysisUi();
                else if (c is CheckBox k) k.CheckedChanged += (s, e) => UpdateAnalysisUi();
                else ((ComboBox)c).SelectedIndexChanged += (s, e) => UpdateAnalysisUi();
            }
            ShowCircuit(null);
        }

        WcMethod SelectedWc() => !rbWc.Checked ? WcMethod.Off : cbWc.SelectedIndex == 1 ? WcMethod.Sensitivity : cbWc.SelectedIndex == 2 ? WcMethod.Corners : WcMethod.Auto;

        /// <summary>Enables the fields of the chosen analysis and says what it does (how much it runs is on the bottom line).</summary>
        void UpdateAnalysisUi()
        {
            if (lblAnalysisHint == null) return;
            bool wc = rbWc.Checked;
            chkStop.Enabled = !wc;
            txtSpread.Enabled = txtYield.Enabled = txtMinRuns.Enabled = txtWatch.Enabled = !wc && chkStop.Checked;
            cbWc.Enabled = txtWcSigma.Enabled = wc;
            txtRuns.Enabled = !wc;
            if (wc) chkPaired.Checked = true;
            chkPaired.Enabled = !wc;
            var inv = CultureInfo.InvariantCulture;
            string hint;
            if (wc)
            {
                var m = WorstCase.Resolve(SelectedWc(), siteCount);
                if (netlist == null) hint = "Every tolerance at an edge of its range instead of a random value. Open a schematic to see how many runs it needs.";
                else if (siteCount == 0) hint = "No mc()/gauss()/flat() SPICA can drive in this schematic: there is nothing to push to its edges.";
                else if (m == WcMethod.Corners && siteCount > WorstCase.MaxCornerSites)
                    hint = $"{siteCount} tolerances: all corners would need 2^{siteCount} runs. Choose Sensitivity or Auto.";
                else if (m == WcMethod.Corners)
                    hint = $"{siteCount} tolerances, all corners: the nominal circuit and every combination of tolerance edges. Runs is not used.";
                else
                    hint = $"{siteCount} tolerances, sensitivity: each one alone at its edge, then all of them pushed the worst way for every measurement. Runs is not used.";
            }
            else if (chkStop.Checked)
            {
                double.TryParse(txtYield.Text, NumberStyles.Float, inv, out var y);
                hint = "Stops when every watched measurement is settled: its yield proven (with spec limits) or its spread known within ± % (without)." +
                       (y > 0 && y < 100 ? $" {y.ToString("0.###", inv)} % with no failure takes ~{StopRule.ZeroFailRuns(y / 100):N0} runs." : "");
            }
            else hint = "Random units: every tolerance gets a new random value in each run. Statistics, yield, sensitivity, temperature drift.";
            lblAnalysisHint.Text = hint;
            UpdatePlan();
        }

        IEnumerable<SignalItem> VisibleSignals()
        {
            string f = txtFilter.Text.Trim();
            return allSignals.Where(s => f.Length == 0 || s.Signal.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        void FillSignalList()
        {
            fillingList = true;
            lstSignals.BeginUpdate();
            lstSignals.Items.Clear();
            foreach (var s in VisibleSignals())
                lstSignals.Items.Add(s, s.Locked || checkedSignals.Contains(s.Signal));
            lstSignals.EndUpdate();
            fillingList = false;
            UpdateSignalCount();
        }

        void OnSignalCheck(object sender, ItemCheckEventArgs e)
        {
            if (fillingList) return;
            var it = (SignalItem)lstSignals.Items[e.Index];
            if (it.Locked) { e.NewValue = CheckState.Checked; return; }
            if (e.NewValue == CheckState.Checked) checkedSignals.Add(it.Signal); else checkedSignals.Remove(it.Signal);
            BeginInvoke((Action)(() => { UpdateSignalCount(); MarkDirty(); }));
        }

        List<string> SelectedSignals()
        {
            var list = allSignals.Where(s => s.Locked).Select(s => s.Signal).ToList();
            list.AddRange(allSignals.Where(s => !s.Locked && checkedSignals.Contains(s.Signal)).Select(s => s.Signal));
            return list;
        }

        void UpdateSignalCount()
        {
            if (netlist == null) { lblSignalCount.Text = "Open a schematic to list its nets."; return; }
            if (!rbSaveSel.Checked) { lblSignalCount.Text = $"All {allSignals.Count} signals (and internal ones) will be saved."; return; }
            int locked = allSignals.Count(s => s.Locked);
            int sel = SelectedSignals().Count;
            lblSignalCount.Text = $"{sel} of {allSignals.Count} signals saved" + (locked > 0 ? $"\n({locked} required by .meas, always kept)" : "");
        }

        /// <summary>The Circuit box: what SPICA read in the schematic, with its headings in bold and its warnings in red.</summary>
        void ShowCircuit(Netlist nl)
        {
            txtCircuit.Clear();
            if (nl == null)
            {
                txtCircuit.ForeColor = Win98.Shadow;
                txtCircuit.Text = "No schematic is open.\n\nOpen an LTspice schematic (.asc) or a SPICA\nsetup (.spica): the Open button, File > Open,\nor drop the file on this window.\n\n" +
                                  "SPICA lists here its analysis, its .meas\nstatements and the random values\n(mc, gauss, flat) it found.";
                return;
            }
            txtCircuit.ForeColor = Color.Black;
            txtCircuit.Text = DescribeCircuit(nl);
            int pos = 0;
            foreach (var line in txtCircuit.Lines)
            {
                bool head = line.Length > 0 && line[0] != ' ' && line.EndsWith(":");
                bool warn = line.StartsWith("  ! ") || line.Contains("(none - add .meas") || line.Contains("every run will be identical");
                if (head || warn)
                {
                    txtCircuit.Select(pos, line.Length);
                    if (head) txtCircuit.SelectionFont = Win98.BoldFont;
                    else txtCircuit.SelectionColor = Color.FromArgb(160, 0, 0);
                }
                pos += line.Length + 1;
            }
            txtCircuit.Select(0, 0);
        }

        // =====================================================================================
        //  Setup <-> project
        // =====================================================================================

        void OnTempModeChanged(Win98RadioButton rb)
        {
            if (!rb.Checked) return;
            txtCorners.Enabled = rbTempCorners.Checked;
            txtTempMin.Enabled = txtTempMax.Enabled = rbTempRandom.Checked;
            // paired samples are what makes temperature corners comparable unit by unit
            if (!loadingUi && !rbTempDefault.Checked && project.Temp == TempMode.Default) chkPaired.Checked = true;
            MarkDirty();
        }

        /// <summary>Copies the Setup tab into the project.</summary>
        void ReadUiIntoProject()
        {
            if (ascPath != null) project.Schematic = ascPath;
            if (int.TryParse(txtRuns.Text, out var runs)) project.Runs = runs;
            project.Seed = uint.TryParse(txtSeed.Text.Trim(), out var seed) ? seed : 0;
            project.ClockReseed = chkReseed.Checked;
            project.KeepRaw = chkKeepRaw.Checked;
            project.Report.Auto = chkAutoReport.Checked;
            project.SaveSelected = rbSaveSel.Checked;
            if (netlist != null) project.Signals = allSignals.Where(s => !s.Locked && checkedSignals.Contains(s.Signal)).Select(s => s.Signal).ToList();
            project.Temp = rbTempCorners.Checked ? TempMode.Corners : rbTempRandom.Checked ? TempMode.Random : TempMode.Default;
            var corners = SpicaProject.ParseList(txtCorners.Text);
            if (corners.Count > 0) project.Corners = corners;
            if (double.TryParse(txtTempMin.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var tmin)) project.TempMin = tmin;
            if (double.TryParse(txtTempMax.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var tmax)) project.TempMax = tmax;
            project.Paired = chkPaired.Checked;
            project.WorstCase = SelectedWc();
            if (double.TryParse(txtWcSigma.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var k) && k > 0 && k <= 10) project.WcSigma = k;
            project.Stop.Enabled = chkStop.Checked;
            if (double.TryParse(txtSpread.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sp) && sp > 0 && sp <= 100) project.Stop.SpreadPct = sp;
            if (double.TryParse(txtYield.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && y > 0 && y < 100) project.Stop.YieldPct = y;
            if (int.TryParse(txtMinRuns.Text, out var mr) && mr > 0) project.Stop.MinRuns = mr;
            project.Stop.Watch = SpicaProject.ParseNames(txtWatch.Text);
        }

        /// <summary>Shows the project on the Setup tab (signal selection is applied when the schematic loads).</summary>
        void WriteProjectToUi()
        {
            loadingUi = true;
            try
            {
                var inv = CultureInfo.InvariantCulture;
                txtRuns.Text = project.Runs.ToString(inv);
                txtSeed.Text = project.Seed.ToString(inv);
                chkReseed.Checked = project.ClockReseed;
                chkKeepRaw.Checked = project.KeepRaw;
                chkAutoReport.Checked = project.Report.Auto;
                rbSaveSel.Checked = project.SaveSelected;
                rbSaveAll.Checked = !project.SaveSelected;
                txtCorners.Text = string.Join(", ", project.Corners.Select(t => t.ToString("0.###", inv)));
                txtTempMin.Text = project.TempMin.ToString("0.###", inv);
                txtTempMax.Text = project.TempMax.ToString("0.###", inv);
                rbTempDefault.Checked = project.Temp == TempMode.Default;
                rbTempCorners.Checked = project.Temp == TempMode.Corners;
                rbTempRandom.Checked = project.Temp == TempMode.Random;
                txtCorners.Enabled = rbTempCorners.Checked;
                txtTempMin.Enabled = txtTempMax.Enabled = rbTempRandom.Checked;
                chkPaired.Checked = project.Paired;
                rbWc.Checked = project.WorstCase != WcMethod.Off;
                rbMc.Checked = !rbWc.Checked;
                cbWc.SelectedIndex = project.WorstCase == WcMethod.Sensitivity ? 1 : project.WorstCase == WcMethod.Corners ? 2 : 0;
                txtWcSigma.Text = project.WcSigma.ToString("0.###", inv);
                chkStop.Checked = project.Stop.Enabled;
                txtSpread.Text = project.Stop.SpreadPct.ToString("0.###", inv);
                txtYield.Text = project.Stop.YieldPct.ToString("0.###", inv);
                txtMinRuns.Text = project.Stop.MinRuns.ToString(inv);
                txtWatch.Text = string.Join(", ", project.Stop.Watch);
                UpdateAnalysisUi();
            }
            finally { loadingUi = false; }
            UpdateTitle();
        }

        // =====================================================================================
        //  The plan: what the button will run, how long it takes, when it starts
        // =====================================================================================

        static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "?";
            if (seconds < 90) return $"{Math.Max(1, Math.Round(seconds)):0} s";
            if (seconds < 90 * 60) return $"{Math.Round(seconds / 60):0} min";
            int h = (int)(seconds / 3600), m = (int)Math.Round((seconds - h * 3600.0) / 60);
            if (m == 60) { h++; m = 0; }
            return $"{h} h {m:00} min";
        }

        /// <summary>What the Setup tab asks for, in one line, and how many simulations that is (0 = cannot run as it is).</summary>
        string PlanText(out int jobs, out bool atMost)
        {
            var inv = CultureInfo.InvariantCulture;
            jobs = 0;
            atMost = false;
            int temps = 1;
            bool random = rbTempRandom.Checked;
            if (rbTempCorners.Checked) temps = Math.Max(1, SpicaProject.ParseList(txtCorners.Text).Distinct().Count());

            if (rbWc.Checked)
            {
                int n = siteCount, meas = netlist.MeasNames.Count;
                if (random) temps = 2;          // a random temperature has no edge: its two limits become corners
                string at = temps > 1 ? (random ? " at the 2 limits of the temperature range" : $" at each of {temps} temperatures") : "";
                var m = WorstCase.Resolve(SelectedWc(), n);
                if (n == 0) return "Worst case: this schematic has no tolerance SPICA can drive.";
                if (meas == 0) return "Worst case: this schematic has no .meas statement to find the extremes of.";
                if (m == WcMethod.Corners && n > WorstCase.MaxCornerSites) return $"Worst case, all corners: 2^{n} runs are too many. Choose Sensitivity or Auto.";
                if (m == WcMethod.Corners)
                {
                    int runs = (1 << n) + 1;
                    jobs = runs * temps;
                    return $"Worst case, all corners: {runs:N0} runs{at}" + (temps > 1 ? $" = {jobs:N0} simulations" : "");
                }
                jobs = (n + 1 + 2 * meas) * temps;
                atMost = true;
                return $"Worst case by sensitivity: {n + 1} screening runs, then up to {2 * meas} verification runs{at}" + (temps > 1 ? $" = up to {jobs:N0} simulations" : "");
            }

            if (!int.TryParse(txtRuns.Text, out int r) || r < 1 || r > 10_000_000) return "Enter a number of runs between 1 and 10,000,000.";
            atMost = chkStop.Checked;
            string upTo = atMost ? "up to " : "";
            string early = atMost ? ", stopping early when the answer is known" : "";
            jobs = r * temps;
            if (temps > 1) return $"Monte Carlo: {upTo}{r:N0} runs x {temps} temperatures = {upTo}{jobs:N0} simulations{early}";
            if (random)
            {
                double.TryParse(txtTempMin.Text, NumberStyles.Float, inv, out var lo);
                double.TryParse(txtTempMax.Text, NumberStyles.Float, inv, out var hi);
                return $"Monte Carlo: {upTo}{r:N0} runs, each at a random temperature from {lo.ToString("0.#", inv)} to {hi.ToString("0.#", inv)} °C{early}";
            }
            return $"Monte Carlo: {upTo}{r:N0} runs{early}";
        }

        /// <summary>The two lines beside the button: what will run; how long it takes and when it starts.</summary>
        void UpdatePlan()
        {
            if (lblPlan == null || loadingUi) return;
            if (netlist == null)
            {
                lblPlan.Text = "Open a schematic to begin.";
                lblPlan2.Text = "The Open button, File > Open, or drop a .asc or .spica file on this window.";
                return;
            }
            string plan = PlanText(out int jobs, out bool atMost);
            lblPlan.Text = plan;
            if (addedNote.Length > 0 && DateTime.UtcNow < addedUntil) { lblPlan2.ForeColor = Win98.Navy; lblPlan2.Text = addedNote; return; }
            addedNote = "";
            lblPlan2.ForeColor = Color.Black;
            if (jobs <= 0) { lblPlan2.Text = ""; return; }

            ProfileStore.Profile prof = null;
            try { prof = profiles.Find(hw.MachineKey, circuitHash ?? ""); } catch { }
            double est = CampaignBuilder.EstimateSeconds(prof, jobs, ManualConfig()?.Instances ?? 0);
            string time = double.IsNaN(est) ? "Run time unknown: this circuit has not run on this PC yet (the first runs measure it)."
                                            : (atMost ? "At most " : "About ") + Duration(est) + " on this PC.";
            int ahead = (Running ? 1 : 0) + queue.QueuedCount;
            string when;
            if (queue.Hold) when = "The queue is on hold: it will wait there.";
            else if (ahead == 0) when = "Starts at once.";
            else
            {
                double wait = queue.WaitSecondsForNew();
                when = $"{ahead} campaign{(ahead == 1 ? "" : "s")} before it: " + (double.IsNaN(wait) ? "starts when " + (ahead == 1 ? "it finishes." : "they finish.") : $"starts in about {Duration(wait)}.");
            }
            lblPlan2.Text = time + "  " + when;
        }

        // =====================================================================================
        //  Add to the queue
        // =====================================================================================

        /// <summary>
        /// Checks the Setup tab and freezes it into a campaign: later edits of the setup do not reach it.
        /// False = nothing to run (the user was told why, or answered No to a question).
        /// </summary>
        /// <param name="resumeDir">an unfinished campaign folder to complete (its setup is already loaded)</param>
        /// <param name="resume">the runs already finished in that folder</param>
        bool PrepareRequest(out CampaignRequest request, out CampaignOptions options, string resumeDir = null, List<RunResult> resume = null, double priorSeconds = 0)
        {
            request = null;
            options = null;
            if (netlist == null) { BrowseSchematic(); return false; }

            string lt = settings.LtspicePath ?? "";
            if (!File.Exists(lt))
            {
                Win98MessageBox.Show(this, "LTspice.exe was not found. Set its location in Tools > Options.", "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
                return false;
            }
            if (!int.TryParse(txtRuns.Text, out int runs) || runs < 1 || runs > 10_000_000)
            {
                Win98MessageBox.Show(this, "Enter a number of runs between 1 and 10,000,000.", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                return false;
            }
            if (resumeDir == null && File.Exists(ascPath) && File.GetLastWriteTimeUtc(ascPath) != ascStamp &&
                Win98MessageBox.Show(this, "The schematic changed since it was loaded. Reload it before starting?", "SPICA",
                    MessageBoxButtons.YesNo, Win98Icon.Question) == DialogResult.Yes)
            {
                _ = LoadSchematic(ascPath, true);
                return false;
            }

            ReadUiIntoProject();
            var inv = CultureInfo.InvariantCulture;
            if (project.Temp == TempMode.Corners)
            {
                var corners = SpicaProject.ParseList(txtCorners.Text).Distinct().ToList();
                if (corners.Count == 0 || corners.Any(t => t < -273 || t > 1000))
                {
                    Win98MessageBox.Show(this, "Enter the temperature corners in °C, separated by commas (e.g. -40, 25, 90).", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                    return false;
                }
            }
            if (project.Temp == TempMode.Random &&
                (!double.TryParse(txtTempMin.Text, NumberStyles.Float, inv, out var tlo) || !double.TryParse(txtTempMax.Text, NumberStyles.Float, inv, out var thi) || !(thi > tlo)))
            {
                Win98MessageBox.Show(this, "Enter a random temperature range with min < max (°C).", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                return false;
            }

            request = new CampaignRequest
            {
                LtspiceExe = lt,
                AscPath = ascPath,
                OutputRoot = resumeDir != null ? Path.GetDirectoryName(resumeDir) : txtOut.Text.Trim(),
                Netlist = netlist,
                Project = project.Clone(),
                Manual = ManualConfig(),
                ResumeDir = resumeDir,
                Resume = resume,
                PriorSeconds = priorSeconds,
            };
            options = CampaignBuilder.Build(request,
                (text, warn) => Win98MessageBox.Show(this, text, "SPICA", MessageBoxButtons.YesNo, warn ? Win98Icon.Warning : Win98Icon.Question) == DialogResult.Yes,
                out string error);
            if (options == null && error != null) Win98MessageBox.Show(this, error, "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
            return options != null;
        }

        void AddToQueue() => AddToQueue(null, null, 0);

        /// <summary>Puts the campaign of the Setup tab in the queue; it starts at once when nothing is running.</summary>
        void AddToQueue(string resumeDir, List<RunResult> resume, double priorSeconds)
        {
            if (resumeDir != null && queue.Jobs.Any(j => j.Pending && SameFile(j.OutputDir ?? j.Request.ResumeDir, resumeDir)))
            {
                Win98MessageBox.Show(this, "This campaign is already in the queue.", "SPICA", MessageBoxButtons.OK, Win98Icon.Information);
                return;
            }
            if (!PrepareRequest(out var request, out var options, resumeDir, resume, priorSeconds)) return;
            SaveSettings();

            QueueJob job;
            addingLocal = true;
            try { job = queue.Add(request, options); }
            finally { addingLocal = false; }

            if (job.Status == JobStatus.Running) tabs.SelectedIndex = TabQueue;
            else if (job.Status == JobStatus.Queued)
            {
                int ahead = queue.Ahead(job);
                double wait = queue.WaitSeconds(job);
                addedNote = $"Added to the queue: {job.Name} is number {ahead + 1} in line" +
                            (queue.Hold ? " (the queue is on hold)." : double.IsNaN(wait) ? ", it starts when the ones before it finish." : $", it starts in about {Duration(wait)}.");
                addedUntil = DateTime.UtcNow.AddSeconds(8);
                SetStatus(addedNote);
                UpdatePlan();
            }
            UpdateCommandState();
        }

        /// <summary>
        /// Writes the campaign of the Setup tab as a bundle (.zip: schematic, setup, models) into a folder another SPICA
        /// is listening to, e.g. a share of the workstation: it runs there and its results appear in that listener's results folder.
        /// </summary>
        void SendToListener()
        {
            if (!PrepareRequest(out var request, out var options)) return;
            string folder;
            using (var d = new FolderBrowserDialog
            {
                Description = "Folder a SPICA listener is watching (e.g. a share of the workstation):",
                SelectedPath = Directory.Exists(settings.SendFolder ?? "") ? settings.SendFolder : "",
                ShowNewFolderButton = false,
            })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                folder = d.SelectedPath;
            }
            settings.SendFolder = folder;
            SaveSettings();

            options.Seed = request.Project.Seed;        // 0 stays "a new seed", chosen where it runs
            string zip = Path.Combine(folder, Path.GetFileNameWithoutExtension(ascPath) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".zip");
            var warnings = new List<string>();
            UseWaitCursor = true;
            try
            {
                string summary = SourceBundle.Write(zip, options, hw, DateTime.Now, warnings);
                UseWaitCursor = false;
                SetStatus("Sent: " + zip);
                bool listening = File.Exists(Path.Combine(folder, Listener.StatusFile));
                Win98MessageBox.Show(this,
                    "The campaign was written to the folder:\n" + summary +
                    (warnings.Count > 0 ? "\n\nCheck:\n  " + string.Join("\n  ", warnings.Take(8)) + (warnings.Count > 8 ? $"\n  ... and {warnings.Count - 8} more" : "") : "") +
                    (listening ? "\n\nThe listener there takes it at its next look at the folder; " + Listener.StatusFile + " in that folder shows how it goes."
                               : "\n\nNo listener has written its status in this folder yet: the campaign runs once a SPICA listens to it."),
                    "Send to a Listener Folder", MessageBoxButtons.OK, warnings.Count > 0 ? Win98Icon.Warning : Win98Icon.Information);
            }
            catch (Exception ex)
            {
                UseWaitCursor = false;
                Win98MessageBox.Show(this, "The campaign could not be written to that folder:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
            }
        }
    }
}
