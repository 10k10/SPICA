using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Spica.Core
{
    /// <summary>
    /// Reads a schematic the way SPICA simulates it: LTspice converts it to a netlist, SPICA parses that. A conversion
    /// is kept (in %TEMP%\SPICA\netlists) while the schematic, the sheets and symbols beside it and LTspice itself are
    /// unchanged, so opening or running the same schematic again does not wait for LTspice.
    /// </summary>
    public static class SchematicLoader
    {
        static readonly object Gate = new object();     // LTspice writes the .net beside the schematic: one conversion at a time

        public static Netlist Load(string ltspiceExe, string ascPath)
        {
            lock (Gate)
            {
                string tmp = Path.Combine(Path.GetTempPath(), "SPICA");
                string cacheDir = Path.Combine(tmp, "netlists");
                Directory.CreateDirectory(cacheDir);
                string cached = Path.Combine(cacheDir, Key(ltspiceExe, ascPath) + ".net");
                if (File.Exists(cached))
                {
                    try
                    {
                        // the first line is the code page of the netlist LTspice wrote (the chunks are written in it)
                        var all = File.ReadAllText(cached, System.Text.Encoding.UTF8);
                        int nl = all.IndexOf('\n');
                        var enc = System.Text.Encoding.GetEncoding(int.Parse(all.Substring(0, nl).Trim(), CultureInfo.InvariantCulture));
                        return Netlist.Parse(all.Substring(nl + 1), ascPath, enc);
                    }
                    catch { }
                }
                string ini = Ltspice.WritePrivateIni(Path.Combine(tmp, "convert.ini"), true);
                string text = Ltspice.ConvertSchematic(ltspiceExe, ascPath, ini, out var encoding);
                try { File.WriteAllText(cached, encoding.CodePage.ToString(CultureInfo.InvariantCulture) + "\n" + text, System.Text.Encoding.UTF8); } catch { }
                return Netlist.Parse(text, ascPath, encoding);
            }
        }

        /// <summary>What the conversion depends on: LTspice, the schematic, and every sheet and symbol in its folder.</summary>
        static string Key(string exe, string asc)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Path.GetFullPath(exe).ToLowerInvariant()).Append('|').Append(File.GetLastWriteTimeUtc(exe).Ticks).Append('|').Append(Path.GetFullPath(asc).ToLowerInvariant());
            string dir = Path.GetDirectoryName(Path.GetFullPath(asc));
            foreach (var f in Directory.GetFiles(dir, "*.asc").Concat(Directory.GetFiles(dir, "*.asy")).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var fi = new FileInfo(f);
                sb.Append('|').Append(fi.Name.ToLowerInvariant()).Append(':').Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks);
            }
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return string.Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).Take(10).Select(b => b.ToString("x2")));
        }
    }

    /// <summary>How the tolerances are set in a campaign.</summary>
    public enum RunKind
    {
        /// <summary>One run, every tolerance SPICA drives at its nominal value.</summary>
        Nominal,
        /// <summary>N units drawn from the tolerances.</summary>
        MonteCarlo,
        /// <summary>The nominal unit, then every combination of the tolerance edges (2^n runs).</summary>
        WorstCase,
    }

    /// <summary>Everything a campaign needs, frozen when it is put in the queue: later edits of the study do not reach it.</summary>
    public sealed class CampaignRequest
    {
        public string Name = "";
        public string LtspiceExe, AscPath;
        public string OutputDir;                // the campaign folder (made when it starts)
        public Netlist Netlist;
        public RunScheduler.Config? Manual;     // null = SPICA finds the instances and threads

        public RunKind Kind = RunKind.Nominal;
        public int Runs = 1;                    // Monte Carlo
        public uint Seed;                       // 0 = a new seed
        public bool Paired;                     // Monte Carlo with SPICA's generator: the same units at every temperature
        public double Sigma = 3;                // worst case: gaussian tolerances pushed to +/- this many sigma
        public List<double> Temps;              // null or empty = the schematic's own temperature
        public List<string> Save;               // signals to keep in the .raw; null or empty = everything
    }

    /// <summary>Turns a request into the options of a campaign, or says why it cannot run.</summary>
    public static class CampaignBuilder
    {
        public static CampaignOptions Build(CampaignRequest r, out string error)
        {
            error = null;
            var netlist = r.Netlist;
            if (!File.Exists(r.LtspiceExe ?? "")) { error = "LTspice.exe was not found: set it in Tools > Options."; return null; }
            if (r.Manual.HasValue && (r.Manual.Value.Instances < 1 || r.Manual.Value.Threads < 1))
            {
                error = "The instances and threads per instance (Tools > Options) must be 1 or more.";
                return null;
            }
            List<double> temps = r.Temps?.Distinct().ToList();
            if (temps != null && temps.Any(t => t < -273 || t > 1000)) { error = "A temperature is out of range (-273 .. 1000 °C)."; return null; }
            if (temps != null && temps.Count == 0) temps = null;
            int cc = temps?.Count ?? 1;
            string at = cc > 1 ? $" at {cc} temperatures" : temps != null ? $" at {temps[0].ToString("0.###", CultureInfo.InvariantCulture)} °C" : "";

            McPlan plan = null;
            int runs;
            string analysis;
            switch (r.Kind)
            {
                case RunKind.Nominal:
                    // SPICA's generator with one design row of zeros: every tolerance it can drive at its nominal value
                    plan = McPlan.Build(netlist.Lines, netlist.SourcePath);
                    plan.Design = new List<double[]> { new double[plan.Sites.Count] };
                    runs = 1;
                    analysis = "Nominal" + at;
                    break;
                case RunKind.WorstCase:
                    plan = McPlan.Build(netlist.Lines, netlist.SourcePath);
                    int n = plan.Sites.Count;
                    if (n == 0) { error = "Worst case needs tolerances SPICA can drive: mc(), gauss() or flat() outside .subckt definitions. This schematic has none."; return null; }
                    if (n > WorstCase.MaxCornerSites) { error = $"{n} tolerances: every combination of their edges would be 2^{n} = {Math.Pow(2, n):N0} runs per temperature. Use Monte Carlo."; return null; }
                    plan.Design = WorstCase.CornerDesign(plan.Sites, r.Sigma);
                    runs = plan.Design.Count;
                    analysis = $"Worst case: nominal + 2^{n} combinations of {n} tolerance edges = {runs} runs" + at;
                    break;
                default:
                    if (r.Runs < 1 || r.Runs > 10_000_000) { error = "Monte Carlo runs must be 1 .. 10,000,000."; return null; }
                    runs = r.Runs;
                    if (r.Paired)
                    {
                        plan = McPlan.Build(netlist.Lines, netlist.SourcePath);
                        if (plan.Sites.Count == 0) plan = null;      // nothing SPICA can draw: LTspice's mc() as written
                    }
                    analysis = $"Monte Carlo, {runs:N0} runs" + at;
                    break;
            }

            List<string> save = null;
            if (r.Save != null && r.Save.Count > 0)
            {
                // what the .meas need, then the signals asked for that the circuit has
                save = new List<string>(netlist.RequiredSignals);
                var have = new HashSet<string>(save, StringComparer.OrdinalIgnoreCase);
                var known = new HashSet<string>(netlist.Nodes.Select(x => $"V({x})").Concat(netlist.Currents), StringComparer.OrdinalIgnoreCase);
                foreach (var s in r.Save)
                {
                    if (!known.Contains(s)) { error = $"The schematic has no signal {s} to save."; return null; }
                    if (have.Add(s)) save.Add(s);
                }
            }

            uint seed = r.Seed != 0 ? r.Seed : (uint)(Guid.NewGuid().GetHashCode() & 0x7FFFFFFF) | 1;
            return new CampaignOptions
            {
                LtspiceExe = r.LtspiceExe,
                AscPath = r.AscPath,
                OutputDir = r.OutputDir,
                Netlist = netlist,
                Runs = runs,
                SaveSignals = save,
                KeepRaw = true,              // the waveforms are what the results show
                Manual = r.Manual,
                Corners = temps,
                Mc = plan,
                Seed = seed,
                Analysis = analysis,
            };
        }

        /// <summary>Wall time of a campaign from the run times saved for its circuit on this machine; NaN when it never ran here.</summary>
        public static double EstimateSeconds(CampaignOptions o, HardwareInfo hw, ProfileStore profiles)
        {
            ProfileStore.Profile p;
            try { p = profiles.Find(hw.MachineKey, o.Netlist.CircuitHash()); } catch { p = null; }
            if (p == null || !(p.SecondsPerRun > 0)) return double.NaN;
            int jobs = o.Jobs;
            int inst = Math.Min(Math.Max(1, o.Manual?.Instances > 0 ? o.Manual.Value.Instances : p.Instances), jobs);
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
        public CampaignRequest Request;
        public CampaignOptions Options;
        public CampaignEngine Engine;           // while it runs
        public JobStatus Status = JobStatus.Queued;
        public string Message = "";             // how it ended, when that needs words
        public DateTime? Started, Ended;
        public string OutputDir;                // known once it starts
        public int Total, Done, Ok, NoData, Failed;
        public TimeSpan Elapsed;
        public double EstimateSeconds = double.NaN;
        public bool DiscardOnFinish;            // stopped with "delete what it simulated"

        public string Analysis => Options?.Analysis ?? "";
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

        public event Action<QueueJob> Started;
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

        public QueueJob Add(CampaignRequest request, CampaignOptions options)
        {
            var j = new QueueJob
            {
                Id = nextId++,
                Name = request.Name.Length > 0 ? request.Name : Path.GetFileNameWithoutExtension(request.AscPath),
                Request = request,
                Options = options,
                Total = options.Jobs,
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

        public double RemainingSeconds(QueueJob j)
        {
            if (j.Engine == null) return double.NaN;
            var s = j.Engine.Snapshot();
            double eta = s.EtaSeconds;
            if (double.IsNaN(eta) && !double.IsNaN(j.EstimateSeconds)) eta = Math.Max(0, j.EstimateSeconds - s.Elapsed.TotalSeconds);
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
                    Started?.Invoke(next);
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

            j.Status = snap.FatalError != null ? JobStatus.Failed : snap.Cancelled ? JobStatus.Stopped : JobStatus.Finished;
            j.Message = snap.FatalError ?? "";
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
