using System;
using System.IO;
using System.Linq;
using System.Threading;
using Spica.Core;

// Headless end-to-end check of the campaign engine:
//   SPICA.Smoke <schematic.asc> <runs> [instances threads]
static class Program
{
    static int Main(string[] args)
    {
        // SPICA.Smoke --ui <campaign folder> <png dir> [meas lsl usl] [tfX tfY theory budget] : screenshot every Results tab
        if (args.Length > 0 && args[0] == "--ui") return UiCapture(args);
        // SPICA.Smoke --setup <file.asc|.spica> <png> : render the main window's Setup tab after loading a file
        if (args.Length > 0 && args[0] == "--setup") return SetupCapture(args);
        // SPICA.Smoke --resume <schematic.asc> <runs> <corners> : kill mid-run, resume, compare with an uninterrupted campaign
        if (args.Length > 0 && args[0] == "--resume") return ResumeTest(args);
        // SPICA.Smoke --dialog <png> : render the four-button Stop dialog
        if (args.Length > 0 && args[0] == "--dialog")
        {
            var timer = new System.Windows.Forms.Timer { Interval = 400 };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var f = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().Last();
                using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(args[1]);
                }
                f.Close();
            };
            timer.Start();
            Spica.UI.Win98MessageBox.Choose(null, "Stop the campaign?  529 of 1,200 simulations are finished, 24 are running now.\n\n" +
                "Analyze now:  stop at once and open the finished runs (the running ones are lost).\n" +
                "Finish running:  start nothing new, wait for the running simulations, then analyze.\n" +
                "Discard:  stop and delete this campaign folder with everything in it.\n\n" +
                "A stopped campaign can be resumed later from File > Open Results.",
                "Stop Campaign", Spica.UI.Win98Icon.Question, "&Analyze now", "&Finish running", "&Discard", "Keep running");
            return 0;
        }

        // SPICA.Smoke --stats <campaign folder> <meas> [lsl usl] : print the analysis of saved results
        if (args.Length > 0 && args[0] == "--stats")
        {
            var rs = ResultSet.Load(args[1]);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (args.Length > 4) rs.Setup.SetSpec(args[2], double.Parse(args[3], inv), double.Parse(args[4], inv));
            Console.WriteLine($"mode {rs.Mode}, rows {rs.Rows.Count}, meas {string.Join(",", rs.Meas)}");
            var ms = MeasStats.Of(args[2], "all", rs.Values(args[2], 0), rs.Setup.Spec(args[2]));
            Console.WriteLine(ms.Narrative());
            Console.WriteLine($"RAW A2={ms.AdA2:R} p={ms.AdP:R} skew={ms.D.Skew:R} kurt={ms.D.ExKurt:R} q00135={ms.P00135:R} lo={ms.Cap.YieldLo:R} hi={ms.Cap.YieldHi:R} ppm={ms.Cap.PredictedPpm:R}");
            // SPICA_TF=X,Y,theory,budget% overrides the default (first two measurements, 1.93*x, 2 %)
            var tfArgs = (Environment.GetEnvironmentVariable("SPICA_TF") ?? "").Split(',');
            if (rs.Meas.Count >= 2)
            {
                var tf = new TfSetup { X = rs.Meas[0], Y = rs.Meas[1], Theory = "1.93*x", Budget = 2, BudgetPercent = true, K = 3 };
                if (tfArgs.Length == 4) { tf.X = tfArgs[0]; tf.Y = tfArgs[1]; tf.Theory = tfArgs[2]; tf.Budget = double.Parse(tfArgs[3], inv); }
                Console.WriteLine(TfReport.Compute(rs, tf, 0).Narrative());
            }
            return 0;
        }

        string asc = Path.GetFullPath(args.Length > 0 ? args[0] : @"..\..\samples\rc_mc.asc");
        int runs = args.Length > 1 ? int.Parse(args[1]) : 200;
        AdaptiveScheduler.Config? manual = args.Length > 3 ? new AdaptiveScheduler.Config(int.Parse(args[2]), int.Parse(args[3])) : (AdaptiveScheduler.Config?)null;

        string lt = Ltspice.Locate();
        Console.WriteLine($"LTspice: {lt} {Ltspice.Version(lt)}");
        var hw = HardwareInfo.Detect();
        Console.WriteLine($"HW: {hw}  SMT={hw.HasSmt}");

        string tmp = Path.Combine(Path.GetTempPath(), "SPICA");
        Directory.CreateDirectory(tmp);
        string ini = Ltspice.WritePrivateIni(Path.Combine(tmp, "convert.ini"), true);
        string text = Ltspice.ConvertSchematic(lt, asc, ini, out var enc);
        var nl = Netlist.Parse(text, asc, enc);
        Console.WriteLine($"Nodes: {string.Join(" ", nl.Nodes)}");
        Console.WriteLine($"Currents: {string.Join(" ", nl.Currents)}");
        Console.WriteLine($"Meas: {string.Join(" ", nl.MeasNames)}  required: {string.Join(" ", nl.RequiredSignals)}");
        Console.WriteLine($"Steps removed: {string.Join(" | ", nl.RemovedSteps)}");

        // Optional: SPICA_TEMP=corners:-40,25,90 | random:-40,90   SPICA_PAIRED=1   SPICA_SEED=n
        string outDir = Path.Combine(tmp, "smoke_" + DateTime.Now.ToString("HHmmss"));
        var options = new CampaignOptions
        {
            LtspiceExe = lt, AscPath = asc, OutputDir = outDir, Netlist = nl, Runs = runs,
            SaveSignals = nl.RequiredSignals.ToList(), KeepRaw = Environment.GetEnvironmentVariable("SPICA_NORAW") == null, ClockReseed = true, Manual = manual,
            Seed = uint.TryParse(Environment.GetEnvironmentVariable("SPICA_SEED"), out var seed) ? seed : 12345,
        };
        string temp = Environment.GetEnvironmentVariable("SPICA_TEMP") ?? "";
        if (temp.StartsWith("corners:")) options.Corners = SpicaProject.ParseList(temp.Substring(8));
        if (temp.StartsWith("random:"))
        {
            var mm = SpicaProject.ParseList(temp.Substring(7));
            options.RandomTemp = true; options.TempMin = mm[0]; options.TempMax = mm[1];
        }
        if (Environment.GetEnvironmentVariable("SPICA_PAIRED") == "1")
        {
            options.Mc = McPlan.Build(nl.Lines);
            foreach (var s in options.Mc.Sites) Console.WriteLine($"site {s.Index} {s.Kind} {s.Label}: {s.Original}");
            foreach (var w in options.Mc.Warnings) Console.WriteLine("WARN " + w);
        }
        var engine = new CampaignEngine(options, hw, new ProfileStore());

        var done = new ManualResetEventSlim();
        engine.Finished += () => done.Set();
        engine.Start();
        while (!done.Wait(1000))
        {
            while (engine.LogQueue.TryDequeue(out var l)) Console.WriteLine(l);
            var s = engine.Snapshot();
            Console.WriteLine($"   [{s.Done}/{s.Total}] {s.RunsPerMinute:0} runs/min, {s.Config}, {s.Phase}, workers={s.Workers.Count}");
        }
        while (engine.LogQueue.TryDequeue(out var l)) Console.WriteLine(l);
        Console.WriteLine(File.ReadAllText(Path.Combine(outDir, "summary.txt")));
        Console.WriteLine(string.Join(Environment.NewLine, File.ReadLines(Path.Combine(outDir, "results.csv")).Take(6)));
        var first = Directory.GetFiles(Path.Combine(outDir, "chunks"), "*.net").OrderBy(f => f).First();
        Console.WriteLine("---- first chunk netlist ----");
        Console.WriteLine(File.ReadAllText(first));
        return 0;
    }

    static int ResumeTest(string[] args)
    {
        string asc = Path.GetFullPath(args[1]);
        int runs = int.Parse(args[2]);
        var corners = SpicaProject.ParseList(args[3]);
        string lt = Ltspice.Locate();
        var hw = HardwareInfo.Detect();
        string tmp = Path.Combine(Path.GetTempPath(), "SPICA");
        Directory.CreateDirectory(tmp);
        string text = Ltspice.ConvertSchematic(lt, asc, Ltspice.WritePrivateIni(Path.Combine(tmp, "convert.ini"), true), out var enc);
        var nl = Netlist.Parse(text, asc, enc);
        string stamp = DateTime.Now.ToString("HHmmss");

        CampaignOptions Opt(string dir) => new CampaignOptions
        {
            LtspiceExe = lt, AscPath = asc, OutputDir = dir, Netlist = nl, Runs = runs, SaveSignals = nl.RequiredSignals.ToList(),
            KeepRaw = false, ClockReseed = true, Corners = corners, Mc = McPlan.Build(nl.Lines), Seed = 777,
            Setup = new SpicaProject { Schematic = asc, Runs = runs, Seed = 777, Paired = true, Temp = TempMode.Corners, Corners = corners },
        };
        CampaignEngine Run(CampaignOptions o, Action<CampaignEngine> during)
        {
            var e = new CampaignEngine(o, hw, new ProfileStore());
            var done = new ManualResetEventSlim();
            e.Finished += () => done.Set();
            e.Start();
            during?.Invoke(e);
            done.Wait();
            return e;
        }
        int Lines(string dir) { try { return File.ReadAllLines(Path.Combine(dir, "results.csv")).Length - 1; } catch { return -1; } }

        // 1. reference, uninterrupted
        string refDir = Path.Combine(tmp, "resume_ref_" + stamp);
        Run(Opt(refDir), null);
        Console.WriteLine($"reference: {Lines(refDir)} rows, state {CampaignState.Load(refDir).Status}");

        // 2. hard stop part-way, watching results.csv grow
        string dir = Path.Combine(tmp, "resume_cut_" + stamp);
        Run(Opt(dir), e =>
        {
            var seen = new System.Collections.Generic.List<int>();
            while (e.Snapshot().Done < e.Options.Jobs * 0.4) { Thread.Sleep(250); seen.Add(Lines(dir)); }
            Console.WriteLine("results.csv rows while running: " + string.Join(" ", seen.Distinct()));
            e.Cancel();
        });
        var st = CampaignState.Load(dir);
        Console.WriteLine($"after Cancel: state {st.Status}, done {st.Done}/{st.Jobs}, rows {Lines(dir)}, unfinished={st.Unfinished}");

        // 3. resume
        var o2 = Opt(dir);
        o2.Resume = CampaignState.LoadResults(dir, corners);
        o2.PriorSeconds = st.ElapsedSeconds;
        Run(o2, null);
        st = CampaignState.Load(dir);
        Console.WriteLine($"after resume: state {st.Status}, done {st.Done}/{st.Jobs}, rows {Lines(dir)}");

        // 4. identical to the reference? (paired samples: same unit, same value)
        var a = File.ReadAllLines(Path.Combine(refDir, "results.csv")).Skip(1).Select(l => string.Join(",", l.Split(',').Take(l.Split(',').Length - 1))).OrderBy(s => s).ToList();
        var b = File.ReadAllLines(Path.Combine(dir, "results.csv")).Skip(1).Select(l => string.Join(",", l.Split(',').Take(l.Split(',').Length - 1))).OrderBy(s => s).ToList();
        int diff = a.Zip(b, (x, y) => x == y ? 0 : 1).Sum() + Math.Abs(a.Count - b.Count);
        Console.WriteLine($"resumed vs uninterrupted: {a.Count} vs {b.Count} rows, {diff} rows differ");

        // 5. graceful stop: running chunks must complete
        string dir3 = Path.Combine(tmp, "resume_drain_" + stamp);
        int atStop = 0, runningAtStop = 0;
        Run(Opt(dir3), e =>
        {
            while (e.Snapshot().Running == 0 || e.Snapshot().Done < e.Options.Jobs * 0.3) Thread.Sleep(50);
            var s = e.Snapshot(); atStop = s.Done; runningAtStop = s.Running;
            e.StopAfterRunning();
        });
        st = CampaignState.Load(dir3);
        Console.WriteLine($"finish-running stop: {atStop} done + {runningAtStop} running at the request -> {st.Done} done, state {st.Status}");
        Console.WriteLine("folders: " + refDir + " | " + dir + " | " + dir3);
        return diff == 0 ? 0 : 1;
    }

    static int SetupCapture(string[] args)
    {
        var t = new Thread(() =>
        {
            var form = new Spica.UI.MainForm { StartupFile = Path.GetFullPath(args[1]) };
            var nlField = typeof(Spica.UI.MainForm).GetField("netlist", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            bool run = args.Length > 3 && args[3] == "run";   // also start the campaign and capture Monitor/Results
            var timer = new System.Windows.Forms.Timer { Interval = 200 };
            int ticks = 0, stage = 0;
            void Snap(string file)
            {
                for (int k = 0; k < 10; k++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(30); }
                using (var bmp = new System.Drawing.Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(file);
                }
            }
            bool busy = false;
            timer.Tick += (s, e) =>
            {
                if (busy) return;   // Snap pumps messages: don't re-enter
                busy = true;
                try { Step(); } finally { busy = false; }
            };
            void Step()
            {
                if (stage == 0)
                {
                    if (nlField.GetValue(form) == null && ++ticks < 150) return;
                    Snap(args[2]);
                    if (!run) { timer.Stop(); form.Close(); return; }
                    typeof(Spica.UI.MainForm).GetMethod("StartCampaign", flags).Invoke(form, new object[] { null, null, 0.0 });
                    stage = 1; ticks = 0;
                    return;
                }
                var engine = (CampaignEngine)typeof(Spica.UI.MainForm).GetField("engine", flags).GetValue(form);
                if (stage == 1 && ++ticks == 15) Snap(args[2].Replace(".png", "_monitor_running.png"));
                if (engine == null || !engine.IsFinished) return;
                // close the "campaign finished" message box, then capture the results tabs
                foreach (var f in System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().Where(f => f != form).ToList())
                {
                    Snap(args[2].Replace(".png", "_finished_box.png"));
                    f.DialogResult = System.Windows.Forms.DialogResult.No;
                }
                if (stage == 1) { stage = 2; return; }
                timer.Stop();
                var tabs = (Spica.UI.Win98TabControl)typeof(Spica.UI.MainForm).GetField("tabs", flags).GetValue(form);
                tabs.SelectedIndex = 1; Snap(args[2].Replace(".png", "_monitor.png"));
                tabs.SelectedIndex = 2;
                var view = (Spica.UI.ResultsView)typeof(Spica.UI.MainForm).GetField("resultsView", flags).GetValue(form);
                for (int i = 0; i < view.PageCount; i++) { view.SelectPage(i); Snap(args[2].Replace(".png", $"_res{i}.png")); }
                Console.WriteLine("output: " + engine.Options.OutputDir);
                form.Close();
            }
            form.Shown += (s, e) => timer.Start();
            System.Windows.Forms.Application.Run(form);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return 0;
    }

    [STAThread]
    static int UiCapture(string[] args)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var rs = ResultSet.Load(args[1]);
        Directory.CreateDirectory(args[2]);
        if (args.Length > 5) rs.Setup.SetSpec(args[3], double.Parse(args[4], inv), double.Parse(args[5], inv));
        if (args.Length > 9) rs.Setup.Tf = new TfSetup { X = args[6], Y = args[7], Theory = args[8], Budget = double.Parse(args[9], inv) };
        var t = new Thread(() =>
        {
            var form = new Spica.UI.Win98Form { Text = "SPICA - Results", ClientSize = new System.Drawing.Size(1280, 820), StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(20, 20), TopMost = true };
            var view = new Spica.UI.ResultsView();
            form.Controls.Add(view);
            form.Shown += (s, e) =>
            {
                view.SetData(rs, false);
                for (int i = 0; i < view.PageCount; i++)
                {
                    view.SelectPage(i);
                    for (int k = 0; k < 10; k++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(40); }
                    var b = form.Bounds;
                    using (var bmp = new System.Drawing.Bitmap(b.Width, b.Height))
                    {
                        // off-screen rendering: works with a locked screen too
                        form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, b.Width, b.Height));
                        bmp.Save(Path.Combine(args[2], $"tab{i}.png"));
                    }
                }
                view.WriteReport(Path.Combine(args[2], "report.html"));
                form.Close();
            };
            System.Windows.Forms.Application.Run(form);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return 0;
    }
}
