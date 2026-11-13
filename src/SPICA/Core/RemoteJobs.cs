using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Spica.Core
{
    /// <summary>What a listener says about itself and its jobs in its state file (Listener.StateFile).</summary>
    public sealed class RemoteState
    {
        public sealed class Row
        {
            public string File = "", Status = "", Results = "", Message = "";
            public DateTime? Received, Started, Ended;
            public int Done, Total, Ok, NoData, Failed, Ahead;
            public double Eta = double.NaN, Wait = double.NaN;
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public string Machine = "", Spica = "", State = "", Input = "", Output = "";
        public DateTime Beat;
        public readonly List<Row> Jobs = new List<Row>();

        /// <summary>Listening, and it said so in the last few minutes (it writes every 30 s; the clocks of two PCs may differ a little).</summary>
        public bool Alive => State == "listening" && Math.Abs((DateTime.Now - Beat).TotalMinutes) < 3;

        /// <summary>The state file in a watched folder; null when there is none, or it is being written.</summary>
        public static RemoteState Read(string folder)
        {
            try
            {
                string path = Path.Combine(folder, Listener.StateFile);
                if (!File.Exists(path)) return null;
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length < 2 || !lines[0].StartsWith("SPICA-listener\t") || lines.Last(l => l.Length > 0) != "end") return null;
                var st = new RemoteState();
                foreach (var line in lines)
                {
                    var p = line.Split('\t');
                    switch (p[0])
                    {
                        case "machine": st.Machine = At(p, 1); break;
                        case "spica": st.Spica = At(p, 1); break;
                        case "state": st.State = At(p, 1); break;
                        case "beat": st.Beat = D(At(p, 1)) ?? DateTime.MinValue; break;
                        case "input": st.Input = At(p, 1); break;
                        case "output": st.Output = At(p, 1); break;
                        case "job":
                            if (p.Length < 16) break;
                            st.Jobs.Add(new Row
                            {
                                File = p[1], Status = p[2], Received = D(p[3]), Started = D(p[4]), Ended = D(p[5]),
                                Done = I(p[6]), Total = I(p[7]), Ok = I(p[8]), NoData = I(p[9]), Failed = I(p[10]),
                                Eta = N(p[11]), Ahead = I(p[12]), Wait = N(p[13]), Results = p[14], Message = p[15],
                            });
                            break;
                    }
                }
                return st;
            }
            catch { return null; }
        }

        /// <summary>A results folder of the listener as this PC reaches it: inside the shared folder, else not at all (null).</summary>
        public string Reach(string folder, string results)
        {
            if (string.IsNullOrEmpty(results) || string.IsNullOrEmpty(Input)) return null;
            string root = Input.TrimEnd('\\') + "\\";
            if (!results.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return Path.Combine(folder, results.Substring(root.Length));
        }

        static string At(string[] p, int i) => i < p.Length ? p[i] : "";
        static int I(string s) => int.TryParse(s, NumberStyles.Integer, Inv, out var v) ? v : 0;
        static double N(string s) => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : double.NaN;
        static DateTime? D(string s) => DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out var t) ? t : (DateTime?)null;
    }

    public enum RemoteStatus { Sent, Waiting, Queued, Running, Done, Stopped, Failed, Lost }

    /// <summary>A campaign this PC sent to a listener's folder, as that listener last described it.</summary>
    public sealed class RemoteJob
    {
        public string Folder = "", File = "", Name = "", Analysis = "";
        public DateTime Sent = DateTime.Now;
        public RemoteStatus Status = RemoteStatus.Sent;
        public string Machine = "", Results = "", ResultsHere, Message = "", Note = "";
        public DateTime? Started, Ended;
        public int Done, Total, Ok, NoData, Failed, Ahead;
        public double Eta = double.NaN, Wait = double.NaN;
        public DateTime Seen = DateTime.MinValue;      // when the listener last spoke of it

        public bool Pending => Status == RemoteStatus.Sent || Status == RemoteStatus.Waiting || Status == RemoteStatus.Queued || Status == RemoteStatus.Running;
    }

    /// <summary>
    /// The campaigns sent to other PCs: they are followed through the listeners' state files and shown in this PC's
    /// queue beside the local ones. Kept between sessions. Used from the UI thread; the folders are read on a worker thread.
    /// </summary>
    public sealed class RemoteTracker
    {
        const double PollSeconds = 5;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly Action<Action> post;
        readonly List<RemoteJob> jobs = new List<RemoteJob>();
        readonly string file = KeyValueFile.Resolve("SPICA.remote.tsv");
        DateTime nextPoll = DateTime.MinValue;
        bool polling;

        public IReadOnlyList<RemoteJob> Jobs => jobs;
        public event Action Changed;
        public event Action<RemoteJob> Ended;
        /// <summary>The state of each folder at the last look (null = no listener has written there).</summary>
        public readonly Dictionary<string, RemoteState> States = new Dictionary<string, RemoteState>(StringComparer.OrdinalIgnoreCase);

        public RemoteTracker(Action<Action> post)
        {
            this.post = post;
            Load();
        }

        public void Add(RemoteJob j)
        {
            jobs.Add(j);
            Save();
            nextPoll = DateTime.MinValue;
            Changed?.Invoke();
        }

        public void Remove(RemoteJob j)
        {
            if (jobs.Remove(j)) { Save(); Changed?.Invoke(); }
        }

        public void ClearEnded()
        {
            if (jobs.RemoveAll(j => !j.Pending) > 0) { Save(); Changed?.Invoke(); }
        }

        /// <summary>Call often (the UI timer): looks at the folders of the pending campaigns every few seconds.</summary>
        public void Tick()
        {
            if (polling || DateTime.UtcNow < nextPoll) return;
            var folders = jobs.Where(j => j.Pending).Select(j => j.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (folders.Count == 0) { nextPoll = DateTime.UtcNow.AddSeconds(PollSeconds); return; }
            var files = jobs.Where(j => j.Pending).Select(j => Path.Combine(j.Folder, j.File)).ToList();
            polling = true;
            Task.Run(() =>
            {
                var read = new Dictionary<string, RemoteState>(StringComparer.OrdinalIgnoreCase);
                var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in folders)
                {
                    try { if (!Directory.Exists(f)) continue; } catch { continue; }
                    reachable.Add(f);
                    read[f] = RemoteState.Read(f);
                }
                foreach (var p in files) { try { if (System.IO.File.Exists(p)) present.Add(p); } catch { } }
                post(() =>
                {
                    polling = false;
                    nextPoll = DateTime.UtcNow.AddSeconds(PollSeconds);
                    Apply(read, reachable, present);
                });
            });
        }

        void Apply(Dictionary<string, RemoteState> read, HashSet<string> reachable, HashSet<string> present)
        {
            bool changed = false, save = false;
            foreach (var j in jobs.Where(x => x.Pending).ToList())
            {
                string before = Key(j);
                var was = j.Status;
                if (!reachable.Contains(j.Folder)) j.Note = "The shared folder cannot be reached now.";
                else
                {
                    read.TryGetValue(j.Folder, out var st);
                    if (st != null) States[j.Folder] = st;
                    var row = st?.Jobs.FirstOrDefault(r => r.File.Equals(j.File, StringComparison.OrdinalIgnoreCase));
                    if (row != null) Take(j, st, row);
                    else if (present.Contains(Path.Combine(j.Folder, j.File)))
                        j.Note = st == null ? "No SPICA listener has written its status in this folder yet."
                               : st.Alive ? "Waiting for the listener on " + st.Machine + " to take it."
                               : "The listener on " + st.Machine + " is not running now.";
                    else if (j.Status == RemoteStatus.Sent) { j.Status = RemoteStatus.Lost; j.Message = "The file was taken out of the shared folder before a listener ran it."; j.Ended = DateTime.Now; }
                    if (st != null && j.Status == RemoteStatus.Running && !st.Alive) j.Note = "The listener on " + st.Machine + " has not given a sign of life since " + st.Beat.ToString("HH:mm", Inv) + ".";
                }
                if (Key(j) != before) changed = true;
                if (j.Status != was) save = true;
                if (!j.Pending && was != j.Status) Ended?.Invoke(j);
            }
            if (save) Save();
            if (changed) Changed?.Invoke();
        }

        static string Key(RemoteJob j) => string.Join("|", j.Status, j.Done, j.Total, j.Ahead, j.Note, j.Message, j.Results, double.IsNaN(j.Eta) ? -1 : Math.Round(j.Eta / 30));

        static void Take(RemoteJob j, RemoteState st, RemoteState.Row r)
        {
            j.Machine = st.Machine;
            j.Seen = DateTime.Now;
            j.Note = st.Alive ? "" : "The listener on " + st.Machine + " is not running now.";
            Enum.TryParse(r.Status, true, out ListenerStatus ls);
            switch (ls)
            {
                case ListenerStatus.Waiting: j.Status = RemoteStatus.Waiting; break;
                case ListenerStatus.Queued: j.Status = RemoteStatus.Queued; break;
                case ListenerStatus.Running: j.Status = RemoteStatus.Running; break;
                case ListenerStatus.Done: j.Status = RemoteStatus.Done; break;
                case ListenerStatus.Stopped: j.Status = RemoteStatus.Stopped; break;
                default: j.Status = RemoteStatus.Failed; break;
            }
            j.Started = r.Started; j.Ended = r.Ended;
            if (r.Total > 0) j.Total = r.Total;
            j.Done = r.Done; j.Ok = r.Ok; j.NoData = r.NoData; j.Failed = r.Failed; j.Ahead = r.Ahead;
            j.Eta = r.Eta; j.Wait = r.Wait;
            j.Results = r.Results;
            j.ResultsHere = st.Reach(j.Folder, r.Results);
            j.Message = r.Message;
        }

        // ------------------------------------------------------------------ kept between sessions

        static string T(DateTime? t) => t.HasValue ? t.Value.ToString("yyyy-MM-dd HH:mm:ss", Inv) : "";
        static DateTime? D(string s) => DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out var t) ? t : (DateTime?)null;
        static string C(string s) => (s ?? "").Replace('\t', ' ').Replace("\r", "").Replace('\n', ' ');

        void Save()
        {
            try
            {
                var sb = new StringBuilder("folder\tfile\tname\tanalysis\tsent\tstatus\tmachine\tdone\ttotal\tok\tnodata\tfailed\tstarted\tended\tresults\tresultsHere\tmessage\r\n");
                foreach (var j in jobs)
                    sb.Append(string.Join("\t", C(j.Folder), C(j.File), C(j.Name), C(j.Analysis), T(j.Sent), j.Status.ToString(), C(j.Machine),
                        j.Done.ToString(Inv), j.Total.ToString(Inv), j.Ok.ToString(Inv), j.NoData.ToString(Inv), j.Failed.ToString(Inv),
                        T(j.Started), T(j.Ended), C(j.Results), C(j.ResultsHere), C(j.Message))).Append("\r\n");
                System.IO.File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        void Load()
        {
            try
            {
                if (!System.IO.File.Exists(file)) return;
                foreach (var line in System.IO.File.ReadAllLines(file, Encoding.UTF8).Skip(1))
                {
                    var p = line.Split('\t');
                    if (p.Length < 17 || !Enum.TryParse(p[5], out RemoteStatus st)) continue;
                    int I(string s) => int.TryParse(s, NumberStyles.Integer, Inv, out var v) ? v : 0;
                    jobs.Add(new RemoteJob
                    {
                        Folder = p[0], File = p[1], Name = p[2], Analysis = p[3], Sent = D(p[4]) ?? DateTime.Now, Status = st, Machine = p[6],
                        Done = I(p[7]), Total = I(p[8]), Ok = I(p[9]), NoData = I(p[10]), Failed = I(p[11]),
                        Started = D(p[12]), Ended = D(p[13]), Results = p[14], ResultsHere = p[15].Length > 0 ? p[15] : null, Message = p[16],
                    });
                }
            }
            catch { }
        }
    }
}
