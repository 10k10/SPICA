using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Spica.Core
{
    public enum ListenerStatus { Waiting, Queued, Running, Done, Stopped, Failed }

    /// <summary>One job file seen in the watched folder, and what became of it.</summary>
    public sealed class ListenerEntry
    {
        public string File = "";            // relative to the watched folder
        public string Stamp = "";           // size and time of the file when it was taken: a changed file is a new job
        public ListenerStatus Status;
        public DateTime Received = DateTime.Now;
        public DateTime? Started, Ended;
        public string Results = "";         // the campaign folder
        public string Message = "";
        public QueueJob Job;                // while it is in this session's queue
    }

    /// <summary>
    /// Watches a folder (usually shared with other PCs): every setup (.spica, with its schematic beside it) or source
    /// bundle (.zip) placed there is queued once and run on this machine; the results go to another folder. A ledger
    /// in the results folder remembers what already ran, and a status file in the watched folder shows the queue to
    /// whoever sent the job. Used from the UI thread; the folder is read on a worker thread.
    /// </summary>
    public sealed class Listener
    {
        public const string StatusFile = "SPICA_listener.txt";
        public const string LedgerFile = "SPICA_listener_jobs.tsv";
        const double ScanSeconds = 4, HeartbeatSeconds = 30;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly CampaignQueue queue;
        readonly Action<Action> post;
        readonly List<ListenerEntry> entries = new List<ListenerEntry>();
        readonly Dictionary<string, string> seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // worker thread only
        bool scanning;
        DateTime nextScan = DateTime.MinValue, lastStatusWrite = DateTime.MinValue;
        string lastStatus = "";
        int session;                        // scans started before a Stop/Start are ignored when they return

        public Func<string> LtspiceExe = () => null;
        public Func<RunScheduler.Config?> Manual = () => null;

        public Listener(CampaignQueue queue, Action<Action> post)
        {
            this.queue = queue;
            this.post = post;
            queue.Started += (j, second) => { if (j.Tag is ListenerEntry e && Owns(e)) { e.Status = ListenerStatus.Running; e.Started = e.Started ?? DateTime.Now; e.Results = j.OutputDir ?? ""; Touch(); } };
            queue.Finished += j =>
            {
                if (!(j.Tag is ListenerEntry e) || !Owns(e)) return;
                e.Status = j.Status == JobStatus.Finished ? ListenerStatus.Done : j.Status == JobStatus.Stopped ? ListenerStatus.Stopped : ListenerStatus.Failed;
                e.Ended = DateTime.Now;
                e.Results = j.OutputDir ?? e.Results;
                e.Message = j.Message.Length > 0 ? j.Message : $"{j.Ok:N0} runs with data, {j.NoData:N0} without, {j.Failed:N0} failed";
                e.Job = null;
                Touch();
            };
            queue.Discarded += (j, gone) =>
            {
                if (!(j.Tag is ListenerEntry e) || !Owns(e)) return;
                e.Status = ListenerStatus.Stopped; e.Ended = DateTime.Now; e.Results = ""; e.Message = "Stopped and discarded here."; e.Job = null;
                Touch();
            };
            queue.Removed += j =>
            {
                if (!(j.Tag is ListenerEntry e) || !Owns(e)) return;
                e.Status = ListenerStatus.Stopped; e.Ended = DateTime.Now; e.Message = "Removed from the queue before it started."; e.Job = null;
                Touch();
            };
        }

        public bool Active { get; private set; }
        public string Input { get; private set; } = "";
        public string Output { get; private set; } = "";
        public string Problem { get; private set; } = "";      // why the folder cannot be read now
        public DateTime? LastScan { get; private set; }
        public IReadOnlyList<ListenerEntry> Entries => entries;
        public event Action Changed;

        bool Owns(ListenerEntry e) => entries.Contains(e);

        /// <summary>A results folder to propose for a watched folder: beside it, so results are never taken for jobs.</summary>
        public static string SuggestOutput(string input)
        {
            input = (input ?? "").Trim().TrimEnd('\\', '/');
            if (input.Length == 0) return "";
            string parent = Path.GetDirectoryName(input);
            // a share root (\\pc\share) or a drive has no place beside it: inside, then
            return string.IsNullOrEmpty(parent) ? Path.Combine(input, "results") : input + "_results";
        }

        /// <summary>Starts watching. Throws when a folder cannot be used.</summary>
        public void Start(string input, string output)
        {
            input = Path.GetFullPath(input.Trim());
            output = Path.GetFullPath(output.Trim());
            if (!Directory.Exists(input)) throw new DirectoryNotFoundException("The folder to watch does not exist:\n" + input);
            if (string.Equals(input.TrimEnd('\\'), output.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The results folder must not be the watched folder itself.");
            Directory.CreateDirectory(output);

            Stop();
            Input = input;
            Output = output;
            entries.Clear();
            seen.Clear();
            LoadLedger();
            // jobs that were waiting or running when SPICA closed never finished: they are taken again
            entries.RemoveAll(e => e.Status == ListenerStatus.Waiting || e.Status == ListenerStatus.Queued || e.Status == ListenerStatus.Running);
            Active = true;
            Problem = "";
            session++;
            nextScan = DateTime.MinValue;
            lastStatus = "";
            Touch();
        }

        public void Stop()
        {
            if (!Active) return;
            Active = false;
            session++;
            // what waits in the queue stays there (it was accepted); only new files are no longer taken
            SaveLedger();
            string text = StatusText();
            string file = Path.Combine(Input, StatusFile);
            Task.Run(() => { try { File.WriteAllText(file, text, Encoding.UTF8); } catch { } });
            Changed?.Invoke();
        }

        /// <summary>Runs a job file once more (its entry is forgotten, so the next look at the folder takes it).</summary>
        public void RunAgain(ListenerEntry e)
        {
            if (e == null || e.Job != null || !entries.Remove(e)) return;
            nextScan = DateTime.MinValue;
            Touch();
        }

        /// <summary>Forgets the jobs that ended; their files are not run again unless they change.</summary>
        public int PendingCount => entries.Count(e => e.Status == ListenerStatus.Waiting || e.Status == ListenerStatus.Queued || e.Status == ListenerStatus.Running);

        void Touch()
        {
            SaveLedger();
            lastStatus = "";            // the status file is rewritten at the next look
            Changed?.Invoke();
        }

        /// <summary>Call often (the UI timer): looks at the folder every few seconds, on a worker thread.</summary>
        public void Tick()
        {
            if (!Active || scanning || DateTime.UtcNow < nextScan) return;

            // a job taken out of the queue by hand
            foreach (var e in entries)
                if (e.Status == ListenerStatus.Queued && e.Job != null && !queue.Jobs.Contains(e.Job))
                {
                    e.Status = ListenerStatus.Stopped; e.Ended = DateTime.Now; e.Message = "Removed from the queue here."; e.Job = null;
                    SaveLedger();
                }

            scanning = true;
            int mine = session;
            string input = Input, output = Output, exe = LtspiceExe();
            var manual = Manual();
            var known = entries.Where(e => e.Status != ListenerStatus.Waiting).ToDictionary(e => e.File, e => e.Stamp, StringComparer.OrdinalIgnoreCase);
            string status = StatusText();
            bool writeStatus = status != lastStatus || (DateTime.UtcNow - lastStatusWrite).TotalSeconds >= HeartbeatSeconds;
            if (writeStatus) { lastStatus = status; lastStatusWrite = DateTime.UtcNow; status = status.Replace("{now}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv)); }

            Task.Run(() =>
            {
                var found = new List<Found>();
                string problem = "";
                try
                {
                    if (writeStatus) { try { File.WriteAllText(Path.Combine(input, StatusFile), status, Encoding.UTF8); } catch { } }
                    found = Scan(input, output, exe, manual, known);
                }
                catch (Exception ex) { problem = ex.Message; }
                post(() =>
                {
                    scanning = false;
                    nextScan = DateTime.UtcNow.AddSeconds(ScanSeconds);
                    if (mine != session) return;
                    LastScan = DateTime.Now;
                    bool changed = problem != Problem;
                    Problem = problem;
                    foreach (var f in found) changed |= Take(f);
                    if (changed) Touch();
                });
            });
        }

        sealed class Found
        {
            public string File, Stamp, Message, Name;
            public CampaignRequest Request;
            public CampaignOptions Options;     // null = not runnable (yet): Message says why
            public bool Wait;                   // may become runnable without the file changing (its schematic is still being copied)
        }

        bool Take(Found f)
        {
            var e = entries.FirstOrDefault(x => x.File.Equals(f.File, StringComparison.OrdinalIgnoreCase));
            if (e != null && e.Status != ListenerStatus.Waiting)
            {
                if (e.Stamp == f.Stamp) return false;
                entries.Remove(e);              // the file changed since it ran: a new job
                e = null;
            }
            if (e == null) { e = new ListenerEntry { File = f.File }; entries.Add(e); }
            else if (f.Options == null && f.Wait && e.Message == f.Message && e.Stamp == f.Stamp) return false;
            e.Stamp = f.Stamp;
            e.Message = f.Message ?? "";
            if (f.Options == null)
            {
                e.Status = f.Wait ? ListenerStatus.Waiting : ListenerStatus.Failed;
                if (!f.Wait) e.Ended = DateTime.Now;
                return true;
            }
            e.Status = ListenerStatus.Queued;
            e.Job = queue.Add(f.Request, f.Options, "Listener", e, f.Name);
            // the queue may have started it at once, or failed to: those events came before Job was set
            if (e.Job.Status == JobStatus.Failed) { e.Status = ListenerStatus.Failed; e.Message = e.Job.Message; e.Ended = DateTime.Now; e.Job = null; }
            return true;
        }

        // ------------------------------------------------------------------ worker thread

        static string StampOf(FileInfo f) => f.Length.ToString(Inv) + "@" + f.LastWriteTimeUtc.Ticks.ToString(Inv);

        List<Found> Scan(string input, string output, string exe, RunScheduler.Config? manual, Dictionary<string, string> known)
        {
            var files = new List<string>();
            files.AddRange(Directory.GetFiles(input, "*.spica"));
            files.AddRange(Directory.GetFiles(input, "*.zip"));
            foreach (var sub in Directory.GetDirectories(input))
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith("_") || name.StartsWith(".") || name.EndsWith("_SPICA", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(Path.GetFullPath(sub).TrimEnd('\\'), output.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                try { files.AddRange(Directory.GetFiles(sub, "*.spica")); } catch { }
            }

            var found = new List<Found>();
            var now = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in files)
            {
                if (Path.GetFileName(path).Equals("campaign.spica", StringComparison.OrdinalIgnoreCase)) continue;
                string rel = path.Substring(input.TrimEnd('\\').Length + 1);
                string stamp;
                try { stamp = StampOf(new FileInfo(path)); } catch { continue; }
                now[rel] = stamp;
                if (known.TryGetValue(rel, out var ran) && ran == stamp) continue;
                // taken only when it has stopped changing between two looks: it may still be copying
                if (!seen.TryGetValue(rel, out var before) || before != stamp) continue;
                found.Add(Prepare(path, rel, stamp, output, exe, manual));
            }
            seen.Clear();
            foreach (var kv in now) seen[kv.Key] = kv.Value;
            return found;
        }

        static Found Prepare(string path, string rel, string stamp, string output, string exe, RunScheduler.Config? manual)
        {
            var f = new Found { File = rel, Stamp = stamp, Name = Path.GetFileNameWithoutExtension(path) };
            try
            {
                if (!File.Exists(exe ?? "")) { f.Message = "LTspice.exe is not set on this PC (Tools > Options)."; f.Wait = true; return f; }
                try { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { } }
                catch (IOException) { f.Message = "The file is still in use (being copied?)."; f.Wait = true; return f; }

                SpicaProject project;
                string asc;
                if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    string dir = Path.Combine(output, "_received", f.Name + "_" + File.GetLastWriteTime(path).ToString("yyyyMMdd_HHmmss", Inv));
                    Unzip(path, dir);
                    string spica = Directory.GetFiles(dir, "*.spica").FirstOrDefault(x => !Path.GetFileName(x).Equals("campaign.spica", StringComparison.OrdinalIgnoreCase));
                    if (spica != null) { project = SpicaProject.Load(spica); asc = project.Schematic; }
                    else
                    {
                        var ascs = Directory.GetFiles(dir, "*.asc");
                        if (ascs.Length != 1) { f.Message = "The .zip has no SPICA setup (.spica)" + (ascs.Length == 0 ? " and no schematic." : " and more than one schematic."); return f; }
                        asc = ascs[0];
                        project = new SpicaProject { Schematic = asc };
                    }
                }
                else
                {
                    project = SpicaProject.Load(path);
                    asc = project.Schematic;
                }
                if (string.IsNullOrEmpty(asc) || !File.Exists(asc))
                {
                    f.Message = "Waiting for its schematic: " + (string.IsNullOrEmpty(asc) ? "(none named in the setup)" : asc);
                    f.Wait = true;
                    return f;
                }

                var netlist = SchematicLoader.Load(exe, asc, out _);
                f.Request = new CampaignRequest
                {
                    LtspiceExe = exe,
                    AscPath = asc,
                    OutputRoot = Path.Combine(output, f.Name),
                    Netlist = netlist,
                    Project = project,
                    Manual = manual,
                };
                f.Options = CampaignBuilder.Build(f.Request, null, out var error);
                if (f.Options == null) f.Message = (error ?? "The setup cannot be run.").Replace("\r", "").Replace('\n', ' ');
            }
            catch (Exception ex)
            {
                f.Options = null;
                f.Message = ex.Message.Replace("\r", "").Replace('\n', ' ');
            }
            return f;
        }

        static void Unzip(string zip, string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            using (var a = ZipFile.OpenRead(zip))
                foreach (var en in a.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(dir, en.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;      // never outside the folder
                    if (en.FullName.EndsWith("/") || en.FullName.EndsWith("\\")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    en.ExtractToFile(target, true);
                }
        }

        // ------------------------------------------------------------------ ledger and status file

        static string T(DateTime? t) => t.HasValue ? t.Value.ToString("yyyy-MM-dd HH:mm:ss", Inv) : "";
        static DateTime? D(string s) => DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out var t) ? t : (DateTime?)null;
        static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace("\r", "").Replace('\n', ' ');

        void SaveLedger()
        {
            if (Output.Length == 0) return;
            try
            {
                var sb = new StringBuilder("file\tstamp\tstatus\treceived\tstarted\tended\tresults\tmessage\r\n");
                foreach (var e in entries)
                    sb.Append(string.Join("\t", Clean(e.File), e.Stamp, e.Status.ToString(), T(e.Received), T(e.Started), T(e.Ended), Clean(e.Results), Clean(e.Message))).Append("\r\n");
                string path = Path.Combine(Output, LedgerFile);
                File.WriteAllText(path + ".part", sb.ToString(), Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".part", path);
            }
            catch { }
        }

        void LoadLedger()
        {
            try
            {
                string path = Path.Combine(Output, LedgerFile);
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8).Skip(1))
                {
                    var p = line.Split('\t');
                    if (p.Length < 8 || !Enum.TryParse(p[2], true, out ListenerStatus st)) continue;
                    entries.Add(new ListenerEntry { File = p[0], Stamp = p[1], Status = st, Received = D(p[3]) ?? DateTime.Now, Started = D(p[4]), Ended = D(p[5]), Results = p[6], Message = p[7] });
                }
            }
            catch { }
        }

        /// <summary>What an entry is doing, for the list and the status file.</summary>
        public string Describe(ListenerEntry e)
        {
            var j = e.Job;
            switch (e.Status)
            {
                case ListenerStatus.Running:
                    if (j?.Engine == null) return "Running";
                    var s = j.Engine.Snapshot();
                    string eta = double.IsNaN(s.EtaSeconds) ? "" : ", ends about " + DateTime.Now.AddSeconds(s.EtaSeconds).ToString("HH:mm", Inv);
                    return $"Running: {s.Done:N0} of {s.Total:N0} ({100.0 * s.Done / Math.Max(1, s.Total):0} %){eta}";
                case ListenerStatus.Queued:
                    if (j == null) return "Queued";
                    int ahead = queue.Ahead(j);
                    double w = queue.WaitSeconds(j);
                    return ahead == 0 ? "Queued: next" : $"Queued: {ahead} before it" + (double.IsNaN(w) ? "" : ", starts about " + DateTime.Now.AddSeconds(w).ToString("HH:mm", Inv));
                case ListenerStatus.Waiting: return "Waiting";
                case ListenerStatus.Done: return "Done";
                case ListenerStatus.Stopped: return "Stopped";
                default: return "Failed";
            }
        }

        /// <summary>The status file's text; {now} is filled in when it is written.</summary>
        string StatusText()
        {
            var sb = new StringBuilder();
            sb.Append("SPICA listener\r\n\r\n");
            sb.Append($"Machine:   {Environment.MachineName} (user {Environment.UserName}), SPICA {SourceBundle.AppVersion}\r\n");
            sb.Append(Active ? "State:     LISTENING - last sign of life {now} (if that is long ago, SPICA is not running there)\r\n"
                             : "State:     NOT LISTENING since {now}\r\n".Replace("{now}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv)));
            sb.Append($"Watching:  {Input}\r\n");
            sb.Append($"Results:   {Output}\r\n\r\n");
            sb.Append("Put a SPICA setup (.spica) with its schematic, or a SPICA bundle (.zip, File > Send to a Listener Folder), in the watched folder.\r\n");
            sb.Append("Each file runs once; change it or use Run Again in the listener window to run it again.\r\n\r\n");
            if (entries.Count == 0) { sb.Append("No jobs yet.\r\n"); return sb.ToString(); }
            int wFile = Math.Max(4, entries.Max(e => e.File.Length)), wState = Math.Max(6, entries.Max(e => Describe(e).Length));
            sb.Append("Job".PadRight(wFile)).Append("  ").Append("Status".PadRight(wState)).Append("  Received          Finished          Results\r\n");
            foreach (var e in entries.OrderByDescending(e => e.Received))
            {
                sb.Append(e.File.PadRight(wFile)).Append("  ").Append(Describe(e).PadRight(wState)).Append("  ")
                  .Append(e.Received.ToString("yyyy-MM-dd HH:mm", Inv)).Append("  ")
                  .Append((e.Ended.HasValue ? e.Ended.Value.ToString("yyyy-MM-dd HH:mm", Inv) : "").PadRight(16)).Append("  ")
                  .Append(e.Results).Append("\r\n");
                if (e.Message.Length > 0 && e.Status != ListenerStatus.Running && e.Status != ListenerStatus.Queued)
                    sb.Append("".PadRight(wFile)).Append("  ").Append(e.Message).Append("\r\n");
            }
            return sb.ToString();
        }
    }
}
