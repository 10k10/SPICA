using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Spica.Core;

namespace Spica.UI
{
    /// <summary>
    /// The Qualification tab: one sub-tab per standard (a library file of test waveforms by equipment category), where
    /// the tests are ticked and the net the waveform goes into is chosen; a Pass / Fail sub-tab with the criteria and
    /// the verdicts; at the bottom, nominal or Monte Carlo and the button that queues one campaign per ticked test.
    /// </summary>
    public partial class MainForm
    {
        /// <summary>The controls of one standard's sub-tab.</summary>
        sealed class QualPage
        {
            public QualLibrary Lib;
            public ComboBox Category, Net;
            public CheckedListBox Tests;
            public Win98Chart Chart;
            public RichTextBox Info;
            public Win98TextField Rser;
            public Label Count, Replaces;
            public string CategoryName => Category.SelectedItem as string ?? "";
            public QualTest Selected => Tests.SelectedItem is string n ? Lib.Tests.FirstOrDefault(t => t.Name == n) : null;
        }

        Win98TabPage pageQual;
        Win98TabControl qualTabs;
        readonly List<QualPage> qualPages = new List<QualPage>();
        Win98ListView lvCriteria, lvVerdicts;
        Win98RadioButton rbQualNominal, rbQualMc;
        Win98TextField txtQualRuns, txtQualPlot;

        /// <summary>What a verdict row points to: the test's newest campaign and the test itself.</summary>
        sealed class QualRow
        {
            public string Dir;
            public QualPage Page;
            public QualTest Test;
        }
        Label lblQualPlan;
        bool fillingQual;
        ToolTip qualTip;

        void BuildQualificationPage()
        {
            var p = pageQual;
            qualTip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 400 };

            // --- bottom: how the ticked tests run ------------------------------------------------
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = Win98.Face };
            bar.Paint += (s, e) => Win98.EtchedHLine(e.Graphics, 0, bar.Width, 4);
            rbQualNominal = new Win98RadioButton { Text = "&Nominal (one unit, tolerances at their nominal values)", Location = new Point(2, 13), AutoSize = true };
            rbQualMc = new Win98RadioButton { Text = "&Monte Carlo:", Location = new Point(330, 13), AutoSize = true };
            txtQualRuns = new Win98TextField { Location = new Point(424, 12), Width = 60 };
            var lblRuns = new Label { Text = "runs per test", Location = new Point(490, 15), AutoSize = true, BackColor = Win98.Face };
            txtQualRuns.Box.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            rbQualNominal.CheckedChanged += (s, e) => { if (fillingQual) return; project.QualNominal = rbQualNominal.Checked; txtQualRuns.Enabled = !project.QualNominal; MarkDirty(); UpdateQualPlan(); };
            txtQualRuns.Box.TextChanged += (s, e) =>
            {
                if (fillingQual) return;
                if (int.TryParse(txtQualRuns.Text, out var n) && n > 0) { project.QualRuns = n; MarkDirty(); UpdateQualPlan(); }
            };
            lblQualPlan = new Label { Location = new Point(580, 15), AutoSize = false, Height = 15, ForeColor = Win98.Shadow, BackColor = Win98.Face, AutoEllipsis = true, UseMnemonic = false };
            var btnRun = new Win98Button { Text = "&Run the Ticked Tests", Width = 140 };
            btnRun.Click += (s, e) => RunQualification();
            qualTip.SetToolTip(btnRun, "One campaign per ticked test in the queue, on this PC, at the temperatures of the Setup tab");
            bar.Controls.AddRange(new Control[] { rbQualNominal, rbQualMc, txtQualRuns, lblRuns, lblQualPlan, btnRun });
            bar.Resize += (s, e) =>
            {
                btnRun.Location = new Point(bar.Width - btnRun.Width, 10);
                lblQualPlan.Width = Math.Max(10, btnRun.Left - 8 - lblQualPlan.Left);
            };

            qualTabs = new Win98TabControl { Dock = DockStyle.Fill };
            qualTabs.SelectedIndexChanged += (s, e) => { if (qualTabs.SelectedIndex == qualTabs.Pages.Count - 1) ShowQualVerdicts(); };
            p.Controls.Add(qualTabs);
            p.Controls.Add(bar);

            LoadQualLibraries();
        }

        // ===================================================================================== standards

        void LoadQualLibraries()
        {
            int keep = qualTabs.SelectedIndex;
            var libs = QualLibrary.LoadAll();
            var problems = libs.Where(l => l.Problems.Count > 0).Select(l => Path.GetFileName(l.Path) + ":\n  " + string.Join("\n  ", l.Problems.Take(8))).ToList();
            qualTabs.ClearPages();
            qualPages.Clear();
            foreach (var lib in libs) qualTabs.AddPage(BuildStdPage(lib));
            qualTabs.AddPage(BuildPassFailPage());
            qualTabs.SelectedIndex = Math.Min(Math.Max(0, keep), qualTabs.Pages.Count - 1);
            if (problems.Count > 0)
                Win98MessageBox.Show(this, "The qualification library has problems:\n\n" + string.Join("\n\n", problems), "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
            ShowQualification();
        }

        Win98TabPage BuildStdPage(QualLibrary lib)
        {
            var page = new Win98TabPage(lib.Tab, Win98.Icon("check16"));
            var q = new QualPage { Lib = lib };
            qualPages.Add(q);

            // the standard, the category and where the waveform goes
            var grpStd = new Win98GroupBox { Text = "Standard", Dock = DockStyle.Top, Height = 100 };
            var lblStd = new Label { Text = lib.Standard.Length > 0 ? lib.Standard : "(no Standard line in the library)", Location = new Point(84, 21), AutoSize = true,
                                     BackColor = Win98.Face, Font = Win98.BoldFont, UseMnemonic = false };
            q.Category = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(84, 44), Width = 60, Font = Win98.Font };
            foreach (var c in lib.CategoryOrder) q.Category.Items.Add(c);
            q.Category.SelectedIndexChanged += (s, e) =>
            {
                if (fillingQual) return;
                project.QualOf(lib.Key).Category = q.CategoryName;
                MarkDirty();
                FillQualTests(q);
            };
            var warn = new Label
            {
                Text = "Starting values, not checked against the standard: review every level and time in the library before relying on a result.",
                Location = new Point(156, 47), AutoSize = true, ForeColor = Color.FromArgb(128, 0, 0), BackColor = Win98.Face, UseMnemonic = false,
            };
            q.Net = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(84, 70), Width = 160, Font = Win98.Font };
            void NetChanged()
            {
                if (fillingQual) return;
                project.QualOf(lib.Key).Net = q.Net.Text.Trim();
                MarkDirty();
                ShowQualReplaces(q);
            }
            q.Net.SelectedIndexChanged += (s, e) => NetChanged();
            q.Net.TextChanged += (s, e) => NetChanged();
            qualTip.SetToolTip(q.Net, "The net the supply waveform is applied to (against ground); a voltage source already there is taken out");
            q.Rser = new Win98TextField { Location = new Point(360, 70), Width = 60 };
            q.Rser.Box.TextChanged += (s, e) =>
            {
                if (fillingQual) return;
                var v = ParseEng(q.Rser.Text);
                project.QualOf(lib.Key).Rser = double.IsNaN(v) || v < 0 ? 0 : v;
                MarkDirty();
            };
            qualTip.SetToolTip(q.Rser, "Source resistance in ohms (0 = ideal source); SPICE suffixes work: 50m, 1");
            q.Replaces = new Label { Location = new Point(436, 73), AutoSize = true, ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false };
            var btnEdit = new Win98Button { Text = "&Edit Library...", Width = 100 };
            btnEdit.Click += (s, e) => EditQualLibrary(lib);
            qualTip.SetToolTip(btnEdit, "Open the library file (levels, times, categories, tests) in Notepad; SPICA reads it again when you come back");
            var btnFolder = new Win98Button { Text = "Libraries...", Width = 90 };
            btnFolder.Click += (s, e) => { try { Process.Start("explorer.exe", "\"" + QualLibrary.Folder + "\""); } catch { } };
            qualTip.SetToolTip(btnFolder, "The folder of the libraries: each .txt file there is one standard, one sub-tab (copy one to start another)");
            var btnReload = new Win98Button { Text = "Re&load", Width = 75 };
            btnReload.Click += (s, e) => LoadQualLibraries();
            grpStd.Controls.AddRange(new Control[] { Lbl("Standard:", 10, 18), lblStd, Lbl("Ca&tegory:", 10, 44), q.Category, warn,
                Lbl("Inject &at net:", 10, 70), q.Net, Lbl("Source R (Ω):", 270, 70), q.Rser, q.Replaces, btnEdit, btnFolder, btnReload });
            grpStd.Resize += (s, e) =>
            {
                btnReload.Location = new Point(grpStd.Width - 10 - btnReload.Width, 16);
                btnFolder.Location = new Point(btnReload.Left - 6 - btnFolder.Width, 16);
                btnEdit.Location = new Point(btnFolder.Left - 6 - btnEdit.Width, 16);
            };

            // the tests
            var grpTests = new Win98GroupBox { Text = "Tests", Dock = DockStyle.Left, Width = 330 };
            q.Tests = new CheckedListBox { BorderStyle = BorderStyle.None, CheckOnClick = true, IntegralHeight = false, Font = Win98.Font };
            q.Tests.ItemCheck += (s, e) =>
            {
                if (fillingQual) return;
                string name = (string)q.Tests.Items[e.Index];
                var ch = project.QualOf(lib.Key);
                ch.Tests.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (e.NewValue == CheckState.Checked) ch.Tests.Add(name);
                MarkDirty();
                BeginInvoke(new Action(() => { UpdateQualCount(q); UpdateQualPlan(); }));
            };
            q.Tests.SelectedIndexChanged += (s, e) => ShowQualTest(q);
            var wellTests = new Win98Well(q.Tests) { Location = new Point(10, 20) };
            var btnAll = new Win98Button { Text = "Select &All" };
            var btnNone = new Win98Button { Text = "Clea&r" };
            btnAll.Click += (s, e) => { for (int i = 0; i < q.Tests.Items.Count; i++) q.Tests.SetItemChecked(i, true); };
            btnNone.Click += (s, e) => { for (int i = 0; i < q.Tests.Items.Count; i++) q.Tests.SetItemChecked(i, false); };
            q.Count = new Label { AutoSize = false, Height = 28, ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false };
            grpTests.Controls.AddRange(new Control[] { wellTests, btnAll, btnNone, q.Count });
            grpTests.Resize += (s, e) =>
            {
                int w = grpTests.Width - 20, h = grpTests.Height;
                q.Count.SetBounds(10, h - 8 - 28, w, 28);
                btnAll.Location = new Point(10, q.Count.Top - 6 - 23);
                btnNone.Location = new Point(10 + 75 + 6, btnAll.Top);
                wellTests.Size = new Size(w, Math.Max(20, btnAll.Top - 6 - wellTests.Top));
            };

            // the waveform of the selected test
            var grpWave = new Win98GroupBox { Text = "Supply waveform", Dock = DockStyle.Fill, Padding = new Padding(10, 20, 10, 10) };
            q.Chart = new Win98Chart { Dock = DockStyle.Fill };
            q.Info = new RichTextBox
            {
                ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Win98.Window, Font = Win98.Font, WordWrap = true,
                DetectUrls = false, TabStop = false, ScrollBars = RichTextBoxScrollBars.Vertical,
            };
            var wellInfo = new Win98Well(q.Info) { Dock = DockStyle.Bottom, Height = 76 };
            var btnBar = new Panel { Dock = DockStyle.Bottom, Height = 33, BackColor = Win98.Face };
            var btnCopy = new Win98Button { Text = "&Copy PWL", Location = new Point(0, 8), Width = 90 };
            btnCopy.Click += (s, e) => CopyQualPwl(q);
            qualTip.SetToolTip(btnCopy, "Copy the selected test as an LTspice source value, PWL(t1 v1 t2 v2 ...)");
            var btnExport = new Win98Button { Text = "E&xport PWL Files...", Location = new Point(96, 8), Width = 120 };
            btnExport.Click += (s, e) => ExportQualPwl(q);
            qualTip.SetToolTip(btnExport, "Write one PWL file per ticked test (time and volts per line), for PWL file=... in LTspice");
            btnBar.Controls.AddRange(new Control[] { btnCopy, btnExport });
            grpWave.Controls.Add(q.Chart);
            grpWave.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 6, BackColor = Win98.Face });
            grpWave.Controls.Add(wellInfo);
            grpWave.Controls.Add(btnBar);

            var mid = new Panel { Dock = DockStyle.Fill, BackColor = Win98.Face };
            mid.Controls.Add(grpWave);
            mid.Controls.Add(Gap(DockStyle.Left));
            mid.Controls.Add(grpTests);
            page.Controls.Add(mid);
            page.Controls.Add(Gap(DockStyle.Top));
            page.Controls.Add(grpStd);
            return page;
        }

        static readonly CultureInfo QInv = CultureInfo.InvariantCulture;

        /// <summary>A number with an optional SPICE suffix ("50m", "1.5k"); NaN when empty or not a number.</summary>
        static double ParseEng(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return double.NaN;
            return Expr.TryParse(s, out var e, out _, true) ? e.Eval(new Dictionary<string, double>()) : double.NaN;
        }

        static string EngText(double v) => double.IsNaN(v) ? "" : ErrorBudget.Eng(v).Replace("µ", "u");

        /// <summary>Every sub-tab and the Pass / Fail page from the setup (called when the setup or the schematic changes).</summary>
        void ShowQualification()
        {
            if (qualTabs == null) return;
            fillingQual = true;
            try
            {
                var nets = netlist?.Nodes ?? new List<string>();
                foreach (var q in qualPages)
                {
                    var ch = project.QualOf(q.Lib.Key);
                    int ci = q.Lib.CategoryOrder.FindIndex(c => c.Equals(ch.Category, StringComparison.OrdinalIgnoreCase));
                    q.Category.SelectedIndex = q.Category.Items.Count == 0 ? -1 : Math.Max(0, ci);
                    if (ch.Category.Length == 0) ch.Category = q.CategoryName;
                    q.Net.Items.Clear();
                    foreach (var n in nets) q.Net.Items.Add(n);
                    q.Net.Text = ch.Net;
                    q.Rser.Text = ch.Rser > 0 ? EngText(ch.Rser) : "0";
                }
                rbQualNominal.Checked = project.QualNominal;
                rbQualMc.Checked = !project.QualNominal;
                txtQualRuns.Text = project.QualRuns.ToString(QInv);
                txtQualRuns.Enabled = !project.QualNominal;
                if (txtQualPlot != null) txtQualPlot.Text = string.Join(" ", project.QualPlot);
            }
            finally { fillingQual = false; }
            foreach (var q in qualPages) { FillQualTests(q); ShowQualReplaces(q); }
            FillCriteria();
            UpdateQualPlan();
        }

        void FillQualTests(QualPage q)
        {
            string keep = q.Tests.SelectedItem as string;
            var ch = project.QualOf(q.Lib.Key);
            fillingQual = true;
            try
            {
                q.Tests.Items.Clear();
                foreach (var t in q.Lib.TestsFor(q.CategoryName))
                    q.Tests.Items.Add(t.Name, ch.Tests.Contains(t.Name, StringComparer.OrdinalIgnoreCase));
                int i = keep != null ? q.Tests.Items.IndexOf(keep) : -1;
                if (q.Tests.Items.Count > 0) q.Tests.SelectedIndex = Math.Max(0, i);
            }
            finally { fillingQual = false; }
            UpdateQualCount(q);
            ShowQualTest(q);
        }

        /// <summary>What the chosen net has now: the source that will be taken out, or none (the waveform is added).</summary>
        void ShowQualReplaces(QualPage q)
        {
            string net = q.Net.Text.Trim();
            if (net.Length == 0) { q.Replaces.Text = "Choose the supply net."; return; }
            if (netlist == null) { q.Replaces.Text = ""; return; }
            if (!netlist.Nodes.Contains(net, StringComparer.OrdinalIgnoreCase)) { q.Replaces.Text = $"No net {net} in the schematic."; return; }
            var src = QualBuild.SourcesAt(netlist.Lines, net);
            q.Replaces.Text = src.Count > 0 ? $"Replaces {string.Join(", ", src)} (to ground)." : "No source there: the waveform is added between the net and ground.";
        }

        void UpdateQualCount(QualPage q)
        {
            var vars = q.Lib.Variables(q.CategoryName);
            int n = 0; double total = 0;
            foreach (string name in q.Tests.CheckedItems)
            {
                var t = q.Lib.Tests.FirstOrDefault(x => x.Name == name);
                if (t == null) continue;
                var w = QualLibrary.Waveform(t, vars, out var err);
                n++;
                if (err == null) total += w[w.Count - 1][0];
            }
            q.Count.Text = n == 0 ? $"{q.Tests.Items.Count} tests for category {q.CategoryName}; none ticked."
                                  : $"{n} of {q.Tests.Items.Count} ticked: {ErrorBudget.Eng(total)}s of simulated time per unit.";
        }

        void ShowQualTest(QualPage q)
        {
            q.Chart.Clear();
            var t = q.Selected;
            if (t == null) { q.Chart.EmptyText = "Choose a test."; q.Info.Text = ""; return; }
            var vars = q.Lib.Variables(q.CategoryName);
            var pts = QualLibrary.Waveform(t, vars, out var err);
            q.Chart.Title = $"{t.Name} - category {q.CategoryName}";
            q.Chart.XLabel = "time (s)";
            q.Chart.YLabel = "supply (V)";
            if (err == null)
            {
                q.Chart.Add(new ChartSeries { Kind = SeriesKind.Line, Label = "supply", Color = Win98Chart.Navy, Size = 2,
                    X = pts.Select(v => v[0]).ToArray(), Y = pts.Select(v => v[1]).ToArray() });
                var ev = QualEvents.Of(pts);
                if (ev.End > ev.Start)
                    q.Chart.Add(new ChartSeries { Kind = SeriesKind.VLine, Label = "event start / end", Color = Win98Chart.Silver, Dashed = true, X = new[] { ev.Start, ev.End } });
            }
            else q.Chart.EmptyText = "Cannot draw it: " + err;

            var sb = new StringBuilder();
            sb.Append($"{q.Lib.Standard}, {t.Section}".Trim(' ', ',')).Append("    ");
            if (err == null)
            {
                var ev = QualEvents.Of(pts);
                sb.Append($"{pts.Count} points, {ErrorBudget.Eng(ev.Stop)}s");
                if (ev.End > ev.Start) sb.Append($"; event {ErrorBudget.Eng(ev.Start)}s .. {ErrorBudget.Eng(ev.End)}s");
            }
            else sb.Append("error: " + err);
            sb.Append('\n').Append("Steps: ").Append(t.Steps).Append('\n');
            if (t.Note.Length > 0) sb.Append(t.Note).Append('\n');
            var used = vars.Where(kv => t.Steps.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (used.Count > 0) sb.Append($"Category {q.CategoryName}: ").Append(string.Join(", ", used.Select(kv => $"{kv.Key} = {ErrorBudget.Eng(kv.Value)}")));
            q.Info.Text = sb.ToString();
        }

        void CopyQualPwl(QualPage q)
        {
            var t = q.Selected;
            if (t == null) return;
            var pts = QualLibrary.Waveform(t, q.Lib.Variables(q.CategoryName), out var err);
            if (err != null) { Win98MessageBox.Show(this, err, "SPICA", MessageBoxButtons.OK, Win98Icon.Warning); return; }
            Clipboard.SetText(QualLibrary.PwlValue(pts));
            SetStatus($"Copied the PWL of {t.Name} (category {q.CategoryName}).");
        }

        void ExportQualPwl(QualPage q)
        {
            var tests = q.Tests.CheckedItems.Cast<string>().Select(n => q.Lib.Tests.FirstOrDefault(t => t.Name == n)).Where(t => t != null).ToList();
            if (tests.Count == 0) { Win98MessageBox.Show(this, "Tick the tests to export first.", "SPICA", MessageBoxButtons.OK, Win98Icon.Information); return; }
            using (var d = new FolderBrowserDialog { Description = "Folder for the PWL files:", ShowNewFolderButton = true })
            {
                if (ascPath != null) d.SelectedPath = Path.GetDirectoryName(ascPath);
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var vars = q.Lib.Variables(q.CategoryName);
                var bad = new List<string>();
                int n = 0;
                foreach (var t in tests)
                {
                    var pts = QualLibrary.Waveform(t, vars, out var err);
                    if (err != null) { bad.Add($"{t.Name}: {err}"); continue; }
                    File.WriteAllText(Path.Combine(d.SelectedPath, QualCriterion.Safe(q.Lib.Key + "_" + q.CategoryName + "_" + t.Name) + ".pwl"), QualLibrary.PwlFile(pts));
                    n++;
                }
                SetStatus($"Wrote {n} PWL file{(n == 1 ? "" : "s")} to {d.SelectedPath}.");
                if (bad.Count > 0) Win98MessageBox.Show(this, "Not written:\n\n" + string.Join("\n", bad), "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
            }
        }

        /// <summary>The library in Notepad; read again when SPICA gets the focus back.</summary>
        void EditQualLibrary(QualLibrary lib)
        {
            if (!File.Exists(lib.Path)) { Win98MessageBox.Show(this, "The library file could not be written:\n" + lib.Path, "SPICA", MessageBoxButtons.OK, Win98Icon.Warning); return; }
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + lib.Path + "\"") { UseShellExecute = false });
                EventHandler back = null;
                back = (s, e) => { Activated -= back; LoadQualLibraries(); };
                Activated += back;
            }
            catch (Exception ex) { Win98MessageBox.Show(this, "Could not open the library.\n\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); }
        }

        // ===================================================================================== pass / fail

        Win98TabPage BuildPassFailPage()
        {
            var page = new Win98TabPage("Pass / Fail", Win98.Icon("results16"));

            var grpCrit = new Win98GroupBox { Text = "Criteria: what each test must keep (checked in every test they apply to)", Dock = DockStyle.Top, Height = 210 };
            lvCriteria = new Win98ListView { MultiSelect = false };
            foreach (var (t, w) in new[] { ("Name", 120), ("Rule", 230), ("Window", 260), ("Tests", 180) })
                lvCriteria.Columns.Add(t, w);
            lvCriteria.DoubleClick += (s, e) => EditCriterion(SelectedCriterion());
            var wellCrit = new Win98Well(lvCriteria) { Location = new Point(10, 20) };
            var btnAdd = new Win98Button { Text = "&Add...", Width = 80 };
            var btnEditC = new Win98Button { Text = "&Edit...", Width = 80 };
            var btnDel = new Win98Button { Text = "Re&move", Width = 80 };
            btnAdd.Click += (s, e) => EditCriterion(null);
            btnEditC.Click += (s, e) => EditCriterion(SelectedCriterion());
            btnDel.Click += (s, e) =>
            {
                var c = SelectedCriterion();
                if (c == null) return;
                project.QualCriteria.Remove(c);
                MarkDirty();
                FillCriteria();
            };
            var lblPlot = new Label { Text = "Also &plot:", AutoSize = true, BackColor = Win98.Face };
            txtQualPlot = new Win98TextField { Width = 260 };
            var lblPlotHint = new Label { Text = "more signals saved and drawn with every test, e.g. V(out) I(Rload)", AutoSize = true, ForeColor = Win98.Shadow, BackColor = Win98.Face };
            txtQualPlot.Box.TextChanged += (s, e) =>
            {
                if (fillingQual) return;
                project.QualPlot = txtQualPlot.Text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                MarkDirty();
            };
            grpCrit.Controls.AddRange(new Control[] { wellCrit, btnAdd, btnEditC, btnDel, lblPlot, txtQualPlot, lblPlotHint });
            grpCrit.Resize += (s, e) =>
            {
                btnAdd.Location = new Point(grpCrit.Width - 10 - btnAdd.Width, 20);
                btnEditC.Location = new Point(btnAdd.Left, btnAdd.Bottom + 6);
                btnDel.Location = new Point(btnAdd.Left, btnEditC.Bottom + 6);
                txtQualPlot.Location = new Point(74, grpCrit.Height - 10 - txtQualPlot.Height);
                lblPlot.Location = new Point(10, txtQualPlot.Top + 3);
                lblPlotHint.Location = new Point(txtQualPlot.Right + 8, txtQualPlot.Top + 3);
                wellCrit.Size = new Size(btnAdd.Left - 8 - wellCrit.Left, txtQualPlot.Top - 8 - wellCrit.Top);
            };

            var grpRes = new Win98GroupBox { Text = "Verdicts: the newest run of each ticked test", Dock = DockStyle.Fill };
            lvVerdicts = new Win98ListView { MultiSelect = false };
            foreach (var (t, w) in new[] { ("Test", 230), ("Criterion", 110), ("Verdict", 70), ("Worst seen", 120), ("Margin", 80), ("Runs failing", 90), ("Rule", 200), ("Folder", 160) })
                lvVerdicts.Columns.Add(t, w);
            lvVerdicts.CellColor = (row, col) =>
            {
                if (col != 2 || row < 0 || row >= lvVerdicts.Items.Count) return null;
                string v = lvVerdicts.Items[row].SubItems[2].Text;
                return v == "PASS" ? Color.FromArgb(0, 100, 0) : v == "FAIL" ? Color.FromArgb(160, 0, 0) : (Color?)null;
            };
            QualRow SelectedRow() => lvVerdicts.SelectedItems.Count == 1 ? lvVerdicts.SelectedItems[0].Tag as QualRow : null;
            lvVerdicts.DoubleClick += (s, e) => ShowQualWaveforms(SelectedRow());
            var wellRes = new Win98Well(lvVerdicts) { Location = new Point(10, 20) };
            var btnWave = new Win98Button { Text = "&Waveforms...", Width = 96 };
            var btnOpen = new Win98Button { Text = "&Open Results", Width = 96 };
            var btnRefresh = new Win98Button { Text = "Re&fresh", Width = 96 };
            btnWave.Click += (s, e) => ShowQualWaveforms(SelectedRow());
            btnOpen.Click += (s, e) => { var r = SelectedRow(); if (r?.Dir != null && Directory.Exists(r.Dir)) OpenResultsFolder(r.Dir); };
            btnRefresh.Click += (s, e) => ShowQualVerdicts();
            var lblHint = new Label { Text = "Double-click a row to see its waveforms: the supply, each judged signal, the windows and where a limit was crossed.", AutoSize = true, ForeColor = Win98.Shadow, BackColor = Win98.Face };
            grpRes.Controls.AddRange(new Control[] { wellRes, btnWave, btnOpen, btnRefresh, lblHint });
            grpRes.Resize += (s, e) =>
            {
                btnWave.Location = new Point(grpRes.Width - 10 - btnWave.Width, 20);
                btnOpen.Location = new Point(btnWave.Left, btnWave.Bottom + 6);
                btnRefresh.Location = new Point(btnWave.Left, btnOpen.Bottom + 6);
                lblHint.Location = new Point(10, grpRes.Height - 8 - lblHint.Height);
                wellRes.Size = new Size(btnWave.Left - 8 - wellRes.Left, lblHint.Top - 6 - wellRes.Top);
            };

            page.Controls.Add(grpRes);
            page.Controls.Add(Gap(DockStyle.Top));
            page.Controls.Add(grpCrit);
            return page;
        }

        QualCriterion SelectedCriterion() =>
            lvCriteria.SelectedItems.Count == 1 ? lvCriteria.SelectedItems[0].Tag as QualCriterion : null;

        void FillCriteria()
        {
            if (lvCriteria == null) return;
            lvCriteria.BeginUpdate();
            lvCriteria.Items.Clear();
            foreach (var c in project.QualCriteria)
                lvCriteria.Items.Add(new ListViewItem(new[] { c.Name, c.RuleText(), c.WindowText(), c.Tests.Trim().Length == 0 ? "every test" : "names with: " + c.Tests }) { Tag = c });
            if (project.QualCriteria.Count == 0)
                lvCriteria.Items.Add(new ListViewItem(new[] { "", "No criteria yet: Add... e.g. V(pgood) ≥ 2.5 from event end + 100 ms to test end", "", "" }) { ForeColor = Win98.Shadow });
            lvCriteria.EndUpdate();
        }

        void EditCriterion(QualCriterion c)
        {
            var signals = new List<string>();
            if (netlist != null)
            {
                signals.AddRange(netlist.Nodes.Select(n => $"V({n})"));
                signals.AddRange(netlist.Currents);
            }
            using (var f = new QualCriterionForm(c, signals, project.QualCriteria.Where(x => x != c).Select(x => x.Name)))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                if (c == null) project.QualCriteria.Add(f.Result);
                else project.QualCriteria[project.QualCriteria.IndexOf(c)] = f.Result;
                MarkDirty();
                FillCriteria();
            }
        }

        // ===================================================================================== running

        /// <summary>The cases of every ticked test, with their netlists (or why one cannot run).</summary>
        List<QualCase> QualCases()
        {
            var list = new List<QualCase>();
            foreach (var q in qualPages)
            {
                var ch = project.QualOf(q.Lib.Key);
                foreach (var t in q.Lib.TestsFor(q.CategoryName).Where(t => ch.Tests.Contains(t.Name, StringComparer.OrdinalIgnoreCase)))
                    list.Add(QualBuild.Make(netlist, q.Lib, t, q.CategoryName, ch, project.QualCriteria));
            }
            return list;
        }

        string QualRoot() =>
            Path.Combine(txtOut.Text.Trim().Length > 0 ? txtOut.Text.Trim() : CampaignBuilder.DefaultRoot(ascPath), "qualification");

        void UpdateQualPlan()
        {
            if (lblQualPlan == null) return;
            int n = qualPages.Sum(q => q.Tests.CheckedItems.Count);
            int temps = project.Temp == TempMode.Corners ? Math.Max(1, project.Corners.Distinct().Count()) : 1;
            int runs = project.QualNominal ? 1 : project.QualRuns;
            lblQualPlan.Text = n == 0 ? "No test ticked." :
                $"{n} test{(n == 1 ? "" : "s")} x {runs:N0} run{(runs == 1 ? "" : "s")}" + (temps > 1 ? $" x {temps} temperatures" : "") +
                $" = {n * runs * temps:N0} simulations; {project.QualCriteria.Count} criteri{(project.QualCriteria.Count == 1 ? "on" : "a")}.";
        }

        /// <summary>Queues one campaign per ticked test: its own netlist (source, .tran, .meas), its folder under qualification\.</summary>
        void RunQualification()
        {
            if (netlist == null) { BrowseSchematic(); return; }
            string lt = settings.LtspicePath ?? "";
            if (!File.Exists(lt)) { Win98MessageBox.Show(this, "LTspice.exe was not found. Set its location in Tools > Options.", "SPICA", MessageBoxButtons.OK, Win98Icon.Error); return; }
            if (!project.QualNominal && !(project.QualRuns >= 1)) { Win98MessageBox.Show(this, "Enter the number of Monte Carlo runs per test.", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning); return; }
            var cases = QualCases();
            if (cases.Count == 0) { Win98MessageBox.Show(this, "Tick the tests to run first.", "SPICA", MessageBoxButtons.OK, Win98Icon.Information); return; }
            var bad = cases.Where(c => c.Error != null).Select(c => c.Error).Distinct().ToList();
            if (bad.Count > 0) { Win98MessageBox.Show(this, "Cannot run:\n\n" + string.Join("\n", bad), "SPICA", MessageBoxButtons.OK, Win98Icon.Warning); return; }
            foreach (var q in qualPages)
            {
                var ch = project.QualOf(q.Lib.Key);
                if (ch.Tests.Count > 0 && netlist.Nodes.Count > 0 && !netlist.Nodes.Contains(ch.Net, StringComparer.OrdinalIgnoreCase) &&
                    Win98MessageBox.Show(this, $"The schematic has no net {ch.Net}: the waveform would drive a net of its own.\nRun anyway?", "SPICA",
                        MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;
            }
            if (project.QualCriteria.Count == 0 &&
                Win98MessageBox.Show(this, "No pass/fail criteria yet: the tests run, but nothing is judged.\nRun anyway?", "SPICA",
                    MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;

            ReadUiIntoProject();
            string root = QualRoot();
            int longest = cases.Max(c => Path.Combine(root, c.FolderName).Length) + QualCase.PathReserve;
            if (longest > 259 &&
                Win98MessageBox.Show(this, $"The result folders would be too deep: about {longest} characters of path, and LTspice cannot open files past 260.\n\n{root}\n\n" +
                    "Choose a shorter output folder on the Setup tab. Run anyway?", "SPICA", MessageBoxButtons.YesNo, Win98Icon.Warning) != DialogResult.Yes) return;
            int added = 0;
            var notes = new List<string>();
            addingLocal = true;
            try
            {
                foreach (var c in cases)
                {
                    var setup = project.Clone();
                    setup.Runs = project.QualNominal ? 1 : project.QualRuns;
                    setup.WorstCase = WcMethod.Off;
                    setup.Stop.Enabled = false;
                    setup.Errors = new List<ErrorItem>();
                    setup.Tf = new TfSetup();
                    setup.Report.Auto = false;
                    // the waveforms are drawn from the .raw files: keep them, with the supply, the judged and the extra signals
                    setup.KeepRaw = true;
                    var plotSignals = new[] { $"V({project.QualOf(c.Library.Key).Net})" }.Concat(c.Criteria.Select(x => x.Signal)).Concat(project.QualPlot);
                    foreach (var sig in plotSignals)
                        if (!setup.Signals.Contains(sig, StringComparer.OrdinalIgnoreCase)) setup.Signals.Add(sig);
                    foreach (var cr in c.Criteria)
                    {
                        if (!double.IsNaN(cr.Lo)) setup.Specs[cr.MinMeas] = new SpecLimit { Lsl = cr.Lo, Usl = double.NaN };
                        if (!double.IsNaN(cr.Hi)) setup.Specs[cr.MaxMeas] = new SpecLimit { Lsl = double.NaN, Usl = cr.Hi };
                    }
                    var request = new CampaignRequest
                    {
                        LtspiceExe = lt,
                        AscPath = ascPath,
                        OutputRoot = Path.Combine(root, c.FolderName),
                        Netlist = netlist.WithLines(c.Lines),
                        Project = setup,
                        Manual = ManualConfig(),
                        NominalOnly = project.QualNominal,
                    };
                    // the questions of a normal campaign are asked once, for the first test
                    var options = CampaignBuilder.Build(request,
                        added == 0 ? (text, warn) => Win98MessageBox.Show(this, text, "SPICA", MessageBoxButtons.YesNo, warn ? Win98Icon.Warning : Win98Icon.Question) == DialogResult.Yes
                                   : (Func<string, bool, bool>)null,
                        out string error);
                    if (options == null)
                    {
                        if (error != null) notes.Add($"{c.Test.Name}: {error}");
                        if (added == 0 && error == null) return;     // the user said no
                        continue;
                    }
                    if (c.Replaced.Count == 0) notes.Add($"{c.Test.Name}: no source on {project.QualOf(c.Library.Key).Net}, the waveform was added there.");
                    queue.Add(request, options, name: $"{c.Test.Name} ({c.Category})");
                    added++;
                }
            }
            finally { addingLocal = false; }
            SaveSettings();
            SetStatus($"Queued {added} qualification test{(added == 1 ? "" : "s")}; results under {root}.");
            if (notes.Count > 0) Win98MessageBox.Show(this, string.Join("\n", notes.Distinct().Take(12)), "SPICA", MessageBoxButtons.OK, Win98Icon.Information);
            if (added > 0) tabs.SelectedIndex = TabQueue;
            UpdateCommandState();
        }

        /// <summary>The waveforms of a test's newest campaign: the supply, the judged signals, the windows and the limits.</summary>
        void ShowQualWaveforms(QualRow r)
        {
            if (r == null) return;
            if (r.Dir == null || !Directory.Exists(r.Dir)) { Win98MessageBox.Show(this, "This test has not run yet.", "SPICA", MessageBoxButtons.OK, Win98Icon.Information); return; }
            ResultSet rs;
            try { rs = ResultSet.Load(r.Dir); }
            catch (Exception ex) { Win98MessageBox.Show(this, "Cannot read the results:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); return; }
            var ch = project.QualOf(r.Page.Lib.Key);
            var qc = netlist != null ? QualBuild.Make(netlist, r.Page.Lib, r.Test, r.Page.CategoryName, ch, project.QualCriteria) : null;
            if (qc == null || qc.Error != null) { Win98MessageBox.Show(this, qc?.Error ?? "Open the schematic first.", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning); return; }
            var f = new QualWaveForm(qc, ch.Net, r.Dir, rs, project.QualPlot, ch.Rser) { OpenResults = d => { if (Directory.Exists(d)) OpenResultsFolder(d); } };
            f.Show(this);
            lastQualWave = f;
        }

        /// <summary>The waveform window opened last (for the smoke test).</summary>
        internal QualWaveForm lastQualWave;

        /// <summary>The verdict of every criterion on the newest finished run of each ticked test.</summary>
        void ShowQualVerdicts()
        {
            if (lvVerdicts == null) return;
            lvVerdicts.BeginUpdate();
            lvVerdicts.Items.Clear();
            try
            {
                if (ascPath == null)
                {
                    lvVerdicts.Items.Add(new ListViewItem(new[] { "Open a schematic first.", "", "", "", "", "", "", "" }) { ForeColor = Win98.Shadow });
                    return;
                }
                string root = QualRoot();
                int rows = 0;
                foreach (var q in qualPages)
                {
                    var ch = project.QualOf(q.Lib.Key);
                    foreach (var t in q.Lib.TestsFor(q.CategoryName).Where(t => ch.Tests.Contains(t.Name, StringComparer.OrdinalIgnoreCase)))
                    {
                        var probe = new QualCase { Library = q.Lib, Test = t, Category = q.CategoryName };
                        string dir = QualVerdict.Latest(Path.Combine(root, probe.FolderName));
                        var tag = new QualRow { Dir = dir, Page = q, Test = t };
                        string label = $"{t.Name} ({q.CategoryName})";
                        if (dir == null)
                        {
                            lvVerdicts.Items.Add(new ListViewItem(new[] { label, "", "not run", "", "", "", "", "" }) { ForeColor = Win98.Shadow });
                            rows++;
                            continue;
                        }
                        ResultSet rs;
                        try { rs = ResultSet.Load(dir); }
                        catch (Exception ex)
                        {
                            lvVerdicts.Items.Add(new ListViewItem(new[] { label, "", "error", ex.Message, "", "", "", dir }) { Tag = tag });
                            rows++;
                            continue;
                        }
                        var crit = project.QualCriteria.Where(c => c.AppliesTo(t.Name)).ToList();
                        if (crit.Count == 0)
                            lvVerdicts.Items.Add(new ListViewItem(new[] { label, "", "no criteria", "", "", $"{rs.Rows.Count} runs", "", Path.GetFileName(dir) }) { Tag = tag });
                        foreach (var v in QualVerdict.Of(rs, label, crit))
                            lvVerdicts.Items.Add(new ListViewItem(new[]
                            {
                                label, v.Criterion, v.Verdict, v.WorstText(), double.IsNaN(v.Margin) ? "" : ErrorBudget.Eng(v.Margin),
                                v.Runs > 0 ? $"{v.Failing} of {v.Runs}" : "", v.Rule, Path.GetFileName(dir),
                            }) { Tag = tag });
                        rows++;
                    }
                }
                if (rows == 0)
                    lvVerdicts.Items.Add(new ListViewItem(new[] { "Tick tests on a standard's tab and run them.", "", "", "", "", "", "", "" }) { ForeColor = Win98.Shadow });
            }
            finally { lvVerdicts.EndUpdate(); }
        }
    }
}
