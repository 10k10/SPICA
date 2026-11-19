using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Spica.Core;

namespace Spica.UI
{
    /// <summary>
    /// Qualification > Pass / Fail > Waveforms: the supply the test applied on top, then one plot per judged signal
    /// (and the extra signals) on the same time axis. Each criterion's window is shaded, its limit drawn over the
    /// window, the samples outside it marked red, and the worst point of the run labelled.
    /// </summary>
    public sealed class QualWaveForm : Win98Form
    {
        readonly QualCase qc;
        readonly string net, dir;
        readonly ResultSet rs;
        readonly List<string> extra;
        readonly List<RawFile.Chunk> chunks;
        readonly ComboBox cbRun;
        readonly Panel stack;
        readonly Label lblInfo;
        readonly List<Win98Chart> charts = new List<Win98Chart>();
        readonly List<RunChoice> choices = new List<RunChoice>();
        bool linking;

        /// <summary>Runs drawn at most in "all runs": more would only be a red and blue blur.</summary>
        public const int MaxOverlay = 200;

        sealed class RunChoice
        {
            public string Text;
            public List<ResultSet.Row> Rows;
            public override string ToString() => Text;
        }

        static readonly Color[] WindowShade = { Color.FromArgb(255, 255, 196), Color.FromArgb(214, 236, 255), Color.FromArgb(222, 255, 222), Color.FromArgb(255, 226, 240) };
        static readonly Color PassLine = Win98Chart.Navy, FailRun = Win98Chart.Purple, FailLine = Color.FromArgb(200, 0, 0), Violation = Color.FromArgb(255, 0, 0);

        static string Eng(double v) => ErrorBudget.Eng(v);

        /// <summary>The verdict of a criterion over some runs, from the .meas values: the text and whether it failed; null without data.</summary>
        Tuple<string, bool> Verdict(QualCriterion cr, List<ResultSet.Row> rows)
        {
            int n = 0, fail = 0;
            foreach (var r in rows)
            {
                double lo = !double.IsNaN(cr.Lo) && rs.Col(cr.MinMeas) >= 0 ? rs.Get(r, cr.MinMeas) : double.NaN;
                double hi = !double.IsNaN(cr.Hi) && rs.Col(cr.MaxMeas) >= 0 ? rs.Get(r, cr.MaxMeas) : double.NaN;
                if (double.IsNaN(lo) && double.IsNaN(hi)) continue;
                n++;
                if ((!double.IsNaN(lo) && lo < cr.Lo) || (!double.IsNaN(hi) && hi > cr.Hi)) fail++;
            }
            if (n == 0) return null;
            return Tuple.Create(fail == 0 ? "PASS" : n == 1 ? "FAIL" : $"FAIL in {fail} of {n}", fail > 0);
        }

        public QualWaveForm(QualCase qc, string net, string dir, ResultSet rs, IEnumerable<string> extra, double rser)
        {
            this.qc = qc; this.net = net; this.dir = dir; this.rs = rs;
            this.extra = (extra ?? new string[0]).Where(s => s.Trim().Length > 0).ToList();
            chunks = RawFile.Chunks(dir);
            Text = $"Waveforms - {qc.Test.Name} ({qc.Category})";
            MinimizeBox = true; MaximizeBox = true; Resizable = true; ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(980, 760 + Padding.Vertical);
            MinimumSize = new Size(600, 420);

            var top = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Win98.Face };
            top.Controls.Add(new Label { Text = "&Run:", Location = new Point(6, 9), AutoSize = true });
            cbRun = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(42, 6), Width = 300, Font = Win98.Font };
            top.Controls.Add(cbRun);
            top.Controls.Add(new Label
            {
                Text = $"Supply into {net}" + (rser > 0 ? $" through {ErrorBudget.Eng(rser)}Ω" : "") + ".  Drag to zoom (all plots follow), double-click to reset, right-click to save one plot.",
                Location = new Point(352, 9), AutoSize = true, ForeColor = Win98.Shadow, UseMnemonic = false,
            });

            stack = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Win98.Face };
            stack.Resize += (s, e) => LayoutCharts();

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 112, BackColor = Win98.Face };
            lblInfo = new Label { Location = new Point(6, 6), UseMnemonic = false, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(4) };
            var btnSave = new Win98Button { Text = "&Save Image...", Size = new Size(100, 23) };
            var btnCopy = new Win98Button { Text = "&Copy Image", Size = new Size(100, 23) };
            var btnOpen = new Win98Button { Text = "&Open Results", Size = new Size(100, 23) };
            var btnClose = new Win98Button { Text = "Close", Size = new Size(100, 23), DialogResult = DialogResult.Cancel };
            btnSave.Click += (s, e) => SaveImage();
            btnCopy.Click += (s, e) => { using (var b = Composite()) if (b != null) Clipboard.SetImage(b); };
            btnOpen.Click += (s, e) => OpenResults?.Invoke(dir);
            btnClose.Click += (s, e) => Close();
            bottom.Controls.AddRange(new Control[] { lblInfo, btnSave, btnCopy, btnOpen, btnClose });
            bottom.Resize += (s, e) =>
            {
                int bx = bottom.Width - 6 - 100;
                btnSave.Location = new Point(bx, 6); btnCopy.Location = new Point(bx, 33); btnOpen.Location = new Point(bx, 60); btnClose.Location = new Point(bx, 87);
                lblInfo.Size = new Size(bx - 12, bottom.Height - 12);
            };
            CancelButton = btnClose;

            var body = new Panel { Dock = DockStyle.Fill };
            body.Controls.Add(stack);
            body.Controls.Add(bottom);
            body.Controls.Add(top);
            Controls.Add(body);

            FillRuns();
            cbRun.SelectedIndexChanged += (s, e) => Draw();
            if (cbRun.Items.Count > 0) cbRun.SelectedIndex = 0;
            else Draw();
        }

        /// <summary>Opens the test's results (the main window's Results tab).</summary>
        public Action<string> OpenResults;

        // ------------------------------------------------------------------ runs

        /// <summary>The smaller distance to a limit of any criterion in a run (negative = outside), NaN without data.</summary>
        double Margin(ResultSet.Row row)
        {
            double m = double.NaN;
            foreach (var cr in qc.Criteria)
            {
                if (!double.IsNaN(cr.Lo) && rs.Col(cr.MinMeas) >= 0) { double v = rs.Get(row, cr.MinMeas); if (!double.IsNaN(v)) m = double.IsNaN(m) ? v - cr.Lo : Math.Min(m, v - cr.Lo); }
                if (!double.IsNaN(cr.Hi) && rs.Col(cr.MaxMeas) >= 0) { double v = rs.Get(row, cr.MaxMeas); if (!double.IsNaN(v)) m = double.IsNaN(m) ? cr.Hi - v : Math.Min(m, cr.Hi - v); }
            }
            return m;
        }

        string RunText(ResultSet.Row r)
        {
            string t = $"Run {r.Run}" + (double.IsNaN(r.Temp) ? "" : " at " + ResultSet.TempLabel(r.Temp));
            double m = Margin(r);
            return t + (double.IsNaN(m) ? "" : m < 0 ? "  - FAIL" : "  - pass");
        }

        void FillRuns()
        {
            var rows = rs.Rows.Where(r => r.Status == RunStatus.Ok).ToList();
            if (rows.Count == 0) return;
            if (rows.Count > 1)
            {
                var worst = rows.Where(r => !double.IsNaN(Margin(r))).OrderBy(Margin).FirstOrDefault();
                if (worst != null) choices.Add(new RunChoice { Text = "Worst: " + RunText(worst), Rows = new List<ResultSet.Row> { worst } });
                int fails = rows.Count(r => Margin(r) < 0);
                choices.Add(new RunChoice
                {
                    Text = $"All {rows.Count} runs" + (fails > 0 ? $" ({fails} failing)" : "") + (rows.Count > MaxOverlay ? $", first {MaxOverlay} drawn" : ""),
                    // the failing runs first, so that they are among the ones drawn
                    Rows = rows.OrderBy(r => Margin(r) < 0 ? 0 : 1).ThenBy(r => r.Run).Take(MaxOverlay).ToList(),
                });
            }
            foreach (var r in rows) choices.Add(new RunChoice { Text = RunText(r), Rows = new List<ResultSet.Row> { r } });
            foreach (var c in choices) cbRun.Items.Add(c);
        }

        sealed class Trace
        {
            public ResultSet.Row Row;
            public double[] X;
            public Dictionary<string, double[]> Y = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Reads the signals of some runs from the chunk .raw files (one pass per file).</summary>
        List<Trace> Load(List<ResultSet.Row> rows, List<string> signals, List<string> problems)
        {
            var traces = new List<Trace>();
            if (chunks.Count == 0) { problems.Add("No .raw files in this campaign's chunks folder (was \"Keep .raw waveform files\" off?). Run the test again."); return traces; }
            var byFile = rows.Select(r => new { r, c = RawFile.ChunkOf(chunks, r.Run, r.Temp) }).ToList();
            int missing = byFile.Count(x => x.c == null);
            if (missing > 0) problems.Add($"{missing} run{(missing == 1 ? " has" : "s have")} no .raw file.");
            foreach (var g in byFile.Where(x => x.c != null).GroupBy(x => x.c.Path))
            {
                RawFile raw;
                try { raw = RawFile.Open(g.Key); }
                catch (Exception ex) { problems.Add(Path.GetFileName(g.Key) + ": " + ex.Message); continue; }
                var idx = signals.Select(raw.Find).ToList();
                foreach (var sname in signals.Where((s, i) => idx[i] < 0)) problems.Add($"{sname} is not in the .raw file (not saved).");
                var first = g.First().c.First;
                var steps = g.Select(x => x.r.Run - first).Distinct().ToList();
                Dictionary<int, RawFile.Step> data;
                try { data = raw.ReadSteps(idx, steps); }
                catch (Exception ex) { problems.Add(Path.GetFileName(g.Key) + ": " + ex.Message); continue; }
                foreach (var x in g)
                {
                    if (!data.TryGetValue(x.r.Run - first, out var st)) { problems.Add($"Run {x.r.Run} is not in {Path.GetFileName(g.Key)}."); continue; }
                    var tr = new Trace { Row = x.r, X = st.X };
                    for (int k = 0; k < signals.Count; k++) if (st.Y[k] != null) tr.Y[signals[k]] = st.Y[k];
                    traces.Add(tr);
                }
            }
            return traces.OrderBy(t => t.Row.Run).ToList();
        }

        // ------------------------------------------------------------------ plots

        string SupplySignal => $"V({net})";

        List<string> JudgedSignals() =>
            qc.Criteria.Select(c => c.Signal).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        void Draw()
        {
            Cursor = Cursors.WaitCursor;
            try { DrawCore(); }
            finally { Cursor = Cursors.Default; }
        }

        void DrawCore()
        {
            foreach (var c in charts) { stack.Controls.Remove(c); c.Dispose(); }
            charts.Clear();
            var choice = cbRun.SelectedItem as RunChoice;
            var judged = JudgedSignals();
            var others = extra.Where(s => !s.Equals(SupplySignal, StringComparison.OrdinalIgnoreCase) && !judged.Contains(s, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var signals = new[] { SupplySignal }.Concat(judged).Concat(others).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var problems = new List<string>();
            var traces = choice == null ? new List<Trace>() : Load(choice.Rows, signals, problems);
            bool many = traces.Count > 1;

            var ev = qc.Events;
            ChartSeries Events(bool legend) => new ChartSeries { Kind = SeriesKind.VLine, Label = "event start / end", InLegend = legend, Color = Color.Gray, Dashed = true, X = new[] { ev.Start, ev.End }.Distinct().ToArray() };
            string Unit(string sig) => sig.StartsWith("I", StringComparison.OrdinalIgnoreCase) ? "A" : "V";

            // 1. the supply: what the test programs (wide, grey) under what reached the net
            var sup = NewChart($"Supply at {net}: {qc.Test.Name}", "V");
            sup.Add(new ChartSeries { Kind = SeriesKind.Line, Label = "programmed (test waveform)", Color = Win98Chart.Silver, Size = 5, X = qc.Wave.Select(p => p[0]).ToArray(), Y = qc.Wave.Select(p => p[1]).ToArray() });
            AddTraces(sup, traces, SupplySignal, null, many);
            sup.Add(Events(true));

            // 2. one plot per criterion: its window shaded, its limits over the window, the samples outside them, the worst point
            var verdictLines = new List<string>();
            foreach (var cr in qc.Criteria)
            {
                string sig = cr.Signal;
                bool has = cr.Window(ev, out double t1, out double t2);
                var verdict = has ? Verdict(cr, choice?.Rows ?? new List<ResultSet.Row>()) : null;
                var ch = NewChart($"{cr.Name}: {cr.RuleText()}, {cr.WindowText()}" + (verdict == null ? "" : "  -  " + verdict.Item1), Unit(sig));
                if (verdict != null) ch.TitleColor = verdict.Item2 ? FailLine : Win98Chart.Green;
                if (has) ch.Add(new ChartSeries { Kind = SeriesKind.Span, Label = $"window {Eng(t1)}s .. {Eng(t2)}s", Color = WindowShade[0], X = new[] { t1, t2 } });
                ch.Add(Events(false));
                AddTraces(ch, traces, sig, r => Fails(cr, r), many);
                if (!has) continue;
                bool firstLim = true;
                foreach (var l in new[] { cr.Lo, cr.Hi }.Where(v => !double.IsNaN(v)))
                {
                    ch.Add(new ChartSeries { Kind = SeriesKind.Line, Label = firstLim ? "limit" : null, Color = Win98Chart.Maroon, Dashed = true, Size = 2, X = new[] { t1, t2 }, Y = new[] { l, l } });
                    firstLim = false;
                }
                var vx = new List<double>(); var vy = new List<double>();
                foreach (var tr in traces)
                {
                    if (!tr.Y.TryGetValue(sig, out var y)) continue;
                    for (int i = 0; i < tr.X.Length; i++)
                        if (tr.X[i] >= t1 && tr.X[i] <= t2 && ((!double.IsNaN(cr.Lo) && y[i] < cr.Lo) || (!double.IsNaN(cr.Hi) && y[i] > cr.Hi)))
                        { vx.Add(tr.X[i]); vy.Add(y[i]); }
                }
                if (vx.Count > 0)
                    ch.Add(new ChartSeries { Kind = SeriesKind.Points, Label = "outside the limit", Color = Violation, Size = 4, X = vx.ToArray(), Y = vy.ToArray() });
                var w = Worst(traces, sig, cr, t1, t2);
                if (w != null)
                {
                    bool fail = (!double.IsNaN(cr.Lo) && w.Item3 < cr.Lo) || (!double.IsNaN(cr.Hi) && w.Item3 > cr.Hi);
                    ch.Add(new ChartSeries { Kind = SeriesKind.Points, Label = $"worst: {Eng(w.Item3)}{Unit(sig)} at {Eng(w.Item2)}s" + (many ? $" (run {w.Item1.Run})" : ""),
                                             Color = fail ? Win98Chart.Fuchsia : Win98Chart.Green, Size = 9, X = new[] { w.Item2 }, Y = new[] { w.Item3 } });
                }
                verdictLines.Add(VerdictLine(cr, t1, t2, choice?.Rows ?? new List<ResultSet.Row>()));
            }

            // 3. the extra signals
            foreach (var sig in others)
            {
                var ch = NewChart(sig, Unit(sig));
                ch.Add(Events(false));
                AddTraces(ch, traces, sig, null, many);
            }

            charts.Last().XLabel = "time (s)";
            foreach (var c in charts)
            {
                c.XViewChanged += (s, e) => Follow((Win98Chart)s);
                c.FixedX = new KeyValuePair<double, double>(0, ev.Stop);       // every plot on the test's time
            }
            LayoutCharts();

            var sb = new StringBuilder();
            if (qc.Criteria.Count == 0) sb.AppendLine("No criterion applies to this test: add them on the Pass / Fail tab.");
            foreach (var l in verdictLines) sb.AppendLine(l);
            foreach (var p in problems.Distinct().Take(4)) sb.AppendLine("! " + p);
            sb.Append("Verdicts come from LTspice's .meas; the plots from the .raw file, which LTspice may thin out between time steps.");
            lblInfo.Text = sb.ToString();
        }

        Win98Chart NewChart(string title, string unit)
        {
            var c = new Win98Chart { Title = title, YLabel = unit };
            charts.Add(c);
            stack.Controls.Add(c);
            return c;
        }

        /// <summary>The runs' traces of a signal; with <paramref name="fails"/> (a criterion's plot) the runs failing it are drawn apart.</summary>
        void AddTraces(Win98Chart ch, List<Trace> traces, string sig, Func<ResultSet.Row, bool> fails, bool many)
        {
            bool judged = fails != null && many, labelled = false, passLabel = false, failLabel = false;
            // passing runs first, failing ones drawn over them
            foreach (var tr in traces.OrderBy(t => judged && fails(t.Row) ? 1 : 0))
            {
                if (!tr.Y.TryGetValue(sig, out var y)) continue;
                bool fail = judged && fails(tr.Row);
                string label = null;
                if (!judged) { if (!labelled) label = sig + (many ? $" ({traces.Count} runs)" : ""); labelled = true; }
                else if (fail && !failLabel) { label = $"{sig}: runs failing it"; failLabel = true; }
                else if (!fail && !passLabel) { label = $"{sig}: runs passing it"; passLabel = true; }
                ch.Add(new ChartSeries { Kind = SeriesKind.Line, Label = label, Color = fail ? FailRun : PassLine, Size = many ? 3 : 2, X = tr.X, Y = y });
            }
        }

        /// <summary>Whether a run fails one criterion, from its .meas values.</summary>
        bool Fails(QualCriterion cr, ResultSet.Row r)
        {
            double lo = !double.IsNaN(cr.Lo) && rs.Col(cr.MinMeas) >= 0 ? rs.Get(r, cr.MinMeas) : double.NaN;
            double hi = !double.IsNaN(cr.Hi) && rs.Col(cr.MaxMeas) >= 0 ? rs.Get(r, cr.MaxMeas) : double.NaN;
            return (!double.IsNaN(lo) && lo < cr.Lo) || (!double.IsNaN(hi) && hi > cr.Hi);
        }

        /// <summary>The run and point farthest past (or closest to) the limit inside the window.</summary>
        static Tuple<ResultSet.Row, double, double> Worst(List<Trace> traces, string sig, QualCriterion cr, double t1, double t2)
        {
            Tuple<ResultSet.Row, double, double> best = null;
            double bestM = double.PositiveInfinity;
            foreach (var tr in traces)
            {
                if (!tr.Y.TryGetValue(sig, out var y)) continue;
                for (int i = 0; i < tr.X.Length; i++)
                {
                    if (tr.X[i] < t1 || tr.X[i] > t2) continue;
                    double m = double.PositiveInfinity;
                    if (!double.IsNaN(cr.Lo)) m = Math.Min(m, y[i] - cr.Lo);
                    if (!double.IsNaN(cr.Hi)) m = Math.Min(m, cr.Hi - y[i]);
                    if (m < bestM) { bestM = m; best = Tuple.Create(tr.Row, tr.X[i], y[i]); }
                }
            }
            return best;
        }

        string VerdictLine(QualCriterion cr, double t1, double t2, List<ResultSet.Row> rows)
        {
            var sb = new StringBuilder($"{cr.Name}: {cr.RuleText()} from {Eng(t1)}s to {Eng(t2)}s ({cr.WindowText()})");
            var vals = new List<double>();
            int fail = 0;
            foreach (var r in rows)
            {
                double lo = !double.IsNaN(cr.Lo) && rs.Col(cr.MinMeas) >= 0 ? rs.Get(r, cr.MinMeas) : double.NaN;
                double hi = !double.IsNaN(cr.Hi) && rs.Col(cr.MaxMeas) >= 0 ? rs.Get(r, cr.MaxMeas) : double.NaN;
                if (double.IsNaN(lo) && double.IsNaN(hi)) continue;
                if ((!double.IsNaN(lo) && lo < cr.Lo) || (!double.IsNaN(hi) && hi > cr.Hi)) fail++;
                if (!double.IsNaN(lo)) vals.Add(lo);
                if (!double.IsNaN(hi)) vals.Add(hi);
            }
            if (vals.Count == 0) return sb.Append(" - no data").ToString();
            sb.Append(rows.Count == 1 ? $" - measured {string.Join(" / ", vals.Select(v => Eng(v)))}" : $" - {rows.Count} runs, {Eng(vals.Min())} .. {Eng(vals.Max())}");
            sb.Append(fail > 0 ? (rows.Count == 1 ? " - FAIL" : $" - FAIL in {fail}") : " - PASS");
            return sb.ToString();
        }

        void Follow(Win98Chart from)
        {
            if (linking) return;
            linking = true;
            try
            {
                foreach (var c in charts)
                {
                    if (c == from) continue;
                    if (from.IsZoomed) c.LinkX(from.ViewX0, from.ViewX1);
                    else { c.LinkX(null, null); c.FixedX = new KeyValuePair<double, double>(0, qc.Events.Stop); }
                }
                if (!from.IsZoomed) from.FixedX = new KeyValuePair<double, double>(0, qc.Events.Stop);
            }
            finally { linking = false; }
        }

        void LayoutCharts()
        {
            if (charts.Count == 0) return;
            int w = stack.ClientSize.Width, avail = stack.ClientSize.Height;
            // the supply plot smaller; the others share the rest, at least 190 pixels each (the panel scrolls past that)
            int each = Math.Max(190, (avail - 4) / Math.Max(1, charts.Count));
            int supH = charts.Count == 1 ? each : Math.Max(170, each * 4 / 5);
            int rest = charts.Count == 1 ? 0 : Math.Max(190, (avail - supH - 4) / (charts.Count - 1));
            if (supH + rest * (charts.Count - 1) > avail) w -= SystemInformation.VerticalScrollBarWidth;
            int y = -stack.VerticalScroll.Value;
            for (int i = 0; i < charts.Count; i++)
            {
                int h = i == 0 ? supH : rest;
                charts[i].SetBounds(0, y, Math.Max(100, w), h - 2);
                y += h;
            }
        }

        // ------------------------------------------------------------------ images

        /// <summary>All the plots, one under the other, as one picture.</summary>
        Bitmap Composite()
        {
            if (charts.Count == 0) return null;
            int w = Math.Max(800, charts[0].Width);
            var parts = charts.Select(c => c.ToBitmap(w, Math.Max(220, c.Height))).ToList();
            var bmp = new Bitmap(w, parts.Sum(p => p.Height), PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                int y = 0;
                foreach (var p in parts) { g.DrawImage(p, 0, y); y += p.Height; p.Dispose(); }
            }
            return bmp;
        }

        void SaveImage()
        {
            string name = new string($"{qc.Category}_{qc.Test.Name}".Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray()).Trim('_');
            using (var d = new SaveFileDialog { Title = "Save Waveforms", Filter = "PNG image (*.png)|*.png", FileName = name + ".png", AutoUpgradeEnabled = false })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { using (var b = Composite()) b?.Save(d.FileName, ImageFormat.Png); }
                catch (Exception ex) { Win98MessageBox.Show(this, "Could not save the image:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); }
            }
        }

        /// <summary>For the smoke test: the plots as one picture.</summary>
        public Bitmap Snapshot() => Composite();
    }
}
