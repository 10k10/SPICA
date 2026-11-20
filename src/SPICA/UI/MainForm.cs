using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Spica.Core;
using Spica.Core.Graph;

namespace Spica.UI
{
    /// <summary>
    /// The main window: the Workbench (the studies, as graphs of blocks), the Queue (the campaigns they run) and the
    /// Results (their waveforms). This file holds the frame, the project file and the commands; MainForm.Studies.cs,
    /// MainForm.StudyRun.cs and MainForm.Queue.cs hold the rest.
    /// </summary>
    public partial class MainForm : Win98Form
    {
        public string StartupFile;

        readonly AppSettings settings = new AppSettings();
        readonly ProfileStore profiles = new ProfileStore();
        readonly HardwareInfo hw = HardwareInfo.Detect();
        readonly CampaignQueue queue;

        SpicaProject project = new SpicaProject();
        Netlist netlist;                           // of the project's schematic, once LTspice has converted it
        string ascPath;                            // the schematic that netlist is of
        bool closing;

        // chrome
        MenuStrip menu;
        ToolStrip toolbar;
        Win98StatusBar status;
        Win98TabControl tabs;
        Win98TabPage pageStudies, pageQueue, pageResults;
        ToolStripButton tbSave, tbRun, tbStop, tbFolder;
        ToolStripMenuItem mFile, miSave, miSaveAs, miRun, miStop, miRename, miDelete;
        ToolStripSeparator recentSep;

        const int TabStudies = 0, TabQueue = 1, TabResults = 2;
        const int PaneText = 0, PaneLtspice = 1, PaneClock = 3;

        readonly Timer uiTimer = new Timer { Interval = 250 };

        public MainForm()
        {
            Text = AppTitle;
            MinimumSize = new Size(1000, 720);
            PlaceWindow();
            CaptionIcon = Win98.Icon("app16");
            KeyPreview = true;

            queue = new CampaignQueue(hw, profiles, Post);
            queue.Started += OnJobStarted;
            queue.Finished += OnJobFinished;
            queue.Discarded += OnJobDiscarded;
            // a study's campaigns tell the study when they end, or leave the queue
            queue.Finished += OnStudyJobEnded;
            queue.Removed += OnStudyJobEnded;
            queue.Discarded += (j, gone) => OnStudyJobEnded(j);
            queue.Changed += () => { UpdateQueueList(); UpdateCommandState(); };

            BuildChrome();
            BuildStudiesPage();
            BuildQueuePage();
            BuildResultsPage();

            Controls.Add(tabs);
            Controls.Add(status);
            Controls.Add(toolbar);
            Controls.Add(menu);
            MainMenuStrip = menu;

            // a schematic or a project dropped on the window opens
            AllowDrop = true;
            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) BeginInvoke((Action)(() => OpenFile(files[0])));
            };

            uiTimer.Tick += (s, e) => UpdateUi();
            uiTimer.Start();
            if (!File.Exists(settings.LtspicePath ?? "")) settings.LtspicePath = Ltspice.Locate() ?? "";
            UpdateLtspiceInfo();
            UpdateTitle();
            ShowStudies();
        }

        /// <summary>Runs something on the window's thread later (the engines report from worker threads).</summary>
        void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!File.Exists(settings.LtspicePath ?? ""))
            {
                Win98MessageBox.Show(this, "LTspice was not found in the usual places.\nPoint SPICA at LTspice.exe in the options that open next.",
                    "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                ShowOptions();
            }
            if (!string.IsNullOrEmpty(StartupFile)) OpenFile(StartupFile);
        }

        // =====================================================================================
        //  Menus / toolbar / status bar
        // =====================================================================================

        void BuildChrome()
        {
            menu = new MenuStrip { Dock = DockStyle.Top, Padding = new Padding(2, 1, 0, 1) };
            Win98Renderer.Apply(menu);

            mFile = new ToolStripMenuItem("&File");
            var miOpen = new ToolStripMenuItem("&Open Schematic or Project...", Win98.Icon("open16"), (s, e) => BrowseOpen()) { ShortcutKeys = Keys.Control | Keys.O };
            miSave = new ToolStripMenuItem("&Save Project", Win98.Icon("save16"), (s, e) => SaveProject(false)) { ShortcutKeys = Keys.Control | Keys.S };
            miSaveAs = new ToolStripMenuItem("Save Project &As...", null, (s, e) => SaveProject(true));
            // the recent files are listed in File itself, numbered, above Exit, as Windows 98 programs did (a Recent
            // submenu made WinForms open File away from its name)
            var miExit = new ToolStripMenuItem("E&xit", null, (s, e) => Close());
            recentSep = new ToolStripSeparator();
            mFile.DropDownItems.AddRange(new ToolStripItem[] { miOpen, miSave, miSaveAs, recentSep, new ToolStripSeparator(), miExit });
            mFile.DropDownOpening += (s, e) => FillRecent();

            var mStudy = new ToolStripMenuItem("&Study");
            var miNew = new ToolStripMenuItem("&New Study...", Win98.Icon("doc16"), (s, e) => NewStudy()) { ShortcutKeys = Keys.Control | Keys.N };
            miRename = new ToolStripMenuItem("Re&name Study...", Glyphs.Pencil(), (s, e) => RenameStudy());
            miDelete = new ToolStripMenuItem("&Delete Study", Glyphs.Cross(), (s, e) => DeleteStudy());
            miRun = new ToolStripMenuItem("&Run", Win98.Icon("run16"), (s, e) => RunStudy()) { ShortcutKeys = Keys.F5 };
            miStop = new ToolStripMenuItem("S&top", Win98.Icon("stop16"), (s, e) => StopStudy()) { ShortcutKeys = Keys.Shift | Keys.F5 };
            mStudy.DropDownItems.AddRange(new ToolStripItem[] { miNew, miRename, miDelete, new ToolStripSeparator(), miRun, miStop });

            var mView = new ToolStripMenuItem("&View");
            mView.DropDownItems.Add(new ToolStripMenuItem("&Workbench", Win98.Icon("chart16"), (s, e) => tabs.SelectedIndex = TabStudies) { ShortcutKeys = Keys.Control | Keys.D1 });
            mView.DropDownItems.Add(new ToolStripMenuItem("&Queue", Win98.Icon("queue16"), (s, e) => tabs.SelectedIndex = TabQueue) { ShortcutKeys = Keys.Control | Keys.D2 });
            mView.DropDownItems.Add(new ToolStripMenuItem("&Results", Win98.Icon("results16"), (s, e) => tabs.SelectedIndex = TabResults) { ShortcutKeys = Keys.Control | Keys.D3 });
            mView.DropDownItems.Add(new ToolStripSeparator());
            mView.DropDownItems.Add(new ToolStripMenuItem("&Fit the Study", null, (s, e) => { tabs.SelectedIndex = TabStudies; studyCanvas.FitAll(); }) { ShortcutKeyDisplayString = "Space" });

            var mTools = new ToolStripMenuItem("&Tools");
            mTools.DropDownItems.Add(new ToolStripMenuItem("&Options...", Win98.Icon("options16"), (s, e) => ShowOptions()));
            mTools.DropDownItems.Add(new ToolStripSeparator());
            mTools.DropDownItems.Add(new ToolStripMenuItem("&Hardware Information...", Win98.Icon("hw16"), (s, e) => ShowHardware()));
            mTools.DropDownItems.Add(new ToolStripMenuItem("&Forget Saved Run Times", null, (s, e) => ForgetProfiles()));

            var mHelp = new ToolStripMenuItem("&Help");
            mHelp.DropDownItems.Add(new ToolStripMenuItem("&About SPICA...", Win98.Icon("help16"), (s, e) => ShowAbout()));

            menu.Items.AddRange(new ToolStripItem[] { mFile, mStudy, mView, mTools, mHelp });
            Win98Renderer.StyleDropDowns(menu.Items);

            // the toolbar: the project, the study's run, its results, this PC
            toolbar = new ToolStrip { Dock = DockStyle.Top, ImageScalingSize = new Size(32, 32), Padding = new Padding(4, 2, 4, 2), AutoSize = true };
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
            Btn("Open", "open32", (s, e) => BrowseOpen(), "Open an LTspice schematic (.asc) or a SPICA project (.spica)  (Ctrl+O)");
            tbSave = Btn("Save", "save32", (s, e) => SaveProject(false), "Save the project (.spica): its studies  (Ctrl+S)");
            toolbar.Items.Add(new ToolStripSeparator());
            tbRun = Btn("Run", "run32", (s, e) => RunStudy(), "Run the study shown on the Workbench: its campaigns go into the queue  (F5)");
            tbStop = Btn("Stop", "stop32", (s, e) => StopStudy(), "Stop the study that runs  (Shift+F5)");
            toolbar.Items.Add(new ToolStripSeparator());
            tbFolder = Btn("Folder", "folder32", (s, e) => OpenStudyFolder(), "Open the folder of the study's campaigns");
            toolbar.Items.Add(new ToolStripSeparator());
            Btn("Options", "options32", (s, e) => ShowOptions(), "Where LTspice is, how many instances run on this PC, where the results go");
            Btn("Help", "help32", (s, e) => ShowAbout(), "About SPICA");

            status = new Win98StatusBar();
            status.Add("Ready", 0);
            status.Add("LTspice ?", 130);
            status.Add($"{hw.PhysicalCores}C/{hw.LogicalProcessors}T  {hw.TotalMemory / (double)(1L << 30):0.0} GB", 150, Win98.Icon("hw16"));
            status.Add(DateTime.Now.ToString("HH:mm"), 56);

            tabs = new Win98TabControl { Dock = DockStyle.Fill };
            pageStudies = new Win98TabPage("Workbench", Win98.Icon("chart16"));
            pageQueue = new Win98TabPage("Queue", Win98.Icon("queue16"));
            pageResults = new Win98TabPage("Results", Win98.Icon("results16"));
            tabs.AddPage(pageStudies);
            tabs.AddPage(pageQueue);
            tabs.AddPage(pageResults);
            tabs.SelectedIndexChanged += (s, e) => UpdateCommandState();
        }

        /// <summary>The recent files between Save As and Exit (none: no list, one separator).</summary>
        void FillRecent()
        {
            var items = mFile.DropDownItems;
            int at = items.IndexOf(recentSep) + 1;
            while (items[at] is ToolStripMenuItem) items.RemoveAt(at);
            int i = 1;
            foreach (var r in settings.RecentFiles)
            {
                string path = r;
                items.Insert(at++, new ToolStripMenuItem($"&{i++} {Shorten(path, 60)}", null, (s, e) => OpenFile(path)));
            }
            recentSep.Visible = i > 1;
        }

        static string Shorten(string p, int max) => p.Length <= max ? p : "..." + p.Substring(p.Length - max + 3);

        static Panel Gap(DockStyle d, int size = 6) => new Panel { Dock = d, Height = size, Width = size, BackColor = Win98.Face };

        static bool SameFile(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // =====================================================================================
        //  The project (.spica)
        // =====================================================================================

        /// <summary>Version without the build metadata the SDK appends ("0.2.0+abc123" -> "0.2.0").</summary>
        public static string AppVersion =>
            (typeof(MainForm).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
             ?? typeof(MainForm).Assembly.GetName().Version.ToString()).Split('+')[0];

        public static string AppTitle => $"SPICA {AppVersion} - Simulation Platform for Investigation, Compliance and Analysis";

        void MarkDirty()
        {
            project.Dirty = true;
            UpdateTitle();
            UpdateCommandState();
        }

        void UpdateTitle()
        {
            string name = project.FilePath != null ? Path.GetFileName(project.FilePath)
                        : project.Schematic.Length > 0 ? Path.GetFileName(project.Schematic) + " (project not saved)" : null;
            Text = AppTitle + (name != null ? " - " + name + (project.Dirty && project.FilePath != null ? "*" : "") : "");
        }

        static string SidecarOf(string asc) => Path.Combine(Path.GetDirectoryName(asc), Path.GetFileNameWithoutExtension(asc) + ".spica");

        void BrowseOpen()
        {
            using (var d = new OpenFileDialog
            {
                Title = "Open Schematic or Project",
                Filter = "Schematics and projects (*.asc;*.spica)|*.asc;*.spica|LTspice schematics (*.asc)|*.asc|SPICA projects (*.spica)|*.spica|All files (*.*)|*.*",
                AutoUpgradeEnabled = false,
                InitialDirectory = project.Schematic.Length > 0 ? Path.GetDirectoryName(project.Schematic) : "",
            })
            {
                if (d.ShowDialog(this) == DialogResult.OK) OpenFile(d.FileName);
            }
        }

        /// <summary>
        /// Opens a project (.spica), or a schematic (.asc): with the project saved beside it when there is one, else as a
        /// new project with one study ready to run (the schematic, nominal, simulated).
        /// </summary>
        void OpenFile(string path)
        {
            if (!File.Exists(path)) { Win98MessageBox.Show(this, "File not found:\n" + path, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); return; }
            if (StudyRunning) { Win98MessageBox.Show(this, "A study is running: stop it before opening another project.", "SPICA", MessageBoxButtons.OK, Win98Icon.Information); return; }
            if (!ConfirmDiscard()) return;
            bool isProject = path.EndsWith(".spica", StringComparison.OrdinalIgnoreCase);
            string spica = isProject ? path : File.Exists(SidecarOf(path)) ? SidecarOf(path) : null;
            SpicaProject p;
            try { p = spica != null ? SpicaProject.Load(spica) : new SpicaProject { Schematic = path }; }
            catch (Exception ex) { Win98MessageBox.Show(this, "Could not read the project:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); return; }
            if (!isProject && !SameFile(p.Schematic, path)) p.Schematic = path;
            if (p.Studies.Count == 0 && p.Schematic.Length > 0) p.Studies.Add(DefaultStudy(p.Schematic));
            p.Dirty = false;
            project = p;
            netlist = null;
            ascPath = null;
            settings.PushRecent(spica ?? path);
            settings.Save();
            UpdateTitle();
            ShowStudies();
            LoadRuns();
            UpdateCommandState();
            if (isProject && p.Schematic.Length > 0 && !File.Exists(p.Schematic))
                Win98MessageBox.Show(this, "The schematic of this project was not found:\n" + p.Schematic + "\n\nChoose it in its Schematic block.", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
            if (File.Exists(p.Schematic)) _ = LoadNetlist(p.Schematic);
            SetStatus($"Opened {Path.GetFileName(spica ?? path)}: {p.Studies.Count} stud{(p.Studies.Count == 1 ? "y" : "ies")}.");
        }

        /// <summary>A study that runs as it stands: the schematic, every tolerance nominal, simulated.</summary>
        static Study DefaultStudy(string asc)
        {
            var st = new Study { Name = Path.GetFileNameWithoutExtension(asc) };
            var sch = st.Add(BlockKind.Schematic, 40, 40).Set("File", asc);
            var nom = st.Add(BlockKind.Tolerances, 40, 170);
            var sim = st.Add(BlockKind.Simulate, 320, 100);
            st.Connect(sch, sim);
            st.Connect(nom, sim);
            return st;
        }

        /// <summary>The project's schematic, converted by LTspice: its nets and measurements for the blocks' settings.</summary>
        async Task LoadNetlist(string path)
        {
            string lt = settings.LtspicePath ?? "";
            if (!File.Exists(lt)) return;
            // the window stays usable meanwhile: only what needs the nets (the Save list, the worst-case count) waits for it
            SetStatus("LTspice is reading " + Path.GetFileName(path) + "...");
            try
            {
                var nl = await Task.Run(() => SchematicLoader.Load(lt, path));
                if (!SameFile(project.Schematic, path)) return;       // another project opened meanwhile
                netlist = nl;
                ascPath = path;
                SetStatus($"Loaded {Path.GetFileName(path)}: {nl.Nodes.Count} nets, {nl.MeasNames.Count} measurements.");
                ShowInspector(studyCanvas.Selected);
                studyCanvas.Invalidate();
            }
            catch (Exception ex)
            {
                SetStatus("Ready");
                Win98MessageBox.Show(this, "LTspice could not read the schematic.\n\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
            }
        }

        bool SaveProject(bool saveAs)
        {
            if (project.Studies.Count == 0 && project.Schematic.Length == 0) { Win98MessageBox.Show(this, "Open a schematic first.", "SPICA", MessageBoxButtons.OK, Win98Icon.Information); return false; }
            string path = project.FilePath;
            if (saveAs || path == null)
            {
                using (var d = new SaveFileDialog
                {
                    Title = "Save Project",
                    Filter = "SPICA project (*.spica)|*.spica",
                    FileName = Path.GetFileName(path ?? (project.Schematic.Length > 0 ? SidecarOf(project.Schematic) : "project.spica")),
                    InitialDirectory = Path.GetDirectoryName(path ?? (project.Schematic.Length > 0 ? project.Schematic : Application.StartupPath)),
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
                settings.Save();
                SetStatus("Project saved: " + path);
                UpdateTitle();
                UpdateCommandState();
                return true;
            }
            catch (Exception ex)
            {
                Win98MessageBox.Show(this, "Could not save the project:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
                return false;
            }
        }

        /// <summary>Asks to save an edited project. False = the user cancelled.</summary>
        bool ConfirmDiscard()
        {
            if (!project.Dirty) return true;
            var r = Win98MessageBox.Show(this, "Save the changes to the project" + (project.FilePath != null ? " " + Path.GetFileName(project.FilePath) : "") + "?",
                "SPICA", MessageBoxButtons.YesNoCancel, Win98Icon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return SaveProject(false);
            return true;
        }

        /// <summary>Where the studies put their campaigns: the folder of the options, else &lt;name&gt;_SPICA beside the project or the schematic; null when the project has neither.</summary>
        string ResultsRoot()
        {
            if (settings.ResultsFolder.Trim().Length > 0) return settings.ResultsFolder.Trim();
            string basis = project.FilePath ?? (project.Schematic.Length > 0 ? project.Schematic : null);
            return basis == null ? null : Path.Combine(Path.GetDirectoryName(basis), Path.GetFileNameWithoutExtension(basis) + "_SPICA");
        }

        string StudyFolder(Study st) => ResultsRoot() is string root ? Path.Combine(root, "studies", StudyExecutors.Short(st.Name, 20)) : null;

        void OpenStudyFolder()
        {
            string dir = CurrentStudy == null ? null : StudyFolder(CurrentStudy);
            if (Directory.Exists(dir ?? "")) Process.Start("explorer.exe", "\"" + dir + "\"");
            else Win98MessageBox.Show(this, "This study has not run yet: its folder is made when it runs.", "SPICA", MessageBoxButtons.OK, Win98Icon.Information);
        }

        // =====================================================================================
        //  Window and options
        // =====================================================================================

        /// <summary>
        /// The window where it was last time, when that is still on a screen; else most of the screen it opens on
        /// (the Workbench needs the room), centred.
        /// </summary>
        void PlaceWindow()
        {
            var p = (settings.WindowBounds ?? "").Split(',');
            if (p.Length == 4 && int.TryParse(p[0], out int x) && int.TryParse(p[1], out int y) && int.TryParse(p[2], out int w) && int.TryParse(p[3], out int h))
            {
                var r = new Rectangle(x, y, Math.Max(w, MinimumSize.Width), Math.Max(h, MinimumSize.Height));
                // at least its caption on some screen, so it can be dragged back
                if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(r.X + 40, r.Y, Math.Max(1, r.Width - 80), 24))))
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = r;
                    if (settings.WindowMaximized) WindowState = FormWindowState.Maximized;
                    return;
                }
            }
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Max(MinimumSize.Width, Math.Min(1500, wa.Width * 92 / 100)), Math.Max(MinimumSize.Height, Math.Min(980, wa.Height * 92 / 100)));
            StartPosition = FormStartPosition.CenterScreen;
        }

        /// <summary>The instances and threads set by hand in the options; null = automatic.</summary>
        RunScheduler.Config? ManualConfig() =>
            settings.AutoParallel ? (RunScheduler.Config?)null
                                  : new RunScheduler.Config(Math.Max(1, settings.ManualInstances > 0 ? settings.ManualInstances : hw.PhysicalCores), Math.Max(1, settings.ManualThreads));

        void UpdateLtspiceInfo()
        {
            var lt = settings.LtspicePath ?? "";
            string v = "";
            if (File.Exists(lt)) { try { v = Ltspice.Version(lt); } catch { } }
            status.SetText(PaneLtspice, File.Exists(lt) ? "LTspice " + v : "LTspice not found");
        }

        void ShowOptions()
        {
            using (var f = new OptionsForm(settings, hw))
                if (f.ShowDialog(this) != DialogResult.OK) return;
            UpdateLtspiceInfo();
            // the schematic could not be read without LTspice: read it now
            if (netlist == null && File.Exists(project.Schematic)) _ = LoadNetlist(project.Schematic);
            UpdateCommandState();
        }

        // =====================================================================================
        //  Commands
        // =====================================================================================

        bool Running => queue.Running != null;

        void UpdateCommandState()
        {
            if (tbRun == null || studyCanvas == null) return;
            bool hasProject = project.Studies.Count > 0 || project.Schematic.Length > 0;
            // Save only when there is something to save (a project never saved can always be)
            tbSave.Enabled = miSave.Enabled = hasProject && (project.Dirty || project.FilePath == null);
            miSaveAs.Enabled = hasProject;
            // Run says what is missing when pressed; it is off only with no study, or one running
            tbRun.Enabled = miRun.Enabled = CurrentStudy != null && !StudyRunning;
            tbStop.Enabled = miStop.Enabled = StudyRunning;
            tbFolder.Enabled = CurrentStudy != null && Directory.Exists(StudyFolder(CurrentStudy) ?? "");
            miRename.Enabled = miDelete.Enabled = CurrentStudy != null;
            UpdateQueueButtons();
        }

        void SetStatus(string s) => status.SetText(PaneText, s);

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
                "Simulation Platform for Investigation, Compliance and Analysis\n\n" +
                "Studies of a circuit on LTspice, drawn as graphs of blocks:\n" +
                "nominal, Monte Carlo and worst case, run as parallel batch\n" +
                "instances, with the waveforms of every run.\n\n" +
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
            if (!ConfirmDiscard()) { e.Cancel = true; return; }
            int waiting = queue.QueuedCount;
            if (Running || waiting > 0)
            {
                string what = Running
                    ? "A campaign is running" + (waiting > 0 ? $" and {waiting} more {(waiting == 1 ? "is" : "are")} waiting in the queue" : "")
                    : $"{waiting} campaign{(waiting == 1 ? " is" : "s are")} waiting in the queue";
                if (Win98MessageBox.Show(this, what + ". Stop and exit?\nWhat finished is kept: running the study again takes it from there.", "SPICA", MessageBoxButtons.YesNo, Win98Icon.Warning) != DialogResult.Yes)
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
            // where the window is, for next time (its normal size when it closes maximized)
            var nb = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            settings.WindowBounds = $"{nb.X},{nb.Y},{nb.Width},{nb.Height}";
            settings.WindowMaximized = WindowState == FormWindowState.Maximized;
            settings.Save();
            base.OnFormClosing(e);
        }
    }
}
