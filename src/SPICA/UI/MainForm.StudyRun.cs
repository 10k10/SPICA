using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Spica.Core;
using Spica.Core.Graph;

namespace Spica.UI
{
    /// <summary>
    /// Running a study from the window: the blocks run in wire order, Simulate puts its campaigns in the queue, the
    /// study goes on when they end, and its waveforms open on the Results tab.
    /// </summary>
    public partial class MainForm
    {
        StudyRunner studyRunner;
        StudyHost studyHost;

        /// <summary>What the blocks need from the window: the schematic open, netlists, folders and the queue.</summary>
        sealed class StudyHost : IStudyHost
        {
            readonly MainForm f;
            readonly Dictionary<string, Netlist> netlists = new Dictionary<string, Netlist>(StringComparer.OrdinalIgnoreCase);
            /// <summary>The campaigns this run put in the queue, and whom to tell when each ends.</summary>
            public readonly Dictionary<QueueJob, Action<string, string>> Jobs = new Dictionary<QueueJob, Action<string, string>>();
            public StudyHost(MainForm f) { this.f = f; }

            public string DefaultSchematic => f.project.Schematic;

            /// <summary>The schematic as it is now (edited since it was opened, it is read again; unchanged, it comes from the cache).</summary>
            public Netlist NetlistFor(string asc)
            {
                string key = Path.GetFullPath(asc);
                if (netlists.TryGetValue(key, out var nl)) return nl;
                string lt = f.settings.LtspicePath ?? "";
                if (!File.Exists(lt)) throw new Exception("LTspice.exe was not found (Tools > Options).");
                f.SetStatus("Reading " + Path.GetFileName(asc) + "...");
                nl = SchematicLoader.Load(lt, key);
                netlists[key] = nl;
                if (SameFile(asc, f.project.Schematic)) { f.netlist = nl; f.ascPath = asc; }
                return nl;
            }

            public string StudyFolder(Study study) => f.StudyFolder(study) ?? throw new Exception("Save the project first: its results go beside it.");

            public void Submit(CampaignRequest request, Action<string, string> done)
            {
                // the answer always comes later, on the window's thread, so that Simulate counts its campaigns first
                void Later(string dir, string why) => f.BeginInvoke(new Action(() => done(dir, why)));
                request.LtspiceExe = f.settings.LtspicePath ?? "";
                request.Manual = f.ManualConfig();
                var options = CampaignBuilder.Build(request, out string error);
                if (options == null) { Later(null, error); return; }
                Jobs[f.queue.Add(request, options)] = done;
            }
        }

        /// <summary>A study campaign ended (or left the queue): tell the Simulate block that waits for it.</summary>
        void OnStudyJobEnded(QueueJob job)
        {
            if (studyHost == null || !studyHost.Jobs.TryGetValue(job, out var done) || done == null) return;
            studyHost.Jobs[job] = null;
            // a campaign stopped by hand keeps what it finished: its waveforms can be looked at (it is not reused as a cache)
            bool ok = (job.Status == JobStatus.Finished || job.Status == JobStatus.Stopped) && job.Done > 0 &&
                      job.OutputDir != null && File.Exists(Path.Combine(job.OutputDir, "results.csv"));
            done(ok ? job.OutputDir : null, ok ? null : (job.Message.Length > 0 ? job.Message : job.Status.ToString().ToLowerInvariant()));
        }

        bool StudyRunning => studyRunner != null && studyRunner.Busy;

        /// <summary>
        /// Runs the study shown. What stops it from running (a block not set up, an input not wired, no LTspice) is said
        /// in a window, with the blocks concerned drawn red.
        /// </summary>
        void RunStudy()
        {
            var st = CurrentStudy;
            if (st == null || StudyRunning) return;
            var problems = StudyProblems();
            if (!File.Exists(settings.LtspicePath ?? "")) problems.Insert(0, "LTspice.exe was not found: set it in Tools > Options.");
            if (StudyFolder(st) == null) problems.Insert(0, "The project has no file and no schematic: open a schematic first.");
            if (problems.Count > 0)
            {
                studyCanvas.Invalidate();
                Win98MessageBox.Show(this, $"The study \"{st.Name}\" cannot run yet:\n\n" + string.Join("\n", problems.Take(15).Select(x => "- " + x)) +
                                     (problems.Count > 15 ? $"\n... and {problems.Count - 15} more" : "") +
                                     "\n\nThe blocks with a red border show it too: click one to set it up.", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
                return;
            }

            studyHost = new StudyHost(this);
            studyRunner = StudyExecutors.Register(new StudyRunner(), studyHost);
            studyRunner.Log = SetStatus;
            studyCanvas.States.Clear();
            staleBlocks.Clear();
            studyRunner.Changed = (b, s) => { if (CurrentStudy == st) { studyCanvas.States[b.Id] = s; studyCanvas.Invalidate(); RefreshModelSoon(); } };
            studyRunner.Ended = ok =>
            {
                var failed = studyRunner.State.Where(kv => kv.Value == BlockState.Error || kv.Value == BlockState.Skipped)
                                              .Select(kv => $"{st[kv.Key]?.Spec?.Title ?? kv.Key.ToString()}: {(studyRunner.Message.TryGetValue(kv.Key, out var m) ? m : kv.Value.ToString().ToLowerInvariant())}").ToList();
                UpdateCommandState();
                StudyEnded(st, failed);
            };
            SetStatus($"Running the study \"{st.Name}\"...");
            bool started;
            try { started = studyRunner.Start(st); }
            catch (Exception ex) { Win98MessageBox.Show(this, "The study could not start:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); return; }
            if (!started) return;
            if (studyRunner.Waiting) tabs.SelectedIndex = TabQueue;
            UpdateCommandState();
        }

        /// <summary>Stops the study that runs: its campaigns still waiting leave the queue, the one running stops.</summary>
        void StopStudy()
        {
            if (!StudyRunning) return;
            if (Win98MessageBox.Show(this, $"Stop the study \"{studyRunner.Study.Name}\"?\nIts campaigns still waiting leave the queue; the one running stops (what it finished is kept).", "SPICA",
                    MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;
            foreach (var j in studyHost.Jobs.Keys.Where(j => j.Status == JobStatus.Queued).ToList()) queue.Remove(j);
            var running = studyHost.Jobs.Keys.FirstOrDefault(j => j.Status == JobStatus.Running);
            studyRunner.Stop();
            running?.Engine?.Cancel();
            UpdateCommandState();
        }

        /// <summary>A study ended: its campaigns go to the Results tab (in place of an earlier run of the same study).</summary>
        void StudyEnded(Study st, List<string> failed)
        {
            var run = new StudyRunData
            {
                Study = st.Name, Problems = failed,
                Saved = st.Blocks.Where(b => b.Kind == BlockKind.Simulate).SelectMany(SaveList).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Sets = studyRunner.Results.Values.SelectMany(x => x.Values).OfType<List<RunSet>>().SelectMany(x => x).Distinct().ToList(),
            };
            int shown = run.Sets.Count(s => s.Dir != null);
            SetStatus($"Study \"{st.Name}\" " + (failed.Count == 0 ? "done" : "ended with problems") + $": {shown} campaign{(shown == 1 ? "" : "s")} to look at.");
            if (closing) return;
            System.Media.SystemSounds.Asterisk.Play();
            if (shown > 0)
            {
                studyRuns.RemoveAll(x => x.Study == st.Name);
                studyRuns.Insert(0, run);
                while (studyRuns.Count > 12) studyRuns.RemoveAt(studyRuns.Count - 1);
                ShowRun(run, null);
            }
            if (failed.Count > 0)
                Win98MessageBox.Show(this, $"The study \"{st.Name}\" ended with problems:\n\n" + string.Join("\n", failed.Take(10).Select(x => "- " + x)),
                    "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
        }

        // ------------------------------------------------------------------ the Results tab

        TreeView tvResults;
        WaveformView waveView;
        Label lblNoResults;
        readonly List<StudyRunData> studyRuns = new List<StudyRunData>();
        bool fillingResults;

        /// <summary>The Results tab: the studies that ran (each with its analyses) on the left, their waveforms on the right.</summary>
        void BuildResultsPage()
        {
            var left = new Win98GroupBox { Text = "Studies run", Dock = DockStyle.Left, Width = 220 };
            var images = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            foreach (var n in new[] { "app16", "chart16" }) images.Images.Add(n, Win98.Icon(n));
            tvResults = new TreeView { Font = Win98.Font, ImageList = images, BorderStyle = BorderStyle.Fixed3D, HideSelection = false, ShowLines = true, ItemHeight = 18 };
            tvResults.AfterSelect += (s, e) =>
            {
                if (fillingResults) return;
                if (e.Node?.Tag is StudyRunData r) ShowRun(r, null);
                else if (e.Node?.Tag is RunSet set && e.Node.Parent?.Tag is StudyRunData pr) ShowRun(pr, set);
            };
            left.Controls.Add(tvResults);
            left.Resize += (s, e) => tvResults.SetBounds(8, 20, left.Width - 16, left.Height - 28);

            waveView = new WaveformView();
            lblNoResults = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false,
                Text = "No results yet.\n\nRun a study (Run, F5): when it ends, its waveforms open here.\nWhat ran before comes back at once when the study runs again unchanged.",
            };
            pageResults.Controls.Add(waveView);
            pageResults.Controls.Add(lblNoResults);
            pageResults.Controls.Add(Gap(DockStyle.Left));
            pageResults.Controls.Add(left);
            waveView.Visible = false;
        }

        void FillResultsTree(StudyRunData select, RunSet selectSet)
        {
            fillingResults = true;
            tvResults.BeginUpdate();
            tvResults.Nodes.Clear();
            TreeNode pick = null;
            foreach (var r in studyRuns)
            {
                var sn = new TreeNode($"{r.Study}  ({r.Ended:HH:mm})") { Tag = r, ImageKey = "app16", SelectedImageKey = "app16" };
                foreach (var s in r.Sets.Where(x => x.Dir != null))
                {
                    var n = new TreeNode(s.Label + (s.Cached ? "  (earlier run)" : "")) { Tag = s, ImageKey = "chart16", SelectedImageKey = "chart16" };
                    sn.Nodes.Add(n);
                    if (r == select && s == selectSet) pick = n;
                }
                tvResults.Nodes.Add(sn);
                sn.Expand();
                if (r == select && pick == null) pick = sn;
            }
            tvResults.EndUpdate();
            tvResults.SelectedNode = pick;
            fillingResults = false;
        }

        /// <summary>A study's waveforms on the Results tab, <paramref name="set"/> ticked (or its first analysis).</summary>
        void ShowRun(StudyRunData r, RunSet set)
        {
            FillResultsTree(r, set);
            lblNoResults.Visible = false;
            waveView.Visible = true;
            bool first = waveView.Plotted.Count == 0;
            waveView.ShowStudy(r, set);
            // the first look, one pane each (at most four): the signals the Simulate saves, else the ones the .meas read, else the first nets
            if (first)
            {
                var nl = set?.Circuit?.Netlist ?? r.Sets.FirstOrDefault()?.Circuit?.Netlist;
                var sigs = r.Saved.Count > 0 ? r.Saved
                         : nl == null ? new List<string>() : nl.RequiredSignals.Concat(nl.Nodes.Select(n => $"V({n})")).ToList();
                waveView.Plot(sigs.Distinct(StringComparer.OrdinalIgnoreCase).Take(4));
            }
            tabs.SelectedIndex = TabResults;
        }

        /// <summary>A campaign of the queue on the Results tab: as one analysis of its own, as far as it has run.</summary>
        void ShowJobResults(QueueJob job)
        {
            if (job == null) return;
            if (job.OutputDir == null || !File.Exists(Path.Combine(job.OutputDir, "results.csv")))
            {
                Win98MessageBox.Show(this, job.Status == JobStatus.Queued ? "This campaign has not started yet." : "This campaign left no results" + (job.Message.Length > 0 ? ":\n" + job.Message : "."),
                    "SPICA", MessageBoxButtons.OK, Win98Icon.Information);
                return;
            }
            var set = new RunSet { Condition = new Condition { Name = job.Name }, Dir = job.OutputDir, Circuit = new Circuit { Asc = job.Request.AscPath, Netlist = job.Request.Netlist } };
            ShowRun(new StudyRunData { Study = job.Name, Sets = new List<RunSet> { set } }, set);
        }
    }
}
