using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Spica.Core
{
    /// <summary>Reads a schematic the way SPICA simulates it: LTspice converts it to a netlist, SPICA parses that.</summary>
    public static class SchematicLoader
    {
        static readonly object Gate = new object();     // LTspice writes the .net beside the schematic: one conversion at a time

        /// <param name="redirected">library references taken from a source bundle unzipped beside the schematic</param>
        /// <param name="preferShipped">
        /// use the netlist of the source bundle the schematic came in, without asking LTspice: a campaign sent by
        /// another PC runs as it was netlisted there, whatever symbols and library paths this PC has
        /// </param>
        /// <param name="shipped">the netlist came from the bundle (asked for, or because LTspice could not convert the schematic here)</param>
        public static Netlist Load(string ltspiceExe, string ascPath, out int redirected, bool preferShipped, out bool shipped)
        {
            lock (Gate)
            {
                var parsed = preferShipped ? SourceBundle.ShippedNetlist(ascPath) : null;
                if (parsed == null)
                {
                    try
                    {
                        string tmp = Path.Combine(Path.GetTempPath(), "SPICA");
                        Directory.CreateDirectory(tmp);
                        string ini = Ltspice.WritePrivateIni(Path.Combine(tmp, "convert.ini"), true);
                        string text = Ltspice.ConvertSchematic(ltspiceExe, ascPath, ini, out var enc);
                        parsed = Netlist.Parse(text, ascPath, enc);
                    }
                    catch
                    {
                        parsed = preferShipped ? null : SourceBundle.ShippedNetlist(ascPath);
                        if (parsed == null) throw;
                    }
                    shipped = false;
                }
                else shipped = true;
                redirected = SourceBundle.Redirect(parsed);
                return parsed;
            }
        }

        public static Netlist Load(string ltspiceExe, string ascPath, out int redirected) => Load(ltspiceExe, ascPath, out redirected, false, out _);
    }

    /// <summary>Everything a campaign needs, frozen when it is put in the queue: later edits of the setup do not reach it.</summary>
    public sealed class CampaignRequest
    {
        public string LtspiceExe, AscPath;
        public string OutputRoot;               // one sub-folder per campaign is created here when it starts
        public Netlist Netlist;
        public SpicaProject Project;
        public RunScheduler.Config? Manual;
        public string ResumeDir;                // an unfinished campaign folder to complete
        public List<RunResult> Resume;          // the runs already finished in that folder
        public double PriorSeconds;
        public uint Seed;                       // 0 = the project's seed (0 there = a new one)

        public CampaignRequest Clone() => (CampaignRequest)MemberwiseClone();
    }

    /// <summary>Turns a setup into the options of a campaign, with the checks that used to sit behind the Start button.</summary>
    public static class CampaignBuilder
    {
        /// <param name="confirm">asks a yes/no question (text, is it a warning); null = go on without asking</param>
        /// <param name="error">why there is no campaign; null when the user answered No to a question</param>
        public static CampaignOptions Build(CampaignRequest r, Func<string, bool, bool> confirm, out string error)
        {
            error = null;
            var project = r.Project;
            var netlist = r.Netlist;
            bool Ask(string text, bool warning = false) => confirm == null || confirm(text, warning);

            if (!File.Exists(r.LtspiceExe ?? "")) { error = "LTspice.exe was not found."; return null; }
            int runs = project.Runs;
            if (runs < 1 || runs > 10_000_000) { error = "Enter a number of runs between 1 and 10,000,000."; return null; }
            if (r.Manual.HasValue && (r.Manual.Value.Instances < 1 || r.Manual.Value.Threads < 1))
            {
                error = "Enter the number of instances and threads per instance (1 or more).";
                return null;
            }

            List<double> corners = null;
            if (project.Temp == TempMode.Corners)
            {
                corners = project.Corners.Distinct().ToList();
                if (corners.Count == 0 || corners.Any(t => t < -273 || t > 1000))
                {
                    error = "Enter the temperature corners in °C, separated by commas (e.g. -40, 25, 90).";
                    return null;
                }
            }
            if (project.Temp == TempMode.Random && !(project.TempMax > project.TempMin))
            {
                error = "Enter a random temperature range with min < max (°C).";
                return null;
            }

            McPlan plan = null;
            bool wc = project.WorstCase != WcMethod.Off;
            if (project.Paired || wc)
            {
                plan = McPlan.Build(netlist.Lines, netlist.SourcePath);
                if (wc && (plan.Sites.Count == 0 || netlist.MeasNames.Count == 0))
                {
                    error = plan.Sites.Count == 0
                        ? "Worst case needs tolerances SPICA can drive: mc(), gauss() or flat() outside .subckt definitions. None were found."
                        : "Worst case needs .meas statements: there is nothing to find the extremes of.";
                    return null;
                }
                if (!wc && plan.Sites.Count == 0 &&
                    !Ask("Paired samples are on, but SPICA found no mc()/gauss()/flat() it can take over in this schematic" +
                         (plan.Warnings.Count > 0 ? " (random values inside .subckt definitions stay with LTspice)" : "") + ".\nRun anyway?")) return null;
            }

            // worst case: the runs are fixed combinations of tolerance edges instead of random units
            var setup = project;
            bool randomTemp = project.Temp == TempMode.Random;
            int wcStage = 0;
            string stage = null;
            if (wc)
            {
                int n = plan.Sites.Count;
                double k = project.WcSigma;
                var method = WorstCase.Resolve(project.WorstCase, n);
                if (method == WcMethod.Corners && n > WorstCase.MaxCornerSites)
                {
                    error = $"{n} tolerances: all corners would need 2^{n} = {Math.Pow(2, n):N0} runs per temperature.\nChoose Sensitivity or Auto.";
                    return null;
                }
                if (randomTemp)
                {
                    // a random temperature has no edge: its two limits become corners
                    corners = new List<double> { project.TempMin, project.TempMax };
                    randomTemp = false;
                    setup = project.Clone();
                    setup.Temp = TempMode.Corners;
                    setup.Corners = new List<double>(corners);
                }
                int cc = corners != null && corners.Count > 0 ? corners.Count : 1;
                string temps = cc > 1 ? $" at {cc} temperatures" : "";
                if (method == WcMethod.Corners)
                {
                    plan.Design = WorstCase.CornerDesign(plan.Sites, k);
                    stage = $"Worst case, all corners: nominal + 2^{n} combinations of {n} tolerance edges = {plan.Design.Count} runs{temps}.";
                }
                else
                {
                    var screen = WorstCase.ScreenDesign(plan.Sites, k);
                    if (r.Resume != null && Screened(r.Resume, n, cc))
                    {
                        var verify = WorstCase.VerifyDesign(r.Resume, netlist.MeasNames, plan.Sites, k, cc);
                        if (verify.Count == 0)
                        {
                            error = "The screening runs gave no usable .meas values, so the worst-case directions are unknown.";
                            return null;
                        }
                        plan.Design = screen.Concat(verify).ToList();
                        wcStage = 2;
                        stage = $"Worst case, step 2 of 2 (verification): {verify.Count} runs with every tolerance pushed the way that moves a measurement up or down{temps}.";
                    }
                    else
                    {
                        plan.Design = screen;
                        wcStage = 1;
                        stage = $"Worst case, step 1 of 2 (screening): nominal + each of the {n} tolerances alone at its upper edge = {n + 1} runs{temps}.";
                    }
                }
                runs = plan.Design.Count;
            }

            if (netlist.MeasNames.Count == 0 && !Ask("This schematic has no .meas statements, so the results table will be empty.\nRun anyway?")) return null;
            if (!project.Paired && !project.ClockReseed &&
                !Ask("Without clock reseeding every LTspice instance repeats the SAME random sequence, so chunks produce duplicate runs.\nContinue anyway?", true)) return null;
            if (!project.Paired && corners != null && corners.Count > 1 &&
                !Ask("Temperature corners without paired samples simulate DIFFERENT units at each temperature,\nso per-unit drift and 'pass at all temperatures' cannot be computed.\nContinue anyway?")) return null;

            List<string> save = null;
            if (project.SaveSelected)
            {
                save = new List<string>(netlist.RequiredSignals);
                var have = new HashSet<string>(save, StringComparer.OrdinalIgnoreCase);
                var known = new HashSet<string>(netlist.Nodes.Select(n => $"V({n})").Concat(netlist.Currents), StringComparer.OrdinalIgnoreCase);
                foreach (var s in project.Signals) if (known.Contains(s) && have.Add(s)) save.Add(s);
                if (save.Count == 0)
                {
                    if (!Ask("No signals are selected, so LTspice will save everything.\nContinue?")) return null;
                    save = null;
                }
            }

            string root = (r.OutputRoot ?? "").Trim();
            if (r.ResumeDir == null && root.Length == 0) root = DefaultRoot(r.AscPath);
            try
            {
                // the campaign folder itself is made when the campaign starts; the drive is checked now, while someone can answer
                string probe = r.ResumeDir ?? root;
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(probe)));
                if (project.KeepRaw && confirm != null && drive.IsReady && drive.AvailableFreeSpace < 2L * (1L << 30) &&
                    !Ask($"Only {drive.AvailableFreeSpace / 1048576} MB free on {drive.Name}. .raw files can grow quickly.\nContinue?", true)) return null;
            }
            catch (Exception ex) when (!(ex is ArgumentException))
            {
                error = "Cannot use the output folder:\n" + ex.Message;
                return null;
            }
            catch (ArgumentException) { }       // a network share has no DriveInfo: nothing to check

            uint seed = r.Seed != 0 ? r.Seed : project.Seed;
            if (seed == 0) seed = (uint)(Guid.NewGuid().GetHashCode() & 0x7FFFFFFF) | 1;
            return new CampaignOptions
            {
                LtspiceExe = r.LtspiceExe,
                AscPath = r.AscPath,
                OutputDir = r.ResumeDir,          // a new campaign gets its folder when it starts
                Netlist = netlist,
                Runs = runs,
                SaveSignals = save,
                KeepRaw = project.KeepRaw,
                ClockReseed = project.ClockReseed,
                Manual = r.Manual,
                Corners = corners,
                RandomTemp = randomTemp,
                TempMin = project.TempMin,
                TempMax = project.TempMax,
                Mc = plan,
                Seed = seed,
                Setup = setup,
                Resume = r.Resume,
                PriorSeconds = r.PriorSeconds,
                Stop = wc ? null : project.Stop.Clone(),
                Stage = stage,
                WcStage = wcStage,
            };
        }

        public static string DefaultRoot(string ascPath) =>
            Path.Combine(Path.GetDirectoryName(ascPath), Path.GetFileNameWithoutExtension(ascPath) + "_SPICA");

        /// <summary>Worst case by sensitivity: are the screening runs (nominal + one per tolerance) done at every temperature?</summary>
        public static bool Screened(IList<RunResult> done, int sites, int corners) =>
            done.Count(r => r.Status != RunStatus.Failed && r.Run >= 1 && r.Run <= sites + 1 && r.Corner < corners) >= (sites + 1) * corners;

        /// <summary>What a campaign runs, in a few words (queue list, summaries).</summary>
        public static string Describe(CampaignOptions o)
        {
            string temps = o.CornerCount > 1 ? $" x {o.CornerCount} temperatures" : o.RandomTemp ? ", random temperature" : "";
            if (o.WcStage == 1) return $"Worst case by sensitivity: {o.Mc.Sites.Count + 1} screening runs{temps}, then verification";
            if (o.WcStage == 2) return $"Worst case by sensitivity: verification{temps}";
            if (o.Mc?.Design != null) return $"Worst case, all corners: {o.Runs:N0} runs{temps}";
            return (o.Stop != null && o.Stop.Enabled ? $"Monte Carlo, up to {o.Runs:N0} runs (stops early)" : $"Monte Carlo, {o.Runs:N0} runs") + temps;
        }

        /// <summary>Wall time of a campaign from the run times saved for its circuit on this machine; NaN when it never ran here.</summary>
        public static double EstimateSeconds(CampaignOptions o, HardwareInfo hw, ProfileStore profiles)
        {
            ProfileStore.Profile p;
            try { p = profiles.Find(hw.MachineKey, o.Netlist.CircuitHash()); } catch { p = null; }
            int jobs = o.Jobs - (o.Resume?.Count(x => x.Status != RunStatus.Failed) ?? 0);
            return EstimateSeconds(p, jobs, o.Manual?.Instances ?? 0);
        }

        /// <param name="instances">0 = as many as the saved profile used</param>
        public static double EstimateSeconds(ProfileStore.Profile p, int jobs, int instances)
        {
            if (p == null || !(p.SecondsPerRun > 0)) return double.NaN;
            if (jobs <= 0) return 0;
            int inst = Math.Min(Math.Max(1, instances > 0 ? instances : p.Instances), jobs);
            double sim = jobs * p.SecondsPerRun;
            double launches = Math.Max(inst, Math.Ceiling(sim / RunScheduler.ChunkSeconds));
            double overhead = double.IsNaN(p.OverheadSeconds) ? 0 : Math.Max(0, p.OverheadSeconds);
            return (sim + launches * overhead) / inst;
        }
    }

    public enum JobStatus { Queued, Running, Finished, Stopped, Failed }

    /// <summary>One campaign in the queue.</summary>
    public sealed class QueueJob
    {
        public int Id;
        public string Name = "";
        public string Source = "This PC";
        public CampaignRequest Request;
        public CampaignOptions Options;         // of the step that runs now (a worst case by sensitivity has two)
        public CampaignEngine Engine;           // while it runs
        public JobStatus Status = JobStatus.Queued;
        public string Message = "";             // how it ended, when that needs words
        public DateTime Added = DateTime.Now;
        public DateTime? Started, Ended;
        public string OutputDir;                // known once it starts
        public int Total, Done, Ok, NoData, Failed;
        public TimeSpan Elapsed;
        public double EstimateSeconds = double.NaN;
        public bool AnalyzeOnFinish, DiscardOnFinish;   // what the Stop dialog asked for
        public object Tag;                      // the listener's ledger entry

        public string Analysis => Options != null ? CampaignBuilder.Describe(Options) : "";
        public bool Pending => Status == JobStatus.Queued || Status == JobStatus.Running;
    }

    /// <summary>
    /// Campaigns run one after another, each with the whole machine. Not thread-safe: every member is used from the
    /// thread that created the queue (the engines' callbacks are posted to it).
    /// </summary>
    public sealed class CampaignQueue
    {
        readonly HardwareInfo hw;
        readonly ProfileStore profiles;
        readonly Action<Action> post;
        readonly List<QueueJob> jobs = new List<QueueJob>();
        int nextId = 1;
        bool hold;

        public CampaignQueue(HardwareInfo hw, ProfileStore profiles, Action<Action> post)
        {
            this.hw = hw;
            this.profiles = profiles;
            this.post = post;
        }

        public IReadOnlyList<QueueJob> Jobs => jobs;
        public QueueJob Running { get; private set; }
        public int QueuedCount => jobs.Count(j => j.Status == JobStatus.Queued);

        /// <summary>A job started; true = the second step of a worst case (same campaign, same folder).</summary>
        public event Action<QueueJob, bool> Started;
        /// <summary>A job ended (finished, stopped or failed). Its engine is still attached during the call.</summary>
        public event Action<QueueJob> Finished;
        /// <summary>A job was stopped and its folder deleted; false = some files could not be deleted.</summary>
        public event Action<QueueJob, bool> Discarded;
        /// <summary>A waiting job was taken out of the queue before it started.</summary>
        public event Action<QueueJob> Removed;
        public event Action Changed;

        /// <summary>Finish the running campaign but start nothing new.</summary>
        public bool Hold
        {
            get => hold;
            set { hold = value; if (!hold) StartNext(); Changed?.Invoke(); }
        }

        public QueueJob Add(CampaignRequest request, CampaignOptions options, string source = "This PC", object tag = null, string name = null)
        {
            var j = new QueueJob
            {
                Id = nextId++,
                Name = name ?? Path.GetFileNameWithoutExtension(request.AscPath),
                Source = source,
                Request = request,
                Options = options,
                Total = options.Jobs,
                Tag = tag,
                EstimateSeconds = CampaignBuilder.EstimateSeconds(options, hw, profiles),
            };
            jobs.Add(j);
            StartNext();
            Changed?.Invoke();
            return j;
        }

        /// <summary>Takes a job that is not running out of the list.</summary>
        public bool Remove(QueueJob j)
        {
            if (j == null || j.Status == JobStatus.Running) return false;
            bool gone = jobs.Remove(j);
            if (gone && j.Status == JobStatus.Queued) Removed?.Invoke(j);
            if (gone) Changed?.Invoke();
            return gone;
        }

        /// <summary>Moves a waiting job earlier (-1) or later (+1) among the waiting ones.</summary>
        public bool Move(QueueJob j, int delta)
        {
            int i = jobs.IndexOf(j);
            if (i < 0 || j.Status != JobStatus.Queued) return false;
            int k = i + delta;
            if (k < 0 || k >= jobs.Count || jobs[k].Status != JobStatus.Queued) return false;
            jobs[i] = jobs[k];
            jobs[k] = j;
            Changed?.Invoke();
            return true;
        }

        public void ClearEnded()
        {
            if (jobs.RemoveAll(j => !j.Pending) > 0) Changed?.Invoke();
        }

        /// <summary>How many campaigns run before this one.</summary>
        public int Ahead(QueueJob j)
        {
            int n = Running != null ? 1 : 0;
            foreach (var q in jobs)
            {
                if (q == j) break;
                if (q.Status == JobStatus.Queued) n++;
            }
            return n;
        }

        /// <summary>Seconds until a waiting job starts; NaN when a campaign before it has no estimate yet.</summary>
        public double WaitSeconds(QueueJob j)
        {
            double t = Running != null ? RemainingSeconds(Running) : 0;
            foreach (var q in jobs)
            {
                if (q == j) break;
                if (q.Status == JobStatus.Queued) t += q.EstimateSeconds;
            }
            return t;
        }

        /// <summary>Seconds a new job would wait if it were added now.</summary>
        public double WaitSecondsForNew() => WaitSeconds(null);

        public double RemainingSeconds(QueueJob j)
        {
            if (j.Engine == null) return double.NaN;
            var s = j.Engine.Snapshot();
            double eta = s.EtaSeconds;
            if (double.IsNaN(eta) && !double.IsNaN(j.EstimateSeconds)) eta = Math.Max(0, j.EstimateSeconds - s.Elapsed.TotalSeconds);
            // a worst case by sensitivity still has its verification runs to do: a few more, not estimated
            return eta;
        }

        void StartNext()
        {
            while (Running == null && !hold)
            {
                var next = jobs.FirstOrDefault(j => j.Status == JobStatus.Queued);
                if (next == null) return;
                if (Launch(next))
                {
                    Started?.Invoke(next, false);
                    return;
                }
                Finished?.Invoke(next);       // could not start: reported like any other failure, then the next one
            }
        }

        bool Launch(QueueJob j)
        {
            var o = j.Options;
            try
            {
                if (string.IsNullOrEmpty(o.OutputDir)) o.OutputDir = NewFolder(j.Request.OutputRoot, j.Request.AscPath);
                j.OutputDir = o.OutputDir;
                var e = new CampaignEngine(o, hw, profiles);
                e.Finished += () => post(() => OnEngineFinished(j, e));
                e.Start();
                j.Engine = e;
            }
            catch (Exception ex)
            {
                j.Engine = null;
                j.Status = JobStatus.Failed;
                j.Message = "Could not start the campaign: " + ex.Message;
                j.Ended = DateTime.Now;
                return false;
            }
            j.Status = JobStatus.Running;
            j.Total = o.Jobs;
            if (j.Started == null) j.Started = DateTime.Now;
            Running = j;
            return true;
        }

        static string NewFolder(string root, string ascPath)
        {
            if (string.IsNullOrWhiteSpace(root)) root = CampaignBuilder.DefaultRoot(ascPath);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string dir = Path.Combine(root, stamp);
            for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(root, stamp + "_" + i);
            return dir;
        }

        void OnEngineFinished(QueueJob j, CampaignEngine e)
        {
            if (j.Engine != e) return;
            var snap = e.Snapshot();
            j.Total = snap.Total; j.Done = snap.Done; j.Ok = snap.Ok; j.NoData = snap.NoData; j.Failed = snap.Failed;
            j.Elapsed = snap.Elapsed;

            if (j.DiscardOnFinish)
            {
                bool gone = DeleteFolder(j.OutputDir);
                j.Engine = null;
                j.Status = JobStatus.Stopped;
                j.Ended = DateTime.Now;
                jobs.Remove(j);
                Running = null;
                Discarded?.Invoke(j, gone);
                StartNext();
                Changed?.Invoke();
                return;
            }

            var o = e.Options;
            string note = null;
            if (o.WcStage == 1 && !snap.Cancelled && snap.FatalError == null && !j.AnalyzeOnFinish)
            {
                var done = e.GetResults();
                if (CampaignBuilder.Screened(done, o.Mc.Sites.Count, o.CornerCount))
                {
                    // worst case by sensitivity: the screening told which way each tolerance pushes; now verify
                    var r2 = j.Request.Clone();
                    r2.ResumeDir = o.OutputDir;
                    r2.Resume = done;
                    r2.PriorSeconds = snap.Elapsed.TotalSeconds;
                    r2.Seed = o.Seed;
                    var o2 = CampaignBuilder.Build(r2, null, out note);
                    if (o2 != null)
                    {
                        j.Options = o2;
                        Running = null;
                        if (Launch(j))
                        {
                            Started?.Invoke(j, true);
                            Changed?.Invoke();
                            return;
                        }
                        note = j.Message;
                    }
                }
            }

            j.Status = snap.FatalError != null ? JobStatus.Failed : snap.Cancelled ? JobStatus.Stopped : JobStatus.Finished;
            j.Message = snap.FatalError ?? note ?? (snap.AutoStopped ? "Stopped early: " + (snap.StopStatus ?? "the answer is known") : "");
            j.Ended = DateTime.Now;
            Running = null;
            Finished?.Invoke(j);
            j.Engine = null;        // the results are on disk; the next campaign gets the memory
            StartNext();
            Changed?.Invoke();
        }

        /// <summary>Deletes a campaign folder; LTspice may hold files for a moment after being killed.</summary>
        public static bool DeleteFolder(string dir)
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
    }
}
