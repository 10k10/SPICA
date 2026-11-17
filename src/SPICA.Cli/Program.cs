using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Spica.Core;

namespace Spica.Cli
{
    /// <summary>
    /// SPICA without a window: runs a campaign, watches a folder for campaigns (the listener), shows a listener's
    /// state, prints an error budget. Same engine and same files as the SPICA window; settings from SPICA.ini next
    /// to this exe, overridden by the options given.
    /// </summary>
    static class Program
    {
        // exit codes, kept stable for the scripts that call SPICA
        const int Ok = 0, Some = 1, Bad = 2, Stopped = 3;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "help") { Help(); return args.Length == 0 ? Bad : Ok; }
            if (args[0] == "--version" || args[0] == "version") { Console.WriteLine("SPICA " + Version); return Ok; }
            Args a;
            try { a = Args.Parse(args.Skip(1)); }
            catch (ArgumentException ex) { return Fail(ex.Message + "  (SPICA-cli --help)"); }
            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "run": return RunCampaign(a);
                    case "listen": return Listen(a);
                    case "status": return Status(a);
                    case "budget": return Budget(a);
                    default: return Fail($"Unknown command '{args[0]}'.  (SPICA-cli --help)");
                }
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        static void Help()
        {
            Console.WriteLine($@"SPICA {Version} - Spice Parallel Instancer Circuit Analysis, command line

  SPICA-cli run <setup.spica | bundle.zip | schematic.asc> [options]
      Runs one Monte Carlo campaign and waits for it. The results go to a new
      folder (results.csv, samples.csv, campaign.log, campaign.spica); its path
      is the last line printed.
        --out <folder>      where the campaign folder is made (default: <schematic>_SPICA)
        --runs <n>          runs, instead of the setup's
        --seed <n>          seed, instead of the setup's (0 = a new one)
        --raw / --no-raw    keep the .raw waveform files, or not
        --strict            stop at any warning instead of going on
        --quiet             print only the campaign folder
  SPICA-cli listen <folder> [--out <folder>] [options]
      Watches a folder for campaigns (.spica, or .zip bundles sent with Run Remote)
      and runs them one after the other, until Ctrl+C. Results go to <folder>\results
      by default, so the PCs that send campaigns can open them.
  SPICA-cli status <folder>
      The state of the listener watching a folder, and its campaigns.
  SPICA-cli budget <campaign folder> [--sigma <k>]
      The error budget of a finished campaign, as the Error Budget page shows it.

  Options of run and listen:
        --cores <n>         LTspice instances at a time (default: SPICA.ini, else automatic)
        --threads <n>       threads per instance (default 1)
        --ltspice <path>    LTspice.exe (default: SPICA.ini, else found automatically)

  Ctrl+C once: finish what is running, start nothing new. Twice: stop now.

  Exit codes: 0 done (budget: every error PASS); 1 runs failed or without data
  (budget: a FAIL, or 4 when only CHECK); 2 could not run; 3 stopped by Ctrl+C.");
        }

        static int Fail(string message)
        {
            Console.Error.WriteLine("SPICA: " + message.Replace("\r", "").Replace("\n", " "));
            return Bad;
        }

        // ------------------------------------------------------------------ arguments

        sealed class Args
        {
            public readonly List<string> Files = new List<string>();
            public string Out, Ltspice;
            public int? Cores, Threads, Runs;
            public uint? Seed;
            public bool? Raw;
            public bool Strict, Quiet;
            public double? Sigma;

            public static Args Parse(IEnumerable<string> list)
            {
                var a = new Args();
                var e = list.GetEnumerator();
                string Next(string opt) => e.MoveNext() ? e.Current : throw new ArgumentException($"{opt} needs a value.");
                int Int(string opt, int min) => int.TryParse(Next(opt), NumberStyles.Integer, Inv, out var v) && v >= min ? v : throw new ArgumentException($"{opt} needs a whole number of at least {min}.");
                while (e.MoveNext())
                {
                    string s = e.Current;
                    switch (s.ToLowerInvariant())
                    {
                        case "--out": a.Out = Next(s); break;
                        case "--ltspice": a.Ltspice = Next(s); break;
                        case "--cores": a.Cores = Int(s, 1); break;
                        case "--threads": a.Threads = Int(s, 1); break;
                        case "--runs": a.Runs = Int(s, 1); break;
                        case "--seed": a.Seed = uint.TryParse(Next(s), NumberStyles.Integer, Inv, out var sd) ? sd : throw new ArgumentException("--seed needs a whole number."); break;
                        case "--sigma": a.Sigma = double.TryParse(Next(s), NumberStyles.Float, Inv, out var k) && k > 0 ? k : throw new ArgumentException("--sigma needs a positive number."); break;
                        case "--raw": a.Raw = true; break;
                        case "--no-raw": a.Raw = false; break;
                        case "--strict": a.Strict = true; break;
                        case "--quiet": a.Quiet = true; break;
                        default:
                            if (s.StartsWith("-")) throw new ArgumentException($"Unknown option {s}.");
                            a.Files.Add(s);
                            break;
                    }
                }
                return a;
            }

            public string File1(string what) => Files.Count == 1 ? Files[0] : throw new ArgumentException(Files.Count == 0 ? $"Give {what}." : "Give only one " + what + ".");
        }

        // ------------------------------------------------------------------ the machinery the window would give

        /// <summary>The thread that owns the queue: what the engines and the listener post runs here, in order.</summary>
        sealed class Loop
        {
            readonly BlockingCollection<Action> q = new BlockingCollection<Action>();
            public void Post(Action a) => q.Add(a);

            /// <summary>Runs what is posted, and tick every interval, until tick returns false.</summary>
            public void Run(Func<bool> tick, int ms = 250)
            {
                var clock = Stopwatch.StartNew();
                long next = 0;
                while (true)
                {
                    if (q.TryTake(out var a, Math.Max(1, (int)(next - clock.ElapsedMilliseconds)))) { a(); continue; }
                    next = clock.ElapsedMilliseconds + ms;
                    if (!tick()) return;
                }
            }
        }

        sealed class Machine
        {
            public readonly AppSettings Settings = new AppSettings();
            public readonly ProfileStore Profiles = new ProfileStore();
            public readonly HardwareInfo Hw = HardwareInfo.Detect();
            public string Ltspice;
            public RunScheduler.Config? Manual;

            public Machine(Args a)
            {
                Ltspice = a.Ltspice ?? Settings.LtspicePath;
                if (string.IsNullOrEmpty(Ltspice) || !File.Exists(Ltspice)) Ltspice = Core.Ltspice.Locate();
                if (string.IsNullOrEmpty(Ltspice) || !File.Exists(Ltspice))
                    throw new FileNotFoundException("LTspice.exe was not found: give it with --ltspice, or set it in SPICA (Tools > Options).");
                if (a.Cores.HasValue || a.Threads.HasValue)
                    Manual = new RunScheduler.Config(a.Cores ?? Hw.PhysicalCores, a.Threads ?? 1);
                else if (!Settings.AutoParallel)
                    Manual = new RunScheduler.Config(Math.Max(1, Settings.ManualInstances > 0 ? Settings.ManualInstances : Hw.PhysicalCores), Math.Max(1, Settings.ManualThreads));
            }

            public string Parallel => Manual.HasValue ? Manual.Value.ToString() : $"automatic ({Hw.PhysicalCores} cores / {Hw.LogicalProcessors} threads)";
        }

        /// <summary>Ctrl+C once: finish what runs, start nothing new; twice: stop now.</summary>
        sealed class Interrupt
        {
            public volatile int Count;
            public Interrupt(Action first, Action second)
            {
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    int n = ++Count;
                    if (n == 1) { Console.Error.WriteLine("\nStopping: the running simulations finish, nothing new starts. Ctrl+C again to stop now."); first(); }
                    else if (n == 2) { Console.Error.WriteLine("\nStopping now."); second(); }
                };
            }
        }

        static string Span(double s) => double.IsNaN(s) || double.IsInfinity(s) ? "-" : s >= 3600 ? $"{(int)(s / 3600)}h{(int)(s % 3600 / 60):00}m" : s >= 60 ? $"{(int)(s / 60)}m{(int)(s % 60):00}s" : $"{s:0}s";

        static string Progress(QueueJob j)
        {
            var s = j.Engine?.Snapshot();
            if (s == null) return j.Name + ": starting";
            double pct = s.Total > 0 ? 100.0 * s.Done / s.Total : 0;
            return $"{j.Name}: {pct,3:0}%  {s.Done:N0}/{s.Total:N0} runs  ok {s.Ok:N0}  no data {s.NoData:N0}  failed {s.Failed:N0}  " +
                   $"{s.Config}  elapsed {Span(s.Elapsed.TotalSeconds)}  left {Span(s.EtaSeconds)}";
        }

        // ------------------------------------------------------------------ run

        static int RunCampaign(Args a)
        {
            string path = Path.GetFullPath(a.File1("the setup (.spica), a bundle (.zip) or a schematic (.asc)"));
            if (!File.Exists(path)) return Fail("Not found: " + path);
            var m = new Machine(a);
            void Say(string s) { if (!a.Quiet) Console.WriteLine(s); }

            // what to run: a setup, a bundle (unpacked beside its results), or a schematic with the default setup
            SpicaProject project;
            string asc;
            bool bundle = path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            if (bundle)
            {
                string root = a.Out ?? Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_SPICA");
                string dir = Path.Combine(root, "_received", Path.GetFileNameWithoutExtension(path) + "_" + File.GetLastWriteTime(path).ToString("yyyyMMdd_HHmmss", Inv));
                Listener.Unzip(path, dir);
                string spica = Directory.GetFiles(dir, "*.spica").FirstOrDefault(x => !Path.GetFileName(x).Equals("campaign.spica", StringComparison.OrdinalIgnoreCase));
                if (spica == null) return Fail("The .zip has no SPICA setup (.spica).");
                project = SpicaProject.Load(spica);
                asc = project.Schematic;
                if (a.Out == null) a.Out = root;
            }
            else if (path.EndsWith(".asc", StringComparison.OrdinalIgnoreCase))
            {
                asc = path;
                project = new SpicaProject { Schematic = asc };
            }
            else
            {
                project = SpicaProject.Load(path);
                asc = project.Schematic;
            }
            if (string.IsNullOrEmpty(asc) || !File.Exists(asc)) return Fail("Schematic not found: " + (string.IsNullOrEmpty(asc) ? "(none named in the setup)" : asc));
            if (a.Runs.HasValue) project.Runs = a.Runs.Value;
            if (a.Seed.HasValue) project.Seed = a.Seed.Value;
            if (a.Raw.HasValue) project.KeepRaw = a.Raw.Value;

            Netlist netlist;
            try { netlist = SchematicLoader.Load(m.Ltspice, asc, out _, bundle, out _); }
            catch (Exception ex) { return Fail("Could not read the schematic: " + ex.Message); }

            var request = new CampaignRequest
            {
                LtspiceExe = m.Ltspice, AscPath = asc, OutputRoot = a.Out == null ? null : Path.GetFullPath(a.Out),
                Netlist = netlist, Project = project, Manual = m.Manual,
            };
            bool stop = false;
            var options = CampaignBuilder.Build(request, (text, warn) =>
            {
                Console.Error.WriteLine((a.Strict ? "SPICA: stopped at a warning: " : "warning: ") + text.Replace("\r", "").Replace("\n", " "));
                if (a.Strict) stop = true;
                return !a.Strict;
            }, out string error);
            if (options == null) return stop ? Bad : Fail(error ?? "The setup cannot be run.");

            var loop = new Loop();
            var queue = new CampaignQueue(m.Hw, m.Profiles, loop.Post);
            QueueJob job = null;
            job = queue.Add(request, options, "Command line");
            if (job.Status == JobStatus.Failed) return Fail(job.Message);
            Say($"SPICA {Version}: {job.Name}, {CampaignBuilder.Describe(options)}, {m.Parallel}");
            Say("Results: " + job.OutputDir);

            new Interrupt(() => loop.Post(() => job.Engine?.StopAfterRunning()), () => loop.Post(() => job.Engine?.Cancel()));
            bool live = !Console.IsOutputRedirected && !a.Quiet;
            var shown = Stopwatch.StartNew();
            long lastShown = -1;
            int width = 0;
            loop.Run(() =>
            {
                if (job.Pending)
                {
                    long every = live ? 1000 : 30000;
                    if (!a.Quiet && shown.ElapsedMilliseconds - lastShown >= every)
                    {
                        lastShown = shown.ElapsedMilliseconds;
                        string line = Progress(job);
                        if (live) { Console.Write("\r" + line.PadRight(width)); width = line.Length; }
                        else Console.WriteLine(line);
                    }
                    return true;
                }
                return false;
            });
            if (live) Console.WriteLine();

            Say($"{job.Status}: {job.Ok:N0} runs with data, {job.NoData:N0} without, {job.Failed:N0} failed, in {Span(job.Elapsed.TotalSeconds)}" +
                (job.Message.Length > 0 ? ". " + job.Message : ""));
            if (!a.Quiet && job.OutputDir != null && Directory.Exists(job.OutputDir) && project.Errors.Count > 0) PrintVerdicts(job.OutputDir, null, brief: true);
            Console.WriteLine(job.OutputDir);
            return job.Status == JobStatus.Failed ? Bad : job.Status == JobStatus.Stopped ? Stopped : job.Failed + job.NoData > 0 ? Some : Ok;
        }

        // ------------------------------------------------------------------ listen

        static int Listen(Args a)
        {
            string input = Path.GetFullPath(a.File1("the folder to watch"));
            var m = new Machine(a);
            string output = Path.GetFullPath(a.Out ?? Listener.SuggestOutput(input));
            var loop = new Loop();
            var queue = new CampaignQueue(m.Hw, m.Profiles, loop.Post);
            var listener = new Listener(queue, loop.Post) { LtspiceExe = () => m.Ltspice, Manual = () => m.Manual };
            listener.Start(input, output);
            Console.WriteLine($"SPICA {Version} listening on {input}");
            Console.WriteLine($"Results: {output}   LTspice: {m.Ltspice}   {m.Parallel}");
            Console.WriteLine("Send campaigns here with Run Remote, or copy a .spica / .zip into the folder. Ctrl+C to stop.");

            // what each campaign file is doing, printed when it changes
            var said = new Dictionary<ListenerEntry, string>();
            listener.Changed += () =>
            {
                foreach (var e in listener.Entries)
                {
                    string now = e.Status + "|" + e.Message;
                    if (said.TryGetValue(e, out var was) && was == now) continue;
                    said[e] = now;
                    string at = DateTime.Now.ToString("HH:mm:ss", Inv);
                    string what = e.Status == ListenerStatus.Running ? "started" + (e.Results.Length > 0 ? " -> " + e.Results : "")
                                : e.Status == ListenerStatus.Queued ? $"queued ({queue.QueuedCount} waiting)"
                                : e.Status.ToString().ToLowerInvariant() + (e.Message.Length > 0 ? ": " + e.Message : "");
                    Console.WriteLine($"{at}  {e.File}: {what}");
                }
            };

            bool closing = false;
            new Interrupt(() => loop.Post(() => { closing = true; listener.Stop(); queue.Hold = true; queue.Running?.Engine?.StopAfterRunning(); }),
                          () => loop.Post(() => queue.Running?.Engine?.Cancel()));
            var clock = Stopwatch.StartNew();
            long lastShown = 0;
            loop.Run(() =>
            {
                listener.Tick();
                if (queue.Running != null && clock.ElapsedMilliseconds - lastShown >= 60000)
                {
                    lastShown = clock.ElapsedMilliseconds;
                    Console.WriteLine(DateTime.Now.ToString("HH:mm:ss", Inv) + "  " + Progress(queue.Running));
                }
                return !(closing && queue.Running == null);
            }, 500);
            int left = queue.QueuedCount;
            Console.WriteLine("Stopped." + (left > 0 ? $" {left} campaign(s) not started: they are taken again at the next start." : ""));
            return Ok;
        }

        // ------------------------------------------------------------------ status

        static int Status(Args a)
        {
            string folder = Path.GetFullPath(a.File1("the watched folder"));
            var st = RemoteState.Read(folder);
            if (st == null) return Fail("No listener has written its state in " + folder + " (none started there, or one older than SPICA 0.6.2).");
            Console.WriteLine($"{st.Machine}: {(st.Alive ? "listening" : st.State == "listening" ? "not answering (last seen " + st.Beat.ToString("yyyy-MM-dd HH:mm", Inv) + ")" : "stopped")}   SPICA {st.Spica}");
            Console.WriteLine($"watching {st.Input}, results in {st.Output}");
            if (st.Jobs.Count == 0) { Console.WriteLine("no campaigns"); return st.Alive ? Ok : Some; }
            int w = Math.Max(10, st.Jobs.Max(j => j.File.Length));
            Console.WriteLine($"{"campaign".PadRight(w)}  {"status",-8}  {"runs",-14}  note");
            foreach (var j in st.Jobs)
            {
                string runs = j.Total > 0 ? $"{j.Done:N0}/{j.Total:N0}" : "";
                string note = j.Status == "Running" ? "left " + Span(j.Eta)
                            : j.Status == "Queued" ? $"{j.Ahead} ahead, starts in {Span(j.Wait)}"
                            : j.Message;
                Console.WriteLine($"{j.File.PadRight(w)}  {j.Status,-8}  {runs,-14}  {note}");
            }
            return st.Alive ? Ok : Some;
        }

        // ------------------------------------------------------------------ budget

        static int Budget(Args a)
        {
            string folder = Path.GetFullPath(a.File1("the campaign folder"));
            if (!File.Exists(Path.Combine(folder, "results.csv"))) return Fail("No results.csv in " + folder);
            return PrintVerdicts(folder, a.Sigma, brief: false);
        }

        /// <summary>The error budget of a campaign folder; the exit code of the worst verdict.</summary>
        static int PrintVerdicts(string folder, double? sigma, bool brief)
        {
            var rs = ResultSet.Load(folder);
            ErrorBudget.Register(rs, rs.Setup.Errors);
            if (rs.Setup.Errors.Count == 0) return Fail("No error is defined in this campaign's setup (define them on the Error Budget page).");
            double k = sigma ?? rs.Setup.DesignSigma;
            var verdicts = new List<string>();
            foreach (var it in rs.Setup.Errors)
            {
                var r = ErrorBudget.Compute(rs, it, 0, k, rs.Setup.PessimisticSigma);
                verdicts.Add(r.Error != null ? "ERROR" : r.Verdict);
                if (brief)
                    Console.WriteLine($"  {it.Name}: {(r.Error != null ? "ERROR: " + r.Error : r.Verdict + " at " + k.ToString("0.#", Inv) + " sigma, " + r.VerdictText())}");
                else
                {
                    Console.WriteLine(r.Summary().TrimEnd());
                    Console.WriteLine(new string('-', 78));
                }
            }
            return verdicts.Contains("FAIL") ? Some : verdicts.Contains("CHECK") ? 4 : verdicts.Contains("ERROR") ? Bad : Ok;
        }
    }
}
