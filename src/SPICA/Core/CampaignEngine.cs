using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Spica.Core
{
    public sealed class CampaignOptions
    {
        public string LtspiceExe;
        public string AscPath;
        public string OutputDir;
        public Netlist Netlist;
        public int Runs;
        public List<string> SaveSignals;      // null = no .save (LTspice saves everything)
        public bool KeepRaw = true;
        public bool ClockReseed = true;
        public RunScheduler.Config? Manual;

        public List<double> Corners;          // fixed temperatures: every run is simulated at each (null = one pass)
        public bool RandomTemp;               // per-run temperature, flat between TempMin and TempMax
        public double TempMin = -40, TempMax = 90;
        public McPlan Mc;                     // SPICA's paired sample generator (null = LTspice's mc())
        public uint Seed;
        public SpicaProject Setup;            // copied into the campaign folder as campaign.spica
        public List<RunResult> Resume;        // runs already done in OutputDir (resuming an interrupted campaign)
        public double PriorSeconds;           // wall time of the earlier sessions of a resumed campaign
        public StopRule Stop;                 // early stop when the answer is known (null = run all)
        public string Stage;                  // logged at start instead of the resume line (e.g. worst-case verification)
        public int WcStage;                   // worst case by sensitivity: 1 = screening, 2 = verification (0 = not)

        public int CornerCount => Corners != null && Corners.Count > 0 ? Corners.Count : 1;
        public int Jobs => Runs * CornerCount;

        /// <summary>The temperature of a run (NaN when the schematic's own temperature is used).</summary>
        public double TempOf(int run, int corner)
        {
            if (Corners != null && Corners.Count > 0) return Corners[corner];
            if (RandomTemp) return TempMin + (TempMax - TempMin) * McPlan.Uniform01(Seed, McPlan.TempStream, run);
            return double.NaN;
        }
    }

    public enum RunStatus { Ok, NoData, Failed }

    public sealed class RunResult
    {
        public int Run;
        public int Corner;                    // index into CampaignOptions.Corners (0 when there are none)
        public double Temp = double.NaN;
        public int Chunk;
        public RunStatus Status;
        public Dictionary<string, string> Values;
        public string Note = "";
    }

    public sealed class WorkerView
    {
        public int Id, Pid, First, Last, Threads;
        public double Temp = double.NaN;
        public string State = "";
        public double Seconds;
        public long Memory;
    }

    public sealed class EngineSnapshot
    {
        public int Total, Ok, NoData, Failed, Allocated, Chunks;
        public TimeSpan Elapsed;
        public double EtaSeconds = double.NaN;
        public double RunsPerMinute;
        public string Config = "", Phase = "";
        public double OverheadSeconds = double.NaN, SecondsPerRun = double.NaN;
        public long PeakMemory;
        public long RawBytes;
        public double RawForecast = double.NaN;
        public List<WorkerView> Workers = new List<WorkerView>();
        public bool Finished, Cancelled, Draining, AutoStopped;
        public string StopStatus;             // auto-stop progress (null = no auto-stop)
        public int Running;                   // simulations inside chunks that are running now
        public string FatalError;
        public int Done => Ok + NoData + Failed;
    }

    /// <summary>Runs a Monte Carlo campaign as many parallel LTspice batch instances.</summary>
    public sealed class CampaignEngine
    {
        /// <summary>
        /// A chunk: jobs First..Last. A job is one run at one temperature corner:
        /// job = corner * Runs + run, so a chunk never spans two corners.
        /// </summary>
        sealed class Plan
        {
            public int Id, First, Last, Threads, Instances, Epoch;
            public bool IsRetry;
            public int Count => Last - First + 1;
        }

        sealed class Worker
        {
            public int Id, Pid, First, Last, Threads;
            public double Temp = double.NaN;
            public string State = "Starting";
            public Stopwatch Clock = new Stopwatch();
            public long Memory;
            public Process Proc;
            public bool Exited;
            public int Count;                        // runs in the chunk now running
            public double Expected = double.NaN;     // its expected wall time (start-up + Count * x), NaN while x is unknown
        }

        readonly CampaignOptions opt;
        readonly HardwareInfo hw;
        readonly ProfileStore profiles;
        readonly object sync = new object();
        readonly object launchGate = new object();
        readonly object logLock = new object();
        readonly List<Worker> workers = new List<Worker>();
        readonly List<Thread> threads = new List<Thread>();
        readonly SortedDictionary<int, RunResult> results = new SortedDictionary<int, RunResult>();
        readonly Queue<int> retry = new Queue<int>();
        // (time, runs done + partial credit for running chunks), for the smoothed rate
        readonly List<KeyValuePair<double, double>> history = new List<KeyValuePair<double, double>>();
        double rateEma = double.NaN, etaEma = double.NaN, lastTick = double.NaN;
        readonly Stopwatch clock = new Stopwatch();
        readonly string ascDir;

        public readonly ConcurrentQueue<string> LogQueue = new ConcurrentQueue<string>();
        public event Action Finished;

        // jobs not handed out yet, as inclusive ranges in job order (one range for a fresh campaign)
        readonly List<int[]> pending = new List<int[]>();

        RunScheduler sched;
        StreamWriter logFile;
        string chunksDir, iniPath;
        int pendingCount, resumedCount, chunkSeq, workerSeq, activeWorkers, blockedCount;
        long rawBytes;
        DateTime lastLaunch = DateTime.MinValue, lastSave = DateTime.MinValue;
        double saveInterval = 1;      // seconds between progress saves; grows with the cost of a save
        volatile bool cancel, draining, dirty;
        bool finished, saveWarned;
        string fatal;
        DateTime started = DateTime.Now;      // when the campaign first started (kept when it is resumed)

        public CampaignEngine(CampaignOptions options, HardwareInfo hardware, ProfileStore profileStore)
        {
            opt = options;
            hw = hardware;
            profiles = profileStore;
            ascDir = Path.GetDirectoryName(opt.AscPath);
        }

        public CampaignOptions Options => opt;
        public IList<string> MeasNames => opt.Netlist.MeasNames;

        int CornerOf(int job) => (job - 1) / opt.Runs;
        int RunOf(int job) => (job - 1) % opt.Runs + 1;
        int CornerEnd(int job) => (CornerOf(job) + 1) * opt.Runs;   // last job of the same corner

        // ------------------------------------------------------------------ lifecycle

        public void Start()
        {
            bool resuming = opt.Resume != null;
            Directory.CreateDirectory(opt.OutputDir);
            chunksDir = Path.Combine(opt.OutputDir, "chunks");
            Directory.CreateDirectory(chunksDir);
            logFile = new StreamWriter(Path.Combine(opt.OutputDir, "campaign.log"), resuming, Encoding.UTF8) { AutoFlush = true };
            iniPath = Ltspice.WritePrivateIni(Path.Combine(opt.OutputDir, "LTspice.private.ini"), opt.ClockReseed);

            if (resuming)
            {
                // failed runs are simulated again; chunk numbers continue after the existing files
                foreach (var r in opt.Resume)
                    if (r.Status != RunStatus.Failed && r.Run >= 1 && r.Run <= opt.Runs && r.Corner >= 0 && r.Corner < opt.CornerCount)
                        results[r.Corner * opt.Runs + r.Run] = r;
                resumedCount = results.Count;
                chunkSeq = ExistingChunkCount();
                Log("");
            }
            BuildPending();
            // auto-stop: the first chunks cover the minimum runs; later ones follow the rule's own estimate
            if (opt.Stop != null && opt.Stop.Enabled) stopRemaining = Math.Max(1, opt.Stop.MinRuns * opt.CornerCount - results.Count);

            Log($"SPICA campaign: {opt.Runs} runs of {Path.GetFileName(opt.AscPath)}" +
                (opt.CornerCount > 1 ? $" x {opt.CornerCount} temperatures = {opt.Jobs} simulations" : ""));
            if (opt.Stage != null) Log(opt.Stage);
            else if (resuming) Log($"Resuming: {resumedCount} of {opt.Jobs} simulations already done, {pendingCount} to go.");
            if (opt.Stop != null && opt.Stop.Enabled) Log($"Auto-stop: {opt.Stop.Describe()}; at most {opt.Runs} runs.");
            Log($"Machine: {hw}");
            Log($"LTspice: {opt.LtspiceExe} ({Ltspice.Version(opt.LtspiceExe)})");
            Log($"Seed: {opt.Seed}");
            if (opt.Mc != null)
            {
                Log($"MC generator: SPICA paired samples, {opt.Mc.Sites.Count} random sites (same unit at every temperature, reproducible from the seed).");
                foreach (var s in opt.Mc.Sites) Log($"  site {s.Index}: {s.Label} = {s.Original}");
                if (opt.Mc.Inlined.Count > 0) Log($"  .func inlined at each call: {string.Join(", ", opt.Mc.Inlined)}");
                foreach (var t in opt.Mc.TakenOver) Log("  drawn by SPICA: " + t);
                foreach (var w in opt.Mc.Warnings) Log("  WARNING " + w);
            }
            else Log(opt.ClockReseed ? "MC generator: LTspice mc(), reseeded from the clock (private ini), launches staggered."
                                     : "MC generator: LTspice mc(), NOT reseeded - chunks will repeat the same random sequence!");
            if (opt.Corners != null && opt.Corners.Count > 0)
                Log("Temperature: corners " + string.Join(", ", opt.Corners.Select(t => t.ToString("0.###", CultureInfo.InvariantCulture))) + " °C");
            else if (opt.RandomTemp)
                Log($"Temperature: random per run, flat {opt.TempMin:0.###} .. {opt.TempMax:0.###} °C");
            if ((opt.Corners != null || opt.RandomTemp) && opt.Netlist.TempLines.Any())
                foreach (var t in opt.Netlist.TempLines) Log($"Replaced by SPICA's temperature: {t}");
            Log(opt.SaveSignals == null ? ".save: everything (no .save directive)"
                                        : $".save: {opt.SaveSignals.Count} signals -> {string.Join(" ", opt.SaveSignals.Take(12))}{(opt.SaveSignals.Count > 12 ? " ..." : "")}");
            foreach (var s in opt.Netlist.RemovedSteps) Log($"Removed from netlist: {s}");
            if (opt.Netlist.IsOpAnalysis) Log(".op analysis: each chunk gets a dummy first .step so .meas report one row per run.");

            if (opt.Setup != null)
            {
                try
                {
                    var copy = opt.Setup.Clone();
                    copy.Seed = opt.Seed;
                    copy.Save(Path.Combine(opt.OutputDir, "campaign.spica"));
                }
                catch (Exception ex) { Log("Could not write campaign.spica: " + ex.Message); }
            }
            if (resuming && CampaignState.Load(opt.OutputDir)?.Started is DateTime first) started = first;
            WriteBundle();

            var warm = profiles.Find(hw.MachineKey, opt.Netlist.CircuitHash());
            sched = new RunScheduler(hw, Math.Max(1, pendingCount), warm, opt.Manual, Log);

            WriteState("Running");
            clock.Start();
            var ctl = new Thread(ControllerLoop) { IsBackground = true, Name = "SPICA controller" };
            ctl.Start();
        }

        public void Cancel()
        {
            if (cancel) return;
            cancel = true;
            Log("Stop requested: terminating LTspice instances...");
            lock (sync)
                foreach (var w in workers)
                    KillQuietly(w.Proc);
        }

        /// <summary>Start nothing new; the campaign ends when the chunks already running are done.</summary>
        public void StopAfterRunning()
        {
            if (cancel || draining) return;
            draining = true;
            Log("Stop requested: no new simulations; waiting for the running ones to finish...");
        }

        public bool IsFinished { get { lock (sync) return finished; } }

        bool Stopped => cancel || (draining && Unallocated > 0 && !autoStopped);

        // ------------------------------------------------------------------ auto-stop

        const double AutoStopChunkSeconds = 30;
        volatile bool autoStopped;
        string stopStatus;
        int stopRemaining = int.MaxValue;     // simulations the stop rule still expects to need
        int stopCheckedAt = -1;
        DateTime nextStopCheck = DateTime.MinValue;

        /// <summary>Every 2 s with new results: is the answer known? Then finish what runs and stop.</summary>
        void CheckStop()
        {
            if (opt.Stop == null || !opt.Stop.Enabled || draining || cancel || DateTime.UtcNow < nextStopCheck) return;
            nextStopCheck = DateTime.UtcNow.AddSeconds(2);
            List<RunResult> list;
            lock (sync)
            {
                if (results.Count == stopCheckedAt) return;
                stopCheckedAt = results.Count;
                list = results.Values.ToList();
            }
            var specs = opt.Setup;
            var check = opt.Stop.Evaluate(list, opt.Netlist.MeasNames, m => specs?.Spec(m), opt.CornerCount,
                c => opt.Corners != null && c < opt.Corners.Count ? opt.Corners[c].ToString("0.###", CultureInfo.InvariantCulture) + " °C" : "corner " + (c + 1));
            lock (sync)
            {
                stopStatus = check.Status;
                int least = Enumerable.Range(0, opt.CornerCount).Min(c => list.Count(r => r.Corner == c));
                long rem = check.RunsNeeded >= int.MaxValue ? int.MaxValue : (long)Math.Max(0, check.RunsNeeded - least) * opt.CornerCount;
                // runs handed out but not finished count as already on their way
                stopRemaining = (int)Math.Max(0, Math.Min(int.MaxValue, rem - (opt.Jobs - Unallocated - results.Count)));
                if (!check.Done || draining) return;
                autoStopped = true;
                draining = true;
            }
            Log($"Auto-stop: the answer is known after {list.Count} simulations ({check.Status}). Finishing the running ones, starting nothing new.");
            foreach (var line in check.Lines) Log("  " + line);
        }

        // ------------------------------------------------------------------ controller

        void ControllerLoop()
        {
            // keep Windows from sleeping while the campaign runs (the display may still turn off)
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);
            try
            {
                while (!cancel)
                {
                    lock (sync)
                    {
                        long ours = workers.Sum(w => w.Memory);
                        sched.OnMemorySample(HardwareInfo.AvailableMemoryNow(), ours);

                        while (activeWorkers < sched.Current.Instances)
                        {
                            var plan = TakeChunk();
                            if (plan == null) break;
                            SpawnWorker(plan);
                        }

                        UpdateRate();

                        if (activeWorkers == 0 && (Unallocated == 0 || draining)) break;
                    }
                    SaveProgress(false);
                    CheckStop();
                    Thread.Sleep(200);
                }
            }
            catch (Exception ex)
            {
                fatal = "Controller error: " + ex.Message;
                Log(fatal);
                cancel = true;
            }

            foreach (var t in threads.ToArray()) t.Join();
            Finish();
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
        }

        int Unallocated => pendingCount + retry.Count;

        // ------------------------------------------------------------------ rate and ETA

        /// <summary>
        /// Runs finish in bursts: a chunk of ~2 min reports all its runs at once, and instances started together
        /// finish together. Counting finished runs alone gives a rate that jumps between zero and a spike.
        /// Here a running chunk is credited for the share of its expected time that has passed (at most 95 %),
        /// so progress grows smoothly; the rate is its slope over 90 s, then low-pass filtered (time constant 20 s).
        /// </summary>
        double Progress()
        {
            double p = results.Count;
            foreach (var w in workers)
                if (w.Proc != null && !w.Exited && w.Expected > 0)
                    p += w.Count * Math.Min(0.95, w.Clock.Elapsed.TotalSeconds / w.Expected);
            return p;
        }

        /// <summary>
        /// Seconds of work left, from the measured run time x rather than from the rate. The remaining launches are
        /// played out on the N instances: each one starts when an instance frees up (after its running chunk), so
        /// the last round counts whole. Sharing the work evenly (work / N) is optimistic with long runs: 20 runs
        /// of 260 s on 16 instances take two rounds, not 1.25. NaN while x is unknown.
        /// </summary>
        double ModelEta()
        {
            double x = sched.SecondsPerRun, ov = sched.OverheadSeconds;
            if (double.IsNaN(x) || x <= 0) return double.NaN;
            if (double.IsNaN(ov)) ov = 1;
            int n = Math.Max(1, sched.Current.Instances);
            int left = draining ? 0 : Unallocated;

            // when each instance frees up: running chunks first (a chunk past its expected time needs a bit more)
            var free = new List<double>();
            foreach (var w in workers)
                if (w.Proc != null && !w.Exited && w.Expected > 0)
                {
                    double el = w.Clock.Elapsed.TotalSeconds;
                    free.Add(el < w.Expected ? w.Expected - el : Math.Max(0.05 * w.Expected, 0.1 * el));
                }
            free.Sort();
            double end = free.Count > 0 ? free[free.Count - 1] : 0;
            if (left == 0) return end;
            if (free.Count > n) free.RemoveRange(0, free.Count - n);   // instances being reduced: keep the longest
            while (free.Count < n) free.Insert(0, 0);

            // the scheduler's chunk rule: ~ChunkSeconds of work, at most an equal share of what is left
            double maxK = Math.Max(1, Math.Floor(RunScheduler.ChunkSeconds / x));
            if (left / maxK > 5000)
                return Math.Max(end, (free.Sum() + left * x + Math.Ceiling(left / maxK) * ov) / n);   // many launches: rounding is negligible
            var t = free.ToArray();
            while (left > 0)
            {
                int k = (int)Math.Min(left, Math.Max(1, Math.Min(maxK, Math.Ceiling(left / (double)n))));
                int i = 0;
                for (int j = 1; j < t.Length; j++) if (t[j] < t[i]) i = j;
                t[i] += ov + k * x;
                left -= k;
            }
            return Math.Max(end, t.Max());
        }

        void UpdateRate()
        {
            double now = clock.Elapsed.TotalSeconds;
            history.Add(new KeyValuePair<double, double>(now, Progress()));
            while (history.Count > 2 && history[0].Key < now - 90) history.RemoveAt(0);
            double dt = double.IsNaN(lastTick) ? 0 : now - lastTick;
            lastTick = now;

            var a = history[0]; var b = history[history.Count - 1];
            if (b.Key - a.Key > 5 && b.Value > a.Value)
            {
                double rate = (b.Value - a.Value) / (b.Key - a.Key) * 60;
                rateEma = double.IsNaN(rateEma) ? rate : rateEma + Math.Min(1, dt / 20) * (rate - rateEma);
            }

            // ETA: the model when x is known, else from the rate; it counts down by itself between updates
            double target = ModelEta();
            if (double.IsNaN(target) && rateEma > 0) target = (opt.Jobs - Progress()) / (rateEma / 60);
            if (double.IsNaN(target)) return;
            if (double.IsNaN(etaEma)) etaEma = target;
            else
            {
                double guess = etaEma - dt;
                etaEma = guess + Math.Min(1, dt / 10) * (target - guess);
            }
        }

        void BuildPending()
        {
            pending.Clear();
            pendingCount = 0;
            cornerAlloc = new int[opt.CornerCount];
            foreach (var job in results.Keys) cornerAlloc[CornerOf(job)]++;
            int start = 0;
            for (int job = 1; job <= opt.Jobs + 1; job++)
            {
                // ranges never span two corners, so chunks can be taken from the corners in turn
                bool missing = job <= opt.Jobs && !results.ContainsKey(job);
                bool boundary = start != 0 && job <= opt.Jobs && CornerOf(job) != CornerOf(start);
                if (start != 0 && (!missing || boundary))
                {
                    pending.Add(new[] { start, job - 1 });
                    pendingCount += job - start;
                    start = 0;
                }
                if (missing && start == 0) start = job;
            }
        }

        int[] cornerAlloc = new int[1];      // jobs handed out (or done) per temperature corner

        /// <summary>The pending range of the corner that is furthest behind: all temperatures advance together.</summary>
        int NextRange()
        {
            int best = 0;
            for (int i = 1; i < pending.Count; i++)
                if (cornerAlloc[CornerOf(pending[i][0])] < cornerAlloc[CornerOf(pending[best][0])]) best = i;
            return best;
        }

        int ExistingChunkCount()
        {
            int max = 0;
            try
            {
                foreach (var f in Directory.GetFiles(chunksDir, "chunk*.net"))
                {
                    string n = Path.GetFileName(f);
                    int us = n.IndexOf('_');
                    if (us > 5 && int.TryParse(n.Substring(5, us - 5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) max = Math.Max(max, id);
                }
            }
            catch { }
            return max;
        }

        Plan TakeChunk()
        {
            if (cancel || draining) return null;
            Plan p;
            if (retry.Count > 0)
            {
                int r = retry.Dequeue();
                p = new Plan { First = r, Last = r, IsRetry = true };
            }
            else
            {
                if (pending.Count == 0) return null;
                int ri = NextRange();
                var range = pending[ri];
                // auto-stop: short chunks, and no more than the runs still expected to be needed
                bool auto = opt.Stop != null && opt.Stop.Enabled;
                int n = auto ? sched.NextChunkSize(Math.Min(Unallocated, Math.Max(sched.Current.Instances, stopRemaining)), AutoStopChunkSeconds)
                             : sched.NextChunkSize(Unallocated);
                double x = sched.SecondsPerRun;
                if (auto && x > 0) n = Math.Max(n, (int)Math.Min(Unallocated, Math.Max(1, 5 / x)));   // >= ~5 s: launches stay cheap
                n = Math.Max(1, Math.Min(n, Math.Min(range[1], CornerEnd(range[0])) - range[0] + 1));
                p = new Plan { First = range[0], Last = range[0] + n - 1 };
                cornerAlloc[CornerOf(range[0])] += n;
                range[0] += n;
                pendingCount -= n;
                if (range[0] > range[1]) pending.RemoveAt(ri);
            }
            p.Id = ++chunkSeq;
            p.Threads = sched.Current.Threads;
            p.Instances = sched.Current.Instances;
            p.Epoch = sched.Epoch;
            return p;
        }

        void SpawnWorker(Plan first)
        {
            var w = new Worker { Id = ++workerSeq };
            workers.Add(w);
            activeWorkers++;
            var t = new Thread(() => WorkerLoop(w, first)) { IsBackground = true, Name = "SPICA worker " + w.Id };
            threads.Add(t);
            t.Start();
        }

        void WorkerLoop(Worker w, Plan plan)
        {
            try
            {
                while (plan != null && !cancel)
                {
                    WaitForMemory();
                    RunChunk(w, plan);
                    lock (sync)
                    {
                        plan = null;
                        if (cancel) break;
                        if (activeWorkers > sched.Current.Instances) break;   // scheduler wants fewer instances
                        plan = TakeChunk();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Worker {w.Id} error: {ex.Message}");
                lock (sync) if (plan != null) for (int j = plan.First; j <= plan.Last; j++) if (!results.ContainsKey(j)) retry.Enqueue(j);
            }
            finally
            {
                lock (sync)
                {
                    activeWorkers--;
                    workers.Remove(w);
                }
            }
        }

        void WaitForMemory()
        {
            while (!cancel)
            {
                var avail = HardwareInfo.AvailableMemoryNow();
                bool othersRunning;
                lock (sync) othersRunning = workers.Any(x => x.Proc != null && !x.Exited);
                if (!othersRunning || hw.TotalMemory <= 0 || avail > hw.TotalMemory * 0.06) return;
                Thread.Sleep(500);
            }
        }

        void Stagger()
        {
            // Each instance reseeds its MC generator from the clock: never start two in the same instant.
            lock (launchGate)
            {
                var wait = lastLaunch.AddMilliseconds(100) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                lastLaunch = DateTime.UtcNow;
            }
        }

        double ChunkTimeout(Plan p)
        {
            double perRun = sched.SecondsPerRun, ov = sched.OverheadSeconds;
            if (double.IsNaN(perRun) || perRun <= 0) return double.PositiveInfinity;
            return Math.Max(300, 20 * ((double.IsNaN(ov) ? 1 : ov) + p.Count * perRun));
        }

        // ------------------------------------------------------------------ one chunk

        void RunChunk(Worker w, Plan p)
        {
            int corner = CornerOf(p.First), first = RunOf(p.First), last = RunOf(p.Last);
            bool corners = opt.Corners != null && opt.Corners.Count > 0;
            string name = $"chunk{p.Id:D5}_r{first}-{last}" + (corners ? "_T" + opt.Corners[corner].ToString("0.###", CultureInfo.InvariantCulture) : "");
            string net = Path.Combine(chunksDir, name + ".net");
            var spec = new ChunkSpec
            {
                First = first, Last = last, Threads = p.Threads, Save = opt.SaveSignals, BaseDir = ascDir,
                Mc = opt.Mc, Seed = opt.Seed,
                FixedTemp = corners ? opt.Corners[corner] : (double?)null,
                TempOfRun = opt.RandomTemp && !corners ? (Func<int, double>)(r => opt.TempOf(r, 0)) : null,
            };
            File.WriteAllText(net, opt.Netlist.BuildChunk(spec), opt.Netlist.Encoding);

            Stagger();
            if (cancel) return;

            var psi = new ProcessStartInfo(opt.LtspiceExe, $"-b \"{net}\" -ini \"{iniPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = chunksDir,
            };

            var sw = Stopwatch.StartNew();
            Process proc;
            try { proc = Process.Start(psi); }
            catch (Exception ex)
            {
                Fail(p, "Could not start LTspice: " + ex.Message, false);
                return;
            }
            lock (sync)
            {
                w.Proc = proc; w.Exited = false; w.Pid = proc.Id; w.First = first; w.Last = last;
                w.Temp = corners ? opt.Corners[corner] : double.NaN;
                w.Threads = p.Threads; w.State = p.IsRetry ? "Retry" : "Running"; w.Clock.Restart(); w.Memory = 0;
                w.Count = p.Count;
                double x = sched.SecondsPerRun, ov = sched.OverheadSeconds;
                w.Expected = double.IsNaN(x) || x <= 0 ? double.NaN : (double.IsNaN(ov) ? 1 : ov) + p.Count * x;
            }
            proc.OutputDataReceived += (s, e) => { };
            proc.ErrorDataReceived += (s, e) => { };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            long peak = 0;
            string blockedBy = null;
            bool timedOut = false;
            double timeout = ChunkTimeout(p);
            while (!proc.WaitForExit(200))
            {
                try
                {
                    proc.Refresh();
                    peak = Math.Max(peak, proc.PeakWorkingSet64);
                    lock (sync) w.Memory = proc.WorkingSet64;
                    if (proc.MainWindowHandle != IntPtr.Zero) blockedBy = proc.MainWindowTitle;
                }
                catch { }
                if (blockedBy != null || cancel) { KillQuietly(proc); break; }
                if (sw.Elapsed.TotalSeconds > timeout) { timedOut = true; KillQuietly(proc); break; }
            }
            proc.WaitForExit();
            sw.Stop();
            proc.Dispose();
            lock (sync) { w.Exited = true; w.State = "Parsing"; w.Memory = 0; }

            if (cancel) { lock (sync) w.State = "Stopped"; return; }

            if (blockedBy != null)
            {
                Fail(p, $"LTspice opened a window (\"{blockedBy}\") instead of running in batch mode.", false);
                if (++blockedCount >= 2)
                {
                    fatal = $"LTspice keeps opening the window \"{blockedBy}\" in batch mode. Open LTspice once normally, answer any start-up dialog, then retry.";
                    Log(fatal);
                    Cancel();
                }
                return;
            }

            string logPath = Path.Combine(chunksDir, name + ".log");
            string rawPath = Path.Combine(chunksDir, name + ".raw");
            var cl = LogParser.Parse(logPath, opt.Netlist.MeasNames);
            long raw = 0;
            try
            {
                if (File.Exists(rawPath)) raw = new FileInfo(rawPath).Length;
                string opRaw = Path.Combine(chunksDir, name + ".op.raw");
                if (File.Exists(opRaw)) raw += new FileInfo(opRaw).Length;
                if (!opt.KeepRaw)
                {
                    // LTspice 26 also leaves a .db and a .log.raw beside each chunk.
                    foreach (string f in new[] { rawPath, opRaw, Path.Combine(chunksDir, name + ".db"), logPath + ".raw" })
                        if (File.Exists(f)) File.Delete(f);
                }
            }
            catch { }

            bool chunkFailed = timedOut || !cl.Completed;
            string reason = timedOut ? $"timed out after {sw.Elapsed.TotalSeconds:0} s"
                          : !cl.Completed ? (cl.Errors.FirstOrDefault() ?? "LTspice stopped before finishing")
                          : "";

            int ok = 0, noData = 0, failed = 0, requeued = 0;
            lock (sync)
            {
                if (opt.KeepRaw) rawBytes += raw;
                for (int i = 0; i < p.Count; i++)
                {
                    int job = p.First + i;
                    cl.Steps.TryGetValue(i + 1, out var vals);
                    bool hasData = vals != null && vals.Count > 0;
                    var rr = NewResult(job, p.Id);
                    rr.Values = vals ?? new Dictionary<string, string>();
                    if (hasData || (!chunkFailed && opt.Netlist.MeasNames.Count == 0))
                    {
                        rr.Status = RunStatus.Ok; ok++;
                        if (hasData && vals.Count < opt.Netlist.MeasNames.Count) rr.Note = "some .meas failed";
                    }
                    else if (chunkFailed && !p.IsRetry)
                    {
                        retry.Enqueue(job); requeued++;
                        continue;
                    }
                    else if (chunkFailed)
                    {
                        rr.Status = RunStatus.Failed; rr.Note = reason; failed++;
                    }
                    else
                    {
                        rr.Status = RunStatus.NoData; rr.Note = "all .meas failed"; noData++;
                    }
                    results[job] = rr;
                }
                dirty = true;

                sched.OnChunkDone(new ChunkStat
                {
                    Runs = p.Count,
                    WallSeconds = sw.Elapsed.TotalSeconds,
                    SimSeconds = cl.ElapsedSeconds,
                    Instances = p.Instances,
                    Threads = p.Threads,
                    Epoch = p.Epoch,
                    Clean = p.Epoch == sched.Epoch,
                    PeakMemory = peak,
                    RawBytes = raw,
                    Failed = chunkFailed,
                }, Unallocated);
                w.State = "Idle";
            }

            string tag = $"Chunk {p.Id} {Describe(p)}: {sw.Elapsed.TotalSeconds:0.0} s";
            if (!double.IsNaN(cl.ElapsedSeconds)) tag += $" (sim {cl.ElapsedSeconds:0.0} s)";
            if (chunkFailed) Log($"{tag} - FAILED: {reason}" + (requeued > 0 ? $"; {requeued} runs re-queued one by one" : ""));
            else if (noData > 0) Log($"{tag} - {noData} runs without .meas data");
        }

        void Fail(Plan p, string why, bool retryable)
        {
            Log($"Chunk {p.Id} {Describe(p)}: {why}");
            lock (sync)
                for (int j = p.First; j <= p.Last; j++)
                {
                    if (retryable && !p.IsRetry) { retry.Enqueue(j); continue; }
                    var rr = NewResult(j, p.Id);
                    rr.Status = RunStatus.Failed; rr.Note = why;
                    results[j] = rr;
                    dirty = true;
                }
        }

        RunResult NewResult(int job, int chunk)
        {
            int run = RunOf(job), corner = CornerOf(job);
            return new RunResult { Run = run, Corner = corner, Temp = opt.TempOf(run, corner), Chunk = chunk, Values = new Dictionary<string, string>() };
        }

        string Describe(Plan p)
        {
            string s = $"runs {RunOf(p.First)}-{RunOf(p.Last)}";
            if (opt.Corners != null && opt.Corners.Count > 0) s += $" at {opt.Corners[CornerOf(p.First)]:0.###} °C";
            return s;
        }

        static void KillQuietly(Process p)
        {
            try { if (p != null && !p.HasExited) p.Kill(); } catch { }
        }

        // ------------------------------------------------------------------ results

        void Finish()
        {
            clock.Stop();
            bool stopped;
            lock (sync) stopped = Stopped;
            try
            {
                var list = GetResults();
                CheckDuplicates(list);
                WriteCsvAtomic(list);
                if (opt.Mc != null && opt.Mc.Sites.Count > 0) WriteSamples(Path.Combine(opt.OutputDir, "samples.csv"));
                WriteSummary(Path.Combine(opt.OutputDir, "summary.txt"), list, stopped);
                if (!stopped && !double.IsNaN(sched.SecondsPerRun))
                    profiles.Store(hw.MachineKey, opt.Netlist.CircuitHash(), sched.ToProfile());
                WriteState(stopped ? "Stopped" : "Finished");

                var snap = Snapshot();
                if (!autoStopped && !stopped && opt.Stop != null && opt.Stop.Enabled)
                {
                    nextStopCheck = DateTime.MinValue;    // judge the final results, not the last 2-second check
                    CheckStop();
                }
                if (autoStopped) Log($"Auto-stop: {snap.Done} of at most {opt.Jobs} simulations were needed.");
                else if (!stopped && opt.Stop != null && opt.Stop.Enabled && stopStatus != null)
                    Log($"Auto-stop: the maximum number of runs was reached before the answer was settled: {stopStatus}. " +
                        "A result this close to the requirement needs many more runs to call, or a design change.");
                Log(stopped ? $"Campaign stopped after {snap.Elapsed:hh\\:mm\\:ss}: {snap.Done} of {snap.Total} simulations done ({snap.Ok} ok, {snap.Failed} failed). It can be resumed from File > Open Results."
                            : $"Campaign finished in {snap.Elapsed:hh\\:mm\\:ss}: {snap.Ok} ok, {snap.NoData} without data, {snap.Failed} failed.");
                Log($"Results: {Path.Combine(opt.OutputDir, "results.csv")}");
            }
            catch (Exception ex) { Log("Error while writing results: " + ex.Message); }
            lock (sync) finished = true;
            try { logFile?.Dispose(); } catch { }
            Finished?.Invoke();
        }

        /// <summary>
        /// Writes results.csv and campaign.state while the campaign runs, so a crash can be resumed. The whole file is
        /// rewritten, so the interval is 20 times the cost of the last save (1 .. 30 s): saving stays under ~5 % of one
        /// core however many runs there are (100k runs take ~0.2 s to write).
        /// </summary>
        void SaveProgress(bool force)
        {
            if (!dirty && !force) return;
            if (!force && (DateTime.UtcNow - lastSave).TotalSeconds < saveInterval) return;
            dirty = false;
            var sw = Stopwatch.StartNew();
            WriteCsvAtomic(GetResults());
            WriteState("Running");
            saveInterval = Math.Max(1, Math.Min(30, 20 * sw.Elapsed.TotalSeconds));
            lastSave = DateTime.UtcNow;
        }

        void WriteCsvAtomic(List<RunResult> list)
        {
            string path = Path.Combine(opt.OutputDir, "results.csv"), tmp = path + ".tmp";
            try
            {
                WriteCsv(tmp, list);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                // e.g. results.csv open in a spreadsheet: keep running, try again on the next chunk
                dirty = true;
                if (!saveWarned) { saveWarned = true; Log("Could not update results.csv (" + ex.Message + "); retrying."); }
            }
        }

        /// <summary>The snapshot of what is simulated and where (written once: a resumed campaign keeps the first).</summary>
        void WriteBundle()
        {
            if (SourceBundle.Find(opt.OutputDir) != null) return;
            try
            {
                var warnings = new List<string>();
                Log("Sources: " + SourceBundle.Write(SourceBundle.PathFor(opt.OutputDir, opt.AscPath), opt, hw, started, warnings));
                foreach (var w in warnings) Log("  WARNING " + w);
            }
            catch (Exception ex) { Log("Could not write the sources bundle: " + ex.Message); }
        }

        void WriteState(string status)
        {
            try
            {
                var s = new CampaignState
                {
                    Status = status,
                    Jobs = autoStopped && status == "Finished" ? results.Count : opt.Jobs,
                    Done = results.Count,
                    ElapsedSeconds = opt.PriorSeconds + clock.Elapsed.TotalSeconds,
                    CircuitHash = opt.Netlist.CircuitHash(),
                    Started = started,
                    Ended = status == "Running" ? (DateTime?)null : DateTime.Now,
                };
                s.Save(opt.OutputDir);
            }
            catch { }
        }

        void CheckDuplicates(List<RunResult> list)
        {
            if (opt.Netlist.MeasNames.Count == 0) return;
            // Within one temperature corner two different units must never give identical rows.
            var groups = list.Where(r => r.Status == RunStatus.Ok && r.Values.Count == opt.Netlist.MeasNames.Count)
                             .GroupBy(r => r.Corner + "|" + string.Join("|", opt.Netlist.MeasNames.Select(m => r.Values.TryGetValue(m, out var v) ? v : "")))
                             .Where(g => g.Count() > 1).ToList();
            if (groups.Count == 0) { Log("Duplicate check: no repeated result rows."); return; }
            int n = groups.Sum(g => g.Count());
            Log($"WARNING duplicate check: {n} runs share identical .meas rows in {groups.Count} groups (e.g. runs {string.Join(", ", groups[0].Select(r => r.Run).Take(6))}).");
            foreach (var g in groups) foreach (var r in g) r.Note = (r.Note.Length > 0 ? r.Note + "; " : "") + "duplicate row";
        }

        public List<RunResult> GetResults()
        {
            lock (sync) return results.Values.ToList();
        }

        /// <summary>results.csv: one row per run and temperature.</summary>
        public static void WriteCsv(string path, IList<RunResult> list, IList<string> meas)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("run,temp,status");
            foreach (var m in meas) sb.Append(',').Append(Csv(m));
            sb.Append(",note\r\n");
            foreach (var r in list)
            {
                sb.Append(r.Run.ToString(inv)).Append(',');
                if (!double.IsNaN(r.Temp)) sb.Append(r.Temp.ToString("R", inv));
                sb.Append(',').Append(r.Status);
                foreach (var m in meas)
                    sb.Append(',').Append(Csv(r.Values.TryGetValue(m, out var v) ? v : ""));
                sb.Append(',').Append(Csv(r.Note)).Append("\r\n");
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        void WriteCsv(string path, List<RunResult> list) => WriteCsv(path, list, opt.Netlist.MeasNames);

        /// <summary>samples.csv: the normalized random value of every site for every run (uniform in [-1,1] or standard normal).</summary>
        void WriteSamples(string path)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("run");
            foreach (var s in opt.Mc.Sites) sb.Append(',').Append(Csv(McPlan.ColumnName(s)));
            sb.Append("\r\n");
            for (int r = 1; r <= opt.Runs; r++)
            {
                sb.Append(r.ToString(inv));
                foreach (var s in opt.Mc.Sites) sb.Append(',').Append(opt.Mc.ValueOf(opt.Seed, s, r).ToString("G9", inv));
                sb.Append("\r\n");
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public static string Csv(string s)
        {
            if (s == null) return "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        void WriteSummary(string path, List<RunResult> list, bool stopped)
        {
            var inv = CultureInfo.InvariantCulture;
            var snap = Snapshot();
            var sb = new StringBuilder();
            sb.AppendLine("SPICA - Spice Parallel Instancer Circuit Analysis");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine($"Schematic      : {opt.AscPath}");
            sb.AppendLine($"Runs requested : {opt.Runs}" + (opt.CornerCount > 1 ? $" x {opt.CornerCount} temperatures = {opt.Jobs} simulations" : ""));
            sb.AppendLine($"Seed           : {opt.Seed}{(opt.Mc != null ? " (SPICA paired samples)" : " (LTspice mc())")}");
            if (opt.Corners != null && opt.Corners.Count > 0) sb.AppendLine("Temperature    : corners " + string.Join(", ", opt.Corners.Select(t => t.ToString("0.###", inv))) + " °C");
            else if (opt.RandomTemp) sb.AppendLine($"Temperature    : random flat {opt.TempMin.ToString("0.###", inv)} .. {opt.TempMax.ToString("0.###", inv)} °C");
            sb.AppendLine($"Completed      : {snap.Ok} ok, {snap.NoData} no data, {snap.Failed} failed" +
                          (stopped ? $" (stopped: {snap.Done} of {snap.Total} simulations)" : "") +
                          (resumedCount > 0 ? $" ({resumedCount} from earlier sessions)" : ""));
            sb.AppendLine($"Wall time      : {snap.Elapsed:hh\\:mm\\:ss}");
            sb.AppendLine($"Machine        : {hw}");
            sb.AppendLine($"Final config   : {sched.Current} ({sched.State})");
            if (sched.BestThroughputPerMinute > 0) sb.AppendLine($"Best rate      : {sched.BestThroughputPerMinute:0.0} runs/min");
            sb.AppendLine($"Start-up cost  : {Fmt(sched.OverheadSeconds)} s per LTspice launch");
            sb.AppendLine($"Raw kept       : {rawBytes / 1048576.0:0.0} MB");
            sb.AppendLine();
            sb.AppendLine(string.Format(inv, "{0,-24} {1,8} {2,6} {3,14} {4,14} {5,14} {6,14}", "measurement", "temp", "n", "mean", "std", "min", "max"));
            var groups = new List<KeyValuePair<string, Func<RunResult, bool>>> { new KeyValuePair<string, Func<RunResult, bool>>("all", r => true) };
            if (opt.Corners != null && opt.Corners.Count > 1)
                for (int c = 0; c < opt.Corners.Count; c++)
                {
                    int cc = c;
                    groups.Add(new KeyValuePair<string, Func<RunResult, bool>>(opt.Corners[c].ToString("0.###", inv), r => r.Corner == cc));
                }
            foreach (var m in opt.Netlist.MeasNames)
                foreach (var g in groups)
                {
                    var vals = new List<double>();
                    foreach (var r in list)
                        if (r.Status == RunStatus.Ok && g.Value(r) && r.Values.TryGetValue(m, out var s) && LogParser.TryNumber(s, out var d) && !double.IsNaN(d))
                            vals.Add(d);
                    if (vals.Count == 0) { sb.AppendLine(string.Format(inv, "{0,-24} {1,8} {2,6}", m, g.Key, 0)); continue; }
                    var ds = Descriptive.Of(vals);
                    sb.AppendLine(string.Format(inv, "{0,-24} {1,8} {2,6} {3,14:G6} {4,14:G6} {5,14:G6} {6,14:G6}", m, g.Key, ds.N, ds.Mean, ds.Std, ds.Min, ds.Max));
                }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static string Fmt(double d) => double.IsNaN(d) ? "?" : d.ToString("0.###", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ status

        public EngineSnapshot Snapshot()
        {
            lock (sync)
            {
                var s = new EngineSnapshot
                {
                    Total = opt.Jobs,
                    Elapsed = clock.Elapsed + TimeSpan.FromSeconds(opt.PriorSeconds),
                    Chunks = chunkSeq,
                    Allocated = opt.Jobs - Unallocated,
                    Finished = finished,
                    Cancelled = Stopped,
                    Draining = draining && !finished,
                    FatalError = fatal,
                    RawBytes = rawBytes,
                    AutoStopped = autoStopped,
                    StopStatus = stopStatus,
                };
                if (autoStopped && finished) s.Total = results.Count;
                s.Running = finished ? 0 : Math.Max(0, s.Allocated - results.Count);   // handed out, not finished yet
                foreach (var r in results.Values)
                {
                    if (r.Status == RunStatus.Ok) s.Ok++;
                    else if (r.Status == RunStatus.NoData) s.NoData++;
                    else s.Failed++;
                }
                if (sched != null)
                {
                    s.Config = sched.Current.ToString();
                    s.Phase = sched.State.ToString();
                    s.OverheadSeconds = sched.OverheadSeconds;
                    s.SecondsPerRun = sched.SecondsPerRun;
                    s.PeakMemory = sched.MaxPeakMemory;
                    if (!double.IsNaN(sched.RawBytesPerRun) && opt.KeepRaw)
                        s.RawForecast = rawBytes + sched.RawBytesPerRun * (opt.Jobs - s.Done);
                }

                if (!double.IsNaN(rateEma) && rateEma > 0) s.RunsPerMinute = rateEma;
                if (!finished && !double.IsNaN(etaEma)) s.EtaSeconds = Math.Max(0, etaEma);

                foreach (var w in workers)
                    s.Workers.Add(new WorkerView
                    {
                        Id = w.Id, Pid = w.Pid, First = w.First, Last = w.Last, Threads = w.Threads, Temp = w.Temp,
                        State = w.State, Seconds = w.Clock.Elapsed.TotalSeconds, Memory = w.Memory,
                    });
                return s;
            }
        }

        void Log(string msg)
        {
            string line = $"{DateTime.Now:HH:mm:ss}  {msg}";
            LogQueue.Enqueue(line);
            try { lock (logLock) logFile?.WriteLine(line); } catch { }
        }
    }
}
