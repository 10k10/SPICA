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

        QueueJob shownJob;             // on the monitor: the running campaign, or the last that ran
        CampaignEngine engine;         // shownJob's engine, kept after it ends (final figures, log)
        int meterTick, graphTick;
        double graphShown;             // throughput as drawn: rises from 0 at the start
        bool monitorFinal;             // the monitor already shows the final figures of its campaign

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
            btnQOpen = B("&Waveforms", "chart16", 100, () => ShowJobResults(SelectedJob()), "The waveforms of the selected campaign, as far as it has run (double-click does the same)");
            btnQStop = B("S&top...", "stop16", 80, StopCampaign, "Stop the running campaign: at once, or when the simulations running now finish; or discard it", 16);
            btnQUp = B("Move &Up", null, 75, () => MoveSelected(-1), "The selected waiting campaign runs earlier");
            btnQDown = B("Move &Down", null, 80, () => MoveSelected(+1), "The selected waiting campaign runs later");
            btnQRemove = B("Re&move", null, 75, RemoveSelected, "Take the selected campaign out of the list (Del). The results of an ended campaign stay on disk.", 16);
            btnQClear = B("&Clear Ended", null, 92, () => queue.ClearEnded(), "Take the campaigns that ended out of the list; their results stay on disk", 16);
            chkHold = new Win98CheckBox { Text = "&Hold the queue: finish the running campaign, start nothing new", Location = new Point(x, 3) };
            chkHold.CheckedChanged += (s, e) => { if (queue.Hold != chkHold.Checked) queue.Hold = chkHold.Checked; };
            bar.Controls.Add(chkHold);

            // --- the campaigns ----------------------------------------------------------------
            lvQueue = new Win98ListView { MultiSelect = false };
            foreach (var (t, w) in new[] { ("#", 30), ("Campaign", 200), ("Analysis", 280), ("Status", 76), ("Progress", 124), ("Time", 216), ("Result", 120) })
                lvQueue.Columns.Add(t, w);
            lvQueue.CellColor = (row, col) =>
            {
                if (col != 3 || row < 0 || row >= lvQueue.Items.Count) return null;
                if (!(lvQueue.Items[row].Tag is QueueJob j)) return null;
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
                if (!(lvQueue.Items[row].Tag is QueueJob j)) return null;
                if (col == 6) return ((j.OutputDir ?? "") + (j.Message.Length > 0 ? "\n" + j.Message : "")).Trim();
                return col == 2 || col == 5 ? lvQueue.Items[row].SubItems[col].Text : null;
            };
            lvQueue.SelectedIndexChanged += (s, e) => UpdateQueueButtons();
            lvQueue.DoubleClick += (s, e) => { if (SelectedJob() != null) ShowJobResults(SelectedJob()); };
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
                Text = "The queue is empty. Run a study (Run, F5): its campaigns wait here for their turn, and the one running is followed below.",
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

        void MoveSelected(int delta)
        {
            var j = SelectedJob();
            if (j != null) queue.Move(j, delta);
        }

        void RemoveSelected()
        {
            var j = SelectedJob();
            if (j == null || j.Status == JobStatus.Running) return;
            if (j.Status == JobStatus.Queued &&
                Win98MessageBox.Show(this, $"Take {j.Name} out of the queue? It has not started.", "Queue", MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;
            queue.Remove(j);
        }

        static string Clock(DateTime t) => t.ToString(t.Date == DateTime.Today ? "HH:mm" : "ddd HH:mm", CultureInfo.InvariantCulture);
        static string Hms(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

        /// <summary>A duration in the words of a clock: 40 s, 12 min, 2 h 05 min.</summary>
        static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "?";
            if (seconds < 90) return $"{Math.Max(1, Math.Round(seconds)):0} s";
            if (seconds < 90 * 60) return $"{Math.Round(seconds / 60):0} min";
            int h = (int)(seconds / 3600), m = (int)Math.Round((seconds - h * 3600.0) / 60);
            if (m == 60) { h++; m = 0; }
            return $"{h} h {m:00} min";
        }

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
                    state = s != null && s.Draining ? "Stopping" : "Running";
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
            return new[] { j.Id.ToString(CultureInfo.InvariantCulture), j.Name, j.Analysis, state, prog, time, result };
        }

        /// <summary>Brings the list in line with the queue; texts are touched only when they change (no flicker).</summary>
        void UpdateQueueList()
        {
            if (lvQueue == null) return;
            var jobs = queue.Jobs.ToArray();
            bool same = lvQueue.Items.Count == jobs.Length;
            for (int i = 0; same && i < jobs.Length; i++) same = ReferenceEquals(lvQueue.Items[i].Tag, jobs[i]);
            if (!same)
            {
                var sel = lvQueue.SelectedItems.Count > 0 ? lvQueue.SelectedItems[0].Tag : null;
                lvQueue.BeginUpdate();
                lvQueue.Items.Clear();
                foreach (var j in jobs) lvQueue.Items.Add(new ListViewItem(QueueRow(j)) { Tag = j, Selected = j == sel });
                lvQueue.EndUpdate();
                lblQueueEmpty.Visible = jobs.Length == 0;
                UpdateQueueButtons();
            }
            else
            {
                for (int i = 0; i < jobs.Length; i++)
                {
                    var it = lvQueue.Items[i];
                    var cols = QueueRow(jobs[i]);
                    bool status = it.SubItems[3].Text != cols[3];
                    for (int c = 0; c < cols.Length; c++)
                        if (it.SubItems[c].Text != cols[c]) it.SubItems[c].Text = cols[c];
                    if (status) UpdateQueueButtons();
                }
            }
            int pending = queue.Jobs.Count(j => j.Pending);
            string title = pending > 0 ? $"Queue ({pending})" : "Queue";
            if (pageQueue.Text != title) { pageQueue.Text = title; tabs.Invalidate(); }
        }

        void UpdateQueueButtons()
        {
            if (btnQOpen == null) return;
            var j = SelectedJob();
            var jobs = queue.Jobs;
            int i = j == null ? -1 : jobs.ToList().IndexOf(j);
            bool waiting = j != null && j.Status == JobStatus.Queued;
            btnQOpen.Enabled = j != null && j.Status != JobStatus.Queued;
            btnQStop.Enabled = Running;
            btnQUp.Enabled = waiting && i > 0 && jobs[i - 1].Status == JobStatus.Queued;
            btnQDown.Enabled = waiting && i >= 0 && i + 1 < jobs.Count && jobs[i + 1].Status == JobStatus.Queued;
            btnQRemove.Enabled = j != null && j.Status != JobStatus.Running;
            btnQClear.Enabled = jobs.Any(q => !q.Pending);
            if (chkHold.Checked != queue.Hold) chkHold.Checked = queue.Hold;
        }

        // =====================================================================================
        //  What the queue tells
        // =====================================================================================

        void OnJobStarted(QueueJob job)
        {
            shownJob = job;
            engine = job.Engine;
            monitorFinal = false;
            txtLog.Clear();
            graph.Clear();
            graphShown = 0;
            progress.Value = 0;
            UpdateQueueList();
            foreach (ListViewItem it in lvQueue.Items)
                if (it.Tag == job)
                {
                    if (lvQueue.SelectedItems.Count == 0) it.Selected = true;
                    it.EnsureVisible();
                }
            SetStatus($"Running {job.Name}: {job.Total:N0} simulations...");
            UpdateCommandState();
        }

        void OnJobFinished(QueueJob job)
        {
            if (job.Engine == null) SetStatus($"{job.Name}: {job.Message}");          // it could not even start
            else
            {
                if (job == shownJob) UpdateMonitor(true);
                SetStatus($"{job.Name}: campaign {(job.Status == JobStatus.Finished ? "finished" : job.Status == JobStatus.Stopped ? "stopped" : "failed")}.");
            }
            UpdateCommandState();
        }

        void OnJobDiscarded(QueueJob job, bool gone)
        {
            if (job == shownJob)
            {
                shownJob = null;
                engine = null;
                ClearMonitor();
            }
            UpdateCommandState();
            SetStatus(gone ? "Campaign discarded." : "Campaign stopped; some files could not be deleted.");
            if (!gone && !closing)
                Post(() => Win98MessageBox.Show(this, "The campaign was stopped, but the folder could not be deleted completely:\n" + job.OutputDir, "SPICA", MessageBoxButtons.OK, Win98Icon.Warning));
        }

        /// <summary>Stops the campaign that runs (not its study: the study goes on with its other campaigns).</summary>
        void StopCampaign()
        {
            var job = queue.Running;
            var run = job?.Engine;
            if (run == null) return;
            var s = run.Snapshot();
            int waiting = queue.QueuedCount;
            string head = $"Stop {job.Name}?  {s.Done:N0} of {s.Total:N0} simulations are finished" +
                          (s.Running > 0 ? $", {s.Running:N0} are running now." : ".");
            string tail = "What a stopped campaign finished stays: its waveforms can be looked at." +
                          (waiting > 0 && !queue.Hold ? $"\nThe {(waiting == 1 ? "campaign that waits starts" : waiting + " campaigns that wait start")} when this one stops (tick \"Hold the queue\" first to keep them waiting)." : "");
            int choice;
            if (!s.Draining && s.Running > 0)
            {
                choice = Win98MessageBox.Choose(this, head + "\n\n" +
                    "Stop now:  stop at once (the simulations running now are lost).\n" +
                    "Finish running:  start nothing new, wait for the simulations running now.\n" +
                    "Discard:  stop and delete this campaign folder with everything in it.\n\n" + tail,
                    "Stop Campaign", Win98Icon.Question, "&Stop now", "&Finish running", "&Discard", "Keep running");
            }
            else
            {
                choice = Win98MessageBox.Choose(this, head + "\n\n" +
                    "Stop now:  stop at once.\n" +
                    "Discard:  stop and delete this campaign folder with everything in it.\n\n" + tail,
                    "Stop Campaign", Win98Icon.Question, "&Stop now", "&Discard", "Keep running");
                if (choice >= 1) choice++;      // same meaning as in the four-button box
            }
            if (job != queue.Running || job.Engine != run) return;      // it ended while the question was open
            switch (choice)
            {
                case 0:
                    run.Cancel();
                    SetStatus("Stopping...");
                    break;
                case 1:
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
            if (++meterTick % 4 == 0) UpdateMeter();
            UpdateQueueList();
            UpdateMonitor(false);
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

            string cap = job.Status == JobStatus.Running ? "Running now:" : job.Status == JobStatus.Finished ? "Finished:" : job.Status == JobStatus.Stopped ? "Stopped:" : "Failed:";
            SetNow(cap, job.Name, o.Analysis);
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
            stat["Phase"].Text = s.Draining ? $"Stopping ({s.Running} running)" : s.Cancelled ? "Stopped" : s.Finished ? "Finished" : PhaseText(s.Phase);
            stat["Launch cost"].Text = double.IsNaN(s.OverheadSeconds) ? "-" : $"{s.OverheadSeconds.ToString("0.00", inv)} s per launch";
            stat["Peak memory"].Text = s.PeakMemory > 0 ? $"{s.PeakMemory / 1048576.0:0} MB per instance" : "-";
            stat[".raw kept"].Text = FormatBytes(s.RawBytes);
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
                string dir = queue.Running?.OutputDir ?? ResultsRoot() ?? Application.StartupPath;
                var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir)));
                rawFree = d.AvailableFreeSpace;
                free = $"\nOutput drive {d.Name}: {FormatBytes(d.AvailableFreeSpace)} free of {FormatBytes(d.TotalSize)}";
                var run = queue.Running?.Engine;
                if (run != null)
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
                              $"({FormatBytes(rawFree)} free).\n\nStop it and run it again saving fewer signals (Save, in the Simulate block), with fewer runs, " +
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
