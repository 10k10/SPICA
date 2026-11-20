using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Spica.Core;
using Spica.Core.Graph;

// Checks of SPICA, each printing "ok" or "WRONG" per item and returning the number wrong:
//   SPICA.Smoke --graph                 the study graph, the blocks, the project file, the campaign builder (no LTspice)
//   SPICA.Smoke --studies <asc> <png>   the window on a schematic: its study, the inspector, what is missing (pictures)
//   SPICA.Smoke --studyrun <asc> <png>  a study run with LTspice end to end: queue, waveforms, the cache (pictures)
//   SPICA.Smoke --savesel <asc> <png>   a few signals saved (the .meas ones forced); plotting, expressions, cursors, measurements
//   SPICA.Smoke --waves <dir> <signal>... every signal asked for is drawn from a campaign folder
//   SPICA.Smoke --opentime <asc>        how long reading a schematic takes (LTspice, then the cache)
//   SPICA.Smoke --hittest               the window's edges and corners resize it
//   SPICA.Smoke <asc> [runs] [instances threads]   the campaign engine alone on a schematic (SPICA_TEMP=-40,25,90  SPICA_PAIRED=1  SPICA_SAVE="V(out) I(R1)")
static class Program
{
    static int bad;
    static void Check(bool ok, string what) { if (!ok) bad++; Console.WriteLine($"{(ok ? "ok   " : "WRONG")} {what}"); }

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--graph") return Graph();
        if (args.Length > 2 && args[0] == "--studies") return Window(args[1], args[2], Studies);
        if (args.Length > 2 && args[0] == "--studyrun") return Window(args[1], args[2], StudyRun);
        if (args.Length > 0 && args[0] == "--hittest") return HitTest();
        if (args.Length > 2 && args[0] == "--savesel") return Window(args[1], args[2], SaveSel);
        if (args.Length > 1 && args[0] == "--opentime")
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lt = Ltspice.Locate();
            var nl = SchematicLoader.Load(lt, Path.GetFullPath(args[1]));
            Console.WriteLine($"netlist {sw.ElapsedMilliseconds} ms"); sw.Restart();
            var plan = McPlan.Build(nl.Lines, nl.SourcePath);
            Console.WriteLine($"tolerances {sw.ElapsedMilliseconds} ms ({plan.Sites.Count} sites)"); sw.Restart();
            plan = McPlan.Build(nl.Lines, nl.SourcePath);
            Console.WriteLine($"tolerances again {sw.ElapsedMilliseconds} ms");
            return 0;
        }
        if (args.Length > 2 && args[0] == "--waves") return Waves(args[1], args.Skip(2).ToList());
        return Engine(args);
    }

    // =====================================================================================
    //  --graph
    // =====================================================================================

    /// <summary>The fake host of the executors: one netlist, campaigns recorded and answered at once as finished.</summary>
    sealed class FakeHost : IStudyHost
    {
        public readonly List<CampaignRequest> Sent = new List<CampaignRequest>();
        public Netlist Net;
        public string Folder;
        public string DefaultSchematic => "";
        public Netlist NetlistFor(string asc) => Net;
        public string StudyFolder(Study study) => Path.Combine(Folder, "studies", StudyExecutors.Short(study.Name, 20));
        public void Submit(CampaignRequest request, Action<string, string> done)
        {
            Sent.Add(request);
            Directory.CreateDirectory(request.OutputDir);
            File.WriteAllText(Path.Combine(request.OutputDir, "results.csv"), "run,temp,status,vbulk,note\r\n1,,Ok,27.5,\r\n");
            new CampaignState { Status = "Finished", Jobs = 1, Done = 1 }.Save(request.OutputDir);
            done(request.OutputDir, null);
        }
    }

    static int Graph()
    {
        Check(string.Join(",", BlockKind.All.Select(k => k.Name)) == "Schematic,Nominal,MonteCarlo,WorstCase,Simulate", "the blocks: " + string.Join(", ", BlockKind.All.Select(k => k.Title)));

        // wiring
        var st = new Study { Name = "Hold-up" };
        var sch = st.Add("Schematic").Set("File", "holdup.asc");
        var nom = st.Add("Nominal").Set("Temperature", "25; 85");
        var mc = st.Add("MonteCarlo").Set("Runs", "5").Set("Seed", "7");
        var wc = st.Add("WorstCase");
        var sim = st.Add("Simulate").Set("Save", "V(bulk)");
        Check(st.Connect(sch, sim) == null && st.Connect(nom, sim) == null && st.Connect(mc, sim) == null && st.Connect(wc, sim) == null, "a circuit and three analyses into one Simulate");
        Check(st.Validate().Count == 0, "the study validates: " + string.Join("; ", st.Validate()));
        Check(st.Connect(nom, sch) != null, "an analysis cannot go into a Schematic: " + st.Connect(nom, sch));
        var sch2 = st.Add("Schematic");
        Check(st.Connect(sch2, sim) == null && st.Wires.Count(w => w.To == sim.Id && w.ToPort == "circuit") == 1, "a second circuit replaces the first (one circuit per Simulate)");
        st.Remove(sch2);
        st.Connect(sch, sim);
        var lone = st.Add("Simulate");
        Check(st.Validate().Any(i => i.Block == lone.Id && i.Message.Contains("not wired")), "an input left empty is reported");
        st.Remove(lone);
        Check(st.Order().Last().Kind == "Simulate", "Simulate runs last: " + string.Join(" > ", st.Order().Select(b => b.Kind)));

        // the analyses
        Check(StudyExecutors.Temps("-55; 25, 85 25").SequenceEqual(new[] { -55.0, 25, 85 }), "temperatures: a list, separated by ; , or spaces, each once");
        try { StudyExecutors.Temps("hot"); Check(false, "a word is not a temperature"); } catch (Exception ex) { Check(ex.Message.Contains("hot"), "a word is not a temperature: " + ex.Message); }

        // the real executors with a fake host: what Simulate asks the queue for, and the cache
        string dir = Path.Combine(Path.GetTempPath(), "spica_graph_test");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "holdup.asc"), "");
        sch.Set("File", Path.Combine(dir, "holdup.asc"));
        var host = new FakeHost { Folder = dir, Net = Netlist.Parse("* holdup\nV1 in 0 28\nR1 in bulk {mc(100,0.05)}\nC1 bulk 0 {mc(470u,0.2)}\n.tran 100m\n.meas TRAN vbulk AVG V(bulk)\n.end\n", Path.Combine(dir, "holdup.asc"), null) };
        var runner = StudyExecutors.Register(new StudyRunner(), host);
        Check(runner.Run(st), "the study runs: " + string.Join("; ", runner.Message.Values));
        Check(host.Sent.Count == 3, $"three campaigns, one per analysis: {host.Sent.Count}");
        var rn = host.Sent.FirstOrDefault(r => r.Kind == RunKind.Nominal);
        var rm = host.Sent.FirstOrDefault(r => r.Kind == RunKind.MonteCarlo);
        var rw = host.Sent.FirstOrDefault(r => r.Kind == RunKind.WorstCase);
        Check(rn?.Temps != null && rn.Temps.SequenceEqual(new[] { 25.0, 85 }), "Nominal at 25 and 85 °C");
        Check(rm != null && rm.Runs == 5 && rm.Seed == 7 && rm.Temps == null, "Monte Carlo: 5 runs, seed 7, the schematic's temperature");
        Check(rw != null && rw.Sigma == 3 && rw.Save.SequenceEqual(new[] { "V(bulk)" }), "Worst Case at 3 sigma, saving V(bulk)");
        Check(host.Sent.All(r => Path.GetFullPath(r.OutputDir).StartsWith(Path.Combine(dir, "studies", "Hold_up", "s" + sim.Id))), "the campaigns go into the study's folder, under its Simulate");
        var sets = runner.Results[sim.Id]["data"] as List<RunSet>;
        Check(sets != null && sets.Count == 3 && sets.All(s => s.Dir != null && !s.Cached), "the results: three campaigns");
        host.Sent.Clear();
        runner = StudyExecutors.Register(new StudyRunner(), host);
        runner.Run(st);
        Check(host.Sent.Count == 0 && runner.State[sim.Id] == BlockState.Cached, "run again unchanged: everything from the cache");
        mc.Set("Runs", "6");
        runner = StudyExecutors.Register(new StudyRunner(), host);
        runner.Run(st);
        Check(host.Sent.Count == 1 && host.Sent[0].Runs == 6, "one analysis changed: only it runs again");
        mc.Set("Runs", "zero");
        runner = StudyExecutors.Register(new StudyRunner(), host);
        Check(!runner.Run(st) && runner.State[mc.Id] == BlockState.Error && runner.State[sim.Id] == BlockState.Skipped, "a block in error stops what it feeds: " + (runner.Message.TryGetValue(mc.Id, out var why) ? why : ""));
        mc.Set("Runs", "5");

        // the campaign builder: what each analysis becomes
        string exe = Assembly.GetExecutingAssembly().Location;     // any file: the builder only checks that LTspice is there
        CampaignOptions Build(CampaignRequest r, Netlist nl, out string err)
        {
            r.LtspiceExe = exe; r.AscPath = Path.Combine(dir, "holdup.asc"); r.Netlist = nl; r.OutputDir = dir;
            return CampaignBuilder.Build(r, out err);
        }
        var o = Build(new CampaignRequest { Kind = RunKind.WorstCase, Temps = new List<double> { 25, 85 } }, host.Net, out var e1);
        Check(o != null && o.Runs == 5 && o.CornerCount == 2 && o.Mc.Design.Count == 5, $"worst case of 2 tolerances: 1 + 2^2 = {o?.Runs} runs, at 2 temperatures {e1}");
        o = Build(new CampaignRequest { Kind = RunKind.Nominal }, host.Net, out var e2);
        Check(o != null && o.Runs == 1 && o.Mc.Design[0].All(v => v == 0), "nominal: one run, every tolerance at 0 " + e2);
        var flat = Netlist.Parse("* flat\nV1 in 0 28\nR1 in out 100\n.tran 1m\n.end\n", Path.Combine(dir, "holdup.asc"), null);
        o = Build(new CampaignRequest { Kind = RunKind.WorstCase }, flat, out var e3);
        Check(o == null && e3.Contains("tolerances"), "worst case without tolerances is refused: " + e3);
        o = Build(new CampaignRequest { Kind = RunKind.MonteCarlo, Runs = 10, Save = new List<string> { "V(nowhere)" } }, host.Net, out var e4);
        Check(o == null && e4.Contains("V(nowhere)"), "a signal the circuit does not have is refused: " + e4);

        // the project file
        var p = new SpicaProject { Schematic = Path.Combine(dir, "holdup.asc") };
        p.Studies.Add(st);
        nom.Add("Note", "multi\nline = text");
        string f1 = Path.Combine(dir, "a.spica"), f2 = Path.Combine(dir, "b.spica");
        p.Save(f1);
        var back = SpicaProject.Load(f1);
        back.Save(f2);
        Check(File.ReadAllText(f1) == File.ReadAllText(f2), "a project saves and loads back unchanged");
        var bs = back.Studies.Single();
        Check(bs.Blocks.Count == st.Blocks.Count && bs.Wires.Count == st.Wires.Count && bs.Validate().Count == 0 && bs[nom.Id].Get("Note") == "multi\r\nline = text", "the study comes back whole");
        Check(back.Schematic == p.Schematic && File.ReadAllText(f1).Contains("Schematic=holdup.asc"), "the schematic is kept beside the project, by a relative path");
        string f3 = Path.Combine(dir, "old.spica");
        File.WriteAllText(f3, "[Campaign]\nSchematic=holdup.asc\nRuns=1000\n[Temperature]\nMode=Corners\n[Study Old]\nWire=1.circuit -> 2.circuit\n[Block 1]\nKind=Schematic\nAt=10,10\n[Block 2]\nKind=Limits\nAt=200,10\nCriteria=holdup\n");
        var old = SpicaProject.Load(f3);
        Check(old.Schematic == p.Schematic && old.Studies.Count == 1 && old.Studies[0].Blocks.Count == 2, "a project of an older version opens: its schematic and its study");
        Check(old.Studies[0].Validate().Any(i => i.Message.Contains("unknown kind 'Limits'")), "a block this version does not have stays, marked unknown");
        Console.WriteLine(bad == 0 ? "GRAPH: all ok" : $"GRAPH: {bad} wrong");
        return bad;
    }

    // =====================================================================================
    //  The window
    // =====================================================================================

    static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    sealed class W
    {
        public Spica.UI.MainForm Form;
        public string Png;
        public object Get(string f) => typeof(Spica.UI.MainForm).GetField(f, Flags).GetValue(Form);
        public object Call(string m, params object[] a) => typeof(Spica.UI.MainForm).GetMethod(m, Flags).Invoke(Form, a);
        public object Prop(string p) => typeof(Spica.UI.MainForm).GetProperty(p, Flags).GetValue(Form);
        public T Wave<T>(string f) => (T)typeof(Spica.UI.WaveformView).GetField(f, Flags).GetValue(Get("waveView"));
        /// <summary>The panes of the waveforms: their plots, top to bottom.</summary>
        public List<Spica.UI.WavePlot> Plots()
        {
            var wv = Get("waveView");
            var panes = (System.Collections.IList)typeof(Spica.UI.WaveformView).GetField("panes", Flags).GetValue(wv);
            return panes.Cast<object>().Select(o => (Spica.UI.WavePlot)o.GetType().GetField("Plot").GetValue(o)).ToList();
        }
        /// <summary>A click on a trace's name, as the user does.</summary>
        public void ClickLabel(int pane, Spica.UI.PlotTrace t) =>
            typeof(Spica.UI.WaveformView).GetMethod("LabelClicked", Flags).Invoke(Get("waveView"), new object[] { pane, t, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0) });
        public void ChoosePane(int pane) => typeof(Spica.UI.WaveformView).GetMethod("Choose", Flags).Invoke(Get("waveView"), new object[] { pane });
        public int Tab { get => ((Spica.UI.Win98TabControl)Get("tabs")).SelectedIndex; set => ((Spica.UI.Win98TabControl)Get("tabs")).SelectedIndex = value; }
        public void Pump(int n = 15) { for (int k = 0; k < n; k++) { Application.DoEvents(); Thread.Sleep(30); } }
        public void Shot(Control c, string name)
        {
            Pump();
            using (var bmp = new System.Drawing.Bitmap(c.Width, c.Height))
            {
                c.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, c.Width, c.Height));
                bmp.Save(Png.Replace(".png", "_" + name + ".png"));
            }
        }
    }

    /// <summary>Opens the window on a schematic, waits for LTspice to read it, then runs the test (which closes the window when done).</summary>
    static int Window(string asc, string png, Action<W, Action> test)
    {
        var t = new Thread(() =>
        {
            // the results in a short folder: the scratch folders are long, and LTspice stops at 260 characters of path
            var settings = new AppSettings { ResultsFolder = Path.Combine(Path.GetTempPath(), "spq") };
            settings.Save();
            var w = new W { Form = new Spica.UI.MainForm { StartupFile = Path.GetFullPath(asc), Width = 1360, Height = 860 }, Png = png };
            var start = new System.Windows.Forms.Timer { Interval = 500 };
            var began = DateTime.UtcNow;
            start.Tick += (s, e) =>
            {
                if (w.Get("netlist") == null && (DateTime.UtcNow - began).TotalSeconds < 30) return;
                start.Stop();
                test(w, () => { ((SpicaProject)w.Get("project")).Dirty = false; w.Form.Close(); });
            };
            w.Form.Shown += (s, e) => start.Start();
            Application.Run(w.Form);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return bad;
    }

    /// <summary>--studies: a schematic opens as a study ready to run; the inspector; what is missing when a wire is taken out.</summary>
    static void Studies(W w, Action close)
    {
        var project = (SpicaProject)w.Get("project");
        var st = project.Studies.Single();
        var sim = st.Blocks.First(b => b.Kind == "Simulate");
        Check(st.Blocks.Select(b => b.Kind).OrderBy(x => x).SequenceEqual(new[] { "Nominal", "Schematic", "Simulate" }) && st.Validate().Count == 0,
              "a schematic opens as a study ready to run: schematic, nominal, simulated");
        Check(w.Get("netlist") != null, "LTspice read the schematic");
        var problems = (List<string>)w.Call("StudyProblems");
        Check(problems.Count == 0, "nothing is missing: " + string.Join("; ", problems));
        Check(((ToolStripButton)w.Get("tbRun")).Enabled, "Run is on");
        w.Shot(w.Form, "workbench");

        var canvas = (Spica.UI.StudyCanvas)w.Get("studyCanvas");
        canvas.Select(sim);
        w.Pump(10);
        Check(((Panel)w.Get("inspector")).Visible, "a block selected opens its settings on the right");
        w.Shot(w.Form, "simulate");
        canvas.Select(st.Blocks.First(b => b.Kind == "Schematic"));
        w.Shot(w.Form, "schematic");

        var nom = st.Blocks.First(b => b.Kind == "Nominal");
        var wc = st.Add("WorstCase", nom.X, nom.Y + 130).Set("Temperature", "-55; 25; 85");
        st.Connect(wc, sim);
        w.Call("StudyEdited");
        canvas.Select(wc);
        w.Pump(10);
        Check(((List<string>)w.Call("BlockProblems", wc)).Count == 0, "a Worst Case on the holdup's two tolerances is fine");
        w.Shot(w.Form, "worstcase");

        st.Wires.RemoveAll(x => x.To == sim.Id && x.ToPort == "conditions");
        w.Call("StudyEdited");
        problems = (List<string>)w.Call("StudyProblems");
        Check(problems.Any(p => p.Contains("not wired")), "a Simulate with no analysis is reported: " + string.Join("; ", problems));
        Check(((ToolStripButton)w.Get("tbRun")).Enabled, "Run stays on: pressed, it says what is missing");
        canvas.Select(null);
        w.Shot(w.Form, "missing");

        using (var f = new Spica.UI.OptionsForm(new AppSettings(), HardwareInfo.Detect()))
        {
            f.Show(w.Form);
            w.Shot(f, "options");
            f.Close();
        }
        Console.WriteLine(bad == 0 ? "STUDIES: all ok" : $"STUDIES: {bad} wrong");
        close();
    }

    /// <summary>--studyrun: nominal at two temperatures, Monte Carlo and worst case, run with LTspice; the waveforms; the cache.</summary>
    static void StudyRun(W w, Action close)
    {
        var project = (SpicaProject)w.Get("project");
        var st = project.Studies.Single();
        var sim = st.Blocks.First(b => b.Kind == "Simulate");
        st.Blocks.First(b => b.Kind == "Nominal").Set("Temperature", "25; 85");
        var mc = st.Add("MonteCarlo", 40, 300).Set("Runs", "5");
        var wc = st.Add("WorstCase", 40, 430);
        st.Connect(mc, sim);
        st.Connect(wc, sim);
        w.Call("StudyEdited");
        string spq = Path.Combine(Path.GetTempPath(), "spq");
        if (Directory.Exists(spq)) Directory.Delete(spq, true);

        var began = DateTime.UtcNow;
        double first = 0;
        void RunAndWait(Action after)
        {
            began = DateTime.UtcNow;
            w.Call("RunStudy");
            var poll = new System.Windows.Forms.Timer { Interval = 500 };
            poll.Tick += (s, e) =>
            {
                if ((bool)w.Prop("StudyRunning") && (DateTime.UtcNow - began).TotalSeconds < 600) return;
                poll.Stop();
                w.Pump(20);
                after();
            };
            poll.Start();
        }
        Check(((List<string>)w.Call("StudyProblems")).Count == 0, "the study is ready: nominal at 25 and 85 °C, Monte Carlo of 5, worst case");
        RunAndWait(() =>
        {
            first = (DateTime.UtcNow - began).TotalSeconds;
            var queue = (CampaignQueue)w.Get("queue");
            Check(queue.Jobs.Count == 3 && queue.Jobs.All(j => j.Status == JobStatus.Finished), "three campaigns ran in the queue: " + string.Join(", ", queue.Jobs.Select(j => $"{j.Name} {j.Status} {j.Done}/{j.Total}")));
            Check(queue.Jobs.Select(j => j.Total).OrderBy(x => x).SequenceEqual(new[] { 2, 5, 5 }), "nominal 1 x 2 temperatures, Monte Carlo 5, worst case 1 + 2^2: " + string.Join(", ", queue.Jobs.Select(j => j.Total)));
            var canvas = (Spica.UI.StudyCanvas)w.Get("studyCanvas");
            Check(canvas.States.Values.All(s => s == BlockState.Done), "every block done");
            Check(w.Tab == 2, "the Results tab opens when the study ends");
            w.Tab = 1;
            w.Shot(w.Form, "queue");
            w.Tab = 2;
            var wv = (Spica.UI.WaveformView)w.Get("waveView");
            var sets = w.Wave<CheckedListBox>("lbSets");
            var signals = w.Wave<ListBox>("lbSignals").Items.Cast<string>().ToList();
            Check(wv.Visible && sets.Items.Count == 3, "three analyses to look at: " + string.Join(", ", sets.Items.Cast<object>()));
            Check(signals.Contains("V(bulk)") && signals.Contains("V(in)"), "the .raw holds every signal: " + string.Join(" ", signals));
            Check(wv.Plotted.Contains("V(bulk)") && wv.SeriesCount >= 2, $"the first look plots what the .meas use: {string.Join(", ", wv.Plotted)}; {wv.SeriesCount} traces");
            w.Shot(w.Form, "nominal");
            for (int i = 0; i < sets.Items.Count; i++) sets.SetItemChecked(i, sets.Items[i].ToString().StartsWith("Worst"));
            w.Pump(30);
            var runs = w.Wave<ComboBox>("cbRuns");
            Check(runs.SelectedItem?.ToString().StartsWith("All") == true && wv.SeriesCount == 5 * wv.Plotted.Count, $"the worst case: all its runs drawn ({wv.SeriesCount} traces)");
            var bulk = w.Plots().Select((pl, i) => (pl, i)).First(x => x.pl.Traces.Any(t => t.Label != null && t.Label.StartsWith("V(bulk)")));
            w.ClickLabel(bulk.i, bulk.pl.Traces.First(t => t.Label != null && t.Label.StartsWith("V(bulk)")));
            w.Pump(5);
            var meas = w.Wave<Spica.UI.Win98ListView>("lvMeasure");
            var row = meas.Items.Cast<ListViewItem>().FirstOrDefault(x => x.Text.StartsWith("V(bulk)"));
            Check(bulk.pl.Cursors.Count == 1 && row != null && row.SubItems[1].Text != "-", "a click on V(bulk)'s name puts cursor 1 on it, and it reads " + row?.SubItems[1].Text);
            w.Shot(w.Form, "worstcase");

            // the same study again: from the cache, at once
            w.Tab = 0;
            RunAndWait(() =>
            {
                double t2 = (DateTime.UtcNow - began).TotalSeconds;
                Check(canvas.States.TryGetValue(sim.Id, out var ss) && ss == BlockState.Cached && ((CampaignQueue)w.Get("queue")).Jobs.Count == 3,
                      $"a second run comes from the cache: {t2:0.0} s (first {first:0.0} s), nothing new in the queue");
                Console.WriteLine(bad == 0 ? "STUDY RUN: all ok" : $"STUDY RUN: {bad} wrong");
                close();
            });
        });
    }

    /// <summary>--savesel asc png: a few signals saved; every signal the .raw holds can be plotted.</summary>
    static void SaveSel(W w, Action close)
    {
        var st = ((SpicaProject)w.Get("project")).Studies.Single();
        var simB = st.Blocks.First(b => b.Kind == "Simulate").Set("Save", "I(Rload) I(Cout) V(out)");
        w.Call("StudyEdited");
        // the Simulate's list: what the .meas read is ticked and stays ticked
        ((Spica.UI.StudyCanvas)w.Get("studyCanvas")).Select(simB);
        w.Pump(10);
        var list = ((Panel)w.Get("inspectorBody")).Controls.OfType<CheckedListBox>().Single();
        var forced = list.Items.Cast<string>().Where(x => x.EndsWith("(.meas)")).ToList();
        Check(forced.Count == 3 && forced.All(x => list.CheckedItems.Contains(x)), "the signals the .meas read are ticked: " + string.Join(", ", forced));
        list.SetItemChecked(0, false);
        w.Pump(5);
        Check(list.GetItemChecked(0) && simB.Get("Save") == "I(Rload) I(Cout) V(out)", "and cannot be cleared; Save keeps only the user's: " + simB.Get("Save"));
        w.Shot(w.Form, "simulate");
        w.Call("RunStudy");
        var poll = new System.Windows.Forms.Timer { Interval = 500 };
        poll.Tick += (s, e) =>
        {
            if ((bool)w.Prop("StudyRunning")) return;
            poll.Stop();
            w.Pump(20);
            var wv = (Spica.UI.WaveformView)w.Get("waveView");
            var names = w.Wave<ListBox>("lbSignals").Items.Cast<string>().ToList();
            Check(wv.Plotted.SequenceEqual(new[] { "I(Rload)", "I(Cout)", "V(out)" }), "the first look plots the signals the user saved: " + string.Join(" ", wv.Plotted));
            var stack0 = (Panel)typeof(Spica.UI.WaveformView).GetField("stack", Flags).GetValue(wv);
            Check(stack0.Controls.Count == 3, $"one plot per pane, nothing left over underneath: {stack0.Controls.Count}");
            Check(names.Count == 5, "listed: the saved ones and the ones the .meas read: " + string.Join(" ", names));
            Check(w.Plots().All(pl => pl.Traces.All(t => t.Label == null || !t.Label.Contains(","))), "a pane names its traces once, in their colours (no title)");
            // a click on a signal plots it in the pane chosen (the last one), in view
            wv.Add("V(p)");
            w.Pump(5);
            var last = w.Plots().Last();
            Check(last.Traces.Any(t => t.Label == "V(p)") && last.Top >= 0 && last.Bottom <= stack0.ClientSize.Height + 2, $"V(p) clicked: drawn in the pane chosen, in view");
            // another pane chosen: the next signal goes there
            w.ChoosePane(0);
            wv.Add("I(D1)");
            w.Pump(5);
            Check(w.Plots()[0].Traces.Any(t => t.Label == "I(D1)") && w.Plots()[0].Chosen, "the first pane chosen, then I(D1) clicked: it goes there, framed");
            // expressions, as in LTspice
            wv.Add("2*V(out)");
            wv.Add("V(out,p)");
            w.Pump(5);
            var p0 = w.Plots()[0];
            var vout = w.Plots().SelectMany(pl => pl.Traces).First(t => t.Label == "V(out)");
            var vp = w.Plots().SelectMany(pl => pl.Traces).First(t => t.Label == "V(p)");
            var twice = p0.Traces.FirstOrDefault(t => t.Label == "2*V(out)");
            var diff = p0.Traces.FirstOrDefault(t => t.Label == "V(out,p)");
            int k = vout.Y.Length / 2;
            Check(twice != null && Math.Abs(twice.Y[k] - 2 * vout.Y[k]) < 1e-9, $"2*V(out) is twice V(out): {twice?.Y[k]} = 2 x {vout.Y[k]}");
            Check(diff != null && Math.Abs(diff.Y[k] - (vout.Y[k] - vp.Y[k])) < 1e-9, $"V(out,p) is V(out) - V(p): {diff?.Y[k]}");
            // cursors: a click on a trace's name puts cursor 1, another cursor 2; the table measures between them
            w.ClickLabel(0, twice);
            w.ClickLabel(0, twice);
            w.Pump(5);
            var meas = w.Wave<Spica.UI.Win98ListView>("lvMeasure");
            var head = w.Wave<Label>("lblMeasure").Text;
            Check(p0.Cursors.Count == 2 && head.StartsWith("Between the cursors"), "two clicks on 2*V(out)'s name: cursors 1 and 2 on it; " + head);
            var rOut = meas.Items.Cast<ListViewItem>().First(x => x.Text == "V(out)");
            var rTwice = meas.Items.Cast<ListViewItem>().First(x => x.Text == "2*V(out)");
            double Num(string t) => double.Parse(t.Replace("µ", "e-6").Replace("m", "e-3").Replace("k", "e3"), System.Globalization.CultureInfo.InvariantCulture);
            Check(Math.Abs(Num(rTwice.SubItems[4].Text) - 2 * Num(rOut.SubItems[4].Text)) < 0.01 * Math.Abs(Num(rTwice.SubItems[4].Text)),
                  $"its average is twice V(out)'s: {rTwice.SubItems[4].Text} vs {rOut.SubItems[4].Text}; RMS {rTwice.SubItems[5].Text}, p-p {rTwice.SubItems[8].Text}");
            w.Shot(w.Form, "cursors");
            w.ClickLabel(0, twice);
            w.Pump(5);
            Check(p0.Cursors.Count == 0 && w.Wave<Label>("lblMeasure").Text.StartsWith("Over the time shown"), "a third click takes the cursors off: the table measures the time shown");
            Console.WriteLine(bad == 0 ? "SAVE SELECTION: all ok" : $"SAVE SELECTION: {bad} wrong");
            w.Shot(w.Form, "all");
            close();
        };
        poll.Start();
    }

    /// <summary>--waves campaignDir signal...: every signal asked for is drawn, from a campaign folder.</summary>
    static int Waves(string dir, List<string> signals)
    {
        var form = new Form { Width = 1200, Height = 800 };
        var wv = new Spica.UI.WaveformView();
        form.Controls.Add(wv);
        form.Shown += (s, e) =>
        {
            var set = new RunSet { Condition = new Condition { Name = "campaign" }, Dir = dir };
            wv.ShowStudy(new Spica.UI.StudyRunData { Sets = new List<RunSet> { set } });
            foreach (var sig in signals)
            {
                wv.Plot(new[] { sig });
                Application.DoEvents();
                var info = (Label)typeof(Spica.UI.WaveformView).GetField("lblMeasure", Flags).GetValue(wv);
                Check(wv.SeriesCount > 0, $"{sig}: {wv.SeriesCount} traces  {info.Text.Replace('\n', ' ')}");
            }
            form.Close();
        };
        Application.Run(form);
        return bad;
    }

    // =====================================================================================
    //  --hittest
    // =====================================================================================

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(System.Drawing.Point p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    /// <summary>What Windows finds at the main window's edges and corners (resizing needs a sizing code there).</summary>
    static int HitTest()
    {
        var t = new Thread(() =>
        {
            var form = new Spica.UI.MainForm { TopMost = true, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(40, 40) };
            form.Shown += (s, e) =>
            {
                for (int k = 0; k < 20; k++) { Application.DoEvents(); Thread.Sleep(30); }
                var b = form.Bounds;
                var names = new Dictionary<int, string> { [1] = "client", [2] = "caption", [10] = "left", [11] = "right", [12] = "top", [13] = "top-left", [14] = "top-right", [15] = "bottom", [16] = "bottom-left", [17] = "bottom-right", [-1] = "transparent" };
                foreach (var (name, x, y, want) in new[]
                {
                    ("bottom-right corner", b.Right - 2, b.Bottom - 2, 17), ("bottom-right 8 px in", b.Right - 8, b.Bottom - 8, 17),
                    ("bottom-left corner", b.Left + 2, b.Bottom - 2, 16), ("top-left corner", b.Left + 2, b.Top + 2, 13), ("top-right corner", b.Right - 2, b.Top + 2, 14),
                    ("right edge 2 px", b.Right - 2, b.Top + b.Height / 2, 11), ("right edge 5 px", b.Right - 5, b.Top + b.Height / 2, 11),
                    ("left edge 2 px", b.Left + 2, b.Top + b.Height / 2, 10), ("bottom edge 2 px", b.Left + b.Width / 2, b.Bottom - 2, 15),
                    ("bottom edge 5 px", b.Left + b.Width / 2, b.Bottom - 5, 15),
                })
                {
                    var h = WindowFromPoint(new System.Drawing.Point(x, y));
                    int code = (int)SendMessage(h, 0x84, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)));
                    // a child that answers "transparent" passes the point to the window under it, the form
                    if (code == -1) code = (int)SendMessage(form.Handle, 0x84, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)));
                    Check(code == want, $"{name,-22} -> {(names.TryGetValue(code, out var n) ? n : code.ToString())}" + (h == form.Handle ? "" : "  (over a child)"));
                }
                form.Close();
            };
            Application.Run(form);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return bad;
    }

    // =====================================================================================
    //  The engine alone
    // =====================================================================================

    static int Engine(string[] args)
    {
        string asc = Path.GetFullPath(args.Length > 0 ? args[0] : @"..\..\samples\rc_mc.asc");
        int runs = args.Length > 1 ? int.Parse(args[1]) : 200;
        RunScheduler.Config? manual = args.Length > 3 ? new RunScheduler.Config(int.Parse(args[2]), int.Parse(args[3])) : (RunScheduler.Config?)null;
        string lt = Ltspice.Locate();
        Console.WriteLine($"LTspice: {lt} {Ltspice.Version(lt)}");
        var hw = HardwareInfo.Detect();
        Console.WriteLine($"HW: {hw}  SMT={hw.HasSmt}");
        var nl = SchematicLoader.Load(lt, asc);
        Console.WriteLine($"Nodes: {string.Join(" ", nl.Nodes)}");
        Console.WriteLine($"Meas: {string.Join(" ", nl.MeasNames)}  required: {string.Join(" ", nl.RequiredSignals)}");

        var temps = (Environment.GetEnvironmentVariable("SPICA_TEMP") ?? "").Split(',').Where(x => x.Trim().Length > 0)
                    .Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var req = new CampaignRequest
        {
            LtspiceExe = lt, AscPath = asc, Netlist = nl, Manual = manual, Kind = RunKind.MonteCarlo, Runs = runs, Seed = 12345,
            Paired = Environment.GetEnvironmentVariable("SPICA_PAIRED") == "1", Temps = temps.Count > 0 ? temps : null,
            Save = (Environment.GetEnvironmentVariable("SPICA_SAVE") ?? "").Split(' ').Where(x => x.Length > 0).ToList(),
            OutputDir = Path.Combine(Path.GetTempPath(), "SPICA", "smoke_" + DateTime.Now.ToString("HHmmss")),
        };
        var options = CampaignBuilder.Build(req, out string error) ?? throw new Exception(error);
        var engine = new CampaignEngine(options, hw, new ProfileStore());
        var done = new ManualResetEventSlim();
        engine.Finished += () => done.Set();
        engine.Start();
        while (!done.Wait(1000))
        {
            while (engine.LogQueue.TryDequeue(out var l)) Console.WriteLine(l);
            var s = engine.Snapshot();
            Console.WriteLine($"   [{s.Done}/{s.Total}] {s.RunsPerMinute:0} runs/min, ETA {s.EtaSeconds:0} s, {s.Config}, {s.Phase}, workers={s.Workers.Count}");
        }
        while (engine.LogQueue.TryDequeue(out var l)) Console.WriteLine(l);
        Console.WriteLine(File.ReadAllText(Path.Combine(options.OutputDir, "summary.txt")));
        Console.WriteLine(string.Join(Environment.NewLine, File.ReadLines(Path.Combine(options.OutputDir, "results.csv")).Take(6)));
        return 0;
    }
}
