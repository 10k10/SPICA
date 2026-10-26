using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Spica.Core;

namespace Spica.UI
{
    public class MainForm : Win98Form
    {
        public string StartupFile;

        readonly AppSettings settings = new AppSettings();
        readonly ProfileStore profiles = new ProfileStore();
        readonly HardwareInfo hw = HardwareInfo.Detect();

        Netlist netlist;
        string ascPath;
        DateTime ascStamp;
        CampaignEngine engine;
        bool analyzeOnFinish, discardOnFinish;     // what the Stop dialog asked for
        int graphTick;
        SpicaProject project = new SpicaProject();
        bool loadingUi;

        // chrome
        MenuStrip menu;
        ToolStrip toolbar;
        Win98StatusBar status;
        Win98TabControl tabs;
        Win98TabPage pageSetup, pageMonitor, pageResults;
        ToolStripButton tbOpen, tbStart, tbStop, tbFolder;
        ToolStripMenuItem miStart, miStop, miRecent, miExport, miFolder;

        // setup page
        Win98TextField txtAsc, txtLtspice, txtRuns, txtOut, txtInstances, txtThreads, txtFilter, txtSeed;
        Win98TextField txtCorners, txtTempMin, txtTempMax;
        Win98CheckBox chkReseed, chkKeepRaw, chkPaired;
        Win98RadioButton rbAuto, rbManual, rbSaveSel, rbSaveAll, rbTempDefault, rbTempCorners, rbTempRandom;
        CheckedListBox lstSignals;
        Label lblSignalCount, lblHwHint;
        TextBox txtCircuit;
        Win98Button btnStart;

        // monitor page
        Win98ProgressBar progress;
        readonly Dictionary<string, Label> stat = new Dictionary<string, Label>();
        Win98Graph graph;
        ListView lvWorkers;
        TextBox txtLog;

        // results page
        ResultsView resultsView;

        readonly Timer uiTimer = new Timer { Interval = 250 };

        sealed class SignalItem
        {
            public string Signal;
            public bool Locked;
            public override string ToString() => Locked ? $"{Signal}    (used by .meas)" : Signal;
        }
        readonly List<SignalItem> allSignals = new List<SignalItem>();
        readonly HashSet<string> checkedSignals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool fillingList;

        public MainForm()
        {
            Text = "SPICA - Spice Parallel Instancer Circuit Analysis";
            ClientSize = new Size(1080, 780);
            MinimumSize = new Size(900, 660);
            StartPosition = FormStartPosition.CenterScreen;
            CaptionIcon = Win98.Icon("app16");
            KeyPreview = true;

            BuildChrome();
            BuildSetupPage();
            BuildMonitorPage();
            BuildResultsPage();

            Controls.Add(tabs);
            Controls.Add(status);
            Controls.Add(toolbar);
            Controls.Add(menu);
            MainMenuStrip = menu;

            uiTimer.Tick += (s, e) => UpdateUi();
            uiTimer.Start();
            LoadSettings();
            UpdateCommandState();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (string.IsNullOrEmpty(txtLtspice.Text))
                Win98MessageBox.Show(this, "LTspice was not found in the usual locations.\nUse the \"...\" button next to the LTspice field to point SPICA at LTspice.exe.",
                    "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
            if (!string.IsNullOrEmpty(StartupFile) && File.Exists(StartupFile)) OpenFile(StartupFile);
        }

        /// <summary>Opens a schematic (.asc) or a setup (.spica).</summary>
        void OpenFile(string path)
        {
            if (path.EndsWith(".spica", StringComparison.OrdinalIgnoreCase)) OpenSetup(path);
            else _ = LoadSchematic(path);
        }

        // =====================================================================================
        //  Menus / toolbar / status bar
        // =====================================================================================

        void BuildChrome()
        {
            menu = new MenuStrip { Dock = DockStyle.Top, Padding = new Padding(2, 1, 0, 1) };
            Win98Renderer.Apply(menu);

            var mFile = new ToolStripMenuItem("&File");
            var miOpen = new ToolStripMenuItem("&Open Schematic or Setup...", Win98.Icon("open16"), (s, e) => BrowseSchematic()) { ShortcutKeys = Keys.Control | Keys.O };
            var miSave = new ToolStripMenuItem("&Save Setup", Win98.Icon("save16"), (s, e) => SaveSetup(false)) { ShortcutKeys = Keys.Control | Keys.S };
            var miSaveAs = new ToolStripMenuItem("Save Setup &As...", null, (s, e) => SaveSetup(true));
            var miResults = new ToolStripMenuItem("Open &Results...", Win98.Icon("results16"), (s, e) => OpenResults()) { ShortcutKeys = Keys.Control | Keys.R };
            miRecent = new ToolStripMenuItem("Recent &Files");
            var miExit = new ToolStripMenuItem("E&xit", null, (s, e) => Close());
            mFile.DropDownItems.AddRange(new ToolStripItem[] { miOpen, miSave, miSaveAs, new ToolStripSeparator(), miResults, new ToolStripSeparator(), miRecent, new ToolStripSeparator(), miExit });
            mFile.DropDownOpening += (s, e) => FillRecent();
            miRecent.DropDownItems.Add("(empty)");

            var mCampaign = new ToolStripMenuItem("&Campaign");
            miStart = new ToolStripMenuItem("&Start", Win98.Icon("run16"), (s, e) => StartCampaign()) { ShortcutKeys = Keys.F5 };
            miStop = new ToolStripMenuItem("S&top", Win98.Icon("stop16"), (s, e) => StopCampaign()) { ShortcutKeys = Keys.Shift | Keys.F5 };
            miFolder = new ToolStripMenuItem("Open Output &Folder", Win98.Icon("folder16"), (s, e) => OpenOutputFolder());
            miExport = new ToolStripMenuItem("&Export Results...", Win98.Icon("save16"), (s, e) => ExportResults());
            mCampaign.DropDownItems.AddRange(new ToolStripItem[] { miStart, miStop, new ToolStripSeparator(), miFolder, miExport });

            var mTools = new ToolStripMenuItem("&Tools");
            mTools.DropDownItems.Add(new ToolStripMenuItem("&Hardware Information...", Win98.Icon("hw16"), (s, e) => ShowHardware()));
            mTools.DropDownItems.Add(new ToolStripMenuItem("&Forget Scheduler Profiles", null, (s, e) => ForgetProfiles()));

            var mHelp = new ToolStripMenuItem("&Help");
            mHelp.DropDownItems.Add(new ToolStripMenuItem("&About SPICA...", Win98.Icon("help16"), (s, e) => ShowAbout()));

            menu.Items.AddRange(new ToolStripItem[] { mFile, mCampaign, mTools, mHelp });
            Win98Renderer.StyleDropDowns(menu.Items);

            toolbar = new ToolStrip
            {
                Dock = DockStyle.Top,
                ImageScalingSize = new Size(32, 32),
                Padding = new Padding(4, 2, 4, 2),
                AutoSize = true,
            };
            Win98Renderer.Apply(toolbar);
            ToolStripButton Btn(string text, string icon, EventHandler click, string tip)
            {
                var b = new ToolStripButton(text, Win98.Icon(icon), click)
                {
                    DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                    TextImageRelation = TextImageRelation.ImageAboveText,
                    ImageScaling = ToolStripItemImageScaling.None,
                    AutoSize = false,
                    Size = new Size(64, 54),
                    ToolTipText = tip,
                    Margin = new Padding(1),
                };
                toolbar.Items.Add(b);
                return b;
            }
            tbOpen = Btn("Open", "open32", (s, e) => BrowseSchematic(), "Open an LTspice schematic (.asc) or a SPICA setup (.spica)");
            Btn("Save", "save32", (s, e) => SaveSetup(false), "Save the setup (.spica): runs, temperatures, signals, spec limits, TF");
            Btn("Results", "results32", (s, e) => OpenResults(), "Open the results of an earlier campaign");
            toolbar.Items.Add(new ToolStripSeparator());
            tbStart = Btn("Start", "run32", (s, e) => StartCampaign(), "Start the Monte Carlo campaign (F5)");
            tbStop = Btn("Stop", "stop32", (s, e) => StopCampaign(), "Stop the campaign; finished runs are kept");
            toolbar.Items.Add(new ToolStripSeparator());
            tbFolder = Btn("Output", "folder32", (s, e) => OpenOutputFolder(), "Open the campaign output folder");
            toolbar.Items.Add(new ToolStripSeparator());
            Btn("Hardware", "hw32", (s, e) => ShowHardware(), "What SPICA detected about this machine");
            Btn("Help", "help32", (s, e) => ShowAbout(), "About SPICA");

            status = new Win98StatusBar();
            status.Add("Ready", 0);
            status.Add("LTspice ?", 150);
            status.Add($"{hw.PhysicalCores}C/{hw.LogicalProcessors}T  {hw.TotalMemory / (double)(1L << 30):0.0} GB", 170, Win98.Icon("hw16"));
            status.Add(DateTime.Now.ToString("HH:mm"), 64);

            tabs = new Win98TabControl { Dock = DockStyle.Fill };
            pageSetup = new Win98TabPage("Setup", Win98.Icon("options16"));
            pageMonitor = new Win98TabPage("Monitor", Win98.Icon("hw16"));
            pageResults = new Win98TabPage("Results", Win98.Icon("results16"));
            tabs.AddPage(pageSetup);
            tabs.AddPage(pageMonitor);
            tabs.AddPage(pageResults);
            tabs.SelectedIndexChanged += (s, e) => { if (tabs.SelectedIndex == 2 && engine != null && resultsView.Data == null) RefreshResults(); };
        }

        void FillRecent()
        {
            miRecent.DropDownItems.Clear();
            var rec = settings.RecentFiles;
            if (rec.Count == 0) { miRecent.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false }); return; }
            int i = 1;
            foreach (var r in rec)
            {
                string path = r;
                miRecent.DropDownItems.Add(new ToolStripMenuItem($"&{i++} {Shorten(path, 60)}", null, (s, e) => _ = LoadSchematic(path)));
            }
            Win98Renderer.StyleDropDowns(menu.Items);
        }

        static string Shorten(string p, int max) => p.Length <= max ? p : "..." + p.Substring(p.Length - max + 3);

        // =====================================================================================
        //  Setup page
        // =====================================================================================

        static Label Lbl(string text, int x, int y) => new Label { Text = text, Location = new Point(x, y + 3), AutoSize = true, BackColor = Win98.Face };

        void BuildSetupPage()
        {
            var p = pageSetup;

            // --- schematic & LTspice -------------------------------------------------------
            var grpFiles = new Win98GroupBox { Text = "Schematic", Dock = DockStyle.Top, Height = 84 };
            txtAsc = new Win98TextField { Location = new Point(118, 20), Width = 400, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right, ReadOnly = true };
            var btnAsc = new Win98Button { Text = "&Browse...", Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnAsc.Click += (s, e) => BrowseSchematic();
            txtLtspice = new Win98TextField { Location = new Point(118, 50), Width = 400, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
            var btnLt = new Win98Button { Text = "...", Width = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnLt.Click += (s, e) => BrowseLtspice();
            grpFiles.Controls.AddRange(new Control[] { Lbl("Schematic (.asc):", 10, 20), txtAsc, btnAsc, Lbl("LTspice.exe:", 10, 50), txtLtspice, btnLt });
            grpFiles.Resize += (s, e) =>
            {
                btnAsc.Location = new Point(grpFiles.Width - 10 - btnAsc.Width, 18);
                txtAsc.Width = btnAsc.Left - 6 - txtAsc.Left;
                btnLt.Location = new Point(grpFiles.Width - 10 - btnLt.Width, 48);
                btnLt.Height = 23;
                txtLtspice.Width = btnLt.Left - 6 - txtLtspice.Left;
            };

            // --- left column ---------------------------------------------------------------
            var left = new Panel { Dock = DockStyle.Left, Width = 400, BackColor = Win98.Face, Padding = new Padding(0, 6, 6, 0) };

            var grpMc = new Win98GroupBox { Text = "Monte Carlo", Dock = DockStyle.Top, Height = 154 };
            txtRuns = new Win98TextField { Location = new Point(118, 20), Width = 80 };
            txtRuns.Box.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            txtSeed = new Win98TextField { Location = new Point(250, 20), Width = 90 };
            txtSeed.Box.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            txtOut = new Win98TextField { Location = new Point(118, 48), Width = 200, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
            var btnOut = new Win98Button { Text = "...", Width = 30, Height = 23, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnOut.Click += (s, e) => BrowseOutput();
            chkReseed = new Win98CheckBox { Text = "Reseed LTspice's mc() from the clock (recommended)", Location = new Point(10, 78) };
            chkKeepRaw = new Win98CheckBox { Text = "Keep .raw waveform files", Location = new Point(10, 100) };
            var seedHint = new Label { AutoSize = true, Location = new Point(10, 126), ForeColor = Win98.Shadow, BackColor = Win98.Face, Text = "Seed 0 = new seed per campaign (recorded with the results)." };
            grpMc.Controls.AddRange(new Control[] { Lbl("Number of runs:", 10, 20), txtRuns, Lbl("Seed:", 212, 20), txtSeed, Lbl("Output folder:", 10, 48), txtOut, btnOut, chkReseed, chkKeepRaw, seedHint });
            grpMc.Resize += (s, e) => { btnOut.Location = new Point(grpMc.Width - 10 - btnOut.Width, 46); txtOut.Width = btnOut.Left - 6 - txtOut.Left; };

            var spacer1 = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Win98.Face };

            var grpTemp = new Win98GroupBox { Text = "Temperature", Dock = DockStyle.Top, Height = 124 };
            rbTempDefault = new Win98RadioButton { Text = "Schematic default (no .temp)", Location = new Point(10, 20) };
            rbTempCorners = new Win98RadioButton { Text = "Corners, °C:", Location = new Point(10, 44) };
            txtCorners = new Win98TextField { Location = new Point(118, 42), Width = 222 };
            rbTempRandom = new Win98RadioButton { Text = "Random, flat:", Location = new Point(10, 70) };
            txtTempMin = new Win98TextField { Location = new Point(118, 68), Width = 56 };
            txtTempMax = new Win98TextField { Location = new Point(208, 68), Width = 56 };
            chkPaired = new Win98CheckBox { Text = "Paired samples: same unit at every temperature (SPICA generator)", Location = new Point(10, 96) };
            foreach (var rb in new[] { rbTempDefault, rbTempCorners, rbTempRandom })
                rb.CheckedChanged += (s, e) => OnTempModeChanged((Win98RadioButton)s);
            grpTemp.Controls.AddRange(new Control[] { rbTempDefault, rbTempCorners, txtCorners, rbTempRandom, txtTempMin, Lbl("to", 182, 68), txtTempMax, Lbl("°C", 270, 68), chkPaired });

            var spacerT = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Win98.Face };

            var grpPar = new Win98GroupBox { Text = "Parallelism", Dock = DockStyle.Top, Height = 112 };
            rbAuto = new Win98RadioButton { Text = "Automatic: tune instances and threads while running", Location = new Point(10, 20) };
            rbManual = new Win98RadioButton { Text = "Manual:", Location = new Point(10, 44) };
            txtInstances = new Win98TextField { Location = new Point(144, 42), Width = 40 };
            txtThreads = new Win98TextField { Location = new Point(290, 42), Width = 40 };
            foreach (var t in new[] { txtInstances, txtThreads })
                t.Box.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            lblHwHint = new Label { AutoSize = true, Location = new Point(28, 74), ForeColor = Win98.Shadow, BackColor = Win98.Face };
            rbAuto.CheckedChanged += (s, e) => { txtInstances.Enabled = txtThreads.Enabled = rbManual.Checked; };
            grpPar.Controls.AddRange(new Control[] { rbAuto, rbManual, Lbl("instances", 88, 42), txtInstances, Lbl("threads each", 210, 42), txtThreads, lblHwHint });
            lblHwHint.Text = $"This PC: {hw.PhysicalCores} cores / {hw.LogicalProcessors} threads" + (hw.IsHybrid ? $" ({hw.PerformanceCores}P+{hw.EfficiencyCores}E)" : "") +
                             $", {hw.TotalMemory / (double)(1L << 30):0.0} GB RAM";

            var spacer2 = new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Win98.Face };

            var grpCircuit = new Win98GroupBox { Text = "Circuit", Dock = DockStyle.Bottom, Height = 190 };
            txtCircuit = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Win98.Window, ScrollBars = ScrollBars.Vertical, Font = Win98.Font, WordWrap = false };
            var wellCircuit = new Win98Well(txtCircuit) { Dock = DockStyle.Fill };
            grpCircuit.Padding = new Padding(10, 20, 10, 10);
            grpCircuit.Controls.Add(wellCircuit);

            left.Controls.Add(grpPar);
            left.Controls.Add(spacerT);
            left.Controls.Add(grpTemp);
            left.Controls.Add(spacer1);
            left.Controls.Add(grpMc);

            // --- right column: .save signal picker ------------------------------------------
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Win98.Face, Padding = new Padding(0, 6, 0, 0) };
            var grpSave = new Win98GroupBox { Text = "Signals written to the .raw file (.save)", Dock = DockStyle.Fill };
            rbSaveSel = new Win98RadioButton { Text = "Only the selected signals (smaller, faster .raw)", Location = new Point(10, 20) };
            rbSaveAll = new Win98RadioButton { Text = "Everything (no .save directive, largest .raw)", Location = new Point(10, 42) };
            rbSaveSel.CheckedChanged += (s, e) => { lstSignals.Enabled = txtFilter.Enabled = rbSaveSel.Checked; UpdateSignalCount(); };
            txtFilter = new Win98TextField { Location = new Point(52, 68), Width = 160 };
            txtFilter.Box.TextChanged += (s, e) => FillSignalList();
            var btnAll = new Win98Button { Text = "Select &All", Location = new Point(222, 67) };
            var btnNone = new Win98Button { Text = "C&lear", Location = new Point(302, 67) };
            btnAll.Click += (s, e) => { foreach (var it in VisibleSignals()) checkedSignals.Add(it.Signal); FillSignalList(); };
            btnNone.Click += (s, e) => { foreach (var it in VisibleSignals()) if (!it.Locked) checkedSignals.Remove(it.Signal); FillSignalList(); };

            lstSignals = new CheckedListBox { BorderStyle = BorderStyle.None, CheckOnClick = true, IntegralHeight = false, Font = Win98.Font };
            lstSignals.ItemCheck += OnSignalCheck;
            var wellSignals = new Win98Well(lstSignals) { Location = new Point(10, 98), Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom };
            lblSignalCount = new Label { AutoSize = false, Height = 16, Anchor = AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right, BackColor = Win98.Face };
            grpSave.Controls.AddRange(new Control[] { rbSaveSel, rbSaveAll, Lbl("Filter:", 10, 68), txtFilter, btnAll, btnNone, wellSignals, lblSignalCount });
            grpSave.Resize += (s, e) =>
            {
                wellSignals.Size = new Size(grpSave.Width - 20, grpSave.Height - 98 - 30);
                lblSignalCount.Location = new Point(10, grpSave.Height - 24);
                lblSignalCount.Width = grpSave.Width - 20;
            };
            right.Controls.Add(grpSave);
            right.Controls.Add(spacer2);
            right.Controls.Add(grpCircuit);
            spacer2.Dock = DockStyle.Bottom;

            // --- bottom: start button --------------------------------------------------------
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Win98.Face };
            btnStart = new Win98Button { Text = "&Start Campaign", Image = Win98.Icon("run16"), Size = new Size(140, 27), Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            btnStart.Click += (s, e) => StartCampaign();
            bottom.Controls.Add(btnStart);
            bottom.Resize += (s, e) => btnStart.Location = new Point(bottom.Width - btnStart.Width, bottom.Height - btnStart.Height);

            p.Controls.Add(right);
            p.Controls.Add(left);
            p.Controls.Add(bottom);
            p.Controls.Add(grpFiles);
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
            BeginInvoke((Action)UpdateSignalCount);
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
            lblSignalCount.Text = $"{sel} of {allSignals.Count} signals saved" + (locked > 0 ? $"  ({locked} required by .meas, always kept)" : "");
        }

        // =====================================================================================
        //  Monitor page
        // =====================================================================================

        void BuildMonitorPage()
        {
            var p = pageMonitor;

            var top = new Panel { Dock = DockStyle.Top, Height = 118, BackColor = Win98.Face };
            progress = new Win98ProgressBar { Location = new Point(0, 4), Height = 20, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
            top.Controls.Add(progress);

            string[,] fields =
            {
                { "Runs", "Elapsed", "Remaining", "Rate" },
                { "Config", "Phase", "Start-up", "Per run" },
                { "Failed", "Peak RAM", ".raw kept", ".raw forecast" },
            };
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 4; c++)
                {
                    var cap = new Label { Text = fields[r, c] + ":", AutoSize = false, Width = 74, Height = 18, TextAlign = ContentAlignment.MiddleRight, BackColor = Win98.Face };
                    var val = new SunkenLabel { Width = 150, Height = 20 };
                    top.Controls.Add(cap);
                    top.Controls.Add(val);
                    stat[fields[r, c]] = val;
                    cap.Tag = new Point(c, r);
                    val.Tag = new Point(c, r);
                }
            top.Resize += (s, e) =>
            {
                progress.Width = top.Width;
                int colW = top.Width / 4;
                foreach (Control ctl in top.Controls)
                {
                    if (!(ctl.Tag is Point pt)) continue;
                    int y = 32 + pt.Y * 27;
                    if (ctl is SunkenLabel) { ctl.Location = new Point(pt.X * colW + 80, y); ctl.Width = colW - 86; }
                    else ctl.Location = new Point(pt.X * colW, y + 1);
                }
            };

            graph = new Win98Graph { Dock = DockStyle.Fill, Caption = "Throughput (runs/min)" };
            var graphWell = new Win98Well(graph, Color.Black) { Dock = DockStyle.Top, Height = 92 };

            lvWorkers = MakeListView();
            foreach (var (t, w) in new[] { ("Instance", 70), ("PID", 64), ("Runs", 120), ("State", 80), ("Time", 70), ("Memory", 80), ("Threads", 60) })
                lvWorkers.Columns.Add(t, w);
            var lvWell = new Win98Well(lvWorkers) { Dock = DockStyle.Fill };

            txtLog = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Win98.Window, ScrollBars = ScrollBars.Vertical, Font = Win98.MonoFont, WordWrap = false };
            var logWell = new Win98Well(txtLog) { Dock = DockStyle.Bottom, Height = 170 };

            p.Controls.Add(lvWell);
            p.Controls.Add(Gap(DockStyle.Top));
            p.Controls.Add(graphWell);
            p.Controls.Add(top);
            p.Controls.Add(Gap(DockStyle.Bottom));
            p.Controls.Add(logWell);
            // Dock order: log (bottom), gap, top stats, graph, gap, list fills the rest.
            logWell.SendToBack();
        }

        static Panel Gap(DockStyle d) => new Panel { Dock = d, Height = 6, BackColor = Win98.Face };

        /// <summary>Label drawn as a sunken read-only field (status values).</summary>
        sealed class SunkenLabel : Label
        {
            public SunkenLabel()
            {
                AutoSize = false; BackColor = Win98.Face; TextAlign = ContentAlignment.MiddleLeft; Padding = new Padding(3, 0, 0, 0);
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(Win98.Face);
                Win98.ThinSunken(e.Graphics, ClientRectangle);
                TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(4, 0, Width - 6, Height), Color.Black,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        static ListView MakeListView(bool virtualMode = false)
        {
            var lv = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                BorderStyle = BorderStyle.None,
                HideSelection = false,
                Font = Win98.Font,
                OwnerDraw = true,
                VirtualMode = virtualMode,
                GridLines = false,
            };
            lv.DrawColumnHeader += (s, e) =>
            {
                var g = e.Graphics;
                using (var b = new SolidBrush(Win98.Face)) g.FillRectangle(b, e.Bounds);
                Win98.Ring(g, e.Bounds, Color.White, Color.Black);
                Win98.Ring(g, Rectangle.Inflate(e.Bounds, -1, -1), Win98.Face, Win98.Shadow);
                var r = Rectangle.Inflate(e.Bounds, -4, 0);
                TextRenderer.DrawText(g, e.Header.Text, Win98.Font, r, Color.Black,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            };
            lv.DrawItem += (s, e) => e.DrawDefault = true;
            lv.DrawSubItem += (s, e) => e.DrawDefault = true;
            return lv;
        }

        // =====================================================================================
        //  Results page
        // =====================================================================================

        void BuildResultsPage()
        {
            resultsView = new ResultsView();
            resultsView.OpenResultsRequested += OpenResults;
            resultsView.OpenFolderRequested += () =>
            {
                string dir = resultsView.Data?.Folder;
                if (Directory.Exists(dir ?? "")) Process.Start("explorer.exe", "\"" + dir + "\"");
            };
            resultsView.SetupChanged += () =>
            {
                // edits on the running/just-finished campaign belong to the current setup
                if (resultsView.Data != null && ReferenceEquals(resultsView.Data.Setup, project))
                {
                    project.Dirty = true;
                    resultsView.Data.SaveSetup();
                    UpdateTitle();
                }
            };
            pageResults.Controls.Add(resultsView);
        }

        void RefreshResults()
        {
            if (engine == null) return;
            var rs = ResultSet.FromResults(engine.GetResults(), engine.MeasNames, engine.Options, project);
            resultsView.SetData(rs, !engine.IsFinished);
        }

        void OpenResults()
        {
            string start = engine?.Options.OutputDir ?? (ascPath != null ? txtOut.Text : "");
            using (var d = new FolderBrowserDialog { Description = "Choose a campaign folder (the one containing results.csv):", SelectedPath = Directory.Exists(start) ? start : "" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string dir = d.SelectedPath;
                if (!File.Exists(Path.Combine(dir, "results.csv")))
                {
                    // allow picking the <name>_SPICA folder: take its newest campaign
                    var sub = Directory.Exists(dir) ? Directory.GetDirectories(dir).Where(x => File.Exists(Path.Combine(x, "results.csv"))).OrderByDescending(x => x).FirstOrDefault() : null;
                    if (sub == null)
                    {
                        Win98MessageBox.Show(this, "That folder has no results.csv.\nChoose a campaign folder (named like 20261024_085624).", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                        return;
                    }
                    dir = sub;
                }

                // an unfinished campaign (stopped, crashed or closed) can be completed
                var state = CampaignState.Load(dir);
                bool live = Running && string.Equals(Path.GetFullPath(engine.Options.OutputDir).TrimEnd('\\'), Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                if (state != null && state.Unfinished && !live)
                {
                    int c = Win98MessageBox.Choose(this,
                        $"This campaign is unfinished: {state.Done:N0} of {state.Jobs:N0} simulations are done.\n\n" +
                        "Resume:  simulate only the missing runs, with the same setup and seed.\n" +
                        "Analyze:  open the finished runs as they are.",
                        "Unfinished Campaign", Win98Icon.Question, "&Resume", "&Analyze", "Cancel");
                    if (c == 2 || c < 0) return;
                    if (c == 0) { ResumeCampaign(dir, state); return; }
                }
                try
                {
                    var rs = ResultSet.Load(dir);
                    resultsView.SetData(rs, false);
                    tabs.SelectedIndex = 2;
                    SetStatus($"Opened results: {dir}");
                }
                catch (Exception ex) { Win98MessageBox.Show(this, "Could not read the results:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); }
            }
        }

        async void ResumeCampaign(string dir, CampaignState state)
        {
            if (Running)
            {
                Win98MessageBox.Show(this, "Another campaign is running. Stop it first.", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                return;
            }
            string spica = Path.Combine(dir, "campaign.spica");
            SpicaProject p;
            try { p = SpicaProject.Load(spica); }
            catch (Exception ex)
            {
                Win98MessageBox.Show(this, "This campaign cannot be resumed: its campaign.spica is missing or unreadable.\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
                return;
            }
            if (!File.Exists(p.Schematic))
            {
                Win98MessageBox.Show(this, "The schematic of this campaign was not found:\n" + p.Schematic, "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
                return;
            }
            if (!ConfirmDiscardSetup()) return;

            p.FilePath = null;          // the campaign copy is not the user's setup file
            p.Dirty = false;
            await LoadSchematic(p.Schematic, false, p);
            if (netlist == null || !string.Equals(ascPath, p.Schematic, StringComparison.OrdinalIgnoreCase)) return;

            if (!string.IsNullOrEmpty(state.CircuitHash) && state.CircuitHash != netlist.CircuitHash() &&
                Win98MessageBox.Show(this, "The schematic changed since this campaign started, so the remaining runs would simulate a different circuit.\nResume anyway?",
                    "SPICA", MessageBoxButtons.YesNo, Win98Icon.Warning) != DialogResult.Yes) return;

            List<RunResult> done;
            try { done = CampaignState.LoadResults(dir, p.Temp == TempMode.Corners ? p.Corners.Distinct().ToList() : null); }
            catch (Exception ex)
            {
                Win98MessageBox.Show(this, "Could not read the finished runs:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
                return;
            }
            StartCampaign(dir, done, state.ElapsedSeconds);
        }

        // =====================================================================================
        //  Setup (.spica)
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

        void MarkDirty()
        {
            if (loadingUi) return;
            project.Dirty = true;
            UpdateTitle();
        }

        void UpdateTitle()
        {
            string name = ascPath != null ? Path.GetFileName(ascPath) : null;
            string setup = project.FilePath != null ? Path.GetFileName(project.FilePath) : null;
            Text = "SPICA" + (name != null ? " - " + name : "") + (setup != null ? $"  [{setup}{(project.Dirty ? "*" : "")}]" : project.Dirty && name != null ? "  [unsaved setup]" : "");
        }

        /// <summary>Copies the Setup tab into the project.</summary>
        void ReadUiIntoProject()
        {
            if (ascPath != null) project.Schematic = ascPath;
            if (int.TryParse(txtRuns.Text, out var runs)) project.Runs = runs;
            project.Seed = uint.TryParse(txtSeed.Text.Trim(), out var seed) ? seed : 0;
            project.ClockReseed = chkReseed.Checked;
            project.KeepRaw = chkKeepRaw.Checked;
            project.SaveSelected = rbSaveSel.Checked;
            if (netlist != null) project.Signals = allSignals.Where(s => !s.Locked && checkedSignals.Contains(s.Signal)).Select(s => s.Signal).ToList();
            project.Temp = rbTempCorners.Checked ? TempMode.Corners : rbTempRandom.Checked ? TempMode.Random : TempMode.Default;
            var corners = SpicaProject.ParseList(txtCorners.Text);
            if (corners.Count > 0) project.Corners = corners;
            if (double.TryParse(txtTempMin.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var tmin)) project.TempMin = tmin;
            if (double.TryParse(txtTempMax.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var tmax)) project.TempMax = tmax;
            project.Paired = chkPaired.Checked;
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
            }
            finally { loadingUi = false; }
            UpdateTitle();
        }

        /// <summary>A new setup for a schematic, from the last-used values.</summary>
        SpicaProject DefaultProject(string asc) => new SpicaProject
        {
            Schematic = asc,
            Runs = settings.Runs,
            ClockReseed = settings.ClockReseed,
            KeepRaw = settings.KeepRaw,
            SaveSelected = settings.SaveSelectedOnly,
            Signals = (asc != null ? settings.GetSaveSelection(asc) : null) ?? new List<string>(),
        };

        static string SidecarOf(string asc) => Path.Combine(Path.GetDirectoryName(asc), Path.GetFileNameWithoutExtension(asc) + ".spica");

        void OpenSetup(string path)
        {
            if (Running || !ConfirmDiscardSetup()) return;
            SpicaProject p;
            try { p = SpicaProject.Load(path); }
            catch (Exception ex) { Win98MessageBox.Show(this, "Could not read the setup:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); return; }
            if (!File.Exists(p.Schematic))
            {
                Win98MessageBox.Show(this, "The schematic of this setup was not found:\n" + p.Schematic + "\n\nOpen the schematic, then use File > Save Setup As.", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                return;
            }
            settings.PushRecent(path);
            _ = LoadSchematic(p.Schematic, false, p);
        }

        bool SaveSetup(bool saveAs)
        {
            if (ascPath == null) { Win98MessageBox.Show(this, "Open a schematic first.", "SPICA", MessageBoxButtons.OK, Win98Icon.Information); return false; }
            ReadUiIntoProject();
            string path = project.FilePath;
            if (saveAs || path == null)
            {
                using (var d = new SaveFileDialog
                {
                    Title = "Save Setup",
                    Filter = "SPICA setup (*.spica)|*.spica",
                    FileName = Path.GetFileName(path ?? SidecarOf(ascPath)),
                    InitialDirectory = Path.GetDirectoryName(path ?? ascPath),
                    AutoUpgradeEnabled = false,
                })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return false;
                    path = d.FileName;
                }
            }
            try
            {
                project.Save(path);
                settings.PushRecent(path);
                SetStatus("Setup saved: " + path);
                UpdateTitle();
                return true;
            }
            catch (Exception ex)
            {
                Win98MessageBox.Show(this, "Could not save the setup:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
                return false;
            }
        }

        /// <summary>Asks to save an edited setup. False = the user cancelled.</summary>
        bool ConfirmDiscardSetup()
        {
            if (ascPath == null) return true;
            ReadUiIntoProject();
            if (!project.Dirty) return true;
            var r = Win98MessageBox.Show(this, "Save the changes to the setup" + (project.FilePath != null ? " " + Path.GetFileName(project.FilePath) : "") + "?\n(runs, temperatures, signals, spec limits, TF)",
                "SPICA", MessageBoxButtons.YesNoCancel, Win98Icon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return SaveSetup(false);
            return true;
        }

        // =====================================================================================
        //  Settings
        // =====================================================================================

        void LoadSettings()
        {
            var lt = settings.LtspicePath;
            if (string.IsNullOrEmpty(lt) || !File.Exists(lt)) lt = Ltspice.Locate() ?? "";
            txtLtspice.Text = lt;
            rbAuto.Checked = settings.AutoParallel;
            rbManual.Checked = !settings.AutoParallel;
            txtInstances.Text = (settings.ManualInstances > 0 ? settings.ManualInstances : hw.PhysicalCores).ToString(CultureInfo.InvariantCulture);
            txtThreads.Text = Math.Max(1, settings.ManualThreads).ToString(CultureInfo.InvariantCulture);
            txtInstances.Enabled = txtThreads.Enabled = rbManual.Checked;
            project = DefaultProject(null);
            project.Schematic = "";
            WriteProjectToUi();
            project.Dirty = false;

            // any edit on the Setup tab makes the setup "unsaved"
            foreach (var t in new[] { txtRuns, txtSeed, txtCorners, txtTempMin, txtTempMax }) t.Box.TextChanged += (s, e) => MarkDirty();
            foreach (var c in new[] { chkReseed, chkKeepRaw, chkPaired }) c.CheckedChanged += (s, e) => MarkDirty();
            rbSaveSel.CheckedChanged += (s, e) => MarkDirty();

            UpdateLtspiceVersion();
            UpdateSignalCount();
            UpdateTitle();
        }

        void SaveSettings()
        {
            // application settings, plus the last-used values that seed a new setup
            settings.LtspicePath = txtLtspice.Text.Trim();
            if (int.TryParse(txtRuns.Text, out var runs)) settings.Runs = runs;
            settings.ClockReseed = chkReseed.Checked;
            settings.KeepRaw = chkKeepRaw.Checked;
            settings.AutoParallel = rbAuto.Checked;
            if (int.TryParse(txtInstances.Text, out var inst)) settings.ManualInstances = inst;
            if (int.TryParse(txtThreads.Text, out var thr)) settings.ManualThreads = thr;
            settings.SaveSelectedOnly = rbSaveSel.Checked;
            if (ascPath != null && netlist != null)
                settings.SetSaveSelection(ascPath, allSignals.Where(s => !s.Locked && checkedSignals.Contains(s.Signal)).Select(s => s.Signal));
            settings.Save();
        }

        void UpdateLtspiceVersion()
        {
            var lt = txtLtspice.Text.Trim();
            status.SetText(1, File.Exists(lt) ? "LTspice " + Ltspice.Version(lt) : "LTspice not found");
        }

        // =====================================================================================
        //  Schematic loading
        // =====================================================================================

        void BrowseSchematic()
        {
            if (Running) return;
            using (var d = new OpenFileDialog
            {
                Title = "Open Schematic or Setup",
                Filter = "Schematics and setups (*.asc;*.spica)|*.asc;*.spica|LTspice schematics (*.asc)|*.asc|SPICA setups (*.spica)|*.spica|All files (*.*)|*.*",
                AutoUpgradeEnabled = false,
                InitialDirectory = ascPath != null ? Path.GetDirectoryName(ascPath) : "",
            })
            {
                if (d.ShowDialog(this) == DialogResult.OK) OpenFile(d.FileName);
            }
        }

        void BrowseLtspice()
        {
            using (var d = new OpenFileDialog { Title = "Locate LTspice", Filter = "LTspice (LTspice.exe;XVIIx64.exe)|LTspice.exe;XVIIx64.exe|Programs (*.exe)|*.exe", AutoUpgradeEnabled = false })
            {
                if (d.ShowDialog(this) == DialogResult.OK) { txtLtspice.Text = d.FileName; UpdateLtspiceVersion(); SaveSettings(); }
            }
        }

        void BrowseOutput()
        {
            using (var d = new FolderBrowserDialog { Description = "Folder where SPICA creates one sub-folder per campaign:", SelectedPath = txtOut.Text })
            {
                if (d.ShowDialog(this) == DialogResult.OK) txtOut.Text = d.SelectedPath;
            }
        }

        /// <param name="reload">Re-read the same schematic, keeping the current setup and output folder.</param>
        /// <param name="withSetup">A setup loaded from a .spica file to apply to this schematic.</param>
        async Task LoadSchematic(string path, bool reload = false, SpicaProject withSetup = null)
        {
            string lt = txtLtspice.Text.Trim();
            if (!File.Exists(lt))
            {
                Win98MessageBox.Show(this, "Please locate LTspice.exe first.", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                return;
            }
            if (!File.Exists(path))
            {
                Win98MessageBox.Show(this, "File not found:\n" + path, "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
                return;
            }
            if (ascPath == null || !ascPath.Equals(path, StringComparison.OrdinalIgnoreCase)) reload = false;
            if (reload) ReadUiIntoProject();
            else if (withSetup == null && !ConfirmDiscardSetup()) return;

            SetStatus("Converting schematic with LTspice -netlist...");
            UseWaitCursor = true;
            Enabled = false;
            try
            {
                var nl = await Task.Run(() =>
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "SPICA");
                    Directory.CreateDirectory(tmp);
                    string ini = Ltspice.WritePrivateIni(Path.Combine(tmp, "convert.ini"), true);
                    string text = Ltspice.ConvertSchematic(lt, path, ini, out var enc);
                    return Netlist.Parse(text, path, enc);
                });

                // the setup: given, kept (reload), the .spica beside the schematic, or a fresh one
                string sidecar = SidecarOf(path);
                if (withSetup != null) project = withSetup;
                else if (!reload)
                {
                    project = null;
                    if (File.Exists(sidecar))
                    {
                        try { project = SpicaProject.Load(sidecar); } catch { project = null; }
                    }
                    if (project == null) project = DefaultProject(path);
                }
                project.Schematic = path;

                netlist = nl;
                ascPath = path;
                ascStamp = File.GetLastWriteTimeUtc(path);
                txtAsc.Text = path;
                if (!reload)
                    txtOut.Text = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_SPICA");
                settings.PushRecent(withSetup?.FilePath ?? path);

                // signal list: .meas signals locked first, then nodes, then currents
                allSignals.Clear();
                checkedSignals.Clear();
                var req = new HashSet<string>(nl.RequiredSignals, StringComparer.OrdinalIgnoreCase);
                foreach (var s in nl.RequiredSignals) allSignals.Add(new SignalItem { Signal = s, Locked = true });
                foreach (var n in nl.Nodes) { var s = $"V({n})"; if (!req.Contains(s)) allSignals.Add(new SignalItem { Signal = s }); }
                foreach (var c in nl.Currents) if (!req.Contains(c)) allSignals.Add(new SignalItem { Signal = c });

                bool wasDirty = project.Dirty;
                var remembered = project.Signals.Count > 0 || project.FilePath != null ? project.Signals : nl.ExistingSaves;
                foreach (var s in remembered) if (allSignals.Any(a => a.Signal.Equals(s, StringComparison.OrdinalIgnoreCase))) checkedSignals.Add(s);
                WriteProjectToUi();
                FillSignalList();
                project.Dirty = wasDirty;

                txtCircuit.Text = DescribeCircuit(nl);
                string from = withSetup != null ? $" with setup {Path.GetFileName(withSetup.FilePath)}" :
                              !reload && project.FilePath != null ? $" with its setup {Path.GetFileName(project.FilePath)}" : "";
                SetStatus($"Loaded {Path.GetFileName(path)}{from}: {nl.Nodes.Count} nets, {nl.MeasNames.Count} measurements.");
                SaveSettings();
                UpdateTitle();
            }
            catch (Exception ex)
            {
                SetStatus("Ready");
                Win98MessageBox.Show(this, "Could not read the schematic.\n\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
            }
            finally
            {
                Enabled = true;
                UseWaitCursor = false;
                UpdateCommandState();
            }
        }

        static string DescribeCircuit(Netlist nl)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Analysis:  " + (nl.Analysis.Length > 0 ? nl.Analysis : "(none found)"));
            sb.AppendLine($"Nets:      {nl.Nodes.Count}");
            sb.AppendLine($"Currents:  {nl.Currents.Count}");
            sb.AppendLine();
            sb.AppendLine($"Measurements ({nl.MeasNames.Count}):");
            if (nl.MeasNames.Count == 0) sb.AppendLine("  (none - add .meas statements to get results)");
            foreach (var m in nl.MeasNames) sb.AppendLine("  " + m);
            sb.AppendLine();

            var plan = McPlan.Build(nl.Lines);
            sb.AppendLine($"Random values ({plan.Sites.Count}):");
            foreach (var s in plan.Sites) sb.AppendLine($"  {s.Label}: {s.Original}");
            if (plan.Inlined.Count > 0) sb.AppendLine($"  via .func: {string.Join(", ", plan.Inlined)}");
            foreach (var w in plan.Warnings) sb.AppendLine("  ! " + w);
            if (plan.Sites.Count == 0 && plan.Warnings.Count == 0) sb.AppendLine("  (no mc()/gauss()/flat() found - every run will be identical)");
            sb.AppendLine();

            if (nl.RemovedSteps.Count > 0)
            {
                sb.AppendLine("Replaced by SPICA's run stepping:");
                foreach (var s in nl.RemovedSteps) sb.AppendLine("  " + s);
            }
            var temps = nl.TempLines.ToList();
            if (temps.Count > 0)
            {
                sb.AppendLine("Schematic temperature (replaced when SPICA sets one):");
                foreach (var t in temps) sb.AppendLine("  " + t);
            }
            if (nl.IsOpAnalysis) sb.AppendLine(".op: SPICA adds a dummy .step so .meas give one row per run.");
            return sb.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
        }

        // =====================================================================================
        //  Campaign control
        // =====================================================================================

        bool Running => engine != null && !engine.IsFinished;

        /// <param name="resumeDir">an unfinished campaign folder to complete (its setup is already loaded)</param>
        /// <param name="resume">the runs already finished in that folder</param>
        void StartCampaign(string resumeDir = null, List<RunResult> resume = null, double priorSeconds = 0)
        {
            if (Running) return;
            if (netlist == null) { BrowseSchematic(); if (netlist == null) return; }

            string lt = txtLtspice.Text.Trim();
            if (!File.Exists(lt)) { Win98MessageBox.Show(this, "LTspice.exe was not found.", "SPICA", MessageBoxButtons.OK, Win98Icon.Error); return; }
            if (!int.TryParse(txtRuns.Text, out int runs) || runs < 1 || runs > 10_000_000)
            {
                Win98MessageBox.Show(this, "Enter a number of runs between 1 and 10,000,000.", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                return;
            }
            AdaptiveScheduler.Config? manual = null;
            if (rbManual.Checked)
            {
                if (!int.TryParse(txtInstances.Text, out int inst) || inst < 1 || !int.TryParse(txtThreads.Text, out int thr) || thr < 1)
                {
                    Win98MessageBox.Show(this, "Enter the number of instances and threads per instance (1 or more).", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                    return;
                }
                manual = new AdaptiveScheduler.Config(inst, thr);
            }

            if (resumeDir == null && File.Exists(ascPath) && File.GetLastWriteTimeUtc(ascPath) != ascStamp)
            {
                if (Win98MessageBox.Show(this, "The schematic changed since it was loaded. Reload it before starting?", "SPICA",
                        MessageBoxButtons.YesNo, Win98Icon.Question) == DialogResult.Yes)
                {
                    _ = LoadSchematic(ascPath, true);
                    return;
                }
            }

            ReadUiIntoProject();
            var inv = CultureInfo.InvariantCulture;
            List<double> corners = null;
            if (project.Temp == TempMode.Corners)
            {
                corners = SpicaProject.ParseList(txtCorners.Text).Distinct().ToList();
                if (corners.Count == 0 || corners.Any(t => t < -273 || t > 1000))
                {
                    Win98MessageBox.Show(this, "Enter the temperature corners in °C, separated by commas (e.g. -40, 25, 90).", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                    return;
                }
            }
            if (project.Temp == TempMode.Random &&
                (!double.TryParse(txtTempMin.Text, NumberStyles.Float, inv, out var tlo) || !double.TryParse(txtTempMax.Text, NumberStyles.Float, inv, out var thi) || !(thi > tlo)))
            {
                Win98MessageBox.Show(this, "Enter a random temperature range with min < max (°C).", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                return;
            }

            McPlan plan = null;
            if (project.Paired)
            {
                plan = McPlan.Build(netlist.Lines);
                if (plan.Sites.Count == 0 &&
                    Win98MessageBox.Show(this, "Paired samples are on, but SPICA found no mc()/gauss()/flat() it can take over in this schematic" +
                        (plan.Warnings.Count > 0 ? " (random values inside .subckt definitions stay with LTspice)" : "") + ".\nRun anyway?",
                        "SPICA", MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;
            }

            if (netlist.MeasNames.Count == 0 &&
                Win98MessageBox.Show(this, "This schematic has no .meas statements, so the results table will be empty.\nRun anyway?",
                    "SPICA", MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;
            if (!project.Paired && !chkReseed.Checked &&
                Win98MessageBox.Show(this, "Without clock reseeding every LTspice instance repeats the SAME random sequence, so chunks produce duplicate runs.\nContinue anyway?",
                    "SPICA", MessageBoxButtons.YesNo, Win98Icon.Warning) != DialogResult.Yes) return;
            if (!project.Paired && corners != null && corners.Count > 1 &&
                Win98MessageBox.Show(this, "Temperature corners without paired samples simulate DIFFERENT units at each temperature,\nso per-unit drift and 'pass at all temperatures' cannot be computed.\nContinue anyway?",
                    "SPICA", MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;

            List<string> save = null;
            if (rbSaveSel.Checked)
            {
                save = SelectedSignals();
                if (save.Count == 0)
                {
                    if (Win98MessageBox.Show(this, "No signals are selected, so LTspice will save everything.\nContinue?", "SPICA",
                            MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;
                    save = null;
                }
            }

            string root = txtOut.Text.Trim();
            if (root.Length == 0) root = Path.Combine(Path.GetDirectoryName(ascPath), Path.GetFileNameWithoutExtension(ascPath) + "_SPICA");
            string outDir = resumeDir ?? Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));

            try
            {
                Directory.CreateDirectory(outDir);
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outDir)));
                if (chkKeepRaw.Checked && drive.AvailableFreeSpace < 2L * (1L << 30) &&
                    Win98MessageBox.Show(this, $"Only {drive.AvailableFreeSpace / 1048576} MB free on {drive.Name}. .raw files can grow quickly.\nContinue?",
                        "SPICA", MessageBoxButtons.YesNo, Win98Icon.Warning) != DialogResult.Yes) return;
            }
            catch (Exception ex)
            {
                Win98MessageBox.Show(this, "Cannot create the output folder:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
                return;
            }

            SaveSettings();
            uint seed = project.Seed;
            if (seed == 0) seed = (uint)(Guid.NewGuid().GetHashCode() & 0x7FFFFFFF) | 1;
            var options = new CampaignOptions
            {
                LtspiceExe = lt,
                AscPath = ascPath,
                OutputDir = outDir,
                Netlist = netlist,
                Runs = runs,
                SaveSignals = save,
                KeepRaw = chkKeepRaw.Checked,
                ClockReseed = chkReseed.Checked,
                Manual = manual,
                Corners = corners,
                RandomTemp = project.Temp == TempMode.Random,
                TempMin = project.TempMin,
                TempMax = project.TempMax,
                Mc = plan,
                Seed = seed,
                Setup = project,
                Resume = resume,
                PriorSeconds = priorSeconds,
            };
            analyzeOnFinish = discardOnFinish = false;

            txtLog.Clear();
            graph.Clear();
            resultsView.SetData(null, false);
            progress.Maximum = options.Jobs;
            progress.Value = 0;

            engine = new CampaignEngine(options, hw, profiles);
            engine.Finished += () => BeginInvoke((Action)OnCampaignFinished);
            try { engine.Start(); }
            catch (Exception ex)
            {
                engine = null;
                Win98MessageBox.Show(this, "Could not start the campaign:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
                return;
            }
            tabs.SelectedIndex = 1;
            SetStatus(resume != null ? $"Resuming: {options.Jobs - resume.Count(r => r.Status != RunStatus.Failed)} of {options.Jobs} simulations to go..."
                                     : $"Running {options.Jobs} simulations...");
            UpdateCommandState();
        }

        void StopCampaign()
        {
            if (!Running) return;
            var s = engine.Snapshot();
            string head = $"Stop the campaign?  {s.Done:N0} of {s.Total:N0} simulations are finished" +
                          (s.Running > 0 ? $", {s.Running:N0} are running now." : ".");
            int choice;
            if (!s.Draining && s.Running > 0)
            {
                choice = Win98MessageBox.Choose(this, head + "\n\n" +
                    "Analyze now:  stop at once and open the finished runs (the running ones are lost).\n" +
                    "Finish running:  start nothing new, wait for the running simulations, then analyze.\n" +
                    "Discard:  stop and delete this campaign folder with everything in it.\n\n" +
                    "A stopped campaign can be resumed later from File > Open Results.",
                    "Stop Campaign", Win98Icon.Question, "&Analyze now", "&Finish running", "&Discard", "Keep running");
            }
            else
            {
                choice = Win98MessageBox.Choose(this, head + "\n\n" +
                    "Analyze now:  stop at once and open the finished runs.\n" +
                    "Discard:  stop and delete this campaign folder with everything in it.\n\n" +
                    "A stopped campaign can be resumed later from File > Open Results.",
                    "Stop Campaign", Win98Icon.Question, "&Analyze now", "&Discard", "Keep running");
                if (choice >= 1) choice++;      // same meaning as in the four-button box
            }
            switch (choice)
            {
                case 0:
                    analyzeOnFinish = true;
                    engine.Cancel();
                    SetStatus("Stopping...");
                    break;
                case 1:
                    analyzeOnFinish = true;
                    engine.StopAfterRunning();
                    SetStatus($"Stopping: waiting for {s.Running:N0} running simulations...");
                    break;
                case 2:
                    if (Win98MessageBox.Show(this, "Delete this campaign folder and all its files?\n" + engine.Options.OutputDir, "Discard Campaign",
                            MessageBoxButtons.YesNo, Win98Icon.Warning) != DialogResult.Yes) return;
                    discardOnFinish = true;
                    engine.Cancel();
                    SetStatus("Discarding...");
                    break;
            }
        }

        void OnCampaignFinished()
        {
            if (discardOnFinish)
            {
                discardOnFinish = analyzeOnFinish = false;
                string dir = engine.Options.OutputDir;
                bool gone = DeleteFolder(dir);
                engine = null;
                txtLog.Clear();
                graph.Clear();
                resultsView.SetData(null, false);
                progress.Value = 0;
                UpdateCommandState();
                SetStatus(gone ? "Campaign discarded." : "Campaign stopped; some files could not be deleted.");
                if (!gone)
                    Win98MessageBox.Show(this, "The campaign was stopped, but the folder could not be deleted completely:\n" + dir, "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                return;
            }

            UpdateUi();
            RefreshResults();
            UpdateCommandState();
            var s = engine.Snapshot();
            SetStatus(s.Cancelled ? "Campaign stopped." : "Campaign finished.");
            System.Media.SystemSounds.Asterisk.Play();
            if (s.FatalError != null)
            {
                Win98MessageBox.Show(this, s.FatalError, "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
                return;
            }
            if (analyzeOnFinish)
            {
                analyzeOnFinish = false;
                tabs.SelectedIndex = 2;
                return;
            }
            string msg = $"{(s.Cancelled ? "Campaign stopped" : "Campaign finished")} in {s.Elapsed:hh\\:mm\\:ss}.\n\n" +
                         $"{s.Ok} runs with data, {s.NoData} without data, {s.Failed} failed.\n\nOpen the Results tab?";
            if (Win98MessageBox.Show(this, msg, "SPICA", MessageBoxButtons.YesNo, s.Failed > 0 ? Win98Icon.Warning : Win98Icon.Information) == DialogResult.Yes)
                tabs.SelectedIndex = 2;
        }

        /// <summary>Deletes a campaign folder; LTspice may hold files for a moment after being killed.</summary>
        static bool DeleteFolder(string dir)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    return true;
                }
                catch { System.Threading.Thread.Sleep(300); }
            }
            return !Directory.Exists(dir);
        }

        void UpdateCommandState()
        {
            bool run = Running;
            bool has = netlist != null;
            tbStart.Enabled = miStart.Enabled = btnStart.Enabled = !run && has;
            tbStop.Enabled = miStop.Enabled = run;
            tbOpen.Enabled = !run;
            tbFolder.Enabled = miFolder.Enabled = engine != null;
            miExport.Enabled = engine != null;
            foreach (Control c in pageSetup.Controls) c.Enabled = !run;
        }

        // =====================================================================================
        //  Periodic UI refresh
        // =====================================================================================

        void UpdateUi()
        {
            status.SetText(3, DateTime.Now.ToString("HH:mm"));
            if (engine == null) return;

            var sb = new StringBuilder();
            while (engine.LogQueue.TryDequeue(out var line)) sb.Append(line).Append("\r\n");
            if (sb.Length > 0)
            {
                if (txtLog.TextLength > 400_000) txtLog.Text = txtLog.Text.Substring(txtLog.TextLength - 200_000);
                txtLog.AppendText(sb.ToString());
            }

            var s = engine.Snapshot();
            progress.Maximum = Math.Max(1, s.Total);
            progress.Value = s.Done;
            var inv = CultureInfo.InvariantCulture;
            stat["Runs"].Text = $"{s.Done:N0} / {s.Total:N0}  ({100.0 * s.Done / Math.Max(1, s.Total):0}%)";
            stat["Elapsed"].Text = s.Elapsed.ToString(@"hh\:mm\:ss");
            stat["Remaining"].Text = double.IsNaN(s.EtaSeconds) ? (s.Finished ? "done" : "estimating...") : TimeSpan.FromSeconds(Math.Min(s.EtaSeconds, 359999)).ToString(@"hh\:mm\:ss");
            stat["Rate"].Text = s.RunsPerMinute > 0 ? $"{s.RunsPerMinute:0.0} runs/min" : "-";
            stat["Config"].Text = s.Config;
            stat["Phase"].Text = s.Draining ? $"Stopping ({s.Running} running)" : s.Cancelled ? "Stopped" : s.Finished ? "Finished" : PhaseText(s.Phase);
            stat["Start-up"].Text = double.IsNaN(s.OverheadSeconds) ? "-" : $"{s.OverheadSeconds:0.00} s / launch";
            stat["Per run"].Text = double.IsNaN(s.SecondsPerRun) ? "-" : $"{s.SecondsPerRun:0.000} s (sim)";
            stat["Failed"].Text = $"{s.Failed}" + (s.NoData > 0 ? $"  (+{s.NoData} no data)" : "");
            stat["Peak RAM"].Text = s.PeakMemory > 0 ? $"{s.PeakMemory / 1048576.0:0} MB / instance" : "-";
            stat[".raw kept"].Text = engine.Options.KeepRaw ? FormatBytes(s.RawBytes) : "deleted";
            stat[".raw forecast"].Text = double.IsNaN(s.RawForecast) ? "-" : FormatBytes((long)s.RawForecast);

            if (++graphTick % 4 == 0 && !s.Finished) graph.Push(s.RunsPerMinute);

            lvWorkers.BeginUpdate();
            while (lvWorkers.Items.Count > s.Workers.Count) lvWorkers.Items.RemoveAt(lvWorkers.Items.Count - 1);
            for (int i = 0; i < s.Workers.Count; i++)
            {
                var w = s.Workers[i];
                string[] cols =
                {
                    "#" + w.Id.ToString(inv), w.Pid > 0 ? w.Pid.ToString(inv) : "-",
                    (w.First > 0 ? (w.First == w.Last ? $"{w.First}" : $"{w.First} - {w.Last}") : "-") + (double.IsNaN(w.Temp) ? "" : $"  @ {w.Temp:0.#} °C"),
                    w.State, $"{w.Seconds:0.0} s", w.Memory > 0 ? $"{w.Memory / 1048576.0:0} MB" : "-", w.Threads.ToString(inv),
                };
                if (i >= lvWorkers.Items.Count) lvWorkers.Items.Add(new ListViewItem(cols));
                else
                {
                    var it = lvWorkers.Items[i];
                    for (int c = 0; c < cols.Length; c++)
                    {
                        if (c < it.SubItems.Count) { if (it.SubItems[c].Text != cols[c]) it.SubItems[c].Text = cols[c]; }
                        else it.SubItems.Add(cols[c]);
                    }
                }
            }
            lvWorkers.EndUpdate();

            if (tabs.SelectedIndex == 2 && graphTick % 4 == 0 && !s.Finished) RefreshResults();
            if (!s.Finished) status.SetText(0, $"Running: {s.Done:N0}/{s.Total:N0} runs, {s.Config}");
        }

        static string PhaseText(string phase)
        {
            switch (phase)
            {
                case "WarmUp": return "Measuring baseline";
                case "Probing": return "Probing configuration";
                case "Converged": return "Converged";
                case "Manual": return "Manual";
                default: return phase;
            }
        }

        static string FormatBytes(long b)
        {
            if (b < 1048576) return $"{b / 1024.0:0} KB";
            if (b < 1L << 30) return $"{b / 1048576.0:0.0} MB";
            return $"{b / (double)(1L << 30):0.00} GB";
        }

        void SetStatus(string s) => status.SetText(0, s);

        // =====================================================================================
        //  Misc commands
        // =====================================================================================

        void OpenOutputFolder()
        {
            string dir = engine?.Options.OutputDir ?? txtOut.Text;
            if (Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        void ExportResults()
        {
            if (engine != null && (resultsView.Data == null || resultsView.Data.Folder == engine.Options.OutputDir)) RefreshResults();
            resultsView.ExportCsv();
        }

        void ShowHardware()
        {
            var lines = new StringBuilder();
            lines.AppendLine($"Processor:\t{hw.CpuName}");
            lines.AppendLine($"Physical cores:\t{hw.PhysicalCores}");
            lines.AppendLine($"Logical threads:\t{hw.LogicalProcessors}" + (hw.HasSmt ? "  (SMT / Hyper-Threading on)" : ""));
            if (hw.IsHybrid) lines.AppendLine($"Hybrid:\t{hw.PerformanceCores} performance + {hw.EfficiencyCores} efficiency cores");
            lines.AppendLine($"NUMA nodes:\t{hw.NumaNodes}");
            lines.AppendLine($"Processor groups:\t{hw.ProcessorGroups}");
            HardwareInfo.RefreshMemory(hw);
            lines.AppendLine($"Memory:\t{hw.TotalMemory / (double)(1L << 30):0.0} GB ({hw.AvailableMemory / (double)(1L << 30):0.0} GB free)");
            lines.AppendLine();
            lines.AppendLine($"Automatic mode starts with {hw.PhysicalCores} instances x 1 thread");
            lines.AppendLine("and tunes from there while the campaign runs.");
            Win98MessageBox.Show(this, lines.ToString().Replace("\t", "  "), "Hardware Information", MessageBoxButtons.OK, Win98Icon.Information);
        }

        void ForgetProfiles()
        {
            try
            {
                var path = KeyValueFile.Resolve("SPICA.profiles");
                if (File.Exists(path)) File.Delete(path);
                Win98MessageBox.Show(this, "Saved scheduler profiles were deleted.\nThe next campaign starts tuning from scratch.", "SPICA");
            }
            catch (Exception ex) { Win98MessageBox.Show(this, ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); }
        }

        void ShowAbout()
        {
            Win98MessageBox.Show(this,
                "SPICA  version " + Application.ProductVersion + "\n" +
                "Spice Parallel Instancer Circuit Analysis\n\n" +
                "Runs LTspice Monte Carlo campaigns as many parallel batch\n" +
                "instances, tuning the parallelism to this machine.\n\n" +
                "Icons: alexh/vintage-icons (MIT).\n" +
                "LTspice is a trademark of Analog Devices, Inc.",
                "About SPICA", MessageBoxButtons.OK, Win98Icon.Information);
        }

        // =====================================================================================
        //  Closing
        // =====================================================================================

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (Running)
            {
                if (Win98MessageBox.Show(this, "A campaign is running. Stop it and exit?\nThe finished runs are saved; you can resume the campaign later from File > Open Results.",
                        "SPICA", MessageBoxButtons.YesNo, Win98Icon.Warning) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                engine.Cancel();
                var sw = Stopwatch.StartNew();
                while (!engine.IsFinished && sw.ElapsedMilliseconds < 8000) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
            }
            if (!ConfirmDiscardSetup()) { e.Cancel = true; return; }
            SaveSettings();
            base.OnFormClosing(e);
        }
    }
}
