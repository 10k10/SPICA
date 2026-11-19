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
        StudyResultsForm studyResults;
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
            if (st == null) { Win98MessageBox.Show(this, "Choose or make a study first (New... on the Studies tab).", "SPICA", MessageBoxButtons.OK, Win98Icon.Information); return; }
            if (StudyRunning) { Win98MessageBox.Show(this, $"The study \"{studyRunner.Study.Name}\" is still running.", "SPICA", MessageBoxButtons.OK, Win98Icon.Information); return; }
            if (ascPath == null) { BrowseSchematic(); return; }
            if (!File.Exists(settings.LtspicePath ?? "")) { Win98MessageBox.Show(this, "LTspice.exe was not found. Set its location in Tools > Options.", "SPICA", MessageBoxButtons.OK, Win98Icon.Error); return; }
            var issues = st.Validate();
            if (issues.Count > 0) { ValidateStudy(true); return; }
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

        void ShowStudyResults(Study st, List<string> problems)
        {
            if (studyResults == null || studyResults.IsDisposed)
            {
                studyResults = new StudyResultsForm { OpenWaves = OpenStudyWaves, OpenCampaign = d => { if (Directory.Exists(d)) OpenResultsFolder(d); } };
                studyResults.Show(this);
            }
            studyResults.ShowVerdicts(st.Name, lastVerdicts, problems, lastStudyReport);
            studyResults.Activate();
        }

        /// <summary>The waveforms of one case at one condition, as the qualification shows them.</summary>
        void OpenStudyWaves(VerdictRecord r)
        {
            var set = r?.Run;
            if (set?.Dir == null || set.Case == null) return;
            ResultSet rs;
            try { rs = set.Results; }
            catch (Exception ex) { Win98MessageBox.Show(this, "Cannot read the results:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); return; }
            var v = set.Variant;
            string net = v.Stimulus == null ? (set.Criteria.FirstOrDefault()?.Signal ?? "") : v.Stimulus.IsFault ? v.MapNet(v.Stimulus.From) : v.MapNet(v.Stimulus.Net);
            var m = System.Text.RegularExpressions.Regex.Match(net, @"^V\((.+)\)$");
            if (m.Success) net = m.Groups[1].Value;
            var plot = CurrentStudy?.Blocks.Where(b => b.Kind == BlockKind.Plots).SelectMany(b => b.Get("Signals").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)).Select(v.Map).ToList() ?? new List<string>();
            var qc = set.Case;
            var f = new QualWaveForm(qc, net, set.Dir, rs, plot, v.Stimulus?.Rser ?? 0) { OpenResults = d => { if (Directory.Exists(d)) OpenResultsFolder(d); } };
            f.Text += $" - {v.Label}, {set.Condition.Name}";
            f.Show(this);
        }
    }

    /// <summary>
    /// The verdicts of a study: for each evaluation (criterion), a grid of case × condition; the details of the cell
    /// chosen; its waveforms or its campaign on a double-click.
    /// </summary>
    sealed class StudyResultsForm : Win98Form
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

        public StudyResultsForm()
        {
            Text = "Study Results";
            MinimizeBox = true; MaximizeBox = true; Resizable = true; ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(900, 600 + Padding.Vertical);
            MinimumSize = new Size(560, 380);

            var top = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Win98.Face };
            lblHead = new Label { Location = new Point(6, 6), AutoSize = false, Height = 16, Font = Win98.BoldFont, UseMnemonic = false };
            top.Controls.Add(new Label { Text = "&Criterion:", Location = new Point(6, 31), AutoSize = true });
            cbEval = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(70, 28), Width = 420, Font = Win98.Font };
            cbEval.SelectedIndexChanged += (s, e) => FillGrid();
            top.Controls.AddRange(new Control[] { lblHead, cbEval });
            top.Resize += (s, e) => lblHead.Width = top.Width - 12;

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
            foreach (var (t, w) in new[] { ("Case", 200), ("Condition", 120), ("Verdict", 60), ("Worst seen", 130), ("Margin", 80), ("Runs failing", 90), ("Rule", 220), ("Requirement", 100) })
                details.Columns.Add(t, w);
            details.DoubleClick += (s, e) => { if (details.SelectedItems.Count == 1 && details.SelectedItems[0].Tag is VerdictRecord r) OpenWaves?.Invoke(r); };
            var wellDet = new Win98Well(details) { Location = new Point(0, 6) };
            btnWaves = new Win98Button { Text = "&Waveforms...", Width = 110 };
            btnCampaign = new Win98Button { Text = "&Open Campaign", Width = 110 };
            btnReport = new Win98Button { Text = "Open &Report", Width = 110 };
            var btnClose = new Win98Button { Text = "Close", Width = 110, DialogResult = DialogResult.Cancel };
            btnWaves.Click += (s, e) => { var r = Current(); if (r != null) OpenWaves?.Invoke(r); };
            btnCampaign.Click += (s, e) => { var r = Current(); if (r?.Run?.Dir != null) OpenCampaign?.Invoke(r.Run.Dir); };
            btnReport.Click += (s, e) => { if (report != null && File.Exists(report)) try { Process.Start(report); } catch { } };
            btnClose.Click += (s, e) => Close();
            CancelButton = btnClose;
            bottom.Controls.AddRange(new Control[] { wellDet, btnWaves, btnCampaign, btnReport, btnClose });
            bottom.Resize += (s, e) =>
            {
                int bx = bottom.Width - 116;
                btnWaves.Location = new Point(bx, 6); btnCampaign.Location = new Point(bx, 35); btnReport.Location = new Point(bx, 64); btnClose.Location = new Point(bx, bottom.Height - 29);
                wellDet.Size = new Size(bx - 8, bottom.Height - 8);
            };
            var hint = new Label { Dock = DockStyle.Bottom, Height = 16, ForeColor = Win98.Shadow, BackColor = Win98.Face, Text = "Click a cell for its details; double-click it for its waveforms.", UseMnemonic = false };

            var body = new Panel { Dock = DockStyle.Fill };
            body.Controls.Add(wellGrid);
            body.Controls.Add(hint);
            body.Controls.Add(bottom);
            body.Controls.Add(top);
            Controls.Add(body);
        }

        VerdictRecord selected;
        VerdictRecord Current() => details.SelectedItems.Count == 1 ? details.SelectedItems[0].Tag as VerdictRecord : selected;

        public void ShowVerdicts(string study, List<VerdictRecord> list, List<string> problems, string reportPath)
        {
            recs = list.ToList();
            report = reportPath;
            btnReport.Enabled = report != null;
            int pass = recs.Count(r => r.Verdict == "PASS"), fail = recs.Count(r => r.Verdict == "FAIL");
            lblHead.Text = $"{study}: {recs.Count} verdicts, {pass} PASS, {fail} FAIL" + (problems.Count > 0 ? $"  -  {problems.Count} block(s) did not run: " + string.Join("; ", problems.Take(3)) : "");
            lblHead.ForeColor = fail > 0 || problems.Count > 0 ? Color.FromArgb(150, 0, 0) : Color.FromArgb(0, 100, 0);
            cbEval.Items.Clear();
            foreach (var k in recs.Select(r => r.Source + ": " + r.Criterion).Distinct()) cbEval.Items.Add(k);
            if (cbEval.Items.Count > 0) cbEval.SelectedIndex = 0; else FillGrid();
            Text = "Study Results - " + study;
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
