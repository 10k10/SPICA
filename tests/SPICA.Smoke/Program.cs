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

        // SPICA.Smoke --opamps <png> [part index] : capture Tools > Op-amp Models
        if (args.Length > 1 && args[0] == "--opamps")
        {
            var f = new Spica.UI.OpAmpForm(null) { StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen };
            f.Shown += (s, e) =>
            {
                if (args.Length > 2) f.Controls.OfType<System.Windows.Forms.ComboBox>().First().SelectedIndex = int.Parse(args[2]);
                f.Refresh();
                using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(args[1]);
                }
                f.Close();
            };
            System.Windows.Forms.Application.Run(f);
            return 0;
        }

        // SPICA.Smoke --reportform <png> <campaign folder> : capture Results > Report...
        if (args.Length > 2 && (args[0] == "--reportform" || args[0] == "--inputsform"))
        {
            var rsf = ResultSet.Load(args[2]);
            ErrorBudget.Register(rsf, rsf.Setup.Errors);
            System.Windows.Forms.Form f = args[0] == "--inputsform" ? (System.Windows.Forms.Form)new Spica.UI.InputsForm(rsf) : new Spica.UI.ReportForm(rsf);
            f.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            f.Shown += (s, e) =>
            {
                f.Refresh();
                using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(args[1]);
                }
                f.Close();
            };
            System.Windows.Forms.Application.Run(f);
            return 0;
        }

        // SPICA.Smoke --attach <campaign folder> : open it in the app; which setup analyses it, and does an edit stay out of campaign.spica
        if (args.Length > 1 && args[0] == "--attach")
        {
            int code = 1;
            var t = new Thread(() =>
            {
                var form = new Spica.UI.MainForm { StartupFile = Path.GetFullPath(args[1]) };
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                string rec = Path.Combine(args[1], "campaign.spica"), before = File.ReadAllText(rec);
                var timer = new System.Windows.Forms.Timer { Interval = 500 };
                int ticks = 0;
                timer.Tick += (s, e) =>
                {
                    var view = (Spica.UI.ResultsView)typeof(Spica.UI.MainForm).GetField("resultsView", flags).GetValue(form);
                    if (view.Data == null && ++ticks < 40) return;
                    timer.Stop();
                    var project = (SpicaProject)typeof(Spica.UI.MainForm).GetField("project", flags).GetValue(form);
                    var rs = view.Data;
                    Console.WriteLine($"standalone={rs.Standalone} setup-is-project={ReferenceEquals(rs.Setup, project)} project={project.FilePath ?? "(unsaved)"} schematic={project.Schematic}");
                    Console.WriteLine($"errors={rs.Setup.Errors.Count} report={rs.Setup.Report.Kind}");
                    // an analysis edit, as the Error Budget page makes it
                    rs.Setup.DesignSigma = 5.5;
                    typeof(Spica.UI.ResultsView).GetMethod("NotifySetup", flags).Invoke(view, null);
                    Console.WriteLine($"after edit: project dirty={project.Dirty}, campaign.spica unchanged={File.ReadAllText(rec) == before}, title={form.Text}");
                    project.Dirty = false;
                    code = 0;
                    form.Close();
                };
                form.Shown += (s, e) => timer.Start();
                System.Windows.Forms.Application.Run(form);
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return code;
        }

        // SPICA.Smoke --bench <runs> : cost of the live Results refresh and of the per-second results.csv
        if (args.Length > 1 && args[0] == "--bench") return Bench(int.Parse(args[1]));

        // SPICA.Smoke --tiptest : hover a list header and a cell, report the tip texts
        if (args.Length > 0 && args[0] == "--tiptest") return TipTest();

        // SPICA.Smoke --charttest : drive the chart's mouse handlers (zoom box, wheel, legend toggle, reset)
        if (args.Length > 0 && args[0] == "--charttest") return ChartTest();

        // SPICA.Smoke --stats <campaign folder> <meas> [lsl usl] : print the analysis of saved results
        if (args.Length > 2 && args[0] == "--results")
        {
            // opens a campaign folder in the app and captures every Results page: --results <folder> <png>
            var t = new Thread(() =>
            {
                var form = new Spica.UI.MainForm { StartupFile = Path.GetFullPath(args[1]) };
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var timer = new System.Windows.Forms.Timer { Interval = 800 };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    var view = (Spica.UI.ResultsView)typeof(Spica.UI.MainForm).GetField("resultsView", flags).GetValue(form);
                    for (int i = 0; i < view.PageCount; i++)
                    {
                        view.SelectPage(i);
                        for (int k = 0; k < 10; k++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(30); }
                        using (var bmp = new System.Drawing.Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                            bmp.Save(args[2].Replace(".png", $"_res{i}.png"));
                        }
                    }
                    form.Close();
                };
                form.Shown += (s, e) => timer.Start();
                System.Windows.Forms.Application.Run(form);
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return 0;
        }
        if (args.Length > 1 && args[0] == "--budget")
        {
            // error budget of a campaign folder; with a 3rd argument also writes the HTML report there
            var rs = ResultSet.Load(args[1]);
            ErrorBudget.Register(rs, rs.Setup.Errors);
            foreach (var it in rs.Setup.Errors)
            {
                Console.WriteLine(ErrorBudget.Compute(rs, it, 0, rs.Setup.DesignSigma, rs.Setup.PessimisticSigma).Summary());
                Console.WriteLine(new string('=', 80));
            }
            if (args.Length > 2)
            {
                var view = new Spica.UI.ResultsView();
                view.SetData(rs, false);
                // "auto": as the end of a campaign does (campaign folder, <schematic>_report.html, timestamp when taken)
                string path = args[2] == "auto" ? view.AutoReport() : args[2];
                if (args[2] != "auto") view.WriteReport(path);
                Console.WriteLine("report: " + path);
            }
            return 0;
        }
        if (args.Length > 2 && args[0] == "--sens")
        {
            // SPICA_INPUTS=Vin|... chooses the inputs (default: automatic); also prints the old view, the input as a tolerance
            var rs = ResultSet.Load(args[1]);
            var inEnv = Environment.GetEnvironmentVariable("SPICA_INPUTS");
            if (inEnv != null) rs.Setup.Inputs = ReportOptions.Split(inEnv);
            ErrorBudget.Register(rs, rs.Setup.Errors);
            var names = rs.GroupNames();
            for (int g = 0; g < names.Count; g++)
            {
                var rep = SensitivityReport.Parameters(rs, args[2], g);
                Console.WriteLine($"== {names[g]}\n" + rep.Narrative(12));
                foreach (var it in rep.Items)
                    Console.WriteLine($"   {it.Name,-28} share {100 * it.Share,7:0.000} %  w/o input {100 * it.ShareRest,7:0.00} %  src {it.Src,8:0.0000}  at limit {it.AtLimit:G4} ({it.LimitText}){(it.IsInput ? "  INPUT" : "")}");
                var plain = SensitivityReport.Parameters(rs, args[2], g, null, false);
                Console.WriteLine("-- input as a tolerance (before 0.5):");
                foreach (var it in plain.Items.Take(6)) Console.WriteLine($"   {it.Name,-28} share {100 * it.Share,7:0.000} %");
            }
            foreach (var e in rs.Setup.Errors)
            {
                var r = ErrorBudget.Compute(rs, e, 0, rs.Setup.DesignSigma, rs.Setup.PessimisticSigma);
                Console.WriteLine(r.Summary());
            }
            return 0;
        }

        if (args.Length > 1 && args[0] == "--oplib")
        {
            File.WriteAllText(args[1], OpAmps.LibraryText());
            return 0;
        }

        if (args.Length > 0 && args[0] == "--stats")
        {
            var rs = ResultSet.Load(args[1]);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (args.Length > 4) rs.Setup.SetSpec(args[2], double.Parse(args[3], inv), double.Parse(args[4], inv));
            Console.WriteLine($"mode {rs.Mode}, rows {rs.Rows.Count}, meas {string.Join(",", rs.Meas)}");
            var ms = MeasStats.Of(args[2], "all", rs.Values(args[2], 0), rs.Setup.Spec(args[2]));
            Console.WriteLine(ms.Summary());
            Console.WriteLine("---- details ----");
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
        RunScheduler.Config? manual = args.Length > 3 ? new RunScheduler.Config(int.Parse(args[2]), int.Parse(args[3])) : (RunScheduler.Config?)null;

        string lt = Ltspice.Locate();
        Console.WriteLine($"LTspice: {lt} {Ltspice.Version(lt)}");
        var hw = HardwareInfo.Detect();
        Console.WriteLine($"HW: {hw}  SMT={hw.HasSmt}");

        string tmp = Path.Combine(Path.GetTempPath(), "SPICA");
        Directory.CreateDirectory(tmp);
        string ini = Ltspice.WritePrivateIni(Path.Combine(tmp, "convert.ini"), true);
        string text = Ltspice.ConvertSchematic(lt, asc, ini, out var enc);
        var nl = Netlist.Parse(text, asc, enc);
        int redirected = SourceBundle.Redirect(nl);
        if (redirected > 0) Console.WriteLine($"Redirected to the source bundle: {redirected} references");
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
            options.Mc = McPlan.Build(nl.Lines, nl.SourcePath);
            foreach (var s in options.Mc.Sites) Console.WriteLine($"site {s.Index} {s.Kind} {s.Label}: {s.Original}");
            foreach (var w in options.Mc.Warnings) Console.WriteLine("WARN " + w);
        }
        string sidecar = Path.ChangeExtension(asc, ".spica");
        if (File.Exists(sidecar)) options.Setup = SpicaProject.Load(sidecar);   // copied into campaign.spica and the source bundle
        var engine = new CampaignEngine(options, hw, new ProfileStore());

        var done = new ManualResetEventSlim();
        engine.Finished += () => done.Set();
        engine.Start();
        // (elapsed, done, smoothed rate, ETA) every second; SPICA_RATE=1 prints how good the ETA was afterwards
        var trace = new System.Collections.Generic.List<double[]>();
        while (!done.Wait(1000))
        {
            while (engine.LogQueue.TryDequeue(out var l)) Console.WriteLine(l);
            var s = engine.Snapshot();
            trace.Add(new[] { s.Elapsed.TotalSeconds, s.Done, s.RunsPerMinute, s.EtaSeconds });
            Console.WriteLine($"   [{s.Done}/{s.Total}] {s.RunsPerMinute:0} runs/min, ETA {s.EtaSeconds:0} s, {s.Config}, {s.Phase}, workers={s.Workers.Count}");
        }
        while (engine.LogQueue.TryDequeue(out var l)) Console.WriteLine(l);
        if (Environment.GetEnvironmentVariable("SPICA_RATE") == "1") RateReport(trace, engine.Snapshot().Elapsed.TotalSeconds);
        Console.WriteLine(File.ReadAllText(Path.Combine(outDir, "summary.txt")));
        Console.WriteLine(string.Join(Environment.NewLine, File.ReadLines(Path.Combine(outDir, "results.csv")).Take(6)));
        var first = Directory.GetFiles(Path.Combine(outDir, "chunks"), "*.net").OrderBy(f => f).First();
        Console.WriteLine("---- first chunk netlist ----");
        Console.WriteLine(File.ReadAllText(first));
        return 0;
    }

    /// <summary>
    /// The ETA shown at each second against the real remaining time, and the smoothed rate against the old one
    /// (finished runs only, 45 s window), which jumps when chunks finish together.
    /// </summary>
    static void RateReport(System.Collections.Generic.List<double[]> trace, double end)
    {
        Console.WriteLine("---- rate / ETA quality ----");
        Console.WriteLine("    t   done   rate(new)  rate(old 45s)   ETA     real   error");
        var errs = new System.Collections.Generic.List<double>();
        var newRates = new System.Collections.Generic.List<double>(); var oldRates = new System.Collections.Generic.List<double>();
        for (int i = 0; i < trace.Count; i++)
        {
            double t = trace[i][0], d = trace[i][1], rate = trace[i][2], eta = trace[i][3], real = end - t;
            int j = i; while (j > 0 && trace[j - 1][0] >= t - 45) j--;
            double old = t - trace[j][0] > 1 && d > trace[j][1] ? (d - trace[j][1]) / (t - trace[j][0]) * 60 : 0;
            if (t > end * 0.2 && t < end * 0.9) { newRates.Add(rate); oldRates.Add(old); if (!double.IsNaN(eta)) errs.Add((eta - real) / end); }
            if (i % 5 == 0) Console.WriteLine($"{t,5:0} {d,6:0} {rate,10:0.0} {old,14:0.0} {eta,7:0} {real,7:0} {(double.IsNaN(eta) ? "" : ((eta - real) / end * 100).ToString("+0.0;-0.0") + " %"),7}");
        }
        double Cv(System.Collections.Generic.List<double> v) { var m = v.Average(); return m <= 0 ? double.NaN : Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Count) / m; }
        Console.WriteLine($"between 20 % and 90 % of the campaign: rate variation (sigma/mean) new {Cv(newRates):P0}, old {Cv(oldRates):P0}; " +
                          $"ETA error mean {errs.Average():P1}, worst {errs.Select(Math.Abs).Max():P1} of the campaign time");
    }

    static int TipTest()
    {
        var form = new System.Windows.Forms.Form { ClientSize = new System.Drawing.Size(600, 300), StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(50, 50) };
        var lv = new Spica.UI.Win98ListView { Dock = System.Windows.Forms.DockStyle.Fill };
        lv.Columns.Add("Measurement", 120); lv.Columns.Add("Skew", 80);
        lv.Items.Add(new System.Windows.Forms.ListViewItem(new[] { "vout", "0.8" }));
        lv.HeaderTip = c => Spica.UI.Glossary.Column(lv.Columns[c].Text);
        lv.CellTip = (i, c) => $"cell {i},{c}";
        form.Controls.Add(lv);
        var tipField = typeof(Spica.UI.Win98ListView).GetField("hoverText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        int rc = 1;
        form.Shown += (s, e) =>
        {
            System.Windows.Forms.Application.DoEvents();
            var header = System.Windows.Forms.Control.FromHandle(lv.Handle) == lv ? GetHeader(lv.Handle) : IntPtr.Zero;
            SendMsg(header, 0x0200, IntPtr.Zero, (IntPtr)((8 << 16) | 150));      // mouse over the "Skew" header
            string h = (string)tipField.GetValue(lv);
            System.Windows.Forms.Cursor.Position = lv.PointToScreen(new System.Drawing.Point(150, lv.Items[0].Bounds.Top + 4));
            SendMsg(lv.Handle, 0x0200, IntPtr.Zero, (IntPtr)(((lv.Items[0].Bounds.Top + 4) << 16) | 150));
            System.Windows.Forms.Application.DoEvents();
            string c = (string)tipField.GetValue(lv);
            Console.WriteLine("header tip: " + (h ?? "(none)").Split('\n')[0]);
            Console.WriteLine("cell tip:   " + (c ?? "(none)"));
            rc = h != null && h.StartsWith("Asymmetry") && c == "cell 0,1" ? 0 : 1;
            form.Close();
        };
        System.Windows.Forms.Application.Run(form);
        return rc;
    }

    static IntPtr GetHeader(IntPtr lv) => SendMsg(lv, 0x101F, IntPtr.Zero, IntPtr.Zero);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessage")] static extern IntPtr SendMsg(IntPtr h, int m, IntPtr w, IntPtr l);

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
            KeepRaw = false, ClockReseed = true, Corners = corners, Mc = McPlan.Build(nl.Lines, nl.SourcePath), Seed = 777,
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
                    typeof(Spica.UI.MainForm).GetMethod("StartCampaign", flags).Invoke(form, new object[] { null, null, 0.0, false });
                    stage = 1; ticks = 0;
                    return;
                }
                var engine = (CampaignEngine)typeof(Spica.UI.MainForm).GetField("engine", flags).GetValue(form);
                if (stage == 1 && ++ticks == 15) Snap(args[2].Replace(".png", "_monitor_running.png"));
                if (engine == null || !engine.IsFinished) return;
                if (engine.Options.WcStage == 1 && ++ticks < 600) return;   // worst case: step 2 starts by itself
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
    static int Bench(int runs)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var meas = new[] { "vout", "ripple", "vmin", "headroom", "ipk" };
        var rnd = new Random(1);
        var list = new System.Collections.Generic.List<RunResult>();
        for (int r = 1; r <= runs; r++)
            list.Add(new RunResult { Run = r, Status = RunStatus.Ok, Values = meas.ToDictionary(m => m, m => (rnd.NextDouble() * 10).ToString("R", inv)) });
        var mc = new McPlan();
        for (int i = 1; i <= 10; i++) mc.Sites.Add(new McSite { Index = i, Kind = SiteKind.Uniform, Label = "R" + i });
        var opt = new CampaignOptions { AscPath = "x.asc", OutputDir = Path.GetTempPath(), Runs = runs, Mc = mc, Seed = 7 };
        var setup = new SpicaProject();
        double T(Action a, int n = 3) { var sw = System.Diagnostics.Stopwatch.StartNew(); for (int i = 0; i < n; i++) a(); return sw.Elapsed.TotalMilliseconds / n; }
        string tmp = Path.Combine(Path.GetTempPath(), "spica_bench.csv");
        Console.WriteLine($"{runs} runs x {meas.Length} measurements, 10 random parameters");
        Console.WriteLine($"  copy results (engine lock)   {T(() => list.ToList()),8:0.0} ms");
        Console.WriteLine($"  results.csv write             {T(() => CampaignEngine.WriteCsv(tmp, list, meas)),8:0.0} ms   (engine, every second while running)");
        ResultSet rs = null;
        Console.WriteLine($"  build result set              {T(() => rs = ResultSet.FromResults(list, meas, opt, setup)),8:0.0} ms");
        var form = new System.Windows.Forms.Form { ClientSize = new System.Drawing.Size(1280, 800) };
        var view = new Spica.UI.ResultsView();
        form.Controls.Add(view);
        form.CreateControl(); view.CreateControl();
        var names = new[] { "Table", "Statistics", "Histogram", "Scatter", "Sensitivity", "Temperature", "Convergence" };
        for (int pg = 0; pg < view.PageCount; pg++)
        {
            view.SelectPage(pg);
            view.SetData(rs, false);
            Console.WriteLine($"  refresh {names[pg],-20} {T(() => view.SetData(ResultSet.FromResults(list, meas, opt, setup), true)),8:0.0} ms   (build + page)");
        }
        File.Delete(tmp);
        return 0;
    }

    static int ChartTest()
    {
        var c = new Spica.UI.Win98Chart { Width = 600, Height = 400 };
        c.Add(new Spica.UI.ChartSeries { Kind = Spica.UI.SeriesKind.Line, X = new[] { 0.0, 10 }, Y = new[] { 0.0, 10 }, Label = "a" });
        c.Add(new Spica.UI.ChartSeries { Kind = Spica.UI.SeriesKind.Line, X = new[] { 0.0, 10 }, Y = new[] { 100.0, 100 }, Label = "b" });
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        void Paint() { using (var b = new System.Drawing.Bitmap(600, 400)) using (var g = System.Drawing.Graphics.FromImage(b)) c.Render(g, new System.Drawing.Rectangle(0, 0, 600, 400), true); }
        void Call(string m, object a) => typeof(System.Windows.Forms.Control).GetMethod(m, flags).Invoke(c, new[] { a });
        double Get(string f) => (double)typeof(Spica.UI.Win98Chart).GetField(f, flags).GetValue(c);
        string Range() => $"x {Get("x0"):0.##}..{Get("x1"):0.##}  y {Get("y0"):0.##}..{Get("y1"):0.##}";
        System.Windows.Forms.MouseEventArgs M(System.Windows.Forms.MouseButtons b, int x, int y, int d = 0) => new System.Windows.Forms.MouseEventArgs(b, 1, x, y, d);
        Paint(); Console.WriteLine("full:       " + Range());
        Call("OnMouseDown", M(System.Windows.Forms.MouseButtons.Left, 150, 150)); Call("OnMouseMove", M(System.Windows.Forms.MouseButtons.Left, 350, 300));
        Call("OnMouseUp", M(System.Windows.Forms.MouseButtons.Left, 350, 300)); Paint(); Console.WriteLine("box zoom:   " + Range() + "  zoomed=" + c.IsZoomed);
        Call("OnMouseWheel", M(System.Windows.Forms.MouseButtons.None, 300, 200, -120)); Paint(); Console.WriteLine("wheel out:  " + Range());
        Call("OnMouseDoubleClick", M(System.Windows.Forms.MouseButtons.Left, 300, 200)); Paint(); Console.WriteLine("reset:      " + Range() + "  zoomed=" + c.IsZoomed);
        var hits = (System.Collections.IList)typeof(Spica.UI.Win98Chart).GetField("legendHits", flags).GetValue(c);
        var hb = (System.Collections.Generic.KeyValuePair<System.Drawing.Rectangle, string>)hits[1];
        Call("OnMouseDown", M(System.Windows.Forms.MouseButtons.Left, hb.Key.X + 5, hb.Key.Y + 5)); Paint(); Console.WriteLine("hide 'b':   " + Range());
        c.ResetView(); Paint(); Console.WriteLine("ResetView:  " + Range());
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
