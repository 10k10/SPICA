using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Spica.Core;
using Spica.Core.Graph;

namespace Spica.UI
{
    /// <summary>
    /// Running a study from the window: the blocks run in wire order, Simulate puts its campaigns in this PC's queue,
    /// the study goes on when they end, and the verdicts open in their own window.
    /// </summary>
    public partial class MainForm
    {
        StudyRunner studyRunner;
        StudyHost studyHost;
        readonly List<VerdictRecord> lastVerdicts = new List<VerdictRecord>();
        string lastStudyReport;

        /// <summary>A campaign a study put in the queue (marks it, so its end shows no message box of its own).</summary>
        sealed class StudyJobTag
        {
            public Action<string, string> Done;
        }

        /// <summary>What the blocks need from the window: the setup, netlists, libraries, folders and the queue.</summary>
        sealed class StudyHost : IStudyHost
        {
            readonly MainForm f;
            readonly Dictionary<string, Netlist> netlists = new Dictionary<string, Netlist>(StringComparer.OrdinalIgnoreCase);
            public readonly List<QueueJob> Jobs = new List<QueueJob>();
            public StudyHost(MainForm f) { this.f = f; }

            public SpicaProject Project => f.project;
            public IList<QualLibrary> Libraries => f.qualPages.Select(q => q.Lib).ToList();

            public Netlist NetlistFor(string asc)
            {
                if (f.netlist != null && SameFile(asc, f.ascPath)) return f.netlist;
                string key = Path.GetFullPath(asc);
                if (netlists.TryGetValue(key, out var nl)) return nl;
                string lt = f.settings.LtspicePath ?? "";
                if (!File.Exists(lt)) throw new Exception("LTspice.exe was not found (Tools > Options).");
                f.SetStatus("Reading " + Path.GetFileName(asc) + "...");
                nl = SchematicLoader.Load(lt, key, out _);
                netlists[key] = nl;
                return nl;
            }

            public string StudyFolder(Study study)
            {
                string root = f.txtOut.Text.Trim().Length > 0 ? f.txtOut.Text.Trim() : CampaignBuilder.DefaultRoot(f.ascPath);
                return Path.Combine(root, "studies", StudyExecutors.Short(study.Name, 20));
            }

            public void Submit(string name, CampaignRequest request, Action<string, string> done)
            {
                // the answer always comes later, on the window's thread, so that Simulate counts its campaigns first
                void Later(string dir, string why) => f.BeginInvoke(new Action(() => done(dir, why)));
                request.LtspiceExe = f.settings.LtspicePath ?? "";
                request.Manual = f.ManualConfig();
                var options = CampaignBuilder.Build(request, null, out string error);
                if (options == null) { Later(null, error ?? "could not be prepared"); return; }
                f.addingLocal = true;
                try
                {
                    var job = f.queue.Add(request, options, name: name, tag: new StudyJobTag { Done = done });
                    Jobs.Add(job);
                }
                finally { f.addingLocal = false; }
            }

            public void Wrote(string what, string path)
            {
                if (what == "report") f.lastStudyReport = path;
                f.SetStatus($"Study {what} written: {path}");
            }
        }

        /// <summary>A study campaign ended (or left the queue): tell the Simulate block that waits for it.</summary>
        void OnStudyJobEnded(QueueJob job)
        {
            if (!(job.Tag is StudyJobTag t) || t.Done == null) return;
            var done = t.Done;
            t.Done = null;
            bool ok = job.Status == JobStatus.Finished && job.OutputDir != null && File.Exists(Path.Combine(job.OutputDir, "results.csv"));
            done(ok ? job.OutputDir : null, ok ? null : (job.Message.Length > 0 ? job.Message : job.Status.ToString().ToLowerInvariant()));
        }

        bool StudyRunning => studyRunner != null && studyRunner.Busy;

        /// <summary>Runs the study shown on the Studies tab (Run Here, F5, while that tab is open).</summary>
        void RunStudy()
        {
            var st = CurrentStudy;
            if (st == null) { Win98MessageBox.Show(this, "Choose or make a study first (New, on the Workbench).", "SPICA", MessageBoxButtons.OK, Win98Icon.Information); return; }
            if (StudyRunning) { Win98MessageBox.Show(this, $"The study \"{studyRunner.Study.Name}\" is still running.", "SPICA", MessageBoxButtons.OK, Win98Icon.Information); return; }
            if (ascPath == null) { BrowseSchematic(); return; }
            if (!File.Exists(settings.LtspicePath ?? "")) { Win98MessageBox.Show(this, "LTspice.exe was not found. Set its location in Tools > Options.", "SPICA", MessageBoxButtons.OK, Win98Icon.Error); return; }
            if (!ValidateStudy(false)) { ValidateStudy(true); return; }
            ReadUiIntoProject();

            studyHost = new StudyHost(this);
            studyRunner = StudyExecutors.Register(new StudyRunner(), studyHost);
            var log = new List<string>();
            studyRunner.Log = m => { log.Add(m); SetStatus(m); };
            studyCanvas.States.Clear();
            studyRunner.Changed = (b, s) => { if (CurrentStudy == st) { studyCanvas.States[b.Id] = s; studyCanvas.Invalidate(); } };
            studyRunner.Ended = ok =>
            {
                lastVerdicts.Clear();
                foreach (var r in studyRunner.Results.Values.SelectMany(x => x.Values).OfType<List<VerdictRecord>>()) lastVerdicts.AddRange(r);
                var problems = studyRunner.State.Where(kv => kv.Value == BlockState.Error || kv.Value == BlockState.Skipped)
                                                .Select(kv => $"{st[kv.Key]?.Spec?.Title ?? kv.Key.ToString()} ({kv.Key}): {(studyRunner.Message.TryGetValue(kv.Key, out var m) ? m : kv.Value.ToString())}").ToList();
                int fail = lastVerdicts.Count(v => v.Verdict == "FAIL");
                SetStatus($"Study \"{st.Name}\" " + (ok ? "done" : "ended with problems") + $": {lastVerdicts.Count} verdicts, {fail} FAIL.");
                UpdateCommandState();
                ShowStudyResults(st, problems);
            };
            lastStudyReport = null;
            SetStatus($"Running the study \"{st.Name}\"...");
            bool started;
            try { started = studyRunner.Start(st); }
            catch (Exception ex) { Win98MessageBox.Show(this, "The study could not start:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); return; }
            if (!started) { ValidateStudy(true); return; }
            if (studyRunner.Waiting) tabs.SelectedIndex = TabQueue;
            UpdateCommandState();
        }

        /// <summary>Stops the study that runs: its queued campaigns leave the queue, the running one is stopped as usual.</summary>
        void StopStudy()
        {
            if (!StudyRunning) return;
            if (Win98MessageBox.Show(this, $"Stop the study \"{studyRunner.Study.Name}\"?\nIts campaigns still waiting leave the queue; the one running is stopped.", "SPICA",
                    MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;
            foreach (var j in studyHost.Jobs.Where(j => j.Status == JobStatus.Queued).ToList()) queue.Remove(j);
            var running = studyHost.Jobs.FirstOrDefault(j => j.Status == JobStatus.Running);
            studyRunner.Stop();
            if (running?.Engine != null) running.Engine.Cancel();
            UpdateCommandState();
        }

        // ------------------------------------------------------------------ the Results tab: campaigns and studies

        TreeView tvResults;
        Panel resultsHost;
        StudyVerdictsView verdictsView;
        WaveformView waveView;
        readonly List<StudyRunData> studyRuns = new List<StudyRunData>();
        StudyRunData shownStudy;

        /// <summary>The Results tab's left side: the campaign open, and the studies that ran (Verdicts, Waveforms, Report).</summary>
        void BuildResultsNavigator()
        {
            var left = new Win98GroupBox { Text = "Open", Dock = DockStyle.Left, Width = 200 };
            var images = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            foreach (var n in new[] { "results16", "chart16", "check16", "doc16", "folder16" }) images.Images.Add(n, Win98.Icon(n));
            tvResults = new TreeView { Font = Win98.Font, ImageList = images, BorderStyle = BorderStyle.Fixed3D, HideSelection = false, ShowLines = true, ItemHeight = 18 };
            tvResults.AfterSelect += (s, e) => ShowResultsNode(e.Node);
            left.Controls.Add(tvResults);
            left.Resize += (s, e) => tvResults.SetBounds(8, 20, left.Width - 16, left.Height - 28);

            verdictsView = new StudyVerdictsView { Visible = false };
            verdictsView.OpenWaves = r => ShowStudyWaves(shownStudy, r);
            verdictsView.OpenCampaign = d => { if (Directory.Exists(d)) OpenResultsFolder(d); };
            waveView = new WaveformView { Visible = false, LtspicePath = () => settings.LtspicePath };
            resultsHost = new Panel { Dock = DockStyle.Fill, BackColor = Win98.Face };
            pageResults.Controls.Remove(resultsView);
            resultsView.Dock = DockStyle.Fill;
            resultsHost.Controls.Add(resultsView);
            resultsHost.Controls.Add(verdictsView);
            resultsHost.Controls.Add(waveView);
            pageResults.Controls.Add(resultsHost);
            pageResults.Controls.Add(Gap(DockStyle.Left));
            pageResults.Controls.Add(left);
            resultsView.DataChanged += () => FillResultsTree(null);
            FillResultsTree(null);
        }

        void FillResultsTree(object select)
        {
            if (tvResults == null) return;
            // a refresh keeps what is selected
            if (select == null) select = tvResults.SelectedNode?.Tag;
            tvResults.BeginUpdate();
            tvResults.Nodes.Clear();
            string camp = resultsView.Data == null ? "Campaign (none open)" : "Campaign: " + Path.GetFileName((resultsView.Data.Folder ?? "").TrimEnd('\\', '/'));
            var cn = new TreeNode(camp) { Tag = "campaign", ImageKey = "results16", SelectedImageKey = "results16" };
            tvResults.Nodes.Add(cn);
            TreeNode pick = select == null || (select as string) == "campaign" ? cn : null;
            // its waveforms, when it kept its .raw files
            string cdir = resultsView.Data?.Folder;
            if (cdir != null && RawFile.Chunks(cdir).Count > 0)
            {
                var cw = new TreeNode("Waveforms") { Tag = "campaign-waves", ImageKey = "chart16", SelectedImageKey = "chart16" };
                cn.Nodes.Add(cw);
                cn.Expand();
                if ((select as string) == "campaign-waves") pick = cw;
            }
            foreach (var r in studyRuns)
            {
                var sn = new TreeNode($"Study: {r.Study}  ({r.Ended:HH:mm})") { Tag = r, ImageKey = "folder16", SelectedImageKey = "folder16" };
                var v = new TreeNode($"Verdicts ({r.Verdicts.Count(x => x.Verdict == "FAIL")} FAIL of {r.Verdicts.Count})") { Tag = Tuple.Create(r, "verdicts"), ImageKey = "check16", SelectedImageKey = "check16" };
                var w = new TreeNode("Waveforms") { Tag = Tuple.Create(r, "waves"), ImageKey = "chart16", SelectedImageKey = "chart16" };
                sn.Nodes.Add(v); sn.Nodes.Add(w);
                if (r.Report != null) sn.Nodes.Add(new TreeNode("Report") { Tag = Tuple.Create(r, "report"), ImageKey = "doc16", SelectedImageKey = "doc16" });
                tvResults.Nodes.Add(sn);
                sn.Expand();
                if (select is Tuple<StudyRunData, string> t && t.Item1 == r) pick = t.Item2 == "waves" ? w : v;
            }
            tvResults.EndUpdate();
            if (pick != null && tvResults.SelectedNode != pick) { suppressNode = true; tvResults.SelectedNode = pick; suppressNode = false; }
        }

        bool suppressNode;

        void ShowResultsNode(TreeNode n)
        {
            if (suppressNode || n == null) return;
            if (n.Tag as string == "campaign") { ShowResultsPane(resultsView); return; }
            if (n.Tag as string == "campaign-waves") { ShowCampaignWaves(); return; }
            if (n.Tag is StudyRunData r0) { ShowVerdicts(r0); return; }
            if (n.Tag is Tuple<StudyRunData, string> t)
            {
                if (t.Item2 == "verdicts") ShowVerdicts(t.Item1);
                else if (t.Item2 == "waves") ShowStudyWaves(t.Item1, null);
                else if (t.Item2 == "report" && t.Item1.Report != null && File.Exists(t.Item1.Report)) try { Process.Start(t.Item1.Report); } catch { }
            }
        }

        /// <summary>The campaign page of the Results tab to the front (a campaign was opened or followed).</summary>
        void ShowCampaignResults()
        {
            if (resultsHost == null) return;
            ShowResultsPane(resultsView);
            FillResultsTree("campaign");
        }

        void ShowResultsPane(Control c)
        {
            foreach (Control x in resultsHost.Controls) x.Visible = x == c;
            c.BringToFront();
        }

        void ShowVerdicts(StudyRunData r)
        {
            shownStudy = r;
            verdictsView.ShowVerdicts(r);
            ShowResultsPane(verdictsView);
        }

        /// <summary>The study's waveforms: the case of a verdict ticked (or the first), its stimulus and judged signals in panes.</summary>
        void ShowStudyWaves(StudyRunData r, VerdictRecord focus)
        {
            if (r == null) return;
            shownStudy = r;
            var set = focus?.Run ?? r.Sets.FirstOrDefault(s => s.Dir != null);
            var sigs = new List<string>();
            if (focus != null && focus.Run != null) sigs.AddRange(focus.Run.Criteria.Where(c => c.Name == focus.Criterion).Select(c => c.Signal));
            else if (set != null) sigs.AddRange(set.Criteria.Select(c => c.Signal));
            if (set != null) sigs.AddRange(r.PlotSignals.Select(set.Variant.Map));
            waveView.ShowStudy(r, set, sigs.Distinct(StringComparer.OrdinalIgnoreCase), focus?.Criterion);
            ShowResultsPane(waveView);
            FillResultsTree(Tuple.Create(r, "waves"));
            tabs.SelectedIndex = TabResults;
        }

        /// <summary>The waveforms of the campaign open on the Results tab: its runs, every signal its .raw files kept.</summary>
        void ShowCampaignWaves()
        {
            var rs = resultsView.Data;
            if (rs?.Folder == null) return;
            string name = Path.GetFileName(rs.Folder.TrimEnd('\\', '/'));
            var set = new RunSet
            {
                Variant = new CircuitVariant { Candidate = "", Asc = rs.Record.Schematic ?? "" },
                Condition = new Condition { Name = name, Nominal = rs.Rows.Count <= 1 },
                Dir = rs.Folder,
                Case = new QualCase { Test = new QualTest { Name = name }, Category = "" },
            };
            var data = new StudyRunData { Study = "campaign " + name, Sets = new List<RunSet> { set } };
            waveView.ShowStudy(data, set, new string[0]);
            ShowResultsPane(waveView);
        }

        /// <summary>A study ended: it goes into the Results tab (replacing an earlier run of the same study), its verdicts shown.</summary>
        void ShowStudyResults(Study st, List<string> problems)
        {
            var run = new StudyRunData
            {
                Study = st.Name, Verdicts = lastVerdicts.ToList(), Problems = problems, Report = lastStudyReport,
                Sets = studyRunner == null ? new List<RunSet>() : studyRunner.Results.Values.SelectMany(x => x.Values).OfType<List<RunSet>>().SelectMany(x => x).Distinct().ToList(),
                PlotSignals = st.Blocks.Where(b => b.Kind == BlockKind.Plots).SelectMany(b => b.Get("Signals").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)).ToList(),
            };
            studyRuns.RemoveAll(x => x.Study == st.Name);
            studyRuns.Insert(0, run);
            while (studyRuns.Count > 8) studyRuns.RemoveAt(studyRuns.Count - 1);
            FillResultsTree(Tuple.Create(run, "verdicts"));
            ShowVerdicts(run);
            tabs.SelectedIndex = TabResults;
        }

        /// <summary>The waveforms of the case behind a verdict.</summary>
        void OpenStudyWaves(VerdictRecord r) => ShowStudyWaves(shownStudy ?? studyRuns.FirstOrDefault(), r);
    }

    /// <summary>
    /// Results > a study > Verdicts: for each criterion, a grid of case × condition; the details of the cell chosen;
    /// its waveforms or its campaign on a double-click.
    /// </summary>
    sealed class StudyVerdictsView : Panel
    {
        readonly ComboBox cbEval;
        readonly Win98ListView grid, details;
        readonly Label lblHead;
        readonly Win98Button btnReport, btnWaves, btnCampaign;
        List<VerdictRecord> recs = new List<VerdictRecord>();
        List<string> rows = new List<string>(), cols = new List<string>();
        string report;
        public Action<VerdictRecord> OpenWaves;
        public Action<string> OpenCampaign;

        public StudyVerdictsView()
        {
            Dock = DockStyle.Fill;
            BackColor = Win98.Face;
            var top = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Win98.Face };
            lblHead = new Label { Location = new Point(2, 6), AutoSize = false, Height = 16, Font = Win98.BoldFont, UseMnemonic = false, AutoEllipsis = true };
            top.Controls.Add(new Label { Text = "&Criterion:", Location = new Point(2, 31), AutoSize = true });
            cbEval = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(66, 28), Width = 420, Font = Win98.Font };
            cbEval.SelectedIndexChanged += (s, e) => FillGrid();
            top.Controls.AddRange(new Control[] { lblHead, cbEval });
            top.Resize += (s, e) => lblHead.Width = top.Width - 6;

            grid = new Win98ListView { MultiSelect = false, FullRowSelect = false };
            grid.CellColor = (row, col) =>
            {
                if (col == 0 || row < 0 || row >= grid.Items.Count || col >= grid.Items[row].SubItems.Count) return null;
                string t = grid.Items[row].SubItems[col].Text;
                return t.StartsWith("PASS") ? Color.FromArgb(0, 100, 0) : t.StartsWith("FAIL") ? Color.FromArgb(170, 0, 0) : t.StartsWith("CHECK") ? Color.FromArgb(140, 90, 0) : (Color?)null;
            };
            grid.MouseClick += (s, e) => ShowDetails(CellAt(e.Location));
            grid.MouseDoubleClick += (s, e) => { var r = CellAt(e.Location); if (r != null) OpenWaves?.Invoke(r); };
            var wellGrid = new Win98Well(grid) { Dock = DockStyle.Fill };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 170, BackColor = Win98.Face };
            details = new Win98ListView { MultiSelect = false };
            foreach (var (t, w) in new[] { ("Case", 220), ("Condition", 110), ("Verdict", 60), ("Worst seen", 130), ("Margin", 80), ("Runs failing", 90), ("Rule", 260), ("Requirement", 100) })
                details.Columns.Add(t, w);
            details.DoubleClick += (s, e) => { if (details.SelectedItems.Count == 1 && details.SelectedItems[0].Tag is VerdictRecord r) OpenWaves?.Invoke(r); };
            var wellDet = new Win98Well(details) { Location = new Point(0, 6) };
            btnWaves = new Win98Button { Text = "&Waveforms", Width = 110 };
            btnCampaign = new Win98Button { Text = "&Open Campaign", Width = 110 };
            btnReport = new Win98Button { Text = "Open &Report", Width = 110 };
            btnWaves.Click += (s, e) => { var r = Current(); if (r != null) OpenWaves?.Invoke(r); };
            btnCampaign.Click += (s, e) => { var r = Current(); if (r?.Run?.Dir != null) OpenCampaign?.Invoke(r.Run.Dir); };
            btnReport.Click += (s, e) => { if (report != null && File.Exists(report)) try { Process.Start(report); } catch { } };
            bottom.Controls.AddRange(new Control[] { wellDet, btnWaves, btnCampaign, btnReport });
            bottom.Resize += (s, e) =>
            {
                int bx = bottom.Width - 112;
                btnWaves.Location = new Point(bx, 6); btnCampaign.Location = new Point(bx, 35); btnReport.Location = new Point(bx, 64);
                wellDet.Size = new Size(bx - 8, bottom.Height - 8);
            };
            var hint = new Label { Dock = DockStyle.Bottom, Height = 16, ForeColor = Win98.Shadow, BackColor = Win98.Face, Text = "Click a cell for its details; double-click it for its waveforms.", UseMnemonic = false };
            Controls.Add(wellGrid);
            Controls.Add(hint);
            Controls.Add(bottom);
            Controls.Add(top);
        }

        VerdictRecord selected;
        VerdictRecord Current() => details.SelectedItems.Count == 1 ? details.SelectedItems[0].Tag as VerdictRecord : selected ?? recs.FirstOrDefault();

        public void ShowVerdicts(StudyRunData run)
        {
            recs = run.Verdicts.ToList();
            report = run.Report;
            btnReport.Enabled = report != null;
            int pass = recs.Count(r => r.Verdict == "PASS"), fail = recs.Count(r => r.Verdict == "FAIL");
            lblHead.Text = $"{run.Study}: {recs.Count} verdicts, {pass} PASS, {fail} FAIL" + (run.Problems.Count > 0 ? $"  -  {run.Problems.Count} block(s) did not run: " + string.Join("; ", run.Problems.Take(3)) : "");
            lblHead.ForeColor = fail > 0 || run.Problems.Count > 0 ? Color.FromArgb(150, 0, 0) : Color.FromArgb(0, 100, 0);
            string keep = cbEval.SelectedItem as string;
            cbEval.Items.Clear();
            foreach (var k in recs.Select(r => r.Source + ": " + r.Criterion).Distinct()) cbEval.Items.Add(k);
            int i = keep == null ? -1 : cbEval.Items.IndexOf(keep);
            if (cbEval.Items.Count > 0) cbEval.SelectedIndex = Math.Max(0, i); else FillGrid();
        }

        List<VerdictRecord> Shown => cbEval.SelectedItem is string k ? recs.Where(r => r.Source + ": " + r.Criterion == k).ToList() : new List<VerdictRecord>();

        void FillGrid()
        {
            var g = Shown;
            rows = g.Select(r => r.Row).Distinct().ToList();
            cols = g.Select(r => r.Condition).Distinct().ToList();
            grid.BeginUpdate();
            grid.Clear();
            grid.Columns.Add("Case", 240);
            var table = rows.Select(row => new[] { row }.Concat(cols.Select(c =>
            {
                var r = g.FirstOrDefault(x => x.Row == row && x.Condition == c);
                return r == null ? "-" : $"{r.Verdict}  {r.Worst}" + (double.IsNaN(r.Margin) ? "" : $"  (margin {ErrorBudget.Eng(r.Margin)})");
            })).ToArray()).ToList();
            // each column as wide as its longest cell
            for (int c = 0; c < cols.Count; c++)
                grid.Columns.Add(cols[c], new[] { cols[c] }.Concat(table.Select(t => t[c + 1])).Max(x => TextRenderer.MeasureText(x, Win98.Font).Width) + 18);
            grid.Columns[0].Width = new[] { "Case" }.Concat(rows).Max(x => TextRenderer.MeasureText(x, Win98.Font).Width) + 18;
            foreach (var cells in table) grid.Items.Add(new ListViewItem(cells));
            if (rows.Count == 0) grid.Items.Add(new ListViewItem(new[] { "No verdict: see the problems above, or add an evaluation block (Limits...)." }) { ForeColor = Win98.Shadow });
            grid.EndUpdate();
            ShowDetails(null);
        }

        VerdictRecord CellAt(Point p)
        {
            var hit = grid.HitTest(p);
            if (hit.Item == null || hit.SubItem == null) return null;
            int col = hit.Item.SubItems.IndexOf(hit.SubItem), row = hit.Item.Index;
            if (row >= rows.Count) return null;
            if (col <= 0) return Shown.FirstOrDefault(x => x.Row == rows[row]);
            return col - 1 < cols.Count ? Shown.FirstOrDefault(x => x.Row == rows[row] && x.Condition == cols[col - 1]) : null;
        }

        void ShowDetails(VerdictRecord r)
        {
            selected = r;
            details.BeginUpdate();
            details.Items.Clear();
            var list = r == null ? Shown : recs.Where(x => x.Run == r.Run).ToList();
            foreach (var x in list)
                details.Items.Add(new ListViewItem(new[]
                {
                    x.Row, x.Condition, x.Verdict, x.Worst, double.IsNaN(x.Margin) ? "" : ErrorBudget.Eng(x.Margin),
                    x.Runs > 0 ? $"{x.Failing} of {x.Runs}" : "", x.Rule + (x.Window.Length > 0 ? ", " + x.Window : "") + (x.Criterion.Length > 0 && r != null ? $" [{x.Criterion}]" : ""), x.Requirement,
                }) { Tag = x });
            details.EndUpdate();
            btnWaves.Enabled = btnCampaign.Enabled = r != null || details.Items.Count > 0;
        }
    }
}
