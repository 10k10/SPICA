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
    /// <summary>
    /// The main window: Setup (what to simulate), Queue (what runs and what waits) and Results (the analysis).
    /// This file holds the frame, the files and the commands; MainForm.Setup.cs and MainForm.Queue.cs hold the two tabs.
    /// </summary>
    public partial class MainForm : Win98Form
    {
        public string StartupFile;

        readonly AppSettings settings = new AppSettings();
        readonly ProfileStore profiles = new ProfileStore();
        readonly HardwareInfo hw = HardwareInfo.Detect();
        readonly CampaignQueue queue;
        readonly Listener listener;
        readonly RemoteTracker remote;

        Netlist netlist;
        string ascPath;
        string circuitHash;                        // of the loaded netlist: finds the run times saved for it
        DateTime ascStamp;
        SpicaProject project = new SpicaProject();
        bool loadingUi, closing;

        QueueJob shownJob;                         // on the Queue tab's monitor: the running campaign, or the last that ran
        QueueJob liveJob;                          // the campaign the Results tab follows (null = results opened from disk)
        CampaignEngine engine;                     // shownJob's engine, kept after it ends (final figures, log)
        int graphTick;
        double graphShown;                         // throughput as drawn: rises from 0 at the start

        // chrome
        MenuStrip menu;
        ToolStrip toolbar;
        Win98StatusBar status;
        Win98TabControl tabs;
        Win98TabPage pageSetup, pageQueue, pageResults;
        ToolStripButton tbOpen, tbAdd, tbRemote, tbStop, tbFolder, tbReport, tbListener, tbSave;
        ToolStripMenuItem miOpen, miAdd, miRemote, miStop, miRecent, miExport, miReport, miFolder, miSave, miSaveAs;
        ListenerForm listenerForm;

        // results page
        ResultsView resultsView;

        readonly Timer uiTimer = new Timer { Interval = 250 };

        const int TabSetup = 0, TabQueue = 1, TabResults = 2;
        const int PaneText = 0, PaneListener = 1, PaneLtspice = 2, PaneClock = 4;

        public MainForm()
        {
            Text = AppTitle;
            ClientSize = new Size(1080, 780);
            MinimumSize = new Size(1000, 720);
            StartPosition = FormStartPosition.CenterScreen;
            CaptionIcon = Win98.Icon("app16");
            KeyPreview = true;

            queue = new CampaignQueue(hw, profiles, Post);
            listener = new Listener(queue, Post) { LtspiceExe = () => settings.LtspicePath, Manual = ManualConfig };
            queue.Started += OnJobStarted;
            queue.Finished += OnJobFinished;
            queue.Discarded += OnJobDiscarded;
            queue.Changed += () => { UpdateQueueList(); UpdateCommandState(); UpdatePlan(); };
            remote = new RemoteTracker(Post);
            remote.Changed += () => { UpdateQueueList(); UpdateQueueButtons(); };
            remote.Ended += OnRemoteEnded;

            BuildChrome();
            BuildSetupPage();
            BuildQueuePage();
            BuildResultsPage();

            Controls.Add(tabs);
            Controls.Add(status);
            Controls.Add(toolbar);
            Controls.Add(menu);
            MainMenuStrip = menu;

            // a schematic, a setup or a campaign folder dropped on the window opens
            AllowDrop = true;
            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0 && !Slave) BeginInvoke((Action)(() => OpenDropped(files[0])));
            };

            uiTimer.Tick += (s, e) => UpdateUi();
            uiTimer.Start();
            LoadSettings();
            UpdateCommandState();
        }

        /// <summary>Runs something on the window's thread later (the engines and the listener report from worker threads).</summary>
        void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!File.Exists(settings.LtspicePath ?? ""))
            {
                Win98MessageBox.Show(this, "LTspice was not found in the usual locations.\nPoint SPICA at LTspice.exe in the options that open next.",
                    "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                ShowOptions();
            }
            if (settings.ListenerEnabled && settings.ListenerInput.Length > 0)
            {
                try
                {
                    listener.Start(settings.ListenerInput, settings.ListenerOutput.Length > 0 ? settings.ListenerOutput : Listener.SuggestOutput(settings.ListenerInput));
                    SetStatus("Listening for campaigns in " + listener.Input);
                }
                catch (Exception ex) { SetStatus("The listener could not start: " + ex.Message.Replace("\n", " ")); }
            }
            if (!string.IsNullOrEmpty(StartupFile)) OpenDropped(StartupFile);
        }

        void OpenDropped(string path)
        {
            if (File.Exists(path)) OpenFile(path);
            else if (File.Exists(Path.Combine(path, "results.csv"))) OpenResultsFolder(path);
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
            miOpen = new ToolStripMenuItem("&Open Schematic or Setup...", Win98.Icon("open16"), (s, e) => BrowseSchematic()) { ShortcutKeys = Keys.Control | Keys.O };
            miSave = new ToolStripMenuItem("&Save Setup", Win98.Icon("save16"), (s, e) => SaveSetup(false)) { ShortcutKeys = Keys.Control | Keys.S };
            miSaveAs = new ToolStripMenuItem("Save Setup &As...", null, (s, e) => SaveSetup(true));
            miRecent = new ToolStripMenuItem("Recent &Files");
            var miExit = new ToolStripMenuItem("E&xit", null, (s, e) => Close());
            mFile.DropDownItems.AddRange(new ToolStripItem[] { miOpen, miSave, miSaveAs, new ToolStripSeparator(), miRecent, new ToolStripSeparator(), miExit });
            mFile.DropDownOpening += (s, e) => FillRecent();
            miRecent.DropDownItems.Add("(empty)");

            var mCampaign = new ToolStripMenuItem("&Campaign");
            miAdd = new ToolStripMenuItem("Run on &This PC", Win98.Icon("run16"), (s, e) => AddToQueue()) { ShortcutKeys = Keys.F5 };
            miRemote = new ToolStripMenuItem("Run on the &Remote PC", Win98.Icon("remote16"), (s, e) => RunRemote()) { ShortcutKeys = Keys.F6 };
            miStop = new ToolStripMenuItem("S&top the Running Campaign...", Win98.Icon("stop16"), (s, e) => StopCampaign()) { ShortcutKeys = Keys.Shift | Keys.F5 };
            var miQueue = new ToolStripMenuItem("Show the Q&ueue", Win98.Icon("queue16"), (s, e) => tabs.SelectedIndex = TabQueue) { ShortcutKeys = Keys.Control | Keys.Q };
            mCampaign.DropDownItems.AddRange(new ToolStripItem[] { miAdd, miRemote, miStop, new ToolStripSeparator(), miQueue });

            // everything that acts on the results shown in the Results tab (enabled whenever it can be used)
            var mResults = new ToolStripMenuItem("&Results");
            var miResults = new ToolStripMenuItem("&Open Results...", Win98.Icon("results16"), (s, e) => OpenResults()) { ShortcutKeys = Keys.Control | Keys.R };
            miExport = new ToolStripMenuItem("Export &CSV...", Win98.Icon("save16"), (s, e) => ExportResults()) { ShortcutKeys = Keys.Control | Keys.E };
            miReport = new ToolStripMenuItem("&Report...", Win98.Icon("doc16"), (s, e) => ExportReport()) { ShortcutKeys = Keys.Control | Keys.P };
            miFolder = new ToolStripMenuItem("Open Campaign &Folder", Win98.Icon("folder16"), (s, e) => OpenOutputFolder());
            mResults.DropDownItems.AddRange(new ToolStripItem[] { miResults, new ToolStripSeparator(), miExport, miReport, new ToolStripSeparator(), miFolder });
            mResults.DropDownOpening += (s, e) => UpdateCommandState();

            var mTools = new ToolStripMenuItem("&Tools");
            mTools.DropDownItems.Add(new ToolStripMenuItem("&Listener...", Win98.Icon("listener16"), (s, e) => ShowListener()) { ShortcutKeys = Keys.Control | Keys.L });
            mTools.DropDownItems.Add(new ToolStripSeparator());
            mTools.DropDownItems.Add(new ToolStripMenuItem("Op-amp &Models...", null, (s, e) => ShowOpAmps()));
            mTools.DropDownItems.Add(new ToolStripMenuItem("&Hardware Information...", Win98.Icon("hw16"), (s, e) => ShowHardware()));
            mTools.DropDownItems.Add(new ToolStripMenuItem("&Forget Saved Run Times", null, (s, e) => ForgetProfiles()));
            mTools.DropDownItems.Add(new ToolStripSeparator());
            mTools.DropDownItems.Add(new ToolStripMenuItem("&Options...", Win98.Icon("options16"), (s, e) => ShowOptions()));

            var mHelp = new ToolStripMenuItem("&Help");
            mHelp.DropDownItems.Add(new ToolStripMenuItem("&About SPICA...", Win98.Icon("help16"), (s, e) => ShowAbout()));

            menu.Items.AddRange(new ToolStripItem[] { mFile, mCampaign, mResults, mTools, mHelp });
            Win98Renderer.StyleDropDowns(menu.Items);

            // the toolbar follows the work: the file, the campaign, the listener, the results, this PC
            toolbar = new ToolStrip
            {
                Dock = DockStyle.Top,
                ImageScalingSize = new Size(32, 32),
                Padding = new Padding(4, 2, 4, 2),
                AutoSize = true,
            };
            Win98Renderer.Apply(toolbar);
            ToolStripButton Btn(string text, string icon, EventHandler click, string tip, int width = 64)
            {
                var b = new ToolStripButton(text, Win98.Icon(icon), click)
                {
                    DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                    TextImageRelation = TextImageRelation.ImageAboveText,
                    ImageScaling = ToolStripItemImageScaling.None,
                    AutoSize = false,
                    Size = new Size(width, 54),
                    ToolTipText = tip,
                    Margin = new Padding(1),
                };
                toolbar.Items.Add(b);
                return b;
            }
            tbOpen = Btn("Open", "open32", (s, e) => BrowseSchematic(), "Open an LTspice schematic (.asc) or a SPICA setup (.spica)  (Ctrl+O)");
            tbSave = Btn("Save", "save32", (s, e) => SaveSetup(false), "Save the setup (.spica): runs, temperatures, signals, spec limits, TF, error budget, report  (Ctrl+S)");
            toolbar.Items.Add(new ToolStripSeparator());
            tbAdd = Btn("Run Here", "run32", (s, e) => AddToQueue(), "Run the campaign of the Setup tab on this PC: it joins the queue and starts at once when nothing is running  (F5)", 70);
            tbRemote = Btn("Run Remote", "remote32", (s, e) => RunRemote(), "Send the campaign of the Setup tab to the remote PC (the shared folder in Tools > Options):\nits listener runs it there, and the Queue tab follows it here  (F6)", 76);
            tbStop = Btn("Stop", "stop32", (s, e) => StopCampaign(), "Stop the running campaign; finished runs are kept  (Shift+F5)");
            toolbar.Items.Add(new ToolStripSeparator());
            tbListener = Btn("Listener", "listener32", (s, e) => ShowListener(), "Watch a folder and run every campaign put there  (Ctrl+L)");
            toolbar.Items.Add(new ToolStripSeparator());
            Btn("Results", "results32", (s, e) => OpenResults(), "Open the results of an earlier campaign  (Ctrl+R)");
            tbReport = Btn("Report", "chart32", (s, e) => ExportReport(), "Write the HTML report of the results shown  (Ctrl+P)");
            tbFolder = Btn("Folder", "folder32", (s, e) => OpenOutputFolder(), "Open the folder of the campaign shown in Results");
            toolbar.Items.Add(new ToolStripSeparator());
            Btn("Options", "options32", (s, e) => ShowOptions(), "Where LTspice is and how many instances run on this PC");
            Btn("Help", "help32", (s, e) => ShowAbout(), "About SPICA");

            status = new Win98StatusBar();
            status.Add("Ready", 0);
            status.Add("Listener: off", 150, Win98.Icon("listener16"));
            status.Add("LTspice ?", 130);
            status.Add($"{hw.PhysicalCores}C/{hw.LogicalProcessors}T  {hw.TotalMemory / (double)(1L << 30):0.0} GB", 150, Win98.Icon("hw16"));
            status.Add(DateTime.Now.ToString("HH:mm"), 56);

            tabs = new Win98TabControl { Dock = DockStyle.Fill };
            pageSetup = new Win98TabPage("Setup", Win98.Icon("options16"));
            pageQueue = new Win98TabPage("Queue", Win98.Icon("queue16"));
            pageResults = new Win98TabPage("Results", Win98.Icon("results16"));
            tabs.AddPage(pageSetup);
            tabs.AddPage(pageQueue);
            tabs.AddPage(pageResults);
            tabs.SelectedIndexChanged += (s, e) =>
            {
                if (tabs.SelectedIndex != TabResults) return;
                // nothing shown yet: the campaign on the monitor is what the user means
                if (resultsView.Data == null && engine != null && shownJob != null) { liveJob = shownJob; RefreshResults(); }
                liveDone = -1; nextLiveRefresh = DateTime.MinValue;     // catch up at once when the tab is opened
            };
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
                miRecent.DropDownItems.Add(new ToolStripMenuItem($"&{i++} {Shorten(path, 60)}", null, (s, e) => OpenFile(path)));
            }
            Win98Renderer.StyleDropDowns(menu.Items);
        }

        static string Shorten(string p, int max) => p.Length <= max ? p : "..." + p.Substring(p.Length - max + 3);

        static Label Lbl(string text, int x, int y) => new Label { Text = text, Location = new Point(x, y + 3), AutoSize = true, BackColor = Win98.Face };

        static Panel Gap(DockStyle d, int size = 6) => new Panel { Dock = d, Height = size, Width = size, BackColor = Win98.Face };

        // =====================================================================================
        //  Results page
        // =====================================================================================

        void BuildResultsPage()
        {
            resultsView = new ResultsView();
            resultsView.DataChanged += UpdateCommandState;
            resultsView.SetupChanged += () =>
            {
                // analysis edits on results of the open setup's schematic belong to that setup (saved with File > Save Setup)
                if (resultsView.Data != null && ReferenceEquals(resultsView.Data.Setup, project))
                {
                    project.Dirty = true;
                    UpdateTitle();
                }
            };
            pageResults.Controls.Add(resultsView);
        }

        /// <summary>The engine of the campaign Results follows: attached to its job while it runs, kept here after it ends.</summary>
        CampaignEngine LiveEngine => liveJob == null ? null : liveJob.Engine ?? (liveJob == shownJob ? engine : null);

        /// <summary>Rebuilds the Results tab from the campaign it follows (running, or the last that ran).</summary>
        void RefreshResults()
        {
            var e = LiveEngine;
            if (e == null) return;
            // the open setup serves the campaigns of its schematic; another schematic's campaign is analysed with its own
            bool mine = SameFile(ascPath, liveJob.Request.AscPath);
            var rs = ResultSet.FromResults(e.GetResults(), e.MeasNames, e.Options, mine ? project : e.Options.Setup);
            resultsView.SetData(rs, !e.IsFinished);
        }

        /// <summary>Shows a campaign of the queue on the Results tab: live while it runs, from its folder once it ended.</summary>
        void ShowJobResults(QueueJob job)
        {
            if (job == null) return;
            if (job.Status == JobStatus.Queued)
            {
                Win98MessageBox.Show(this, "This campaign has not started yet.", "SPICA", MessageBoxButtons.OK, Win98Icon.Information);
                return;
            }
            bool current = job == liveJob && resultsView.Data != null;
            if (job.Engine != null || (job == shownJob && engine != null && !current))
            {
                liveJob = job;
                RefreshResults();
                liveDone = -1; nextLiveRefresh = DateTime.MinValue;
                tabs.SelectedIndex = TabResults;
            }
            else if (current) tabs.SelectedIndex = TabResults;
            else if (!string.IsNullOrEmpty(job.OutputDir) && File.Exists(Path.Combine(job.OutputDir, "results.csv"))) OpenResultsFolder(job.OutputDir);
            else Win98MessageBox.Show(this, "This campaign left no results" + (job.Message.Length > 0 ? ":\n" + job.Message : "."), "SPICA", MessageBoxButtons.OK, Win98Icon.Information);
        }

        /// <summary>
        /// Opens a campaign folder together with the setup of its schematic: the one open now, else the .spica beside
        /// the schematic, else the campaign's record as an unsaved setup. One setup serves running and analysing.
        /// </summary>
        async void OpenResultsFolder(string dir)
        {
            ResultSet rs;
            try { rs = ResultSet.Load(dir); }
            catch (Exception ex) { Win98MessageBox.Show(this, "Could not read the results:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); return; }

            string asc = rs.Record.Schematic;
            if (asc.Length > 0 && File.Exists(asc) && !SameFile(ascPath, asc) && File.Exists(settings.LtspicePath ?? "") && ConfirmDiscardSetup())
            {
                string sidecar = SidecarOf(asc);
                SpicaProject p = null;
                if (File.Exists(sidecar)) { try { p = SpicaProject.Load(sidecar); } catch { p = null; } }
                if (p == null)
                {
                    p = rs.Record.Clone();
                    p.FilePath = null;      // the campaign's record is not the user's setup file: Save asks where
                    p.Seed = 0;
                    p.Dirty = false;
                }
                await LoadSchematic(asc, false, p);
            }
            if (SameFile(ascPath, asc)) rs.Setup = project;
            liveJob = null;
            resultsView.SetData(rs, false);
            tabs.SelectedIndex = TabResults;
            SetStatus(rs.Standalone ? $"Opened results: {dir} (analysed with the campaign's own setup: its schematic is not open)"
                                    : $"Opened results: {dir}, analysed with the setup {(project.FilePath != null ? Path.GetFileName(project.FilePath) : "(unsaved)")}");
        }

        static bool SameFile(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>Results opened from disk follow the setup: the open one for their schematic, else their own record.</summary>
        void AttachResults()
        {
            var rs = resultsView.Data;
            if (rs == null || resultsView.Live) return;
            var want = SameFile(ascPath, rs.Record.Schematic) ? project : rs.Record;
            if (ReferenceEquals(rs.Setup, want)) return;
            rs.Setup = want;
            resultsView.SetData(rs, false);
        }

        void OpenResults()
        {
            string start = engine?.Options.OutputDir ?? (ascPath != null ? txtOut.Text : "");
            using (var d = new FolderBrowserDialog { Description = "Choose a campaign folder (the one containing results.csv):", SelectedPath = Directory.Exists(start ?? "") ? start : "" })
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

                // a campaign of this session's queue: shown live while it runs
                var job = queue.Jobs.FirstOrDefault(j => j.Pending && SameFile(j.OutputDir, dir));
                if (job != null) { ShowJobResults(job); return; }

                // an unfinished campaign (stopped, crashed or closed) can be completed
                var state = CampaignState.Load(dir);
                if (state != null && state.Unfinished)
                {
                    int c = Win98MessageBox.Choose(this,
                        $"This campaign is unfinished: {state.Done:N0} of {state.Jobs:N0} simulations are done.\n\n" +
                        "Resume:  put it back in the queue to simulate only the missing runs, with the same setup and seed.\n" +
                        "Analyze:  open the finished runs as they are.",
                        "Unfinished Campaign", Win98Icon.Question, "&Resume", "&Analyze", "Cancel");
                    if (c == 2 || c < 0) return;
                    if (c == 0) { ResumeCampaign(dir, state); return; }
                }
                OpenResultsFolder(dir);
            }
        }

        async void ResumeCampaign(string dir, CampaignState state)
        {
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
            AddToQueue(dir, done, state.ElapsedSeconds);
        }

        // =====================================================================================
        //  Setup (.spica)
        // =====================================================================================

        void MarkDirty()
        {
            if (loadingUi) return;
            project.Dirty = true;
            UpdateTitle();
            UpdatePlan();
        }

        /// <summary>Version without the build metadata the SDK appends ("0.2.0+abc123" -> "0.2.0").</summary>
        public static string AppVersion =>
            (typeof(MainForm).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
             ?? typeof(MainForm).Assembly.GetName().Version.ToString()).Split('+')[0];

        public static string AppTitle => $"SPICA {AppVersion} - Spice Parallel Instancer Circuit Analysis";

        void UpdateTitle()
        {
            string name = ascPath != null ? Path.GetFileName(ascPath) : null;
            string setup = project.FilePath != null ? Path.GetFileName(project.FilePath) : null;
            Text = AppTitle + (name != null ? " - " + name : "") + (setup != null ? $"  [{setup}{(project.Dirty ? "*" : "")}]" : project.Dirty && name != null ? "  [unsaved setup]" : "");
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
            if (!ConfirmDiscardSetup()) return;
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
            var r = Win98MessageBox.Show(this, "Save the changes to the setup" + (project.FilePath != null ? " " + Path.GetFileName(project.FilePath) : "") + "?\n(runs, temperatures, signals, spec limits, TF, error budget, report)",
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
            if (string.IsNullOrEmpty(lt) || !File.Exists(lt)) settings.LtspicePath = Ltspice.Locate() ?? "";
            project = DefaultProject(null);
            project.Schematic = "";
            WriteProjectToUi();
            project.Dirty = false;

            // any edit on the Setup tab makes the setup "unsaved"
            foreach (var t in new[] { txtRuns, txtSeed, txtCorners, txtTempMin, txtTempMax, txtSpread, txtYield, txtMinRuns, txtWatch, txtWcSigma }) t.Box.TextChanged += (s, e) => MarkDirty();
            foreach (var c in new[] { chkReseed, chkKeepRaw, chkPaired, chkStop, chkAutoReport }) c.CheckedChanged += (s, e) => MarkDirty();
            rbWc.CheckedChanged += (s, e) => MarkDirty();
            cbWc.SelectedIndexChanged += (s, e) => MarkDirty();
            txtYield.Box.TextChanged += (s, e) => UpdateAnalysisUi();
            rbTempRandom.CheckedChanged += (s, e) => UpdateAnalysisUi();
            rbSaveSel.CheckedChanged += (s, e) => MarkDirty();

            UpdatePcInfo();
            UpdateSignalCount();
            UpdateTitle();
            UpdatePlan();
        }

        void SaveSettings()
        {
            // the last-used values that seed a new setup (the options are saved by their own window)
            if (int.TryParse(txtRuns.Text, out var runs)) settings.Runs = runs;
            settings.ClockReseed = chkReseed.Checked;
            settings.KeepRaw = chkKeepRaw.Checked;
            settings.SaveSelectedOnly = rbSaveSel.Checked;
            if (ascPath != null && netlist != null)
                settings.SetSaveSelection(ascPath, allSignals.Where(s => !s.Locked && checkedSignals.Contains(s.Signal)).Select(s => s.Signal));
            settings.Save();
        }

        /// <summary>The instances and threads set by hand in the options; null = automatic.</summary>
        RunScheduler.Config? ManualConfig() =>
            settings.AutoParallel ? (RunScheduler.Config?)null
                                  : new RunScheduler.Config(Math.Max(1, settings.ManualInstances > 0 ? settings.ManualInstances : hw.PhysicalCores), Math.Max(1, settings.ManualThreads));

        string ltspiceVersion = "";

        /// <summary>What the options say about this PC, where the window shows it (status bar, Setup tab).</summary>
        void UpdatePcInfo()
        {
            var lt = settings.LtspicePath ?? "";
            bool ok = File.Exists(lt);
            ltspiceVersion = "";
            if (ok) { try { ltspiceVersion = Ltspice.Version(lt); } catch { } }
            status.SetText(PaneLtspice, ok ? "LTspice " + ltspiceVersion : "LTspice not found");
            if (lblPcRuns == null) return;
            var manual = ManualConfig();
            lblPcRuns.Text = manual == null
                ? $"Automatic: one LTspice per core ({hw.PhysicalCores} instances), fewer if RAM is short"
                : $"Manual: {manual.Value.Instances} instance{(manual.Value.Instances == 1 ? "" : "s")} x {manual.Value.Threads} thread{(manual.Value.Threads == 1 ? "" : "s")} each";
            lblPcLtspice.ForeColor = ok ? Win98.Shadow : Color.FromArgb(160, 0, 0);
            lblPcLtspice.Text = ok ? $"LTspice {ltspiceVersion}  -  {lt}" : "LTspice was not found: set its location in the options";
        }

        void ShowOptions()
        {
            using (var f = new OptionsForm(settings, hw))
                if (f.ShowDialog(this) != DialogResult.OK) return;
            UpdatePcInfo();
            UpdatePlan();
        }

        void ShowListener()
        {
            if (listenerForm == null || listenerForm.IsDisposed)
            {
                listenerForm = new ListenerForm(listener, settings, en =>
                {
                    Activate();
                    if (en.Job != null && en.Job.Status == JobStatus.Running) ShowJobResults(en.Job);
                    else if (en.Results.Length > 0) OpenResultsFolder(en.Results);
                });
                listenerForm.Show(this);
            }
            else
            {
                if (listenerForm.WindowState == FormWindowState.Minimized) listenerForm.WindowState = FormWindowState.Normal;
                listenerForm.Activate();
            }
        }

        // =====================================================================================
        //  Schematic loading
        // =====================================================================================

        void BrowseSchematic()
        {
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
            string lt = settings.LtspicePath ?? "";
            if (!File.Exists(lt))
            {
                Win98MessageBox.Show(this, "LTspice.exe was not found. Set its location in Tools > Options first.", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
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
            SetBusy(true);
            try
            {
                int redirected = 0;
                string hash = "";
                var nl = await Task.Run(() =>
                {
                    var parsed = SchematicLoader.Load(lt, path, out redirected);   // unzipped from a campaign's source bundle: uses its libraries
                    hash = parsed.CircuitHash();
                    return parsed;
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
                circuitHash = hash;
                ascPath = path;
                ascStamp = File.GetLastWriteTimeUtc(path);
                txtAsc.Text = path;
                if (!reload)
                    txtOut.Text = CampaignBuilder.DefaultRoot(path);
                settings.PushRecent(withSetup?.FilePath ?? path);

                // signal list: .meas signals locked first, then nodes, then currents
                allSignals.Clear();
                checkedSignals.Clear();
                var req = new HashSet<string>(nl.RequiredSignals, StringComparer.OrdinalIgnoreCase);
                foreach (var s in nl.RequiredSignals) allSignals.Add(new SignalItem { Signal = s, Locked = true });
                foreach (var n in nl.Nodes) { var s = $"V({n})"; if (!req.Contains(s)) allSignals.Add(new SignalItem { Signal = s }); }
                foreach (var c in nl.Currents) if (!req.Contains(c)) allSignals.Add(new SignalItem { Signal = c });

                siteCount = McPlan.Build(nl.Lines, nl.SourcePath).Sites.Count;
                bool wasDirty = project.Dirty;
                var remembered = project.Signals.Count > 0 || project.FilePath != null ? project.Signals : nl.ExistingSaves;
                foreach (var s in remembered) if (allSignals.Any(a => a.Signal.Equals(s, StringComparison.OrdinalIgnoreCase))) checkedSignals.Add(s);
                WriteProjectToUi();
                FillSignalList();
                project.Dirty = wasDirty;

                ShowCircuit(nl);
                string from = withSetup != null ? $" with setup {Path.GetFileName(withSetup.FilePath)}" :
                              !reload && project.FilePath != null ? $" with its setup {Path.GetFileName(project.FilePath)}" : "";
                SetStatus($"Loaded {Path.GetFileName(path)}{from}: {nl.Nodes.Count} nets, {nl.MeasNames.Count} measurements" +
                          (redirected > 0 ? $"; {redirected} library reference{(redirected == 1 ? "" : "s")} taken from the source bundle beside it." : "."));
                SaveSettings();
                UpdateTitle();
                AttachResults();
            }
            catch (Exception ex)
            {
                SetStatus("Ready");
                Win98MessageBox.Show(this, "Could not read the schematic.\n\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
            }
            finally
            {
                SetBusy(false);
                UseWaitCursor = false;
                UpdateCommandState();
                UpdatePlan();
            }
        }

        /// <summary>
        /// Blocks input while a file loads. Disables the menu, toolbar and tabs, not the form itself:
        /// disabling the active top-level window makes Windows activate another one, which sent SPICA to the background.
        /// </summary>
        void SetBusy(bool busy)
        {
            menu.Enabled = toolbar.Enabled = tabs.Enabled = !busy;
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

            var plan = McPlan.Build(nl.Lines, nl.SourcePath);
            sb.AppendLine($"Random values ({plan.Sites.Count}):");
            foreach (var s in plan.Sites) sb.AppendLine($"  {s.Label}: {s.Original}");
            if (plan.Inlined.Count > 0) sb.AppendLine($"  via .func: {string.Join(", ", plan.Inlined)}");
            foreach (var t in plan.TakenOver) sb.AppendLine("  drawn by SPICA: " + t);
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
        //  Commands
        // =====================================================================================

        bool Running => queue.Running != null;

        /// <summary>The listener is on: this PC runs the campaigns put in its folder, and takes no others.</summary>
        bool Slave => listener.Active;

        void UpdateCommandState()
        {
            if (tbAdd == null || resultsView == null || btnReload == null) return;
            bool has = netlist != null, slave = Slave;
            // a PC that listens runs what others send: nothing is opened or started by hand there
            tbAdd.Enabled = miAdd.Enabled = tbRemote.Enabled = miRemote.Enabled = has && !slave;
            tbOpen.Enabled = miOpen.Enabled = miRecent.Enabled = !slave;
            tbSave.Enabled = miSave.Enabled = miSaveAs.Enabled = has;
            btnReload.Enabled = has && !slave;
            tbStop.Enabled = miStop.Enabled = Running;
            // Export / Report: the results shown, or the campaign on the monitor once it has finished runs
            bool can = resultsView.CanExport || (engine != null && engine.Snapshot().Done > 0);
            miExport.Enabled = miReport.Enabled = tbReport.Enabled = can;
            tbFolder.Enabled = miFolder.Enabled = Directory.Exists(CampaignFolder() ?? "");
            UpdateQueueButtons();
        }

        void SetStatus(string s) => status.SetText(PaneText, s);

        /// <summary>The folder of the results shown, else of the campaign on the monitor, else the output root.</summary>
        string CampaignFolder()
        {
            string dir = resultsView.Data?.Folder;
            if (Directory.Exists(dir ?? "")) return dir;
            dir = engine?.Options.OutputDir;
            if (Directory.Exists(dir ?? "")) return dir;
            dir = txtOut.Text.Trim();
            return dir.Length > 0 && Directory.Exists(dir) ? dir : null;
        }

        void OpenOutputFolder()
        {
            string dir = CampaignFolder();
            if (dir != null) Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        /// <summary>A campaign's results are built on demand while it runs; a folder opened from disk is used as it is.</summary>
        void CatchUpResults()
        {
            if (resultsView.Data == null && liveJob == null && engine != null) liveJob = shownJob;
            var e = LiveEngine;
            if (e != null && (resultsView.Data == null || !e.IsFinished)) RefreshResults();
        }

        void ExportResults() { CatchUpResults(); resultsView.ExportCsv(); }
        void ExportReport() { CatchUpResults(); resultsView.ExportReport(); }

        void ShowOpAmps()
        {
            string dir = null;
            try { if (File.Exists(ascPath ?? "")) dir = Path.GetDirectoryName(Path.GetFullPath(ascPath)); } catch { }
            using (var f = new OpAmpForm(dir)) f.ShowDialog(this);
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
            lines.AppendLine($"Automatic mode runs {hw.PhysicalCores} instances x 1 thread (one per physical core),");
            lines.AppendLine("fewer if free RAM is short, with chunks of about 2 minutes of simulation.");
            lines.AppendLine("Change it in Tools > Options.");
            Win98MessageBox.Show(this, lines.ToString().Replace("\t", "  "), "Hardware Information", MessageBoxButtons.OK, Win98Icon.Information);
        }

        void ForgetProfiles()
        {
            try
            {
                var path = KeyValueFile.Resolve("SPICA.profiles");
                if (File.Exists(path)) File.Delete(path);
                Win98MessageBox.Show(this, "Saved run times were deleted.\nThe next campaign measures them again.", "SPICA");
            }
            catch (Exception ex) { Win98MessageBox.Show(this, ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); }
        }

        void ShowAbout()
        {
            Win98MessageBox.Show(this,
                "SPICA  version " + AppVersion + "\n" +
                "Spice Parallel Instancer Circuit Analysis\n\n" +
                "Runs LTspice Monte Carlo campaigns as many parallel batch\n" +
                "instances, one per processor core.\n\n" +
                "Copyright © 2026 10k10.\n" +
                "Free to use, copy, modify and share, at home or at work;\n" +
                "it may not be sold (Apache 2.0 with the Commons Clause).\n\n" +
                "Icons: alexh/vintage-icons (MIT).\n" +
                "LTspice is a trademark of Analog Devices, Inc.",
                "About SPICA", MessageBoxButtons.OK, Win98Icon.Information);
        }

        // =====================================================================================
        //  Closing
        // =====================================================================================

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ConfirmDiscardSetup()) { e.Cancel = true; return; }

            int waiting = queue.QueuedCount;
            if (Running || waiting > 0)
            {
                string what = Running
                    ? "A campaign is running" + (waiting > 0 ? $" and {waiting} more {(waiting == 1 ? "is" : "are")} waiting in the queue" : "")
                    : $"{waiting} campaign{(waiting == 1 ? " is" : "s are")} waiting in the queue";
                string kept = Running ? "The finished runs are saved; you can resume the campaign later from Results > Open Results." : "";
                if (waiting > 0) kept += (kept.Length > 0 ? "\n" : "") + "The waiting campaigns are not kept: add them again next time.";
                if (Win98MessageBox.Show(this, what + ". Stop and exit?\n" + kept, "SPICA", MessageBoxButtons.YesNo, Win98Icon.Warning) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                closing = true;
                queue.Hold = true;                 // nothing new starts while the running one stops
                var run = queue.Running?.Engine;
                if (run != null)
                {
                    run.Cancel();
                    var sw = Stopwatch.StartNew();
                    while (!run.IsFinished && sw.ElapsedMilliseconds < 8000) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                }
            }
            closing = true;
            uiTimer.Stop();
            listener.Stop();
            SaveSettings();
            base.OnFormClosing(e);
        }
    }
}
