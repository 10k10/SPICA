using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Spica.Core;

namespace Spica.UI
{
    // The Queue tab: at the top the campaigns (ended, running, waiting) and what can be done with them; below, the
    // campaign that runs now, from the general (progress, time) to the particular (LTspice instances, log).
    public partial class MainForm
    {
        Win98ListView lvQueue, lvWorkers;
        Label lblQueueEmpty, lblNowCap, lblNowName, lblNowInfo;
        Win98Button btnQOpen, btnQStop, btnQUp, btnQDown, btnQRemove, btnQClear;
        Win98CheckBox chkHold;
        Win98ProgressBar progress;
        Win98Tile tileRuns, tileElapsed, tileRemaining, tileRate, tileFailed;
        Win98Graph graph;
        Win98ResourceMeter meter;
        TextBox txtLog;
        readonly Dictionary<string, Label> stat = new Dictionary<string, Label>();

        int meterTick;
        int liveDone = -1;
        DateTime nextLiveRefresh = DateTime.MinValue;
        bool monitorFinal;             // the monitor already shows the final figures of its campaign
        bool finishBoxOpen;
        // campaigns added here that ended since the last "finished" message: told together, when the queue stops
        readonly List<(QueueJob Job, string Note)> ended = new List<(QueueJob, string)>();

        static readonly Color GoodColor = Color.FromArgb(0, 110, 0), BadColor = Color.FromArgb(176, 0, 0);
        const int QueueListHeight = 118;

        void BuildQueuePage()
        {
            var p = pageQueue;
            var tip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400 };

            // --- what can be done with the queue ----------------------------------------------
            var bar = new Panel { Dock = DockStyle.Top, Height = 29, BackColor = Win98.Face };
            int x = 0;
            Win98Button B(string text, string icon, int width, Action click, string hint, int gap = 6)
            {
                var b = new Win98Button { Text = text, Size = new Size(width, 23), Location = new Point(x, 0), Enabled = false };
                if (icon != null) b.Image = Win98.Icon(icon);
                b.Click += (s, e) => click();
                tip.SetToolTip(b, hint);
                bar.Controls.Add(b);
                x += width + gap;
                return b;
            }
            btnQOpen = B("Open &Results", "results16", 112, () => { if (SelectedRemote() != null) ShowRemoteResults(SelectedRemote()); else ShowJobResults(SelectedJob()); }, "Show the selected campaign on the Results tab: live while it runs (double-click does the same)");
            btnQStop = B("S&top...", "stop16", 80, StopCampaign, "Stop the running campaign: analyse what is done, finish the running simulations, or discard it (Shift+F5)", 16);
            btnQUp = B("Move &Up", null, 75, () => MoveSelected(-1), "The selected waiting campaign runs earlier");
            btnQDown = B("Move &Down", null, 80, () => MoveSelected(+1), "The selected waiting campaign runs later");
            btnQRemove = B("Re&move", null, 75, RemoveSelected, "Take the selected campaign out of the list (Del). The results of an ended campaign stay on disk.", 16);
            btnQClear = B("&Clear Ended", null, 92, () => { queue.ClearEnded(); remote.ClearEnded(); }, "Take the campaigns that ended out of the list; their results stay on disk", 16);
            chkHold = new Win98CheckBox { Text = "&Hold the queue: finish the running campaign, start nothing new", Location = new Point(x, 3) };
            chkHold.CheckedChanged += (s, e) => { if (queue.Hold != chkHold.Checked) queue.Hold = chkHold.Checked; };
            bar.Controls.Add(chkHold);

            // --- the campaigns ----------------------------------------------------------------
            lvQueue = new Win98ListView { MultiSelect = false };
            foreach (var (t, w) in new[] { ("#", 30), ("Campaign", 140), ("Analysis", 250), ("Status", 76), ("Progress", 124), ("Time", 216), ("From", 70), ("Result", 120) })
                lvQueue.Columns.Add(t, w);
            lvQueue.CellColor = (row, col) =>
            {
                if (col != 3 || row < 0 || row >= lvQueue.Items.Count) return null;
                var tag = lvQueue.Items[row].Tag;
                if (tag is RemoteJob r)
                    return r.Status == RemoteStatus.Running ? Win98.Navy : r.Status == RemoteStatus.Done ? GoodColor
                         : r.Status == RemoteStatus.Failed || r.Status == RemoteStatus.Lost ? BadColor : (Color?)null;
                if (!(tag is QueueJob j)) return null;
                switch (j.Status)
                {
                    case JobStatus.Running: return Win98.Navy;
                    case JobStatus.Finished: return GoodColor;
                    case JobStatus.Failed: return BadColor;
                    default: return null;
                }
            };
            lvQueue.CellTip = (row, col) =>
            {
                if (row < 0 || row >= lvQueue.Items.Count) return null;
                var tag = lvQueue.Items[row].Tag;
                if (tag is RemoteJob r)
                {
                    if (col == 6) return "Sent to " + (r.Machine.Length > 0 ? r.Machine : "the remote PC") + " through " + r.Folder + "\n(file " + r.File + ")";
                    if (col == 7) return string.Join("\n", new[] { r.ResultsHere ?? (r.Results.Length > 0 ? r.Results + " (on " + r.Machine + ")" : ""), r.Message, r.Note }.Where(x => !string.IsNullOrEmpty(x)));
                    return col == 2 || col == 5 ? lvQueue.Items[row].SubItems[col].Text : null;
                }
                if (!(tag is QueueJob j)) return null;
                if (col == 7) return ((j.OutputDir ?? "") + (j.Message.Length > 0 ? "\n" + j.Message : "")).Trim();
                return col == 2 || col == 5 ? lvQueue.Items[row].SubItems[col].Text : null;
            };
            lvQueue.SelectedIndexChanged += (s, e) => UpdateQueueButtons();
            lvQueue.DoubleClick += (s, e) => { if (SelectedJob() != null) ShowJobResults(SelectedJob()); else if (SelectedRemote() != null) ShowRemoteResults(SelectedRemote()); };
            lvQueue.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete && btnQRemove.Enabled) RemoveSelected(); };
            bool sizing = false;
            lvQueue.Resize += (s, e) =>
            {
                if (sizing) return;
                sizing = true;
                int used = 0;
                for (int i = 0; i < lvQueue.Columns.Count - 1; i++) used += lvQueue.Columns[i].Width;
                lvQueue.Columns[lvQueue.Columns.Count - 1].Width = Math.Max(120, lvQueue.ClientSize.Width - used);
                sizing = false;
            };
            lblQueueEmpty = new Label
            {
                Text = "The queue is empty. Set up a campaign on the Setup tab and press Run Here (F5) or Run Remote (F6): the campaigns of this PC and the ones sent to the remote PC are followed here.",
                AutoSize = true, Location = new Point(8, 30), ForeColor = Win98.Shadow, BackColor = Win98.Window, UseMnemonic = false,
            };
            lvQueue.Controls.Add(lblQueueEmpty);
            var wellQueue = new Win98Well(lvQueue) { Dock = DockStyle.Top, Height = QueueListHeight };

            var sep = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Win98.Face };
            sep.Paint += (s, e) => Win98.EtchedHLine(e.Graphics, 0, sep.Width, 6);
            sep.Resize += (s, e) => sep.Invalidate();

            // --- the campaign that runs now -----------------------------------------------------
            var now = new Panel { Dock = DockStyle.Top, Height = 21, BackColor = Win98.Face };
            lblNowCap = new Label { AutoSize = true, Location = new Point(0, 2), ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false };
            lblNowName = new Label { AutoSize = true, Location = new Point(0, 2), Font = Win98.BoldFont, BackColor = Win98.Face, UseMnemonic = false };
            lblNowInfo = new Label { AutoSize = false, Height = 15, Location = new Point(0, 2), BackColor = Win98.Face, UseMnemonic = false, AutoEllipsis = true };
            now.Controls.AddRange(new Control[] { lblNowCap, lblNowName, lblNowInfo });
            now.Resize += (s, e) => LayoutNow();

            progress = new Win98ProgressBar { Dock = DockStyle.Top, Height = 22 };

            var tiles = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Win98.Face };
            tileRuns = new Win98Tile { Caption = "Simulations" };
            tileElapsed = new Win98Tile { Caption = "Elapsed" };
            tileRemaining = new Win98Tile { Caption = "Remaining" };
            tileRate = new Win98Tile { Caption = "Rate" };
            tileFailed = new Win98Tile { Caption = "Failed" };
            var allTiles = new[] { tileRuns, tileElapsed, tileRemaining, tileRate, tileFailed };
            tiles.Controls.AddRange(allTiles);
            tiles.Resize += (s, e) =>
            {
                int n = allTiles.Length, w = (tiles.Width - (n - 1) * 6) / n;
                for (int i = 0; i < n; i++) allTiles[i].SetBounds(i * (w + 6), 0, i == n - 1 ? tiles.Width - i * (w + 6) : w, 58);
            };

            // throughput, the machine, and how the engine is running it
            var graphRow = new Panel { Dock = DockStyle.Top, Height = 138, BackColor = Win98.Face };
            graph = new Win98Graph { Dock = DockStyle.Fill, Caption = "Throughput (runs/min)" };
            meter = new Win98ResourceMeter();
            meter.Bars.Add(new Win98ResourceMeter.Bar { Label = "CPU", Color = Win98Chart.Navy, Icon = Win98.Icon("cpu32") });
            meter.Bars.Add(new Win98ResourceMeter.Bar { Label = "RAM", Color = Win98Chart.Teal, Icon = Win98.Icon("ram32") });
            meter.Bars.Add(new Win98ResourceMeter.Bar { Label = "Disk activity", Color = Win98Chart.Purple, Icon = Win98.Icon("drive32") });
            meter.Dock = DockStyle.Fill;
            var grpMeter = new Win98GroupBox { Text = "This PC", Dock = DockStyle.Right, Width = meter.PreferredWidth + 16, Padding = new Padding(8, 16, 8, 6) };
            grpMeter.Controls.Add(meter);
            var grpEngine = new Win98GroupBox { Text = "Engine", Dock = DockStyle.Right, Width = 262 };
            string[] rows = { "Instances", "Phase", "Launch cost", "Peak memory", ".raw kept", ".raw forecast" };
            for (int i = 0; i < rows.Length; i++)
            {
                int y = 19 + i * 17;
                var val = new Label { Text = "-", Location = new Point(92, y), Size = new Size(160, 15), BackColor = Win98.Face, UseMnemonic = false, AutoEllipsis = true };
                grpEngine.Controls.Add(new Label { Text = rows[i] + ":", Location = new Point(10, y), AutoSize = true, ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false });
                grpEngine.Controls.Add(val);
                stat[rows[i]] = val;
            }
            tip.SetToolTip(stat["Instances"], "LTspice processes running at the same time x threads of each (Tools > Options)");
            tip.SetToolTip(stat["Launch cost"], "Time an LTspice process takes to start and load the circuit, paid once per chunk of runs");
            tip.SetToolTip(stat[".raw forecast"], "Disk space the waveform files will take at the end of the campaign (red: more than the output drive has free)");
            graphRow.Controls.Add(new Win98Well(graph, Color.Black) { Dock = DockStyle.Fill });
            graphRow.Controls.Add(Gap(DockStyle.Right));
            graphRow.Controls.Add(grpMeter);
            graphRow.Controls.Add(Gap(DockStyle.Right));
            graphRow.Controls.Add(grpEngine);

            // --- the particular: every LTspice, and what the engine says -------------------------
            var sub = new Win98TabControl { Dock = DockStyle.Fill };
            var pageInst = new Win98TabPage("LTspice Instances", Win98.Icon("hw16"));
            var pageLog = new Win98TabPage("Log", Win98.Icon("console16"));
            lvWorkers = new Win98ListView();
            foreach (var (t, w) in new[] { ("Instance", 70), ("PID", 70), ("Runs", 170), ("State", 110), ("Time", 80), ("Memory", 90), ("Threads", 70) })
                lvWorkers.Columns.Add(t, w);
            pageInst.Controls.Add(new Win98Well(lvWorkers) { Dock = DockStyle.Fill });
            txtLog = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Win98.Window, ScrollBars = ScrollBars.Both, Font = Win98.MonoFont, WordWrap = false };
            pageLog.Controls.Add(new Win98Well(txtLog) { Dock = DockStyle.Fill });
            sub.AddPage(pageInst);
            sub.AddPage(pageLog);

            // docked from the top in reading order (the last one added docks first)
            foreach (var c in new Control[] { sub, Gap(DockStyle.Top), graphRow, Gap(DockStyle.Top), tiles, Gap(DockStyle.Top), progress, now, sep, wellQueue, bar })
                p.Controls.Add(c);
            // a taller window gives a third of the extra height to the list of campaigns
            p.Resize += (s, e) => wellQueue.Height = QueueListHeight + Math.Max(0, p.Height - 610) / 3;

            ClearMonitor();
            UpdateQueueList();
            UpdateQueueButtons();
        }

        // =====================================================================================
        //  The list of campaigns
        // =====================================================================================

        QueueJob SelectedJob() => lvQueue != null && lvQueue.SelectedItems.Count > 0 ? lvQueue.SelectedItems[0].Tag as QueueJob : null;
        RemoteJob SelectedRemote() => lvQueue != null && lvQueue.SelectedItems.Count > 0 ? lvQueue.SelectedItems[0].Tag as RemoteJob : null;

        void MoveSelected(int delta)
        {
            var j = SelectedJob();
            if (j != null) queue.Move(j, delta);
        }

        void RemoveSelected()
        {
            var r = SelectedRemote();
            if (r != null)
            {
                if (r.Pending && Win98MessageBox.Show(this, $"Stop following {r.Name} here?\nIt still runs on {(r.Machine.Length > 0 ? r.Machine : "the remote PC")}; its results stay there.",
                        "Queue", MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;
                remote.Remove(r);
                return;
            }
            var j = SelectedJob();
            if (j == null || j.Status == JobStatus.Running) return;
            if (j.Status == JobStatus.Queued &&
                Win98MessageBox.Show(this, $"Take {j.Name} out of the queue? It has not started.", "Queue", MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;
            queue.Remove(j);
        }

        static string Clock(DateTime t) => t.ToString(t.Date == DateTime.Today ? "HH:mm" : "ddd HH:mm", CultureInfo.InvariantCulture);
        static string Hms(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

        /// <summary>The last two names of a campaign folder: the output folder and the campaign's date.</summary>
        static string ShortFolder(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "";
            try { return Path.Combine(Path.GetFileName(Path.GetDirectoryName(dir)) ?? "", Path.GetFileName(dir)); } catch { return dir; }
        }

        string[] QueueRow(QueueJob j)
        {
            string state, prog, time, result;
            switch (j.Status)
            {
                case JobStatus.Running:
                {
                    var s = j.Engine?.Snapshot();
                    int done = s?.Done ?? 0, total = Math.Max(1, s?.Total ?? j.Total);
                    state = s != null && s.Draining ? (s.AutoStopped ? "Finishing" : "Stopping") : "Running";
                    prog = $"{done:N0} / {total:N0}  ({100.0 * done / total:0} %)";
                    double rem = queue.RemainingSeconds(j);
                    time = double.IsNaN(rem) ? "measuring the run time..." : $"ends about {Clock(DateTime.Now.AddSeconds(rem))}  ({Duration(rem)} left)";
                    result = ShortFolder(j.OutputDir);
                    break;
                }
                case JobStatus.Queued:
                {
                    state = "Waiting";
                    prog = $"{j.Total:N0} to run";
                    double wait = queue.WaitSeconds(j);
                    string takes = double.IsNaN(j.EstimateSeconds) ? "" : $", takes about {Duration(j.EstimateSeconds)}";
                    time = queue.Hold ? "on hold" + takes
                         : double.IsNaN(wait) ? (queue.Ahead(j) <= 1 ? "next" : "after the ones before it") + takes
                         : $"starts about {Clock(DateTime.Now.AddSeconds(wait))}" + takes;
                    result = "";
                    break;
                }
                default:
                    state = j.Status == JobStatus.Finished ? "Finished" : j.Status == JobStatus.Stopped ? "Stopped" : "Failed";
                    prog = j.Total > 0 ? $"{j.Done:N0} / {j.Total:N0}" : "";
                    time = j.Elapsed > TimeSpan.Zero ? $"took {Hms(j.Elapsed)}, ended {Clock(j.Ended ?? DateTime.Now)}" : j.Ended != null ? "ended " + Clock(j.Ended.Value) : "";
                    result = j.Status == JobStatus.Failed && j.Done == 0 ? j.Message
                           : $"{j.Ok:N0} with data" + (j.NoData > 0 ? $", {j.NoData:N0} without" : "") + (j.Failed > 0 ? $", {j.Failed:N0} failed" : "") +
                             (j.Message.Length > 0 ? " - " + j.Message : "");
                    break;
            }
            return new[] { j.Id.ToString(CultureInfo.InvariantCulture), j.Name, j.Analysis, state, prog, time, j.Source, result };
        }

        string[] RemoteRow(RemoteJob r)
        {
            string state, prog, time, result;
            int total = Math.Max(1, r.Total);
            switch (r.Status)
            {
                case RemoteStatus.Running:
                    state = "Running";
                    prog = $"{r.Done:N0} / {r.Total:N0}  ({100.0 * r.Done / total:0} %)";
                    time = double.IsNaN(r.Eta) ? "measuring the run time..." : $"ends about {Clock(DateTime.Now.AddSeconds(r.Eta))}  ({Duration(r.Eta)} left)";
                    result = r.Note;
                    break;
                case RemoteStatus.Queued:
                    state = "Waiting";
                    prog = $"{r.Total:N0} to run";
                    time = !double.IsNaN(r.Wait) ? $"starts about {Clock(DateTime.Now.AddSeconds(r.Wait))} there" : r.Ahead == 0 ? "next there" : $"{r.Ahead} before it there";
                    result = r.Note;
                    break;
                case RemoteStatus.Sent:
                case RemoteStatus.Waiting:
                    state = "Sent";
                    prog = r.Total > 0 ? $"{r.Total:N0} to run" : "";
                    time = "sent " + Clock(r.Sent);
                    result = r.Status == RemoteStatus.Waiting && r.Message.Length > 0 ? r.Message : r.Note;
                    break;
                default:
                    state = r.Status == RemoteStatus.Done ? "Finished" : r.Status == RemoteStatus.Stopped ? "Stopped" : r.Status == RemoteStatus.Lost ? "Lost" : "Failed";
                    prog = r.Total > 0 && r.Status != RemoteStatus.Lost ? $"{r.Done:N0} / {r.Total:N0}" : "";
                    time = r.Started != null && r.Ended != null ? $"took {Hms(r.Ended.Value - r.Started.Value)}, ended {Clock(r.Ended.Value)}" : r.Ended != null ? "ended " + Clock(r.Ended.Value) : "";
                    result = r.Status == RemoteStatus.Done || r.Status == RemoteStatus.Stopped
                        ? $"{r.Ok:N0} with data" + (r.NoData > 0 ? $", {r.NoData:N0} without" : "") + (r.Failed > 0 ? $", {r.Failed:N0} failed" : "") + (r.ResultsHere == null ? "  (results on " + r.Machine + ")" : "")
                        : r.Message;
                    break;
            }
            string to = "-> " + (r.Machine.Length > 0 ? r.Machine : "remote");
            return new[] { "R", r.Name, r.Analysis, state, prog, time, to, result };
        }

        object[] QueueItems() => queue.Jobs.Cast<object>().Concat(remote.Jobs).ToArray();

        string[] Row(object o) => o is QueueJob j ? QueueRow(j) : RemoteRow((RemoteJob)o);

        /// <summary>Brings the list in line with the queue; texts are touched only when they change (no flicker).</summary>
        void UpdateQueueList()
        {
            if (lvQueue == null) return;
            var jobs = QueueItems();
            bool same = lvQueue.Items.Count == jobs.Length;
            for (int i = 0; same && i < jobs.Length; i++) same = ReferenceEquals(lvQueue.Items[i].Tag, jobs[i]);
            if (!same)
            {
                var sel = lvQueue.SelectedItems.Count > 0 ? lvQueue.SelectedItems[0].Tag : null;
                lvQueue.BeginUpdate();
                lvQueue.Items.Clear();
                foreach (var j in jobs) lvQueue.Items.Add(new ListViewItem(Row(j)) { Tag = j, Selected = j == sel });
                lvQueue.EndUpdate();
                lblQueueEmpty.Visible = jobs.Length == 0;
                UpdateQueueButtons();
            }
            else
            {
                for (int i = 0; i < jobs.Length; i++)
                {
                    var it = lvQueue.Items[i];
                    var cols = Row(jobs[i]);
                    bool status = it.SubItems[3].Text != cols[3];
                    for (int c = 0; c < cols.Length; c++)
                        if (it.SubItems[c].Text != cols[c]) it.SubItems[c].Text = cols[c];
                    if (status) UpdateQueueButtons();
                }
            }
            int pending = queue.Jobs.Count(j => j.Pending) + remote.Jobs.Count(r => r.Pending);
            string title = pending > 0 ? $"Queue ({pending})" : "Queue";
            if (pageQueue.Text != title) { pageQueue.Text = title; tabs.Invalidate(); }
        }

        void UpdateQueueButtons()
        {
            if (btnQOpen == null) return;
            var j = SelectedJob();
            var r = SelectedRemote();
            var jobs = queue.Jobs;
            int i = j == null ? -1 : jobs.ToList().IndexOf(j);
            bool waiting = j != null && j.Status == JobStatus.Queued;
            btnQOpen.Enabled = (j != null && j.Status != JobStatus.Queued) || (r != null && r.Results.Length > 0);
            btnQStop.Enabled = Running;
            btnQUp.Enabled = waiting && i > 0 && jobs[i - 1].Status == JobStatus.Queued;
            btnQDown.Enabled = waiting && i >= 0 && i + 1 < jobs.Count && jobs[i + 1].Status == JobStatus.Queued;
            btnQRemove.Enabled = (j != null && j.Status != JobStatus.Running) || r != null;
            btnQClear.Enabled = jobs.Any(q => !q.Pending) || remote.Jobs.Any(q => !q.Pending);
            if (chkHold.Checked != queue.Hold) chkHold.Checked = queue.Hold;
        }

        // =====================================================================================
        //  What the queue tells
        // =====================================================================================

        void OnJobStarted(QueueJob job, bool second)
        {
            shownJob = job;
            engine = job.Engine;
            monitorFinal = false;
            if (!second)
            {
                txtLog.Clear();
                graph.Clear();
                graphShown = 0;
                // Results follow the new campaign, unless the user is reading other results there: the ones opened from
                // disk, or the ones on screen now. The campaign started by the user's own click is always followed.
                if (addingLocal || resultsView.Data == null || (liveJob != null && tabs.SelectedIndex != TabResults))
                {
                    liveJob = job;
                    resultsView.SetData(null, false);
                }
            }
            progress.Value = 0;
            liveDone = -1;
            nextLiveRefresh = DateTime.MinValue;

            UpdateQueueList();
            foreach (ListViewItem it in lvQueue.Items)
                if (it.Tag == job)
                {
                    if (lvQueue.SelectedItems.Count == 0) it.Selected = true;
                    it.EnsureVisible();
                }
            SetStatus(second ? "Worst case: verification runs..." : job.Request.Resume != null ? $"Resuming {job.Name}..." : $"Running {job.Name}: {job.Total:N0} simulations...");
            UpdateCommandState();
        }

        void OnJobFinished(QueueJob job)
        {
            bool local = job.Tag == null;       // added here by the user (the listener's jobs end without a message)
            if (job.Engine == null)
            {
                // it could not even start
                SetStatus($"{job.Name}: {job.Message}");
                if (local) ended.Add((job, ""));
                Post(ShowFinishBox);
                return;
            }

            var s = job.Engine.Snapshot();
            if (job == shownJob) UpdateMonitor(true);
            if (job == liveJob) RefreshResults();

            // Setup > "Write the report when it ends": into the campaign folder, as set in Results > Report...
            string reportNote = "", reportFile = null;
            if (job.Request.Project.Report.Auto && s.Ok > 0 && !closing)
            {
                try
                {
                    reportFile = WriteAutoReport(job);
                    reportNote = "\nReport saved: " + Path.GetFileName(reportFile);
                }
                catch (Exception ex) { reportNote = "\nThe report could not be written: " + ex.Message; }
            }
            SetStatus($"{job.Name}: campaign {(job.Status == JobStatus.Finished ? "finished" : job.Status == JobStatus.Stopped ? "stopped" : "failed")}." +
                      (reportFile != null ? " Report: " + reportFile : ""));
            UpdateCommandState();
            if (closing) return;

            if (job.AnalyzeOnFinish)
            {
                // the Stop dialog asked for the results: no question, show them
                job.AnalyzeOnFinish = false;
                if (job != liveJob || resultsView.Data == null) { liveJob = job; RefreshResults(); }
                tabs.SelectedIndex = TabResults;
            }
            else if (local) ended.Add((job, reportNote));
            Post(ShowFinishBox);
        }

        /// <summary>The report of a campaign that just ended: from the Results tab when it shows that campaign, else built aside.</summary>
        string WriteAutoReport(QueueJob job)
        {
            if (job == liveJob && resultsView.Data != null) return resultsView.AutoReport();
            var e = job.Engine;
            using (var view = new ResultsView())
            {
                view.SetData(ResultSet.FromResults(e.GetResults(), e.MeasNames, e.Options, e.Options.Setup), false);
                return view.AutoReport();
            }
        }

        /// <summary>
        /// Tells how the campaigns added here ended: once, when the queue has nothing more to run, so that a night of
        /// campaigns leaves one message and not a pile of them.
        /// </summary>
        void ShowFinishBox()
        {
            if (closing || finishBoxOpen || ended.Count == 0) return;
            // while a campaign added here still runs or waits its turn, the message waits too (the listener's do not count)
            if (queue.Jobs.Any(j => j.Tag == null && (j.Status == JobStatus.Running || (j.Status == JobStatus.Queued && !queue.Hold)))) return;
            var list = ended.ToList();
            ended.Clear();
            finishBoxOpen = true;
            try
            {
                System.Media.SystemSounds.Asterisk.Play();
                var last = list[list.Count - 1].Job;
                if (list.Count == 1)
                {
                    if (last.Status == JobStatus.Failed)
                    {
                        Win98MessageBox.Show(this, last.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error);
                        return;
                    }
                    string msg = $"{(last.Status == JobStatus.Stopped ? "Campaign stopped" : "Campaign finished")} in {Hms(last.Elapsed)}.\n\n" +
                                 $"{last.Ok:N0} runs with data, {last.NoData:N0} without data, {last.Failed:N0} failed." +
                                 (last.Message.Length > 0 ? "\n" + last.Message : "") + list[0].Note + "\n\nOpen the Results tab?";
                    if (Win98MessageBox.Show(this, msg, "SPICA", MessageBoxButtons.YesNo, last.Failed > 0 ? Win98Icon.Warning : Win98Icon.Information) == DialogResult.Yes)
                        ShowJobResults(last);
                    return;
                }

                var sb = new StringBuilder($"The {list.Count} campaigns added here have ended.\n");
                foreach (var (j, note) in list.Take(12))
                {
                    sb.Append("\n").Append(j.Name).Append(":  ");
                    if (j.Status == JobStatus.Failed && j.Done == 0) sb.Append("failed - ").Append(j.Message);
                    else sb.Append($"{(j.Status == JobStatus.Finished ? "finished" : j.Status == JobStatus.Stopped ? "stopped" : "failed")} in {Hms(j.Elapsed)}, " +
                                   $"{j.Ok:N0} runs with data" + (j.Failed > 0 ? $", {j.Failed:N0} failed" : "") + (note.StartsWith("\nThe report") ? " (no report: see the log)" : ""));
                }
                if (list.Count > 12) sb.Append($"\n... and {list.Count - 12} more");
                sb.Append("\n\nEach one opens from the Queue tab (double-click). Open the results of the last one now?");
                bool bad = list.Any(e => e.Job.Status == JobStatus.Failed || e.Job.Failed > 0);
                if (Win98MessageBox.Show(this, sb.ToString(), "SPICA", MessageBoxButtons.YesNo, bad ? Win98Icon.Warning : Win98Icon.Information) == DialogResult.Yes)
                    ShowJobResults(last);
            }
            finally
            {
                finishBoxOpen = false;
                if (ended.Count > 0) Post(ShowFinishBox);
            }
        }

        /// <summary>The results of a campaign that ran on the remote PC: opened from the shared folder when they are there.</summary>
        void ShowRemoteResults(RemoteJob r)
        {
            string dir = r.ResultsHere;
            if (dir != null && File.Exists(Path.Combine(dir, "results.csv")))
            {
                if (r.Pending)
                    Win98MessageBox.Show(this, "This campaign still runs on " + r.Machine + ": the results open as they are now (reopen them later for the rest).", "SPICA", MessageBoxButtons.OK, Win98Icon.Information);
                OpenResultsFolder(dir);
                return;
            }
            string where = r.Results.Length > 0 ? $"Its results are on {r.Machine}, in\n{r.Results}" : "It has no results yet.";
            Win98MessageBox.Show(this, where + (r.Results.Length > 0 && dir == null
                    ? "\n\nThat folder is not inside the shared folder, so this PC cannot reach it. On " + r.Machine + ", set the listener's results folder inside the watched folder (Tools > Listener) to open the results from here."
                    : ""), "SPICA", MessageBoxButtons.OK, Win98Icon.Information);
        }

        void OnRemoteEnded(RemoteJob r)
        {
            string how = r.Status == RemoteStatus.Done ? "finished" : r.Status == RemoteStatus.Stopped ? "stopped" : "failed";
            SetStatus($"{r.Name} {how} on {(r.Machine.Length > 0 ? r.Machine : "the remote PC")}" + (r.Status == RemoteStatus.Done ? $": {r.Ok:N0} runs with data. Open it from the Queue tab." : r.Message.Length > 0 ? ": " + r.Message : "."));
            if (!closing) System.Media.SystemSounds.Asterisk.Play();
            UpdateQueueButtons();
        }

        void OnJobDiscarded(QueueJob job, bool gone)
        {
            if (job == shownJob)
            {
                shownJob = null;
                engine = null;
                ClearMonitor();
            }
            if (job == liveJob)
            {
                liveJob = null;
                resultsView.SetData(null, false);
            }
            UpdateCommandState();
            SetStatus(gone ? "Campaign discarded." : "Campaign stopped; some files could not be deleted.");
            if (!gone && !closing)
                Post(() => Win98MessageBox.Show(this, "The campaign was stopped, but the folder could not be deleted completely:\n" + job.OutputDir, "SPICA", MessageBoxButtons.OK, Win98Icon.Warning));
            Post(ShowFinishBox);
        }

        static string AnalysisText(CampaignOptions o, EngineSnapshot s)
        {
            if (o.WcStage == 1) return $"Worst case, step 1 of 2: screening (nominal + each of {o.Mc.Sites.Count} tolerances at its edge); verification follows by itself";
            if (o.WcStage == 2) return "Worst case, step 2 of 2: verification (every tolerance pushed the worst way for each measurement)";
            if (o.Mc?.Design != null) return $"Worst case, all corners: {o.Runs:N0} combinations of {o.Mc.Sites.Count} tolerance edges";
            if (o.Stop != null && o.Stop.Enabled)
                return s.AutoStopped ? "Stopped early, the answer is known: " + (s.StopStatus ?? "")
                     : "Monte Carlo, stops early (at most " + o.Runs.ToString("N0", CultureInfo.InvariantCulture) + " runs): " + (s.StopStatus ?? "waiting for the first results...");
            return $"Monte Carlo: {o.Runs:N0} random units" + (o.CornerCount > 1 ? $" x {o.CornerCount} temperatures" : "");
        }

        void StopCampaign()
        {
            var job = queue.Running;
            var run = job?.Engine;
            if (run == null) return;
            var s = run.Snapshot();
            int waiting = queue.QueuedCount;
            string head = $"Stop {job.Name}?  {s.Done:N0} of {s.Total:N0} simulations are finished" +
                          (s.Running > 0 ? $", {s.Running:N0} are running now." : ".");
            string tail = "A stopped campaign can be resumed later from Results > Open Results." +
                          (waiting > 0 && !queue.Hold ? $"\nThe {(waiting == 1 ? "campaign that waits starts" : waiting + " campaigns that wait start")} when this one stops (tick \"Hold the queue\" first to keep them waiting)." : "");
            int choice;
            if (!s.Draining && s.Running > 0)
            {
                choice = Win98MessageBox.Choose(this, head + "\n\n" +
                    "Analyze now:  stop at once and open the finished runs (the running ones are lost).\n" +
                    "Finish running:  start nothing new, wait for the running simulations, then analyze.\n" +
                    "Discard:  stop and delete this campaign folder with everything in it.\n\n" + tail,
                    "Stop Campaign", Win98Icon.Question, "&Analyze now", "&Finish running", "&Discard", "Keep running");
            }
            else
            {
                choice = Win98MessageBox.Choose(this, head + "\n\n" +
                    "Analyze now:  stop at once and open the finished runs.\n" +
                    "Discard:  stop and delete this campaign folder with everything in it.\n\n" + tail,
                    "Stop Campaign", Win98Icon.Question, "&Analyze now", "&Discard", "Keep running");
                if (choice >= 1) choice++;      // same meaning as in the four-button box
            }
            if (job != queue.Running || job.Engine != run) return;      // it ended while the question was open
            switch (choice)
            {
                case 0:
                    job.AnalyzeOnFinish = true;
                    run.Cancel();
                    SetStatus("Stopping...");
                    break;
                case 1:
                    job.AnalyzeOnFinish = true;
                    run.StopAfterRunning();
                    SetStatus($"Stopping: waiting for {s.Running:N0} running simulations...");
                    break;
                case 2:
                    if (Win98MessageBox.Show(this, "Delete this campaign folder and all its files?\n" + run.Options.OutputDir, "Discard Campaign",
                            MessageBoxButtons.YesNo, Win98Icon.Warning) != DialogResult.Yes) return;
                    if (job != queue.Running || job.Engine != run) return;
                    job.DiscardOnFinish = true;
                    run.Cancel();
                    SetStatus("Discarding...");
                    break;
            }
        }

        // =====================================================================================
        //  Periodic refresh (4 times a second)
        // =====================================================================================

        void UpdateUi()
        {
            status.SetText(PaneClock, DateTime.Now.ToString("HH:mm"));
            listener.Tick();
            remote.Tick();
            if (++meterTick % 4 == 0)
            {
                UpdateMeter();
                UpdatePlan();
                string pane = !listener.Active ? "Listener: off" : listener.PendingCount > 0 ? $"Listener: {listener.PendingCount} to run" : "Listener: on";
                status.SetText(PaneListener, pane);
                if (tbListener.Checked != listener.Active)
                {
                    tbListener.Checked = listener.Active;
                    UpdateCommandState();
                    UpdatePlan();
                }
            }
            UpdateQueueList();
            UpdateMonitor(false);

            // Live Results: only with new runs, and at 20x the cost of the last refresh (at least 1 s), so the analysis
            // never takes more than ~5 % of one core (100k runs cost 0.2 .. 0.6 s per refresh).
            var live = LiveEngine;
            if (tabs.SelectedIndex == TabResults && live != null && !live.IsFinished && !resultsView.LivePaused && DateTime.UtcNow >= nextLiveRefresh)
            {
                int done = live.Snapshot().Done;
                if (done != liveDone)
                {
                    var sw = Stopwatch.StartNew();
                    RefreshResults();
                    liveDone = done;
                    nextLiveRefresh = DateTime.UtcNow.AddSeconds(Math.Max(1, 20 * sw.Elapsed.TotalSeconds));
                }
            }
        }

        void LayoutNow()
        {
            if (lblNowInfo == null) return;
            lblNowName.Left = lblNowCap.Text.Length > 0 ? lblNowCap.Right + 2 : 0;
            lblNowInfo.Left = lblNowName.Text.Length > 0 ? lblNowName.Right + 8 : lblNowName.Left;
            lblNowInfo.Width = Math.Max(20, lblNowInfo.Parent.Width - lblNowInfo.Left);
        }

        void SetNow(string caption, string name, string info)
        {
            if (lblNowCap.Text == caption && lblNowName.Text == name && lblNowInfo.Text == info) return;
            lblNowCap.Text = caption;
            lblNowName.Text = name;
            lblNowInfo.Text = info;
            LayoutNow();
        }

        static void SetTile(Win98Tile tile, string text, string note, Color? color = null)
        {
            string key = text + "\n" + note + "\n" + (color?.ToArgb() ?? 0);
            if (tile.Tag as string == key) return;
            tile.Tag = key;
            tile.Set(text, note, color);
        }

        /// <summary>The monitor with nothing to show.</summary>
        void ClearMonitor()
        {
            txtLog.Clear();
            graph.Clear();
            graphShown = 0;
            progress.Value = 0;
            lvWorkers.Items.Clear();
            SetNow("", "Nothing is running.", "The campaign that runs is followed here: progress, time left, what every LTspice is doing.");
            foreach (var t in new[] { tileRuns, tileElapsed, tileRemaining, tileRate, tileFailed }) SetTile(t, "-", "");
            foreach (var l in stat.Values) l.Text = "-";
        }

        /// <summary>The campaign on the monitor: the one that runs, or the last that ran (its final figures stay).</summary>
        void UpdateMonitor(bool force)
        {
            if (engine == null || shownJob == null) return;

            var sb = new StringBuilder();
            while (engine.LogQueue.TryDequeue(out var line)) sb.Append(line).Append("\r\n");
            if (sb.Length > 0)
            {
                if (txtLog.TextLength > 400_000) txtLog.Text = txtLog.Text.Substring(txtLog.TextLength - 200_000);
                txtLog.AppendText(sb.ToString());
            }

            var s = engine.Snapshot();
            if (s.Finished && monitorFinal && !force) return;
            monitorFinal = s.Finished;
            var job = shownJob;
            var o = engine.Options;
            var inv = CultureInfo.InvariantCulture;

            bool can = resultsView.CanExport || s.Done > 0;
            if (miExport.Enabled != can) miExport.Enabled = miReport.Enabled = tbReport.Enabled = can;

            string cap = job.Status == JobStatus.Running ? "Running now:" : job.Status == JobStatus.Finished ? "Finished:" : job.Status == JobStatus.Stopped ? "Stopped:" : "Failed:";
            SetNow(cap, job.Name, AnalysisText(o, s));
            progress.Maximum = Math.Max(1, s.Total);
            progress.Value = s.Done;

            double pct = 100.0 * s.Done / Math.Max(1, s.Total);
            SetTile(tileRuns, $"{s.Done:N0} / {s.Total:N0}", $"{pct:0} % done");
            SetTile(tileElapsed, Hms(s.Elapsed), job.Started != null ? "started " + Clock(job.Started.Value) : "");
            if (s.Finished)
                SetTile(tileRemaining, s.Cancelled ? "stopped" : "done", job.Ended != null ? "ended " + Clock(job.Ended.Value) : "");
            else if (double.IsNaN(s.EtaSeconds))
                SetTile(tileRemaining, "estimating...", "the first runs measure the run time");
            else
                SetTile(tileRemaining, Hms(TimeSpan.FromSeconds(Math.Min(s.EtaSeconds, 359999))), "ends about " + Clock(DateTime.Now.AddSeconds(s.EtaSeconds)));
            SetTile(tileRate, s.RunsPerMinute > 0 ? $"{s.RunsPerMinute.ToString(s.RunsPerMinute >= 100 ? "N0" : "0.0", inv)} runs/min" : "-",
                    double.IsNaN(s.SecondsPerRun) ? "" : $"{s.SecondsPerRun.ToString("0.000", inv)} s per run (simulation)");
            SetTile(tileFailed, s.Failed.ToString("N0", inv),
                    s.NoData > 0 ? $"and {s.NoData:N0} ran without data" : s.Failed > 0 ? "see the log" : "every run gave data so far",
                    s.Failed > 0 ? BadColor : (Color?)null);

            stat["Instances"].Text = s.Config.Length > 0 ? s.Config : "-";
            stat["Phase"].Text = s.Draining ? (s.AutoStopped ? $"Stopping early ({s.Running} finishing)" : $"Stopping ({s.Running} running)") : s.Cancelled ? "Stopped" : s.Finished ? "Finished" : PhaseText(s.Phase);
            stat["Launch cost"].Text = double.IsNaN(s.OverheadSeconds) ? "-" : $"{s.OverheadSeconds.ToString("0.00", inv)} s per launch";
            stat["Peak memory"].Text = s.PeakMemory > 0 ? $"{s.PeakMemory / 1048576.0:0} MB per instance" : "-";
            stat[".raw kept"].Text = o.KeepRaw ? FormatBytes(s.RawBytes) : "none (deleted after each run)";
            stat[".raw forecast"].Text = double.IsNaN(s.RawForecast) ? "-" : FormatBytes((long)s.RawForecast) + (rawShort > 0 ? $"  ({FormatBytes(rawFree)} free)" : "");
            stat[".raw forecast"].ForeColor = rawShort > 0 ? BadColor : Color.Black;

            // once a second; eased so the cold start draws as a climb from zero, not a jump to the running rate
            if (++graphTick % 4 == 0 && !s.Finished)
            {
                graphShown += 0.15 * (s.RunsPerMinute - graphShown);
                graph.Push(graphShown);
            }

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

            if (!s.Finished)
            {
                int waiting = queue.QueuedCount;
                SetStatus($"Running {job.Name}: {s.Done:N0} of {s.Total:N0} simulations, {s.Config}" + (waiting > 0 ? $"  -  {waiting} waiting in the queue" : ""));
            }
        }

        long rawFree, rawShort;             // free space on the running campaign's drive, and what the kept .raw files lack of it
        QueueJob rawWarned;

        /// <summary>
        /// Resource meter, once a second: CPU and RAM of the whole machine, how busy the disks are (a campaign that
        /// keeps its .raw files can wait on the disk). Also checks that the .raw files still to come fit on the drive.
        /// </summary>
        void UpdateMeter()
        {
            double cpu = HardwareInfo.CpuLoadPercent(), ram = HardwareInfo.MemoryLoadPercent();
            HardwareInfo.RefreshMemory(hw);
            meter.Set(0, cpu, $"CPU utility, whole machine ({hw.LogicalProcessors} threads), as in Task Manager");
            meter.Set(1, ram, $"RAM in use: {(hw.TotalMemory - hw.AvailableMemory) / (double)(1L << 30):0.0} of {hw.TotalMemory / (double)(1L << 30):0.0} GB");
            double disk = HardwareInfo.DiskBusyPercent(out double write);
            string free = "";
            rawShort = 0;
            try
            {
                string dir = queue.Running?.OutputDir ?? txtOut.Text.Trim();
                if (dir.Length == 0) dir = Application.StartupPath;
                var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir)));
                rawFree = d.AvailableFreeSpace;
                free = $"\nOutput drive {d.Name}: {FormatBytes(d.AvailableFreeSpace)} free of {FormatBytes(d.TotalSize)}";
                var run = queue.Running?.Engine;
                if (run != null && run.Options.KeepRaw)
                {
                    var s = run.Snapshot();
                    if (!s.Finished && !double.IsNaN(s.RawForecast))
                    {
                        // a little room is kept for the results, the logs and everything else on the drive
                        long need = (long)(s.RawForecast - s.RawBytes), margin = Math.Max(256L << 20, d.TotalSize / 50);
                        if (need + margin > rawFree) rawShort = need + margin - rawFree;
                    }
                }
            }
            catch { }
            meter.Set(2, disk, (double.IsNaN(disk) ? "Disk activity unknown" : $"Disks busy {disk:0} % of the time (Task Manager's active time)") +
                               (double.IsNaN(write) ? "" : $", writing {FormatBytes((long)write)}/s") + free);

            var job = queue.Running;
            if (rawShort > 0 && job != null && rawWarned != job && !closing)
            {
                rawWarned = job;
                string text = $"The .raw files of {job.Name} will not fit on the output drive: about {FormatBytes(rawShort)} more is needed " +
                              $"({FormatBytes(rawFree)} free).\n\nStop it and run it again without \"Keep .raw waveform files\", save fewer signals, " +
                              "or free some space. When the drive is full the remaining runs fail.";
                SetStatus($"Warning: the .raw files of {job.Name} will not fit on the output drive ({FormatBytes(rawShort)} short).");
                Post(() => Win98MessageBox.Show(this, text, "SPICA", MessageBoxButtons.OK, Win98Icon.Warning));
            }
        }

        static string PhaseText(string phase)
        {
            switch (phase)
            {
                case "Measuring": return "Measuring the run time";
                case "Running": return "Running";
                case "Manual": return "Running (manual setting)";
                default: return phase.Length > 0 ? phase : "-";
            }
        }

        static string FormatBytes(long b)
        {
            if (b < 1048576) return $"{b / 1024.0:0} KB";
            if (b < 1L << 30) return $"{b / 1048576.0:0.0} MB";
            return $"{b / (double)(1L << 30):0.00} GB";
        }
    }
}
