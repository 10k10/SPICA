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
        // SPICA.Smoke --queue <file.spica> <png> : two campaigns in the queue plus two through the listener (a bundle, a setup)
        if (args.Length > 0 && args[0] == "--queue") return QueueTest(args);
        // SPICA.Smoke --queueops <file.spica> <png> : hold, move, remove, discard and stop in a queue of three
        if (args.Length > 0 && args[0] == "--queueops") return QueueOps(args);
        // SPICA.Smoke --remote <file.spica> <png> : Run Remote to a listener (a second queue in this process) while a campaign runs here, then the listener lock
        if (args.Length > 0 && args[0] == "--remote") return RemoteTest(args);
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
        if (args.Length > 1 && args[0] == "--locate")
        {
            // a remote campaign's results found in the share when the listener names its folder differently: --locate <empty dir>
            string share = Path.Combine(Path.GetFullPath(args[1]), "jobs"), camp = Path.Combine(share, "results", "job", "20261117_090000");
            string beside = Path.Combine(Path.GetFullPath(args[1]), "jobs_results", "job2", "20261113_151242");
            foreach (var d in new[] { camp, beside })
            {
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "results.csv"), "run\n");
            }
            var cases = new[]
            {
                (share, Path.Combine(share, "results", "job", "20261117_090000"), true),           // same name on both sides
                (@"D:\Fila", @"D:\Fila\results\job\20261117_090000", true),                      // local path on the workstation
                (@"\\WS\fila", @"D:\Fila\results\job\20261117_090000", true),                    // listener started on the UNC name
                (@"Z:\", @"Z:\results\job\20261117_090000", true),                               // mapped drive
                (@"D:\Fila", @"E:\elsewhere\job\20261117_090001", false),                        // not in the share at all
                (@"F:\X\jobs", @"F:\X\jobs_results\job2\20261113_151242", true),                 // beside the watched folder
                (@"F:\X\jobs", beside, true),                                                    // same drive on both PCs
            };
            int bad = 0;
            foreach (var (input, results, found) in cases)
            {
                string got = RemoteState.Locate(share, input, results);
                string want = results.Contains("jobs_results") ? beside : camp;
                bool ok = (got != null) == found && (got == null || string.Equals(Path.GetFullPath(got).TrimEnd('\\'), want, StringComparison.OrdinalIgnoreCase));
                if (!ok) bad++;
                Console.WriteLine($"{(ok ? "ok   " : "WRONG")} input {input}, results {results} -> {got ?? "(not found)"}");
            }
            return bad;
        }
        if (args.Length > 2 && args[0] == "--studyrun")
        {
            // a study run from the graph, end to end with LTspice: <asc> <png>
            // tests x temperatures with limits, a report, the cache on a second run, and a fault study (a short)
            int bad = 0;
            var t = new Thread(() =>
            {
                var form = new Spica.UI.MainForm { StartupFile = Path.GetFullPath(args[1]), Width = 1360, Height = 860 };
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var mf = typeof(Spica.UI.MainForm);
                object Get(string f) => mf.GetField(f, flags).GetValue(form);
                void Call(string m) => mf.GetMethod(m, flags, null, Type.EmptyTypes, null).Invoke(form, null);
                bool Running() => (bool)mf.GetProperty("StudyRunning", flags).GetValue(form);
                void Check(bool ok, string what) { if (!ok) bad++; Console.WriteLine($"{(ok ? "ok   " : "WRONG")} {what}"); }
                void Pump(int n = 15) { for (int k = 0; k < n; k++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(30); } }
                void Shot(System.Windows.Forms.Control c, string name)
                {
                    Pump();
                    using (var bmp = new System.Drawing.Bitmap(c.Width, c.Height))
                    {
                        c.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, c.Width, c.Height));
                        bmp.Save(args[2].Replace(".png", "_" + name + ".png"));
                    }
                }
                var verdicts = (System.Collections.Generic.List<Spica.Core.Graph.VerdictRecord>)null;
                var steps = new System.Collections.Generic.Queue<Action>();
                var began = DateTime.UtcNow;
                System.Windows.Forms.Timer poll = null;
                void Next() { began = DateTime.UtcNow; if (steps.Count > 0) steps.Dequeue()(); else { ((SpicaProject)Get("project")).Dirty = false; form.Close(); } }
                void RunAndWait(Action after)
                {
                    Call("RunStudy");
                    poll = new System.Windows.Forms.Timer { Interval = 1000 };
                    poll.Tick += (s, e) =>
                    {
                        if (Running() && (DateTime.UtcNow - began).TotalSeconds < 900) return;
                        poll.Stop();
                        verdicts = (System.Collections.Generic.List<Spica.Core.Graph.VerdictRecord>)Get("lastVerdicts");
                        after();
                        Next();
                    };
                    poll.Start();
                }
                Spica.Core.Graph.Study study = null, fault = null;
                double firstSeconds = 0;
                steps.Enqueue(() =>
                {
                    var project = (SpicaProject)Get("project");
                    project.QualCriteria = new System.Collections.Generic.List<QualCriterion>
                    {
                        new QualCriterion { Name = "holdup", Signal = "V(bulk)", Lo = 18, Tests = "interruption", Requirement = "REQ-PWR-012" },
                        new QualCriterion { Name = "overvolt", Signal = "V(bulk)", Hi = 45 },
                        new QualCriterion { Name = "recovered", Signal = "V(bulk)", Lo = 26, Hi = 28, From = QualAnchor.EventEnd, FromOffset = 0.05, To = QualAnchor.TestEnd, Tests = "interruption|surge" },
                        new QualCriterion { Name = "shorted", Signal = "I(D1)", Hi = 1, From = QualAnchor.EventStart, FromOffset = 0.002, To = QualAnchor.EventEnd },
                    };
                    study = new Spica.Core.Graph.Study { Name = "Holdup study" };
                    var sw = study.Add("SupplyWaveform").Set("Library", "do160g_s16_28vdc").Set("Category", "A").Set("Net", "in")
                                  .Set("Tests", "Power interruption, single, 10 ms | Power interruption, single, 200 ms | Normal surge, overvoltage");
                    var sch = study.Add("Schematic").Set("File", project.Schematic).Set("Name", "holdup");
                    var co = study.Add("Corners").Set("Temperatures", "25; 85");
                    var sim = study.Add("Simulate");
                    var lim = study.Add("Limits").Set("Criteria", "holdup | overvolt | recovered");
                    var rep = study.Add("Report").Set("Title", "Hold-up qualification");
                    var pl = study.Add("Plots").Set("Signals", "I(D1)");
                    var ex = study.Add("Export");
                    study.Connect(sw, sch); study.Connect(sch, sim); study.Connect(co, sim); study.Connect(sim, lim);
                    study.Connect(lim, rep, "", "verdicts"); study.Connect(sim, pl); study.Connect(lim, ex, "", "verdicts");
                    study.AutoLayout();
                    fault = new Spica.Core.Graph.Study { Name = "Bulk short" };
                    var f = fault.Add("Fault").Set("From", "bulk").Set("To", "0").Set("R", "1").Set("Start", "20m").Set("Duration", "30m");
                    var fs = fault.Add("Schematic").Set("File", project.Schematic).Set("Name", "holdup");
                    var fn = fault.Add("Nominal");
                    var fsim = fault.Add("Simulate");
                    var flim = fault.Add("Limits").Set("Criteria", "shorted");
                    fault.Connect(f, fs); fault.Connect(fs, fsim); fault.Connect(fn, fsim); fault.Connect(fsim, flim);
                    fault.AutoLayout();
                    project.Studies.Clear();
                    project.Studies.Add(study); project.Studies.Add(fault);
                    // a short output folder: the scratch folders alone are long, and LTspice stops at 260 characters of path
                    ((Spica.UI.Win98TextField)Get("txtOut")).Text = Path.Combine(Path.GetTempPath(), "spq");
                    ((Spica.UI.Win98TabControl)Get("tabs")).SelectedIndex = 4;
                    Call("ShowStudies");
                    ((System.Windows.Forms.ComboBox)Get("cbStudy")).SelectedIndex = 0;
                    Check(study.Validate().Count == 0 && fault.Validate().Count == 0, "both studies are wired");
                    RunAndWait(() =>
                    {
                        firstSeconds = (DateTime.UtcNow - began).TotalSeconds;
                        foreach (var v in verdicts) Console.WriteLine($"      {v.Case} | {v.Condition} | {v.Criterion} | {v.Verdict} | {v.Worst} | {v.Requirement}");
                        Check(verdicts.Count == 16, $"16 verdicts (3 tests x 2 temperatures, the criteria that apply): {verdicts.Count}");
                        Check(verdicts.Where(v => v.Criterion == "holdup" && v.Case.Contains("10 ms")).All(v => v.Verdict == "PASS") &&
                              verdicts.Where(v => v.Criterion == "holdup" && v.Case.Contains("200 ms")).All(v => v.Verdict == "FAIL"), "10 ms holds up, 200 ms does not, at both temperatures");
                        Check(verdicts.Any(v => v.Requirement == "REQ-PWR-012"), "the requirement goes with its verdicts");
                        var rep = (string)Get("lastStudyReport");
                        Check(rep != null && File.Exists(rep) && File.ReadAllText(rep).Contains("REQ-PWR-012"), "the report is written: " + rep);
                        var canvas = (Spica.UI.StudyCanvas)Get("studyCanvas");
                        Check(canvas.States.Values.All(s => s == Spica.Core.Graph.BlockState.Done || s == Spica.Core.Graph.BlockState.Cached), "every block done (or from the cache)");
                        Shot(form, "study");
                        var res = (System.Windows.Forms.Form)Get("studyResults");
                        Shot(res, "results");
                        mf.GetMethod("OpenStudyWaves", flags).Invoke(form, new object[] { verdicts.First(v => v.Criterion == "holdup" && v.Case.Contains("200 ms")) });
                        Pump(30);
                        var wave = System.Windows.Forms.Application.OpenForms.OfType<Spica.UI.QualWaveForm>().LastOrDefault();
                        Check(wave != null, "the waveforms of a cell open");
                        if (wave != null) { Shot(wave, "wave"); wave.Close(); }
                    });
                });
                steps.Enqueue(() =>
                {
                    // the same study again: everything from the cache
                    RunAndWait(() =>
                    {
                        double again = (DateTime.UtcNow - began).TotalSeconds;
                        var canvas = (Spica.UI.StudyCanvas)Get("studyCanvas");
                        Check(canvas.States.Values.Contains(Spica.Core.Graph.BlockState.Cached) && verdicts.Count == 16, $"a second run comes from the cache: {again:0.0} s (first {firstSeconds:0.0} s)");
                    });
                });
                steps.Enqueue(() =>
                {
                    ((System.Windows.Forms.ComboBox)Get("cbStudy")).SelectedIndex = 1;
                    RunAndWait(() =>
                    {
                        foreach (var v in verdicts) Console.WriteLine($"      {v.Case} | {v.Condition} | {v.Criterion} | {v.Verdict} | {v.Worst}");
                        Check(verdicts.Count == 1 && verdicts[0].Verdict == "FAIL", "the short draws current through the diode (the ideal source holds the voltage): " + (verdicts.Count > 0 ? verdicts[0].Worst : "no verdict"));
                        Shot(form, "fault");
                    });
                });
                var start = new System.Windows.Forms.Timer { Interval = 4000 };
                start.Tick += (s, e) => { start.Stop(); Next(); };
                form.Shown += (s, e) => start.Start();
                System.Windows.Forms.Application.Run(form);
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            Console.WriteLine(bad == 0 ? "STUDY RUN: all ok" : $"STUDY RUN: {bad} wrong");
            return bad;
        }
        if (args.Length > 2 && args[0] == "--studies")
        {
            // the Studies tab: templates on the canvas, a block's properties, a wire drawn with the mouse, the New Study dialog: <asc> <png>
            int bad = 0;
            var t = new Thread(() =>
            {
                var form = new Spica.UI.MainForm { StartupFile = Path.GetFullPath(args[1]), Width = 1360, Height = 860 };
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var mf = typeof(Spica.UI.MainForm);
                object Get(string f) => mf.GetField(f, flags).GetValue(form);
                void Call(string m) => mf.GetMethod(m, flags, null, Type.EmptyTypes, null).Invoke(form, null);
                void Check(bool ok, string what) { if (!ok) bad++; Console.WriteLine($"{(ok ? "ok   " : "WRONG")} {what}"); }
                void Pump(int n = 15) { for (int k = 0; k < n; k++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(30); } }
                void Shot(System.Windows.Forms.Control c, string name)
                {
                    Pump();
                    using (var bmp = new System.Drawing.Bitmap(c.Width, c.Height))
                    {
                        c.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, c.Width, c.Height));
                        bmp.Save(args[2].Replace(".png", "_" + name + ".png"));
                    }
                }
                var start = new System.Windows.Forms.Timer { Interval = 4000 };
                start.Tick += (s, e) =>
                {
                    start.Stop();
                    var project = (SpicaProject)Get("project");
                    ((Spica.UI.Win98TabControl)Get("tabs")).SelectedIndex = 4;
                    Shot(form, "empty");
                    foreach (var name in new[] { "Qualification at corners + worst case", "Compare candidates", "Derating" })
                        project.Studies.Add(Spica.Core.Graph.StudyTemplates.Find(name).Make(project));
                    project.Studies.AddRange(Spica.Core.Graph.LegacyStudies.From(project));
                    Call("ShowStudies");
                    var cb = (System.Windows.Forms.ComboBox)Get("cbStudy");
                    var canvas = (Spica.UI.StudyCanvas)Get("studyCanvas");
                    Check(cb.Items.Count == project.Studies.Count, $"{cb.Items.Count} studies listed");
                    foreach (var st in project.Studies)
                        Console.WriteLine($"      {st.Name}: {st.Blocks.Count} blocks, {st.Validate().Count} issues" + (st.Validate().Count > 0 ? " (" + string.Join("; ", st.Validate().Select(i => i.Message)) + ")" : ""));
                    for (int i = 0; i < cb.Items.Count; i++) { cb.SelectedIndex = i; Shot(form, "study" + i); }
                    // a block's properties
                    cb.SelectedIndex = 0;
                    var sw = project.Studies[0].Blocks.First(b => b.Kind == "SupplyWaveform");
                    canvas.Select(sw);
                    Shot(form, "props");
                    // a wire with the mouse: a new Nominal block's output onto the Simulate input of a blank study
                    project.Studies.Add(new Spica.Core.Graph.Study { Name = "Mouse" });
                    Call("ShowStudies");
                    cb.SelectedIndex = cb.Items.Count - 1;
                    var nom = canvas.AddBlock("Nominal", new System.Drawing.Point(40, 60));
                    var sim = canvas.AddBlock("Simulate", new System.Drawing.Point(420, 60));
                    Pump();
                    var cv = typeof(Spica.UI.StudyCanvas);
                    var pp = cv.GetMethod("PortPoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    var toScreen = cv.GetMethod("ToScreen", flags);
                    System.Drawing.Point Scr(Spica.Core.Graph.Block b, bool input, int i) => (System.Drawing.Point)toScreen.Invoke(canvas, new object[] { pp.Invoke(null, new object[] { b, input, i }) });
                    var a = Scr(nom, false, 0); var z = Scr(sim, true, 1);
                    void Mouse(string m, System.Windows.Forms.MouseButtons btn, System.Drawing.Point p) =>
                        cv.GetMethod(m, flags).Invoke(canvas, new object[] { new System.Windows.Forms.MouseEventArgs(btn, 1, p.X, p.Y, 0) });
                    Mouse("OnMouseDown", System.Windows.Forms.MouseButtons.Left, a);
                    Mouse("OnMouseMove", System.Windows.Forms.MouseButtons.Left, new System.Drawing.Point((a.X + z.X) / 2, a.Y + 30));
                    Mouse("OnMouseMove", System.Windows.Forms.MouseButtons.Left, z);
                    Mouse("OnMouseUp", System.Windows.Forms.MouseButtons.Left, z);
                    var mouseStudy = project.Studies.Last();
                    Check(mouseStudy.Wires.Count == 1 && mouseStudy.Wires[0].ToPort == "conditions", "a wire drawn with the mouse joins Conditions to Simulate: " + string.Join("; ", mouseStudy.Wires));
                    // a wrong one is refused: Simulate's Data output onto its own Circuit input
                    var d = Scr(sim, false, 0); var c = Scr(sim, true, 0);
                    Mouse("OnMouseDown", System.Windows.Forms.MouseButtons.Left, d);
                    Mouse("OnMouseMove", System.Windows.Forms.MouseButtons.Left, c);
                    Mouse("OnMouseUp", System.Windows.Forms.MouseButtons.Left, c);
                    Check(mouseStudy.Wires.Count == 1, "a Data wire into a Circuit input is refused");
                    canvas.Select(nom);
                    canvas.DeleteSelection();
                    Check(mouseStudy.Blocks.Count == 1 && mouseStudy.Wires.Count == 0, "removing a block removes its wires");
                    // the setup keeps the studies
                    string tmp = Path.Combine(Path.GetTempPath(), "spica_studies_ui.spica");
                    project.Save(tmp);
                    var back = SpicaProject.Load(tmp);
                    Check(back.Studies.Count == project.Studies.Count, $"saved and loaded back: {back.Studies.Count} studies");
                    var nfType = typeof(Spica.UI.MainForm).Assembly.GetType("Spica.UI.NewStudyForm");
                    using (var nf = (System.Windows.Forms.Form)nfType.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0].Invoke(new object[] { false }))
                    {
                        nf.Show(form);
                        Shot(nf, "new");
                        nf.Close();
                    }
                    project.Dirty = false;
                    form.Close();
                };
                form.Shown += (s, e) => start.Start();
                System.Windows.Forms.Application.Run(form);
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            Console.WriteLine(bad == 0 ? "STUDIES: all ok" : $"STUDIES: {bad} wrong");
            return bad;
        }
        if (args.Length > 0 && args[0] == "--graph")
        {
            // the study graphs: typed wires, loops, single inputs, validation, run order, the runner, the file format, older setups
            int bad = 0;
            void Check(bool ok, string what) { if (!ok) bad++; Console.WriteLine($"{(ok ? "ok   " : "WRONG")} {what}"); }
            var st = new Spica.Core.Graph.Study { Name = "Hold-up" };
            var sw = st.Add("SupplyWaveform").Set("Library", "do160g_s16_28vdc").Set("Net", "in").Set("Tests", "Power interruption, single, 10 ms");
            var sch = st.Add("Schematic").Set("File", "holdup.asc");
            var nom = st.Add("Nominal");
            var sim = st.Add("Simulate");
            var lim = st.Add("Limits").Set("Criteria", "holdup").Add("Note", "multi\nline = text");
            var rep = st.Add("Report");
            Check(st.Connect(sw, sch) == null && st.Connect(sch, sim) == null && st.Connect(nom, sim) == null &&
                  st.Connect(sim, lim) == null && st.Connect(lim, rep) == null, "wires of matching types join");
            Check(st.Validate().Count == 0, "a complete study validates: " + string.Join("; ", st.Validate()));
            Check(st.Connect(nom, sch) != null, "Conditions cannot go into a Schematic: " + st.Connect(nom, sch));
            Check(st.Connect(lim, sim) != null, "a Verdicts output finds no input on Simulate");
            var nom2 = st.Add("Nominal");
            Check(st.Connect(nom2, sim) == null && st.Wires.Count(w => w.To == sim.Id && w.ToPort == "conditions") == 1, "a second wire into a single input replaces the first");
            Check(st.Connect(sim, sch, "data", "stimulus") != null, "a wire of the wrong type is refused even when the ports are named");
            var c1 = st.Add("Combine"); var c2 = st.Add("Combine");
            Check(st.Connect(c1, c2) == null && st.Connect(c2, c1) != null, "a loop is refused: " + st.Connect(c2, c1));
            var issues = st.Validate();
            Check(issues.Any(i => i.Block == c1.Id && i.Message.Contains("not wired")), "an empty input is reported: " + string.Join("; ", issues));
            st.Remove(c1); st.Remove(c2); st.Remove(nom);
            var order = st.Order().Select(b => b.Kind).ToList();
            Check(order.IndexOf("SupplyWaveform") < order.IndexOf("Schematic") && order.IndexOf("Schematic") < order.IndexOf("Simulate") &&
                  order.IndexOf("Simulate") < order.IndexOf("Limits") && order.IndexOf("Limits") < order.IndexOf("Report"), "run order follows the wires: " + string.Join(" > ", order));

            // the runner: values flow along the wires; a kind without an executor is skipped, and so is what it feeds
            var ran = new System.Collections.Generic.List<string>();
            var runner = new Spica.Core.Graph.StudyRunner();
            foreach (var k in new[] { "SupplyWaveform", "Schematic", "Nominal", "Simulate" })
                runner.Register(k, new FakeExec(k, ran));
            bool graphDone = runner.Run(st);
            Check(!graphDone && string.Join(",", ran) == "SupplyWaveform,Schematic,Nominal,Simulate", "the runner runs in order: " + string.Join(",", ran));
            Check(runner.Results[sim.Id]["data"] as string == "Simulate(circuit:Schematic(stimulus:SupplyWaveform) conditions:Nominal)", "inputs reach each block: " + runner.Results[sim.Id]["data"]);
            Check(runner.State[lim.Id] == Spica.Core.Graph.BlockState.Skipped && runner.State[rep.Id] == Spica.Core.Graph.BlockState.Skipped, "no executor: skipped, and what follows too");

            // the file format: a project with a study saves and loads back the same; an older one keeps its format
            string dir = Path.Combine(Path.GetTempPath(), "spica_graph_test");
            Directory.CreateDirectory(dir);
            var p = new SpicaProject { Schematic = Path.Combine(dir, "holdup.asc") };
            p.Studies.Add(st);
            string f1 = Path.Combine(dir, "a.spica"), f2 = Path.Combine(dir, "b.spica"), f3 = Path.Combine(dir, "c.spica");
            p.Save(f1);
            var back = SpicaProject.Load(f1);
            back.Save(f2);
            Check(File.ReadAllText(f1) == File.ReadAllText(f2), "a study saves and loads back unchanged");
            var bs = back.Studies.FirstOrDefault();
            Check(bs != null && bs.Blocks.Count == st.Blocks.Count && bs.Wires.Count == st.Wires.Count && bs.Validate().Count == 0, "the loaded study is complete and valid");
            Check(bs != null && bs[lim.Id].Get("Note") == "multi\r\nline = text", "a property with a line break and '=' survives");
            Check(back.Schematic == p.Schematic && back.Runs == p.Runs, "the rest of the setup is read as before");
            var old = new SpicaProject { Schematic = p.Schematic };
            old.Save(f3);
            Check(!File.ReadAllText(f3).Contains("[Study"), "a setup without studies is written as before");

            // an older setup stands for its studies
            old.Qual["do160g_s16_28vdc"] = new QualStdChoice { Category = "A", Net = "in", Tests = { "Power interruption, single, 10 ms" } };
            old.QualCriteria.Add(new QualCriterion { Name = "holdup", Signal = "V(bulk)", Lo = 18 });
            old.Errors.Add(new ErrorItem { Name = "gain_err", Y = "err" });
            old.Temp = TempMode.Corners;
            var legacy = old.StudiesOrLegacy();
            Check(legacy.Count == 2 && legacy.All(x => x.Validate().Count == 0), "an older setup gives a tolerance and a qualification study, both valid: " +
                  string.Join("; ", legacy.SelectMany(x => x.Validate())));
            Check(legacy[0].Blocks.Any(b => b.Kind == "ErrorBudget" && b.Get("Errors") == "gain_err") && legacy[0].Blocks.Any(b => b.Kind == "Corners"),
                  "the tolerance study has the error budget and the temperature corners");
            Check(legacy[1].Blocks.Any(b => b.Kind == "SupplyWaveform" && b.Get("Net") == "in") && legacy[1].Blocks.Any(b => b.Kind == "Limits" && b.Get("Criteria") == "holdup"),
                  "the qualification study has the waveform into its net and the criteria");
            Check(legacy[1].Blocks.First(b => b.Kind == "SupplyWaveform").X < legacy[1].Blocks.First(b => b.Kind == "Schematic").X, "the stimulus is laid out left of the schematic");
            Console.WriteLine(bad == 0 ? "GRAPH: all ok" : $"GRAPH: {bad} wrong");
            return bad;
        }
        if (args.Length > 0 && args[0] == "--qual")
        {
            // the default DO-160G library: every test of every category gives a waveform; the step language's checks
            var lib = QualLibrary.Parse(QualLibrary.Do160G28Vdc);
            int bad = lib.Problems.Count;
            foreach (var pr in lib.Problems) Console.WriteLine("PROBLEM " + pr);
            foreach (var cat in lib.CategoryOrder)
            {
                var vars = lib.Variables(cat);
                foreach (var t in lib.TestsFor(cat))
                {
                    var w = QualLibrary.Waveform(t, vars, out var err);
                    bool inc = w.Zip(w.Skip(1), (a, b) => b[0] > a[0]).All(x => x);
                    if (err != null || !inc) bad++;
                    Console.WriteLine($"{(err == null && inc ? "ok   " : "WRONG")} {cat} {t.Name}: {(err ?? $"{w.Count} points, {w[w.Count - 1][0]:0.###} s, {w.Min(p => p[1]):0.#} .. {w.Max(p => p[1]):0.#} V")}");
                }
            }
            var demo = new QualTest { Steps = "start 28V; hold 10ms; to 0 in 1m; hold 5m; repeat 2 ( jump 40; hold 1m; jump 28; hold 2m )" };
            var dw = QualLibrary.Waveform(demo, new System.Collections.Generic.Dictionary<string, double>(), out var derr);
            Console.WriteLine("demo: " + (derr ?? QualLibrary.PwlValue(dw)));
            foreach (var wrong in new[] { "hold -1", "start 28; jump", "repeat 2 ( hold 1m", "fly 3", "start 28; start 20; hold 1", "hold Vbogus" })
            {
                QualLibrary.Waveform(new QualTest { Steps = wrong }, new System.Collections.Generic.Dictionary<string, double> { ["Vnom"] = 28 }, out var e2);
                if (e2 == null) bad++;
                Console.WriteLine($"{(e2 != null ? "ok   " : "WRONG")} rejects '{wrong}': {e2}");
            }
            return bad;
        }
        if (args.Length > 1 && args[0] == "--critform")
        {
            // Qualification > Pass / Fail > Add: the criterion dialog, new and with a range
            var t = new Thread(() =>
            {
                var sig = new System.Collections.Generic.List<string> { "V(bulk)", "V(in)", "I(Rload)" };
                foreach (var (c, name) in new[] { ((QualCriterion)null, "new"), (new QualCriterion { Name = "recovered", Signal = "V(bulk)", Lo = 26, Hi = 28, From = QualAnchor.EventEnd, FromOffset = 0.05 }, "range") })
                    using (var f = new Spica.UI.QualCriterionForm(c, sig, new string[0]))
                    {
                        f.Show();
                        for (int k = 0; k < 10; k++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(30); }
                        using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                            bmp.Save(args[1].Replace(".png", "_" + name + ".png"));
                        }
                        f.Close();
                    }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return 0;
        }
        if (args.Length > 2 && args[0] == "--qualrun")
        {
            // Qualification end to end: <asc> <png> [mc]: the supply net, tests, criteria, run in the queue, the verdicts
            int bad = 0;
            var t = new Thread(() =>
            {
                var form = new Spica.UI.MainForm { StartupFile = Path.GetFullPath(args[1]), Width = 1280, Height = 860 };
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var mf = typeof(Spica.UI.MainForm);
                object Get(string f) => mf.GetField(f, flags).GetValue(form);
                void Call(string m) => mf.GetMethod(m, flags, null, Type.EmptyTypes, null).Invoke(form, null);
                void Shot(string name)
                {
                    for (int k = 0; k < 15; k++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(30); }
                    using (var bmp = new System.Drawing.Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                        bmp.Save(args[2].Replace(".png", "_" + name + ".png"));
                    }
                }
                var start = new System.Windows.Forms.Timer { Interval = 4000 };
                var poll = new System.Windows.Forms.Timer { Interval = 1000 };
                var began = DateTime.UtcNow;
                start.Tick += (s, e) =>
                {
                    start.Stop();
                    ((Spica.UI.Win98TabControl)Get("tabs")).SelectedIndex = 1;
                    var project = (SpicaProject)Get("project");
                    var ch = project.QualOf("do160g_s16_28vdc");
                    ch.Category = "A";
                    ch.Net = "in";
                    ch.Tests = new System.Collections.Generic.List<string> { "Power interruption, single, 10 ms", "Power interruption, single, 200 ms",
                                                                             "Power interruption, double, 50 ms + 50 ms", "Normal surge, overvoltage" };
                    project.QualCriteria = new System.Collections.Generic.List<QualCriterion>
                    {
                        new QualCriterion { Name = "holdup", Signal = "V(bulk)", Lo = 18, From = QualAnchor.TestStart, To = QualAnchor.TestEnd, Tests = "interruption" },
                        new QualCriterion { Name = "overvolt", Signal = "V(bulk)", Hi = 45, From = QualAnchor.TestStart, To = QualAnchor.TestEnd },
                        new QualCriterion { Name = "recovered", Signal = "V(bulk)", Lo = 26, Hi = 28, From = QualAnchor.EventEnd, FromOffset = 0.05, To = QualAnchor.TestEnd },
                    };
                    project.QualPlot = new System.Collections.Generic.List<string> { "I(D1)" };
                    project.QualNominal = args.Length < 4;
                    project.QualRuns = 20;
                    Call("ShowQualification");
                    Shot("setup");
                    Call("RunQualification");
                    var queue = (CampaignQueue)Get("queue");
                    Console.WriteLine($"queued {queue.Jobs.Count}: " + string.Join(" | ", queue.Jobs.Select(j => j.Name)));
                    if (queue.Jobs.Count != 4) bad++;
                    poll.Start();
                };
                poll.Tick += (s, e) =>
                {
                    var queue = (CampaignQueue)Get("queue");
                    if (queue.Jobs.Any(j => j.Pending) && (DateTime.UtcNow - began).TotalSeconds < 600) return;
                    poll.Stop();
                    foreach (var j in queue.Jobs) Console.WriteLine($"job {j.Name}: {j.Status} {j.Done}/{j.Total}  {j.OutputDir}");
                    ((Spica.UI.Win98TabControl)Get("tabs")).SelectedIndex = 1;
                    var qt = (Spica.UI.Win98TabControl)Get("qualTabs");
                    qt.SelectedIndex = qt.Pages.Count - 1;
                    Call("ShowQualVerdicts");
                    var lv = (System.Windows.Forms.ListView)Get("lvVerdicts");
                    foreach (System.Windows.Forms.ListViewItem it in lv.Items)
                        Console.WriteLine(string.Join(" | ", it.SubItems.Cast<System.Windows.Forms.ListViewItem.ListViewSubItem>().Select(x => x.Text)));
                    if (lv.Items.Count == 0 || lv.Items.Cast<System.Windows.Forms.ListViewItem>().Any(i => i.SubItems[2].Text != "PASS" && i.SubItems[2].Text != "FAIL")) bad++;
                    Shot("verdicts");
                    // the waveforms of each test: supply on top, the judged signal and the extra one below
                    var seen = new System.Collections.Generic.HashSet<object>();
                    int wi = 0;
                    foreach (System.Windows.Forms.ListViewItem it in lv.Items)
                    {
                        if (it.Tag == null || !seen.Add(it.Tag.GetType().GetField("Dir").GetValue(it.Tag) ?? "")) continue;
                        mf.GetMethod("ShowQualWaveforms", flags).Invoke(form, new[] { it.Tag });
                        var wf = (Spica.UI.QualWaveForm)Get("lastQualWave");
                        void WShot(string name)
                        {
                            for (int k = 0; k < 15; k++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(30); }
                            using (var bmp = new System.Drawing.Bitmap(wf.Width, wf.Height))
                            {
                                wf.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, wf.Width, wf.Height));
                                bmp.Save(args[2].Replace(".png", "_" + name + ".png"));
                            }
                        }
                        WShot("wave" + wi);
                        var info = (System.Windows.Forms.Label)typeof(Spica.UI.QualWaveForm).GetField("lblInfo", flags).GetValue(wf);
                        Console.WriteLine($"--- waveforms {it.Text}:\n{info.Text}");
                        if (info.Text.Contains("\n! ")) bad++;
                        var cbRun = (System.Windows.Forms.ComboBox)typeof(Spica.UI.QualWaveForm).GetField("cbRun", flags).GetValue(wf);
                        Console.WriteLine("runs: " + string.Join(" | ", cbRun.Items.Cast<object>().Take(4)));
                        if (cbRun.Items.Count > 2) { cbRun.SelectedIndex = 1; WShot("wave" + wi + "_all"); }
                        wf.Close();
                        wi++;
                    }
                    if (wi == 0) bad++;
                    qt.SelectedIndex = 0;
                    Shot("std");
                    ((SpicaProject)Get("project")).Dirty = false;
                    form.Close();
                };
                form.Shown += (s, e) => start.Start();
                System.Windows.Forms.Application.Run(form);
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return bad;
        }
        if (args.Length > 2 && args[0] == "--budgetcharts")
        {
            // the Error Budget page with each chart (histogram, error vs input, error vs cause), then the Histogram page
            var t = new Thread(() =>
            {
                var form = new Spica.UI.MainForm { StartupFile = Path.GetFullPath(args[1]), Width = 1280, Height = 860 };
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var timer = new System.Windows.Forms.Timer { Interval = 800 };
                void Shot(string name)
                {
                    for (int k = 0; k < 10; k++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(30); }
                    using (var bmp = new System.Drawing.Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                        bmp.Save(args[2].Replace(".png", "_" + name + ".png"));
                    }
                }
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    var view = (Spica.UI.ResultsView)typeof(Spica.UI.MainForm).GetField("resultsView", flags).GetValue(form);
                    view.SelectPage(0);
                    var cb = (System.Windows.Forms.ComboBox)typeof(Spica.UI.ResultsView).GetField("cbBudChart", flags).GetValue(view);
                    for (int i = 0; i < cb.Items.Count; i++) { cb.SelectedIndex = i; Shot("bud" + i); }
                    view.SelectPage(3);
                    Shot("hist");
                    // several sigma lines, on a measurement given as a 4th argument
                    var meas = (System.Windows.Forms.ComboBox)typeof(Spica.UI.ResultsView).GetField("cbHistMeas", flags).GetValue(view);
                    var sig = (System.Windows.Forms.ComboBox)typeof(Spica.UI.ResultsView).GetField("cbHistSigma", flags).GetValue(view);
                    if (args.Length > 3) meas.SelectedItem = args[3];
                    sig.Text = "3, 4.5, 6";
                    typeof(Spica.UI.ResultsView).GetMethod("RefreshHist", flags).Invoke(view, null);
                    Shot("hist_sigmas");
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

    static int QueueTest(string[] args)
    {
        var t = new Thread(() =>
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var mf = typeof(Spica.UI.MainForm);
            string setup = Path.GetFullPath(args[1]), png = Path.GetFullPath(args[2]);
            string watch = Path.Combine(Path.GetDirectoryName(png), "watch"), results = Path.Combine(Path.GetDirectoryName(png), "watch_results");
            if (Directory.Exists(watch)) Directory.Delete(watch, true);
            if (Directory.Exists(results)) Directory.Delete(results, true);
            Directory.CreateDirectory(watch);
            var form = new Spica.UI.MainForm { StartupFile = setup };
            object Get(string field) => mf.GetField(field, flags).GetValue(form);
            void Snap(System.Windows.Forms.Form f, string name)
            {
                for (int k = 0; k < 10; k++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(30); }
                using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(png.Replace(".png", "_" + name + ".png"));
                }
            }
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            int ticks = 0, stage = 0, boxes = 0;
            bool busy = false;
            Spica.UI.ListenerForm lf = null;
            timer.Tick += (s, e) =>
            {
                if (busy) return;
                busy = true;
                try { Step(); } catch (Exception ex) { Console.WriteLine("FAILED: " + ex); timer.Stop(); form.Close(); } finally { busy = false; }
            };
            void Step()
            {
                var queue = (CampaignQueue)Get("queue");
                var listener = (Listener)Get("listener");
                var tabs = (Spica.UI.Win98TabControl)Get("tabs");
                if (stage == 0)
                {
                    if (Get("netlist") == null && ++ticks < 150) return;
                    var add = mf.GetMethod("AddToQueue", flags, null, Type.EmptyTypes, null);
                    add.Invoke(form, null);
                    add.Invoke(form, null);
                    // through the listener: a bundle as File > Send to a Listener Folder writes it, and a setup beside its schematic
                    var a = new object[] { null, null, null, null, 0.0 };
                    if (!(bool)mf.GetMethod("PrepareRequest", flags).Invoke(form, a)) throw new Exception("PrepareRequest refused");
                    var warnings = new System.Collections.Generic.List<string>();
                    Console.WriteLine("bundle: " + SourceBundle.Write(Path.Combine(watch, "bundle_job.zip"), (CampaignOptions)a[1], (HardwareInfo)Get("hw"), DateTime.Now, warnings).Replace("\n", " | "));
                    foreach (var w in warnings) Console.WriteLine("  warning: " + w);
                    string asc = (string)Get("ascPath");
                    File.Copy(asc, Path.Combine(watch, Path.GetFileName(asc)));
                    File.Copy(setup, Path.Combine(watch, "setup_job.spica"));
                    listener.Start(watch, results);
                    lf = new Spica.UI.ListenerForm(listener, (AppSettings)Get("settings"), en => { });
                    lf.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                    lf.Location = new System.Drawing.Point(form.Left + 60, form.Top + 60);
                    lf.Show(form);
                    tabs.SelectedIndex = 2;
                    stage = 1; ticks = 0;
                    return;
                }
                ticks++;
                if (stage == 1 && ticks == 28) { Snap(form, "queue_running"); Snap(lf, "listener_running"); }
                // every finish box is answered No (stay on the Queue tab)
                foreach (var f in System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().Where(f => f != form && f != lf).ToList())
                {
                    Snap(f, "box" + boxes++);
                    Console.WriteLine("box: " + f.Text);
                    f.DialogResult = System.Windows.Forms.DialogResult.No;
                }
                bool idle = queue.Running == null && queue.QueuedCount == 0 && listener.PendingCount == 0;
                if (ticks > 28 && queue.Jobs.Count >= 4 && idle) stage++; else if (stage > 1) stage = 1;
                if (stage < 6 && ticks < 2400) return;     // idle for a second, so that the last box had its turn
                timer.Stop();
                Snap(form, "queue_done"); Snap(lf, "listener_done");
                foreach (var j in queue.Jobs) Console.WriteLine($"job {j.Id}: {j.Name} | {j.Source} | {j.Status} | {j.Done}/{j.Total} ok {j.Ok} | {(int)j.Elapsed.TotalSeconds} s | {j.OutputDir} | {j.Message}");
                foreach (var en in listener.Entries) Console.WriteLine($"entry: {en.File} | {en.Status} | {listener.Describe(en)} | {en.Results} | {en.Message}");
                listener.Stop();
                Thread.Sleep(500);
                string st = Path.Combine(watch, Listener.StatusFile);
                Console.WriteLine("--- " + Listener.StatusFile + (File.Exists(st) ? "\n" + File.ReadAllText(st) : ": missing"));
                Console.WriteLine("--- watch: " + string.Join(", ", Directory.GetFileSystemEntries(watch).Select(Path.GetFileName)));
                Console.WriteLine("--- results: " + string.Join(", ", Directory.GetFileSystemEntries(results).Select(Path.GetFileName)));
                string sf = Path.Combine(watch, Listener.StateFile);
                Console.WriteLine("--- " + Listener.StateFile + (File.Exists(sf) ? "\n" + File.ReadAllText(sf) : ": missing"));
                foreach (var d in Directory.Exists(Path.Combine(results, "_received")) ? Directory.GetDirectories(Path.Combine(results, "_received")) : new string[0])
                    foreach (var asc in Directory.GetFiles(d, "*.asc"))
                        Console.WriteLine("shipped netlist for " + Path.GetFileName(d) + ": " + (SourceBundle.ShippedNetlist(asc) != null ? "used" : "NOT usable"));
                lf.Close();
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

    static int RemoteTest(string[] args)
    {
        var t = new Thread(() =>
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var mf = typeof(Spica.UI.MainForm);
            string setup = Path.GetFullPath(args[1]), png = Path.GetFullPath(args[2]);
            string share = Path.Combine(Path.GetDirectoryName(png), "share");
            if (Directory.Exists(share)) Directory.Delete(share, true);
            Directory.CreateDirectory(share);
            string remoteFile = KeyValueFile.Resolve("SPICA.remote.tsv");
            if (File.Exists(remoteFile)) File.Delete(remoteFile);
            var form = new Spica.UI.MainForm { StartupFile = setup };
            object Get(string field) => mf.GetField(field, flags).GetValue(form);
            void Snap(System.Windows.Forms.Form f, string name)
            {
                for (int k = 0; k < 10; k++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(30); }
                using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(png.Replace(".png", "_" + name + ".png"));
                }
            }
            // the "workstation": its own queue and listener, watching the share, results inside it
            Action<Action> post = a => { try { form.BeginInvoke(a); } catch { } };
            var wsQueue = new CampaignQueue((HardwareInfo)Get("hw"), new ProfileStore(), post);
            var ws = new Listener(wsQueue, post) { LtspiceExe = () => ((AppSettings)Get("settings")).LtspicePath };
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            int ticks = 0, stage = 0;
            bool busy = false;
            timer.Tick += (s, e) =>
            {
                if (busy) return;
                busy = true;
                try { Step(); } catch (Exception ex) { Console.WriteLine("FAILED: " + ex); timer.Stop(); form.Close(); } finally { busy = false; }
            };
            void Step()
            {
                ws.Tick();
                var queue = (CampaignQueue)Get("queue");
                var remote = (RemoteTracker)Get("remote");
                var tabs = (Spica.UI.Win98TabControl)Get("tabs");
                foreach (var f in System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().Where(f => f != form).ToList())
                {
                    Snap(f, "box" + ticks);
                    Console.WriteLine("box: " + f.Text);
                    f.DialogResult = System.Windows.Forms.DialogResult.No;
                }
                ticks++;
                if (stage == 0)
                {
                    if (Get("netlist") == null && ticks < 150) return;
                    ws.Start(share, Listener.SuggestOutput(share));
                    stage = 1; ticks = 0;
                    return;
                }
                if (stage == 1)
                {
                    if (ticks < 8) return;          // the listener has written its state
                    ((AppSettings)Get("settings")).SendFolder = share;
                    mf.GetMethod("RunRemote", flags).Invoke(form, null);
                    mf.GetMethod("AddToQueue", flags, null, Type.EmptyTypes, null).Invoke(form, null);
                    tabs.SelectedIndex = 2;
                    stage = 2; ticks = 0;
                    return;
                }
                if (stage == 2)
                {
                    var r = remote.Jobs.FirstOrDefault();
                    if (r != null && r.Status == RemoteStatus.Running && r.Done > 0 && queue.Running != null && !File.Exists(png.Replace(".png", "_both.png")))
                    {
                        Snap(form, "both");
                        Console.WriteLine($"while both run: remote {r.Status} {r.Done}/{r.Total} on {r.Machine}, local {queue.Running.Done}/{queue.Running.Total}");
                    }
                    bool idle = queue.Running == null && queue.QueuedCount == 0 && r != null && !r.Pending;
                    if (!idle && ticks < 2400) return;
                    Snap(form, "done");
                    foreach (var j in queue.Jobs) Console.WriteLine($"local {j.Name} | {j.Status} | {j.Done}/{j.Total}");
                    foreach (var x in remote.Jobs) Console.WriteLine($"remote {x.Name} | {x.Status} | {x.Done}/{x.Total} ok {x.Ok} | {x.Machine} | here: {x.ResultsHere} | csv: {File.Exists(Path.Combine(x.ResultsHere ?? "", "results.csv"))} | {x.Message} {x.Note}");
                    // the listener lock: this PC starts listening, Open and Run go off
                    ws.Stop();
                    var listener = (Listener)Get("listener");
                    listener.Start(share, Listener.SuggestOutput(share));
                    stage = 3; ticks = 0;
                    return;
                }
                if (stage == 3 && ticks > 8)
                {
                    tabs.SelectedIndex = 0;
                    Snap(form, "slave");
                    foreach (var n in new[] { "tbOpen", "tbAdd", "tbRemote", "miAdd", "miRemote" })
                    {
                        var item = (System.Windows.Forms.ToolStripItem)Get(n);
                        Console.WriteLine($"slave: {n} enabled={item.Enabled}");
                    }
                    ((Listener)Get("listener")).Stop();
                    timer.Stop();
                    form.Close();
                }
            }
            form.Shown += (s, e) => timer.Start();
            System.Windows.Forms.Application.Run(form);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return 0;
    }

    static int QueueOps(string[] args)
    {
        int bad = 0;
        var t = new Thread(() =>
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var mf = typeof(Spica.UI.MainForm);
            string png = Path.GetFullPath(args[2]);
            var form = new Spica.UI.MainForm { StartupFile = Path.GetFullPath(args[1]) };
            object Get(string field) => mf.GetField(field, flags).GetValue(form);
            void Check(bool ok, string what) { Console.WriteLine((ok ? "ok    " : "WRONG ") + what); if (!ok) bad++; }
            void Snap(System.Windows.Forms.Form f, string name)
            {
                for (int k = 0; k < 10; k++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(30); }
                using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(png.Replace(".png", "_" + name + ".png"));
                }
            }
            string Order(CampaignQueue q) => string.Join(",", q.Jobs.Select(j => j.Id + ":" + j.Status));
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            int ticks = 0, stage = 0, boxes = 0;
            bool busy = false;
            QueueJob discarded = null, stopped = null;
            string discardedDir = null;
            timer.Tick += (s, e) =>
            {
                if (busy) return;
                busy = true;
                try { Step(); } catch (Exception ex) { Console.WriteLine("FAILED: " + ex); bad++; timer.Stop(); form.Close(); } finally { busy = false; }
            };
            void Step()
            {
                var queue = (CampaignQueue)Get("queue");
                var tabs = (Spica.UI.Win98TabControl)Get("tabs");
                ticks++;
                foreach (var f in System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().Where(f => f != form).ToList())
                {
                    Snap(f, "ops_box" + boxes++);
                    f.DialogResult = System.Windows.Forms.DialogResult.No;
                }
                switch (stage)
                {
                    case 0:
                        if (Get("netlist") == null && ticks < 150) return;
                        queue.Hold = true;
                        var add = mf.GetMethod("AddToQueue", flags, null, Type.EmptyTypes, null);
                        for (int i = 0; i < 3; i++) add.Invoke(form, null);
                        Check(queue.Jobs.Count == 3 && queue.Running == null && queue.QueuedCount == 3, "held: three wait, none runs  [" + Order(queue) + "]");
                        Check(queue.Move(queue.Jobs[2], -1) && queue.Jobs[1].Id == 3, "move up: 3 before 2  [" + Order(queue) + "]");
                        Check(!queue.Move(queue.Jobs[0], -1), "the first cannot move up");
                        Check(queue.Remove(queue.Jobs[0]) && queue.Jobs.Count == 2 && queue.Jobs[0].Id == 3, "remove 1  [" + Order(queue) + "]");
                        tabs.SelectedIndex = 2;
                        Snap(form, "ops_held");
                        queue.Hold = false;
                        Check(queue.Running != null && queue.Running.Id == 3, "released: 3 runs first  [" + Order(queue) + "]");
                        Check(!queue.Remove(queue.Running), "the running one cannot be removed");
                        stage = 1; ticks = 0;
                        return;
                    case 1:     // discard the running campaign
                        if (ticks < 24) return;
                        discarded = queue.Running;
                        discardedDir = discarded.Engine.Options.OutputDir;
                        Check(Directory.Exists(discardedDir), "the campaign folder exists while it runs");
                        discarded.DiscardOnFinish = true;
                        discarded.Engine.Cancel();
                        stage = 2; ticks = 0;
                        return;
                    case 2:     // the next one starts by itself; stop it and keep what it has
                        if (queue.Running == null || queue.Running == discarded) { if (ticks > 240) throw new Exception("the next campaign did not start"); return; }
                        if (queue.Running.Engine == null || queue.Running.Engine.Snapshot().Done < 60) return;
                        Check(queue.Running.Id == 2, "after the discard, 2 runs  [" + Order(queue) + "]");
                        Check(!Directory.Exists(discardedDir), "the discarded folder is gone");
                        Check(!queue.Jobs.Contains(discarded), "the discarded campaign left the list");
                        stopped = queue.Running;
                        stopped.AnalyzeOnFinish = true;
                        stopped.Engine.Cancel();
                        stage = 3; ticks = 0;
                        return;
                    case 3:
                        if (queue.Running != null || queue.QueuedCount > 0) { if (ticks > 240) throw new Exception("the stopped campaign did not end"); return; }
                        if (ticks < 8) return;
                        Check(stopped.Status == JobStatus.Stopped && stopped.Done > 0 && stopped.Done < stopped.Total, $"stopped with {stopped.Done} of {stopped.Total} runs  [" + Order(queue) + "]");
                        var view = (Spica.UI.ResultsView)Get("resultsView");
                        Check(tabs.SelectedIndex == 3 && view.Data != null, "its results are open (" + (view.Data?.Rows.Count ?? 0) + " rows)");
                        tabs.SelectedIndex = 2;
                        Snap(form, "ops_done");
                        queue.ClearEnded();
                        Check(queue.Jobs.Count == 0, "clear ended empties the list");
                        Snap(form, "ops_empty");
                        timer.Stop();
                        form.Close();
                        return;
                }
            }
            form.Shown += (s, e) => timer.Start();
            System.Windows.Forms.Application.Run(form);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Console.WriteLine(bad == 0 ? "QUEUE OPS: all ok" : $"QUEUE OPS: {bad} wrong");
        return bad;
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
                    typeof(Spica.UI.MainForm).GetMethod("AddToQueue", flags, null, Type.EmptyTypes, null).Invoke(form, null);
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
                // the Temperature page showing the drift from the reference
                var show = (System.Windows.Forms.ComboBox)typeof(Spica.UI.ResultsView).GetField("cbTempShow", flags).GetValue(view);
                for (int i = 0; i < view.PageCount; i++)
                {
                    view.SelectPage(i);
                    if (show.Parent != null && show.Parent.Visible && show.Enabled) { show.SelectedIndex = 1; Snap(args[2].Replace(".png", "_drift.png")); show.SelectedIndex = 0; break; }
                }
                // the dialogs, shown modeless so that they can be drawn and closed from here
                void Dialog(string name, Func<System.Windows.Forms.Form> make)
                {
                    try
                    {
                        using (var f = make())
                        {
                            f.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                            f.Location = new System.Drawing.Point(form.Left + 40, form.Top + 40);
                            f.Show(form);
                            for (int k = 0; k < 10; k++) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(30); }
                            using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                            {
                                f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                                bmp.Save(args[2].Replace(".png", $"_dlg_{name}.png"));
                            }
                            f.Close();
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"dialog {name}: {ex.GetType().Name}: {ex.Message}"); }
                }
                object Get(string field) => typeof(Spica.UI.MainForm).GetField(field, flags).GetValue(form);
                Dialog("options", () => new Spica.UI.OptionsForm((AppSettings)Get("settings"), (HardwareInfo)Get("hw")));
                Dialog("listener", () => new Spica.UI.ListenerForm((Listener)Get("listener"), (AppSettings)Get("settings"), en => { }));
                Dialog("report", () => new Spica.UI.ReportForm(view.Data));
                Dialog("inputs", () => new Spica.UI.InputsForm(view.Data));
                Dialog("opamps", () => new Spica.UI.OpAmpForm(Path.GetDirectoryName(Path.GetFullPath(args[1]))));
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

/// <summary>--graph: an executor that records its run and outputs "Kind(port:inputs ...)" on every output port.</summary>
sealed class FakeExec : Spica.Core.Graph.IBlockExecutor
{
    readonly string kind;
    readonly System.Collections.Generic.List<string> ran;
    public FakeExec(string kind, System.Collections.Generic.List<string> ran) { this.kind = kind; this.ran = ran; }
    public void Run(Spica.Core.Graph.BlockContext ctx)
    {
        ran.Add(kind);
        var ins = ctx.Inputs.Select(kv => $"{kv.Key}:{string.Join("+", kv.Value)}").ToList();
        string v = kind + (ins.Count > 0 ? "(" + string.Join(" ", ins) + ")" : "");
        foreach (var o in ctx.Block.Spec.Outputs) ctx.Out(o.Name, v);
    }
}
