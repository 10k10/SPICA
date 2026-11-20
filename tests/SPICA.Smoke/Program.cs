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
        if (args.Length > 2 && args[0] == "--savesel")
        {
            // from nothing: no campaign, no view kept from an earlier check (the window opens the project with what it finds)
            string spq = Path.Combine(Path.GetTempPath(), "spq");
            if (Directory.Exists(spq)) Directory.Delete(spq, true);
            return Window(args[1], args[2], SaveSel);
        }
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
        Check(string.Join(",", BlockKind.All.Select(k => k.Name)) == "Schematic,Tolerances,Temperature,Simulate", "the blocks: " + string.Join(", ", BlockKind.All.Select(k => k.Title)));

        // wiring
        var st = new Study { Name = "Hold-up" };
        var sch = st.Add("Schematic").Set("File", "holdup.asc");
        var nom = st.Add("Tolerances");
        var mc = st.Add("Tolerances").Set("Mode", "Monte Carlo").Set("Runs", "5").Set("Seed", "7");
        var wc = st.Add("Tolerances").Set("Mode", "Worst Case");
        var temp = st.Add("Temperature").Set("Points", "25; 85");
        var sim = st.Add("Simulate").Set("Save", "V(bulk)");
        Check(st.Connect(sch, sim) == null && st.Connect(nom, sim) == null && st.Connect(mc, sim) == null && st.Connect(wc, sim) == null && st.Connect(temp, sim) == null,
              "a circuit, three sets of tolerances and a temperature, all the conditions into one input of the Simulate");
        Check(st.Validate().Count == 0 && st.Wires.Count(w => w.To == sim.Id && w.ToPort == "conditions") == 4, "the study validates: " + string.Join("; ", st.Validate()));
        Check(st.Connect(nom, sch) != null && st.Connect(temp, nom) != null, "tolerances cannot go into a Schematic, nor a temperature into Tolerances: " + st.Connect(temp, nom));
        var sch2 = st.Add("Schematic");
        Check(st.Connect(sch2, sim) == null && st.Wires.Count(w => w.To == sim.Id && w.ToPort == "circuit") == 1, "a second circuit replaces the first (one circuit per Simulate)");
        st.Remove(sch2);
        st.Connect(sch, sim);
        var lone = st.Add("Simulate");
        Check(st.Validate().Count(i => i.Block == lone.Id) == 1 && st.Validate().Any(i => i.Block == lone.Id && i.Message.Contains("Circuit input is not wired")),
              "a Simulate needs its circuit; its conditions may stay empty");
        st.Remove(lone);
        Check(st.Order().Last().Kind == "Simulate", "Simulate runs last: " + string.Join(" > ", st.Order().Select(b => b.Kind)));

        // the analyses
        Check(StudyExecutors.Temps("-55; 25, 85 25").SequenceEqual(new[] { -55.0, 25, 85 }), "temperatures: a list, separated by ; , or spaces, each once");
        try { StudyExecutors.Temps("hot"); Check(false, "a word is not a temperature"); } catch (Exception ex) { Check(ex.Message.Contains("hot"), "a word is not a temperature: " + ex.Message); }

        // the Tolerances block's modes
        Condition Tol(params string[] kv)
        {
            var b = new Study().Add("Tolerances");
            for (int i = 0; i < kv.Length; i += 2) b.Set(kv[i], kv[i + 1]);
            return StudyExecutors.ReadTolerances(b);
        }
        Check(Tol().Kind == RunKind.Nominal && Tol("Mode", "Monte Carlo").Runs == 100 && Tol("Mode", "Monte Carlo").Paired && Tol("Mode", "Worst Case").Sigma == 3,
              "Tolerances: nominal by default; Monte Carlo of 100, paired; worst case at 3 sigma");
        try { Tol("Mode", "Monte Carlo", "Runs", "1.5"); Check(false, "half a unit is refused"); } catch (Exception ex) { Check(ex.Message.Contains("Runs"), "half a unit is refused: " + ex.Message); }

        // the Temperature block's modes
        TempSpec T(params string[] kv)
        {
            var b = new Study().Add("Temperature");
            for (int i = 0; i < kv.Length; i += 2) b.Set(kv[i], kv[i + 1]);
            return StudyExecutors.ReadTemperature(b);
        }
        string Fails(params string[] kv) { try { T(kv); return null; } catch (Exception ex) { return ex.Message; } }
        Check(T().Points.SequenceEqual(new[] { -55.0, 25, 85 }) && T().Random == null, "a new Temperature: a list, -55, 25 and 85 °C");
        Check(T("Mode", "Corners").Points.SequenceEqual(new[] { -40.0, 25, 85 }), "corners: the edges and the nominal between them: " + T("Mode", "Corners").Text);
        Check(T("Mode", "Corners", "Nominal", "").Points.SequenceEqual(new[] { -40.0, 85 }), "corners without a nominal: the edges only");
        var tu = T("Mode", "Random");
        Check(tu.Random != null && !tu.Random.Normal && tu.Random.A == -40 && tu.Random.B == 85 && tu.Count == 1, "random: uniform over the range, one temperature per unit: " + tu.Text);
        var tn = T("Mode", "Random", "Distribution", "Normal", "Nominal", "30", "Sigma", "10");
        Check(tn.Random.Normal && tn.Random.A == 30 && tn.Random.B == 10, "random normal: mean and deviation: " + tn.Text);
        var draws = Enumerable.Range(1, 2000).Select(r => tu.Random.Of(7, r)).ToList();
        Check(draws.All(x => x >= -40 && x <= 85) && Math.Abs(draws.Average() - 22.5) < 3 && tu.Random.Of(7, 5) == tu.Random.Of(7, 5) && tu.Random.Of(7, 5) != tu.Random.Of(8, 5),
              $"drawn uniform: within the range, mean {draws.Average():0.0} °C, the same again with the same seed, other with another");
        var nd = Enumerable.Range(1, 4000).Select(r => tn.Random.Of(3, r)).ToList();
        double sd = Math.Sqrt(nd.Select(x => (x - nd.Average()) * (x - nd.Average())).Average());
        Check(Math.Abs(nd.Average() - 30) < 1 && Math.Abs(sd - 10) < 1, $"drawn normal: mean {nd.Average():0.0}, deviation {sd:0.0} °C");
        Check(Fails("Mode", "Corners", "Min", "90") != null && Fails("Mode", "Corners", "Nominal", "100") != null && Fails("Points", "") != null &&
              Fails("Mode", "Random", "Distribution", "Normal", "Sigma", "0") != null && Fails("Mode", "Hot") != null,
              "refused: Max under Min, a nominal outside the range, no point, no deviation, an unknown mode: " + Fails("Mode", "Corners", "Min", "90"));

        // the real executors with a fake host: what Simulate asks the queue for, and the cache
        string dir = Path.Combine(Path.GetTempPath(), "spica_graph_test");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "holdup.asc"), "");
        sch.Set("File", Path.Combine(dir, "holdup.asc"));
        var host = new FakeHost { Folder = dir, Net = Netlist.Parse("* holdup\nV1 in 0 28\nR1 in bulk {mc(100,0.05)}\nC1 bulk 0 {mc(470u,0.2)}\n.tran 100m\n.meas TRAN vbulk AVG V(bulk)\n.end\n", Path.Combine(dir, "holdup.asc"), null) };
        var runner = StudyExecutors.Register(new StudyRunner(), host);
        Check(runner.Run(st), "the study runs: " + string.Join("; ", runner.Message.Values));
        Check(host.Sent.Count == 3, $"three campaigns, one per set of tolerances, at the temperature: {host.Sent.Count}");
        var rn = host.Sent.FirstOrDefault(r => r.Kind == RunKind.Nominal);
        var rm = host.Sent.FirstOrDefault(r => r.Kind == RunKind.MonteCarlo);
        var rw = host.Sent.FirstOrDefault(r => r.Kind == RunKind.WorstCase);
        Check(host.Sent.All(r => r.Temps != null && r.Temps.SequenceEqual(new[] { 25.0, 85 })), "every set at 25 and 85 °C");
        Check(rm != null && rm.Runs == 5 && rm.Seed == 7, "Monte Carlo: 5 units, seed 7");
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

        // what a Simulate runs: every set of tolerances at every temperature
        var cN = Tol(); var cM = Tol("Mode", "Monte Carlo", "Runs", "5");
        var tL = T("Points", "25; 85"); var tC = T("Mode", "Corners"); var tR = T("Mode", "Random");
        var none = StudyExecutors.Matrix(new List<Condition>(), new List<TempSpec>());
        Check(none.Count == 1 && none[0].Kind == RunKind.Nominal && none[0].Temps.Count == 0, "nothing wired: nominal, at the schematic's temperature");
        var only = StudyExecutors.Matrix(new List<Condition>(), new List<TempSpec> { tC });
        Check(only.Count == 1 && only[0].Kind == RunKind.Nominal && only[0].Temps.Count == 3, "a temperature only: nominal at each of its corners");
        var grid = StudyExecutors.Matrix(new List<Condition> { cN, cM }, new List<TempSpec> { tL, tC });
        Check(grid.Count == 4 && grid.Select(c => c.Name).Distinct().Count() == 4 && grid.Count(c => c.Kind == RunKind.MonteCarlo && c.Temps.Count == 3) == 1,
              "two sets of tolerances at two temperatures: four campaigns, " + string.Join("; ", grid.Select(c => c.Name)));
        var paired = StudyExecutors.Matrix(new List<Condition> { cM }, new List<TempSpec> { tR }).Single();
        Check(paired.RandomTemp != null && paired.Temps.Count == 0 && paired.Runs == 5, "Monte Carlo with a temperature drawn: 5 units, each at its own");
        string Refused(Condition c) { try { StudyExecutors.Matrix(new List<Condition> { c }, new List<TempSpec> { tR }); return null; } catch (Exception ex) { return ex.Message; } }
        Check(Refused(cN) != null && Refused(Tol("Mode", "Worst Case")) != null && Refused(cN).Contains("Monte Carlo"), "a temperature drawn without Monte Carlo units is refused: " + Refused(cN));

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
        var drawn = new TempDraw { A = -40, B = 85 };
        o = Build(new CampaignRequest { Kind = RunKind.WorstCase, RandomTemp = drawn }, host.Net, out var e5);
        Check(o == null && e5.Contains("Monte Carlo"), "a worst case with a temperature drawn is refused: " + e5);
        o = Build(new CampaignRequest { Kind = RunKind.MonteCarlo, Runs = 4, Seed = 11, Paired = true, RandomTemp = drawn }, host.Net, out var e6);
        Check(o != null && o.Jobs == 4 && o.TempOf(3, 0) == drawn.Of(11, 3), "Monte Carlo of 4 with a temperature drawn: 4 simulations, each at its own " + e6);
        string chunk = host.Net.BuildChunk(new ChunkSpec { First = 1, Last = 4, Mc = o.Mc, Seed = 11, TempOfRun = r => o.TempOf(r, 0) });
        Check(chunk.Contains(".temp {spica_temp}") && chunk.Contains(".param spica_temp = table(run,1,"), "the chunk sets each run's temperature from a table on the run");

        // the units of traces, by their dimensions, as LTspice gives them
        foreach (var (expr, unit) in new[] { ("V(out)", "V"), ("I(R1)", "A"), ("2*V(out)", "V"), ("V(a,b)", "V"), ("V(out)*I(R1)", "W"), ("V(out)/I(R1)", "Ω"),
                                             ("I(R1)/V(out)", "S"), ("V(out)*V(out)", "V²"), ("V(out)-V(in)", "V"), ("V(out)+I(R1)", ""), ("abs(I(D1))", "A"),
                                             ("sqrt(V(a)*V(b))", "V"), ("pow(I(R1),2)*1k", "A²"), ("db(V(out)/V(in))", "dB"), ("V(out)/V(in)", ""), ("V(out)*I(R1)/I(R2)", "V"),
                                             ("1/I(R1)", "1/A"), ("V(out)*1e-3+2", "V"), ("max(V(a),V(b))", "V"), ("exp(V(out))", "") })
            Check(Spica.UI.TraceUnit.Of(expr) == unit, $"{expr} is in {(unit.Length > 0 ? unit : "no unit")}: {Spica.UI.TraceUnit.Of(expr)}");

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
        File.WriteAllText(f3, "[Project]\nSchematic=holdup.asc\n[Other]\nMode=Corners\n[Study Old]\nWire=1.circuit -> 2.circuit\n[Block 1]\nKind=Schematic\nAt=10,10\n[Block 2]\nKind=Limits\nAt=200,10\nCriteria=holdup\n");
        var old = SpicaProject.Load(f3);
        Check(old.Schematic == p.Schematic && old.Studies.Count == 1 && old.Studies[0].Blocks.Count == 2, "a section it does not know is skipped");
        Check(old.Studies[0].Validate().Any(i => i.Message.Contains("unknown kind 'Limits'")), "a block of a kind it does not know stays, marked unknown");
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
        Check(st.Blocks.Select(b => b.Kind).OrderBy(x => x).SequenceEqual(new[] { "Schematic", "Simulate", "Tolerances" }) && st.Validate().Count == 0,
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

        var nom = st.Blocks.First(b => b.Kind == "Tolerances");
        var wc = st.Add("Tolerances", nom.X, nom.Y + 130).Set("Mode", "Worst Case");
        var corners = st.Add("Temperature", nom.X, nom.Y + 260).Set("Mode", "Corners");
        st.Connect(wc, sim);
        st.Connect(corners, sim);
        w.Call("StudyEdited");
        canvas.Select(wc);
        w.Pump(10);
        Check(((List<string>)w.Call("BlockProblems", wc)).Count == 0, "a worst case on the holdup's two tolerances is fine");
        var fieldsW = ((Panel)w.Get("inspectorBody")).Controls.OfType<Label>().Select(l => l.Text).ToList();
        Check(fieldsW.Contains("Sigma:") && !fieldsW.Contains("Runs:"), "the Tolerances' settings: only the ones its mode uses");
        Check((string)w.Call("BlockCaption", nom) == "nominal · 1 unit" && (string)w.Call("BlockCaption", wc) == "worst case ±3σ · 5 units" &&
              (string)w.Call("BlockCaption", corners) == "corners -40 / 25 / 85 °C" && (string)w.Call("BlockCaption", sim) == "2 campaigns · 18 runs",
              $"the captions say what each is set to, and the Simulate its matrix: {w.Call("BlockCaption", wc)}; {w.Call("BlockCaption", corners)}; {w.Call("BlockCaption", sim)}");
        corners.Set("Mode", "Random");
        w.Call("StudyEdited");
        Check(((List<string>)w.Call("BlockProblems", sim)).Any(p => p.Contains("Monte Carlo")), "a temperature drawn without Monte Carlo units is reported on the Simulate");
        corners.Set("Mode", "Corners");
        canvas.Select(corners);
        w.Pump(10);
        var fields = ((Panel)w.Get("inspectorBody")).Controls;
        Check(fields.OfType<ComboBox>().Count() == 1 && fields.OfType<Label>().Any(l => l.Text == "Lowest:") && !fields.OfType<Label>().Any(l => l.Text == "Temperatures:"),
              "the Temperature's settings: its mode chosen from a list, and only the fields the mode uses");
        w.Shot(w.Form, "temperature");
        canvas.Select(wc);
        w.Pump(10);
        w.Shot(w.Form, "worstcase");

        st.Wires.RemoveAll(x => x.To == sim.Id && x.ToPort == "circuit");
        w.Call("StudyEdited");
        problems = (List<string>)w.Call("StudyProblems");
        Check(problems.Any(p => p.Contains("not wired")), "a Simulate with no circuit is reported: " + string.Join("; ", problems));
        Check(((ToolStripButton)w.Get("tbRun")).Enabled, "Run stays on: pressed, it says what is missing");
        canvas.Select(null);
        w.Shot(w.Form, "missing");

        // the File menu opens under its name, with the recent files filled in
        var menu = (MenuStrip)w.Get("menu");
        var file = (ToolStripMenuItem)menu.Items[0];
        // a click on it, as the mouse sends it
        var c = new System.Drawing.Point(file.Bounds.Left + 8, file.Bounds.Top + 8);
        SendMessage(menu.Handle, 0x0201, (IntPtr)1, (IntPtr)((c.Y << 16) | c.X));
        SendMessage(menu.Handle, 0x0202, IntPtr.Zero, (IntPtr)((c.Y << 16) | c.X));
        w.Pump(10);
        GetWindowRect(file.DropDown.Handle, out var wr);
        var at = new System.Drawing.Rectangle(wr.L, wr.T, wr.R - wr.L, wr.B - wr.T);      // where the window really is
        var under = menu.PointToScreen(new System.Drawing.Point(file.Bounds.Left, file.Bounds.Bottom));
        Check(Math.Abs(at.Left - under.X) < 12 && Math.Abs(at.Top - under.Y) < 12, $"File opens under its name: {at.Location} (its name ends at {under})");
        file.HideDropDown();
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
        var mc = st.Add("Tolerances", 40, 300).Set("Mode", "Monte Carlo").Set("Runs", "5");
        var wc = st.Add("Tolerances", 40, 430).Set("Mode", "Worst Case");
        st.Connect(mc, sim);
        st.Connect(wc, sim);
        st.Connect(st.Add("Temperature", 40, 560).Set("Points", "25; 85"), sim);
        // a second Simulate: the same circuit, Monte Carlo units each at a temperature drawn
        var sim2 = st.Add("Simulate", 320, 400);
        st.Connect(st.Blocks.First(b => b.Kind == "Schematic"), sim2);
        st.Connect(mc, sim2);
        st.Connect(st.Add("Temperature", 40, 690).Set("Mode", "Random"), sim2);
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
        Check(((List<string>)w.Call("StudyProblems")).Count == 0, "the study is ready: nominal, Monte Carlo of 5, worst case at 25 and 85 °C; Monte Carlo of 5 at temperatures drawn");
        RunAndWait(() =>
        {
            first = (DateTime.UtcNow - began).TotalSeconds;
            var queue = (CampaignQueue)w.Get("queue");
            Check(queue.Jobs.Count == 4 && queue.Jobs.All(j => j.Status == JobStatus.Finished), "four campaigns ran in the queue: " + string.Join(", ", queue.Jobs.Select(j => $"{j.Name} {j.Status} {j.Done}/{j.Total}")));
            Check(queue.Jobs.Select(j => j.Total).OrderBy(x => x).SequenceEqual(new[] { 2, 5, 10, 10 }), "at 2 temperatures: nominal 1, Monte Carlo 5, worst case 1 + 2^2; Monte Carlo 5 drawn: " + string.Join(", ", queue.Jobs.Select(j => j.Total)));
            var mcJob = queue.Jobs.First(j => j.Request.RandomTemp != null);
            var mcRows = ResultSet.Load(mcJob.Options.OutputDir).Rows;
            Check(mcRows.Count == 5 && mcRows.All(r => r.Status == RunStatus.Ok && r.Temp >= -40 && r.Temp <= 85) && mcRows.Select(r => r.Temp).Distinct().Count() == 5,
                  "the Monte Carlo units ran each at its own temperature, drawn: " + string.Join(", ", mcRows.Select(r => r.Temp.ToString("0.#") + " °C")));
            var canvas = (Spica.UI.StudyCanvas)w.Get("studyCanvas");
            Check(canvas.States.Values.All(s => s == BlockState.Done), "every block done");
            Check(w.Tab == 2, "the Results tab opens when the study ends");
            w.Tab = 1;
            w.Shot(w.Form, "queue");
            w.Tab = 2;
            var wv = (Spica.UI.WaveformView)w.Get("waveView");
            var tree = w.Wave<TreeView>("tvRuns");
            var sets = tree.Nodes.Cast<TreeNode>().SelectMany(n => n.Nodes.Cast<TreeNode>()).ToList();
            var signals = w.Wave<ListBox>("lbSignals").Items.Cast<string>().ToList();
            Check(wv.Visible && sets.Count == 4, "four analyses to look at, under their study: " + string.Join(", ", sets.Select(n => n.Text)));
            Check(signals.Contains("V(bulk)") && signals.Contains("V(in)"), "the .raw holds every signal: " + string.Join(" ", signals));
            Check(wv.Plotted.Contains("V(bulk)") && wv.SeriesCount >= 2, $"the first look plots what the .meas use: {string.Join(", ", wv.Plotted)}; {wv.SeriesCount} traces");
            w.Shot(w.Form, "nominal");
            foreach (var n in sets) n.Checked = n.Text.StartsWith("Worst");
            w.Pump(30);
            var runs = w.Wave<ComboBox>("cbRuns");
            Check(runs.SelectedItem?.ToString().StartsWith("All") == true && wv.SeriesCount == 10 * wv.Plotted.Count, $"the worst case: all its runs drawn, at both temperatures ({wv.SeriesCount} traces)");
            var bulk = w.Plots().Select((pl, i) => (pl, i)).First(x => x.pl.Traces.Any(t => t.Label != null && t.Label.StartsWith("V(bulk)")));
            w.ClickLabel(bulk.i, bulk.pl.Traces.First(t => t.Label != null && t.Label.StartsWith("V(bulk)")));
            w.Pump(5);
            var meas = w.Wave<Spica.UI.Win98ListView>("lvMeasure");
            var row = meas.Items.Cast<ListViewItem>().FirstOrDefault(x => x.Text.StartsWith("V(bulk)"));
            Check(bulk.pl.Cursors.Count == 1 && row != null && row.SubItems[2].Text != "-", "a click on V(bulk)'s name puts cursor 1 on it, and it reads " + row?.SubItems[2].Text);
            w.Shot(w.Form, "worstcase");

            // the same study again: from the cache, at once
            w.Tab = 0;
            RunAndWait(() =>
            {
                double t2 = (DateTime.UtcNow - began).TotalSeconds;
                Check(canvas.States.TryGetValue(sim.Id, out var ss) && ss == BlockState.Cached && ((CampaignQueue)w.Get("queue")).Jobs.Count == 4,
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
            // the measurements are there at once, before any zoom
            var meas0 = w.Wave<Spica.UI.Win98ListView>("lvMeasure");
            Check(meas0.Items.Count > 0 && meas0.Items.Cast<ListViewItem>().All(x => x.SubItems[5].Text != "-"), "the measurements are there at once: average " + (meas0.Items.Count > 0 ? meas0.Items[0].SubItems[5].Text : "none"));
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
            Check(Math.Abs(Num(rTwice.SubItems[5].Text) - 2 * Num(rOut.SubItems[5].Text)) < 0.01 * Math.Abs(Num(rTwice.SubItems[5].Text)),
                  $"its average is twice V(out)'s: {rTwice.SubItems[5].Text} vs {rOut.SubItems[5].Text}; RMS {rTwice.SubItems[6].Text}, p-p {rTwice.SubItems[9].Text}");
            w.Shot(w.Form, "cursors");
            w.ClickLabel(0, twice);
            w.Pump(5);
            Check(p0.Cursors.Count == 0 && w.Wave<Label>("lblMeasure").Text.StartsWith("Over the time shown"), "a third click takes the cursors off: the table measures the time shown");
            // the time cannot be zoomed out past the simulation, and Reset Zoom brings every pane back
            var wheel = typeof(Control).GetMethod("OnMouseWheel", Flags);
            var pl0 = w.Plots()[0];
            for (int z = 0; z < 15; z++) wheel.Invoke(pl0, new object[] { new MouseEventArgs(MouseButtons.None, 0, pl0.Width / 2, pl0.Height / 2, -120) });
            w.Pump(3);
            Check(pl0.ViewX0 >= -1e-12 && pl0.ViewX1 <= 0.3 + 1e-9 && w.Plots().All(x => Math.Abs(x.ViewX1 - pl0.ViewX1) < 1e-9), $"zoomed out 15 times: the time stays 0 .. 0.3 s ({pl0.ViewX0} .. {pl0.ViewX1}), every pane alike");
            for (int z = 0; z < 6; z++) wheel.Invoke(pl0, new object[] { new MouseEventArgs(MouseButtons.None, 0, pl0.Width / 2, pl0.Height / 2, 120) });
            w.Pump(3);
            // zoomed in as far as the wheel goes, on a point and on a sharp edge: every pane still draws
            for (int z = 0; z < 200; z++) wheel.Invoke(pl0, new object[] { new MouseEventArgs(MouseButtons.None, 0, pl0.Width / 2, pl0.Height / 2, 120) });
            w.Pump(3);
            bool drew = true;
            foreach (var pl in w.Plots()) using (var b = new System.Drawing.Bitmap(Math.Max(10, pl.Width), Math.Max(10, pl.Height)))
                try { pl.DrawToBitmap(b, new System.Drawing.Rectangle(0, 0, b.Width, b.Height)); } catch { drew = false; }
            var shownSpan = pl0.ViewX1 - pl0.ViewX0;
            Check(drew && shownSpan >= 0.3e-9 * 0.99, $"zoomed in 200 times: every pane draws, the time shown stays at least a billionth of the run ({shownSpan:G3} s)");
            w.Shot(w.Form, "deepzoom");
            typeof(Spica.UI.WaveformView).GetMethod("ResetZoom", Flags).Invoke(wv, null);
            for (int z = 0; z < 6; z++) wheel.Invoke(pl0, new object[] { new MouseEventArgs(MouseButtons.None, 0, pl0.Width / 2, pl0.Height / 2, 120) });
            w.Pump(3);
            // a box started in the left margin (on the value axis) zooms too
            typeof(Spica.UI.WaveformView).GetMethod("ResetZoom", Flags).Invoke(wv, null);
            w.Pump(3);
            var down = typeof(Control).GetMethod("OnMouseDown", Flags); var move = typeof(Control).GetMethod("OnMouseMove", Flags); var up = typeof(Control).GetMethod("OnMouseUp", Flags);
            down.Invoke(pl0, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 10, pl0.Height / 2, 0) });
            move.Invoke(pl0, new object[] { new MouseEventArgs(MouseButtons.Left, 0, pl0.Width / 2, pl0.Height / 2 + 2, 0) });
            up.Invoke(pl0, new object[] { new MouseEventArgs(MouseButtons.Left, 1, pl0.Width / 2, pl0.Height / 2 + 2, 0) });
            w.Pump(3);
            Check(pl0.ViewX0 < 1e-9 && pl0.ViewX1 < 0.25, $"a box dragged from the left margin zooms from the start of the time: {pl0.ViewX0:G3} .. {pl0.ViewX1:G3} s");
            for (int z = 0; z < 6; z++) wheel.Invoke(pl0, new object[] { new MouseEventArgs(MouseButtons.None, 0, pl0.Width / 2, pl0.Height / 2, 120) });
            w.Pump(3);
            bool zoomed = pl0.ViewX1 - pl0.ViewX0 < 0.2;
            typeof(Spica.UI.WaveformView).GetMethod("ResetZoom", Flags).Invoke(wv, null);
            w.Pump(3);
            Check(zoomed && w.Plots().All(x => Math.Abs(x.ViewX0) < 1e-12 && Math.Abs(x.ViewX1 - 0.3) < 1e-9), "zoomed in, then Reset Zoom: every pane shows the whole simulation again");
            // a signal dropped on a pane, a trace's name dragged to another pane
            var dropped = typeof(Spica.UI.WaveformView).GetMethod("Dropped", Flags);
            int lastPane = w.Plots().Count - 1;
            dropped.Invoke(wv, new object[] { lastPane, "spica-signal:I(Cout)" });
            w.Pump(5);
            Check(w.Plots()[lastPane].Traces.Any(t => t.Label == "I(Cout)"), "I(Cout) dropped on the last pane: plotted there");
            dropped.Invoke(wv, new object[] { lastPane, "spica-trace:0|2*V(out)" });
            w.Pump(5);
            Check(w.Plots()[lastPane].Traces.Any(t => t.Label == "2*V(out)") && !w.Plots()[0].Traces.Any(t => t.Label == "2*V(out)"), "2*V(out) dragged from the first pane to the last: it moved");
            w.Shot(w.Form, "moved");
            // the project saved and opened again: its last run and its panes, as they were left
            var before = wv.PaneList.Select(x => string.Join("|", x)).ToList();
            var proj = (SpicaProject)w.Get("project");
            Check(proj.Dirty && proj.Views.Count == 1, "the run and the panes belong to the project: it is unsaved");
            string saved = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(proj.Schematic), "saved_by_smoke.spica");
            proj.Save(saved);
            Check(System.IO.File.ReadAllText(saved).Contains("[Results linear_psu]") && System.IO.File.ReadAllText(saved).Contains("Pane=V(out)|V(p)"), "saved: the .spica holds the results and the panes");
            w.Call("OpenFile", saved);
            w.Pump(20);
            var after = wv.PaneList.Select(x => string.Join("|", x)).ToList();
            Check(wv.Visible && before.SequenceEqual(after) && w.Plots().Sum(pl => pl.Traces.Count) > 0 && !((SpicaProject)w.Get("project")).Dirty,
                  $"opened again: the results are there, the panes as they were, nothing to save ({string.Join(" / ", after)})");
            Check(!((ToolStripButton)w.Get("tbSave")).Enabled, "nothing to save: Save is off");
            // volts and amperes in one pane: the amperes on a right axis, as LTspice does; every pane's time axis lines up
            var mixed = w.Plots().First(pl => pl.Traces.Any(t => (string)t.Tag == "I(Cout)") && pl.Traces.Any(t => (string)t.Tag == "V(out)"));
            Check(mixed.YLabel == "V" && mixed.RightLabel == "A" && mixed.Traces.Where(t => t.Right).All(t => ((string)t.Tag).StartsWith("I(")) && w.Plots().All(pl => pl.RightRoom),
                  $"V and A in one pane: V on the left, A on the right ({mixed.YLabel} / {mixed.RightLabel}); every pane keeps the room");
            System.IO.File.Delete(saved);
            // power beside a current: W on its own right axis
            var cout = w.Plots().Select((pl, i) => (pl, i)).First(x => x.pl.Traces.Count > 0 && x.pl.Traces.All(t => (string)t.Tag == "I(Cout)"));
            w.ChoosePane(cout.i);
            wv.Add("V(out)*I(Rload)");
            w.Pump(5);
            var pw = w.Plots()[cout.i];
            Check(pw.YLabel == "A" && pw.RightLabel == "W", $"V(out)*I(Rload) beside I(Cout): A on the left, W on the right ({pw.YLabel} / {pw.RightLabel})");
            w.Shot(w.Form, "power");
            // the Add Trace window: its label clear of the field
            var shot = new System.Windows.Forms.Timer { Interval = 300 };
            shot.Tick += (s2, e2) =>
            {
                var dlg = Application.OpenForms.Cast<Form>().FirstOrDefault(x => x.Text == "Add Trace");
                if (dlg == null) return;
                shot.Stop();
                var lbl = dlg.Controls.OfType<Label>().First();
                var field = dlg.Controls.OfType<Spica.UI.Win98TextField>().First();
                Check(lbl.Right <= field.Left, $"Add Trace: the label ends ({lbl.Right}) before the field starts ({field.Left})");
                w.Shot(dlg, "addtrace");
                dlg.DialogResult = DialogResult.Cancel;
            };
            shot.Start();
            typeof(Spica.UI.MainForm).Assembly.GetType("Spica.UI.Win98Prompt").GetMethod("Ask").Invoke(null, new object[] { w.Form, "Add Trace", "&Expression:", "", "e.g.  2*V(out)    V(out)-V(in)    V(a,b)    abs(I(R1))" });
            // every style of pane, with the cursors on: pictures to choose from
            var p0b = w.Plots()[0];
            var tr = p0b.Traces.First(t => t.Label != null);
            w.ClickLabel(0, tr); w.ClickLabel(0, tr);
            foreach (var th in Spica.UI.PlotTheme.All)
            {
                wv.Theme = th;
                typeof(Spica.UI.WaveformView).GetMethod("Draw", Flags).Invoke(wv, null);
                w.Pump(5);
                Check(w.Plots().All(pl => pl.Theme == th), $"style {th.Name}: every pane drawn in it");
                w.Shot(w.Form, "style_" + th.Name);
            }
            wv.Theme = Spica.UI.PlotTheme.All[0];
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
            var run = new Spica.UI.StudyRunData { Study = "campaign", Sets = new List<RunSet> { set } };
            wv.ShowRuns(new[] { run }, run);
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
    struct WinRect { public int L, T, R, B; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out WinRect r);
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
