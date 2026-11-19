using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Spica.Core;
using Spica.Core.Graph;

namespace Spica.UI
{
    /// <summary>A study that ran: its campaigns, its verdicts, its report (what the Results tab shows for it).</summary>
    public sealed class StudyRunData
    {
        public string Study = "";
        public DateTime Ended = DateTime.Now;
        public List<RunSet> Sets = new List<RunSet>();
        public List<VerdictRecord> Verdicts = new List<VerdictRecord>();
        public List<string> Problems = new List<string>();
        public List<string> PlotSignals = new List<string>();
        public string Report;
    }

    /// <summary>
    /// Results > a study > Waveforms: the signals of the study's .raw files, in the manner of LTspice.
    /// - On the left, the cases and conditions (tick several to overlay them: temperatures, candidates), the runs,
    ///   and the signals the .raw files hold; double-click a signal to add it to the selected pane.
    /// - On the right, panes stacked on one time axis (zoom one, all follow); a criterion on a signal draws its
    ///   window and limits, a supply waveform its programmed shape and its events.
    /// </summary>
    public sealed class WaveformView : Panel
    {
        sealed class Pane
        {
            public Win98Chart Chart;
            public readonly List<string> Signals = new List<string>();
        }

        readonly CheckedListBox lbSets;
        readonly ComboBox cbRuns;
        readonly TextBox txtFilter;
        readonly ListBox lbSignals;
        readonly Panel stack;
        readonly Label lblInfo;
        readonly List<Pane> panes = new List<Pane>();
        int active;
        bool filling, linking;
        StudyRunData data;
        double curA = double.NaN, curB = double.NaN;     // the cursors (time), NaN = not placed
        List<Trace> lastTraces = new List<Trace>();
        readonly Label lblCursor;
        string focusCriterion;      // the criterion of the verdict opened: only its window is shaded
        readonly Dictionary<string, List<string>> namesByDir = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>LTspice.exe, to open a .raw there.</summary>
        public Func<string> LtspicePath = () => null;

        public const int MaxOverlay = 50;
        static readonly Color[] Palette =
        {
            Win98Chart.Navy, Color.FromArgb(200, 70, 0), Win98Chart.Green, Win98Chart.Purple, Win98Chart.Teal,
            Win98Chart.Maroon, Win98Chart.Olive, Win98Chart.Blue, Win98Chart.Fuchsia,
        };

        public WaveformView()
        {
            BackColor = Win98.Face;
            Dock = DockStyle.Fill;

            var left = new Panel { Dock = DockStyle.Left, Width = 250, BackColor = Win98.Face, Padding = new Padding(0, 0, 6, 0) };
            var lblSets = new Label { Text = "Cases and conditions (tick to overlay):", Location = new Point(2, 2), AutoSize = true };
            lbSets = new CheckedListBox { CheckOnClick = true, Font = Win98.Font, IntegralHeight = false, BorderStyle = BorderStyle.Fixed3D, HorizontalScrollbar = true };
            lbSets.ItemCheck += (s, e) => { if (!filling) BeginInvoke(new Action(() => { FillRuns(); FillSignals(); Draw(); })); };
            var lblRuns = new Label { Text = "&Runs:", AutoSize = true };
            cbRuns = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Win98.Font };
            cbRuns.SelectedIndexChanged += (s, e) => { if (!filling) Draw(); };
            var lblSig = new Label { Text = "&Signals in the .raw (double-click or Add: into the pane chosen; Ctrl/Shift for several):", AutoSize = false, Height = 28 };
            txtFilter = new TextBox { Font = Win98.Font, BorderStyle = BorderStyle.Fixed3D };
            txtFilter.TextChanged += (s, e) => FillSignals();
            lbSignals = new ListBox { Font = Win98.Font, IntegralHeight = false, BorderStyle = BorderStyle.Fixed3D, SelectionMode = SelectionMode.MultiExtended };
            lbSignals.DoubleClick += (s, e) => AddSignals(false);
            var btnAdd = new Win98Button { Text = "&Add", Width = 58 };
            var btnNew = new Win98Button { Text = "New &Pane", Width = 72 };
            var btnRemove = new Win98Button { Text = "Re&move Pane", Width = 86 };
            var btnClear = new Win98Button { Text = "&Clear Pane", Width = 76 };
            var btnLt = new Win98Button { Text = "Open in &LTspice", Width = 110 };
            var btnSave = new Win98Button { Text = "Sa&ve Image...", Width = 100 };
            btnAdd.Click += (s, e) => AddSignals(false);
            btnNew.Click += (s, e) => AddSignals(true);
            var btnTake = new Win98Button { Text = "Remove &Signal", Width = 100 };
            btnTake.Click += (s, e) =>
            {
                if (active >= panes.Count) return;
                foreach (var sig in lbSignals.SelectedItems.Cast<string>()) panes[active].Signals.RemoveAll(x => x.Equals(sig, StringComparison.OrdinalIgnoreCase));
                Draw();
            };
            var btnCur = new Win98Button { Text = "Clear C&ursors", Width = 100 };
            btnCur.Click += (s, e) => { curA = curB = double.NaN; ShowCursors(); };
            btnRemove.Click += (s, e) => { if (panes.Count > 1 && active < panes.Count) { panes.RemoveAt(active); active = Math.Max(0, active - 1); Draw(); } };
            btnClear.Click += (s, e) => { if (active < panes.Count) { panes[active].Signals.Clear(); Draw(); } };
            btnLt.Click += (s, e) => OpenInLtspice();
            btnSave.Click += (s, e) => SaveImage();
            left.Controls.AddRange(new Control[] { lblSets, lbSets, lblRuns, cbRuns, lblSig, txtFilter, lbSignals, btnAdd, btnNew, btnRemove, btnClear, btnLt, btnSave, btnTake, btnCur });
            left.Resize += (s, e) =>
            {
                int w = left.Width - 10, h = left.Height;
                lbSets.SetBounds(2, 18, w, Math.Max(60, h / 4));
                lblRuns.Location = new Point(2, lbSets.Bottom + 9);
                cbRuns.SetBounds(42, lbSets.Bottom + 6, w - 40, 21);
                lblSig.SetBounds(2, cbRuns.Bottom + 8, w, 28);
                txtFilter.SetBounds(2, lblSig.Bottom, w, 20);
                int bottomButtons = 4 * 27 + 4;
                lbSignals.SetBounds(2, txtFilter.Bottom + 4, w, Math.Max(40, h - txtFilter.Bottom - 8 - bottomButtons));
                int by = lbSignals.Bottom + 6;
                btnAdd.Location = new Point(2, by); btnNew.Location = new Point(btnAdd.Right + 4, by); btnClear.Location = new Point(btnNew.Right + 4, by);
                btnRemove.Location = new Point(2, by + 27); btnSave.Location = new Point(btnRemove.Right + 4, by + 27);
                btnTake.Location = new Point(2, by + 54); btnCur.Location = new Point(btnTake.Right + 4, by + 54);
                btnLt.Location = new Point(2, by + 81);
            };

            stack = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Win98.Face };
            stack.Resize += (s, e) => LayoutPanes();
            lblInfo = new Label { Dock = DockStyle.Bottom, Height = 34, ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false };
            lblCursor = new Label { Dock = DockStyle.Bottom, Height = 0, BackColor = Win98.InfoBack, BorderStyle = BorderStyle.FixedSingle, Font = Win98.MonoFont, UseMnemonic = false, Padding = new Padding(4, 2, 4, 2), Visible = false };

            Controls.Add(stack);
            Controls.Add(lblCursor);
            Controls.Add(lblInfo);
            Controls.Add(left);
        }

        // ------------------------------------------------------------------ what is shown

        List<RunSet> Checked => lbSets.CheckedIndices.Cast<int>().Select(i => data.Sets.Where(s => s.Dir != null).ElementAt(i)).ToList();

        static string SetLabel(RunSet s) => $"{s.Variant.Label} · {s.Condition.Name}";

        /// <summary>The signal a stimulus drives in a case (V(in)), or null.</summary>
        static string StimulusSignal(RunSet s)
        {
            var v = s.Variant;
            if (v.Stimulus == null) return null;
            return $"V({(v.Stimulus.IsFault ? v.MapNet(v.Stimulus.From) : v.MapNet(v.Stimulus.Net))})";
        }

        /// <summary>Shows a study: <paramref name="focus"/> ticked (or the first case), panes for its stimulus and the given signals.</summary>
        public void ShowStudy(StudyRunData d, RunSet focus, IEnumerable<string> signals, string criterion = null)
        {
            data = d;
            focusCriterion = criterion;
            filling = true;
            lbSets.Items.Clear();
            var sets = d.Sets.Where(s => s.Dir != null).ToList();
            foreach (var s in sets) lbSets.Items.Add(SetLabel(s), s == focus || (focus == null && s == sets.FirstOrDefault()));
            filling = false;
            panes.Clear();
            var f = focus ?? sets.FirstOrDefault();
            var stim = f == null ? null : StimulusSignal(f);
            if (stim != null) NewPane().Signals.Add(stim);
            foreach (var s in (signals ?? new string[0]).Where(x => x != null && !x.Equals(stim, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
                NewPane().Signals.Add(s);
            if (panes.Count == 0) NewPane();
            active = panes.Count - 1;
            FillRuns();
            FillSignals();
            Draw();
        }

        Pane NewPane() { var p = new Pane(); panes.Add(p); return p; }

        /// <summary>What tells a case apart from the others overlaid: its condition, its case, or both.</summary>
        static string Distinct(List<RunSet> sets, RunSet s)
        {
            bool sameCase = sets.All(x => x.Variant.Label == s.Variant.Label), sameCond = sets.All(x => x.Condition.Name == s.Condition.Name);
            return sameCase ? s.Condition.Name : sameCond ? s.Variant.Label : SetLabel(s);
        }

        void AddSignals(bool newPane)
        {
            var sel = lbSignals.SelectedItems.Cast<string>().ToList();
            if (sel.Count == 0) return;
            if (newPane || panes.Count == 0) { NewPane(); active = panes.Count - 1; }
            var p = panes[Math.Min(active, panes.Count - 1)];
            foreach (var sig in sel) if (!p.Signals.Contains(sig, StringComparer.OrdinalIgnoreCase)) p.Signals.Add(sig);
            Draw();
        }

        void AddSignal(string sig, bool newPane)
        {
            if (string.IsNullOrEmpty(sig)) return;
            if (newPane || panes.Count == 0) { NewPane(); active = panes.Count - 1; }
            var p = panes[Math.Min(active, panes.Count - 1)];
            if (!p.Signals.Contains(sig, StringComparer.OrdinalIgnoreCase)) p.Signals.Add(sig);
            Draw();
        }

        void FillRuns()
        {
            filling = true;
            string keep = cbRuns.SelectedItem as string;
            cbRuns.Items.Clear();
            var sets = data == null ? new List<RunSet>() : Checked;
            int maxRuns = sets.Select(s => { try { return s.Results.Rows.Count(r => r.Status == RunStatus.Ok); } catch { return 0; } }).DefaultIfEmpty(0).Max();
            if (maxRuns <= 1) cbRuns.Items.Add("Run 1");
            else
            {
                cbRuns.Items.Add("Worst run (closest to a limit)");
                cbRuns.Items.Add($"All runs" + (maxRuns > MaxOverlay ? $" (first {MaxOverlay})" : ""));
                for (int i = 1; i <= Math.Min(maxRuns, 500); i++) cbRuns.Items.Add("Run " + i);
            }
            int k = keep == null ? -1 : cbRuns.Items.IndexOf(keep);
            cbRuns.SelectedIndex = k >= 0 ? k : 0;
            filling = false;
        }

        List<string> NamesIn(RunSet s)
        {
            if (namesByDir.TryGetValue(s.Dir, out var n)) return n;
            n = new List<string>();
            var chunk = RawFile.Chunks(s.Dir).OrderBy(c => c.Id).FirstOrDefault();
            if (chunk != null) try { n = RawFile.Open(chunk.Path).Names.Skip(1).ToList(); } catch { }
            namesByDir[s.Dir] = n;
            return n;
        }

        void FillSignals()
        {
            if (data == null) return;
            string f = txtFilter.Text.Trim();
            var all = Checked.SelectMany(NamesIn).Distinct(StringComparer.OrdinalIgnoreCase)
                             .Where(x => f.Length == 0 || x.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                             .OrderBy(x => x.StartsWith("V(", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            lbSignals.BeginUpdate();
            lbSignals.Items.Clear();
            foreach (var s in all) lbSignals.Items.Add(s);
            lbSignals.EndUpdate();
        }

        // ------------------------------------------------------------------ data

        sealed class Trace
        {
            public RunSet Set;
            public ResultSet.Row Row;
            public double[] X;
            public Dictionary<string, double[]> Y = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        }

        static double Margin(RunSet s, ResultSet.Row row)
        {
            var rs = s.Results;
            double m = double.NaN;
            foreach (var cr in s.Criteria)
            {
                if (!double.IsNaN(cr.Lo) && rs.Col(cr.MinMeas) >= 0) { double v = rs.Get(row, cr.MinMeas); if (!double.IsNaN(v)) m = double.IsNaN(m) ? v - cr.Lo : Math.Min(m, v - cr.Lo); }
                if (!double.IsNaN(cr.Hi) && rs.Col(cr.MaxMeas) >= 0) { double v = rs.Get(row, cr.MaxMeas); if (!double.IsNaN(v)) m = double.IsNaN(m) ? cr.Hi - v : Math.Min(m, cr.Hi - v); }
            }
            return m;
        }

        List<ResultSet.Row> RowsOf(RunSet s)
        {
            var rows = s.Results.Rows.Where(r => r.Status == RunStatus.Ok).ToList();
            string pick = cbRuns.SelectedItem as string ?? "Run 1";
            if (pick.StartsWith("Worst")) { var w = rows.Where(r => !double.IsNaN(Margin(s, r))).OrderBy(r => Margin(s, r)).FirstOrDefault() ?? rows.FirstOrDefault(); return w == null ? rows.Take(0).ToList() : new List<ResultSet.Row> { w }; }
            if (pick.StartsWith("All")) return rows.Take(MaxOverlay).ToList();
            int n = int.TryParse(pick.Substring(4), out var k) ? k : 1;
            return rows.Where(r => r.Run == n).Take(1).ToList();
        }

        List<Trace> Load(List<RunSet> sets, List<string> signals, List<string> problems)
        {
            var traces = new List<Trace>();
            foreach (var s in sets)
            {
                List<ResultSet.Row> rows;
                try { rows = RowsOf(s); } catch (Exception ex) { problems.Add($"{SetLabel(s)}: {ex.Message}"); continue; }
                var chunks = RawFile.Chunks(s.Dir);
                if (chunks.Count == 0) { problems.Add($"{SetLabel(s)}: no .raw file was kept."); continue; }
                foreach (var g in rows.Select(r => new { r, c = RawFile.ChunkOf(chunks, r.Run, r.Temp) }).Where(x => x.c != null).GroupBy(x => x.c.Path))
                {
                    try
                    {
                        var raw = RawFile.Open(g.Key);
                        var idx = signals.Select(raw.Find).ToList();
                        int first = g.First().c.First;
                        var steps = raw.ReadSteps(idx, g.Select(x => x.r.Run - first).Distinct().ToList());
                        foreach (var x in g)
                        {
                            if (!steps.TryGetValue(x.r.Run - first, out var st)) continue;
                            var tr = new Trace { Set = s, Row = x.r, X = st.X };
                            for (int k = 0; k < signals.Count; k++) if (st.Y[k] != null) tr.Y[signals[k]] = st.Y[k];
                            traces.Add(tr);
                        }
                    }
                    catch (Exception ex) { problems.Add($"{Path.GetFileName(g.Key)}: {ex.Message}"); }
                }
            }
            return traces;
        }

        // ------------------------------------------------------------------ drawing

        void Draw()
        {
            if (data == null) return;
            Cursor = Cursors.WaitCursor;
            try { DrawCore(); }
            finally { Cursor = Cursors.Default; }
        }

        void DrawCore()
        {
            foreach (var p in panes) if (p.Chart != null) { stack.Controls.Remove(p.Chart); p.Chart.Dispose(); p.Chart = null; }
            var sets = Checked;
            var signals = panes.SelectMany(p => p.Signals).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var problems = new List<string>();
            var traces = sets.Count == 0 || signals.Count == 0 ? new List<Trace>() : Load(sets, signals, problems);
            bool manySets = sets.Count > 1;
            double stop = traces.Where(t => t.X.Length > 0).Select(t => t.X[t.X.Length - 1]).DefaultIfEmpty(0).Max();
            var focus = sets.FirstOrDefault();

            for (int pi = 0; pi < panes.Count; pi++)
            {
                var pane = panes[pi];
                int paneIndex = pi;
                var ch = new Win98Chart { Title = pane.Signals.Count == 0 ? "(empty pane: double-click a signal on the left)" : string.Join(", ", pane.Signals), YLabel = "" };
                ch.TitleColor = pi == active ? Win98.Navy : Color.FromArgb(80, 80, 80);
                ch.MouseClick += (s, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    double t = ((Win98Chart)s).DataX(e.Location);
                    if (double.IsNaN(t)) return;
                    if ((ModifierKeys & Keys.Shift) != 0) curB = t; else curA = t;
                    ShowCursors();
                };
                ch.MouseDown += (s, e) => { if (active != paneIndex) { active = paneIndex; foreach (var q in panes) if (q.Chart != null) { q.Chart.TitleColor = q == panes[active] ? Win98.Navy : Color.FromArgb(80, 80, 80); q.Chart.Invalidate(); } } };
                pane.Chart = ch;
                stack.Controls.Add(ch);
                if (pane.Signals.Count == 0) continue;
                ch.YLabel = pane.Signals.All(x => x.StartsWith("I", StringComparison.OrdinalIgnoreCase)) ? "A" : pane.Signals.All(x => x.StartsWith("V", StringComparison.OrdinalIgnoreCase)) ? "V" : "";

                // the criteria on these signals (of the first case ticked): their window and limits
                if (focus != null)
                    foreach (var cr in focus.Criteria.Where(c => pane.Signals.Contains(c.Signal, StringComparer.OrdinalIgnoreCase)))
                        if (cr.Window(focus.Case.Events, out double t1, out double t2))
                        {
                            // the criterion opened is shaded and drawn strong; the others are only their limit lines, lighter
                            bool mainOne = focusCriterion == null ? focus.Criteria.Count(c => pane.Signals.Contains(c.Signal, StringComparer.OrdinalIgnoreCase)) == 1 : cr.Name == focusCriterion;
                            if (mainOne) ch.Add(new ChartSeries { Kind = SeriesKind.Span, Label = $"{cr.Name}: {cr.RuleText()}", Color = Color.FromArgb(255, 255, 196), X = new[] { t1, t2 } });
                            bool firstLine = true;
                            foreach (var l in new[] { cr.Lo, cr.Hi }.Where(v => !double.IsNaN(v)))
                            {
                                ch.Add(new ChartSeries { Kind = SeriesKind.Line, Label = !mainOne && firstLine ? $"{cr.Name}: {cr.RuleText()}" : null, Color = mainOne ? Win98Chart.Maroon : Color.FromArgb(200, 140, 140), Dashed = true, Size = mainOne ? 2 : 1, X = new[] { t1, t2 }, Y = new[] { l, l } });
                                firstLine = false;
                            }
                        }
                // a supply waveform: its programmed shape under the signal it drives, and the event lines
                if (focus?.Case?.Wave != null && pane.Signals.Contains(StimulusSignal(focus), StringComparer.OrdinalIgnoreCase))
                    ch.Add(new ChartSeries { Kind = SeriesKind.Line, Label = "programmed", Color = Win98Chart.Silver, Size = 5, X = focus.Case.Wave.Select(p => p[0]).ToArray(), Y = focus.Case.Wave.Select(p => p[1]).ToArray() });
                if (focus?.Case != null && focus.Variant.Stimulus != null && focus.Case.Events.End > focus.Case.Events.Start)
                    ch.Add(new ChartSeries { Kind = SeriesKind.VLine, Label = "event start / end", InLegend = pi == 0, Color = Color.Gray, Dashed = true, X = new[] { focus.Case.Events.Start, focus.Case.Events.End } });

                for (int si = 0; si < pane.Signals.Count; si++)
                {
                    string sig = pane.Signals[si];
                    for (int k = 0; k < sets.Count; k++)
                    {
                        var mine = traces.Where(t => t.Set == sets[k] && t.Y.ContainsKey(sig)).ToList();
                        if (mine.Count == 0) { if (!problems.Any(p => p.Contains(sig))) problems.Add($"{sig} is not in the .raw of {SetLabel(sets[k])}."); continue; }
                        var col = Palette[(manySets ? k : si) % Palette.Length];
                        bool first = true;
                        foreach (var tr in mine)
                        {
                            string label = first ? sig + (manySets ? " · " + Distinct(sets, sets[k]) : "") + (mine.Count > 1 ? $" ({mine.Count} runs)" : "") : null;
                            ch.Add(new ChartSeries { Kind = SeriesKind.Line, Label = label, Color = col, Size = mine.Count > 1 ? 3 : 2, X = tr.X, Y = tr.Y[sig] });
                            first = false;
                        }
                    }
                }
            }
            if (panes.Count > 0 && panes.Last().Chart != null) panes.Last().Chart.XLabel = "time (s)";
            foreach (var p in panes)
                if (p.Chart != null)
                {
                    p.Chart.XViewChanged += (s, e) => Follow((Win98Chart)s, stop);
                    if (stop > 0) p.Chart.FixedX = new KeyValuePair<double, double>(0, stop);
                }
            lastTraces = traces;
            ShowCursors();
            LayoutPanes();
            lblInfo.Text = (problems.Count > 0 ? "! " + string.Join("   ! ", problems.Distinct().Take(3)) + "\n" : "") +
                           "Click a pane to choose it and place cursor A there (Shift+click: cursor B). Drag to zoom (every pane follows), double-click a pane to reset.";
            lblInfo.ForeColor = problems.Count > 0 ? Color.FromArgb(140, 0, 0) : Win98.Shadow;
        }

        /// <summary>The cursors on every pane, and what each signal reads there (and the difference B - A).</summary>
        void ShowCursors()
        {
            foreach (var p in panes.Where(x => x.Chart != null))
            {
                p.Chart.Series.RemoveAll(x => x.Label == "cursor A" || x.Label == "cursor B");
                if (!double.IsNaN(curA)) p.Chart.Series.Add(new ChartSeries { Kind = SeriesKind.VLine, Label = "cursor A", InLegend = false, Color = Color.FromArgb(200, 0, 0), X = new[] { curA } });
                if (!double.IsNaN(curB)) p.Chart.Series.Add(new ChartSeries { Kind = SeriesKind.VLine, Label = "cursor B", InLegend = false, Color = Color.FromArgb(0, 90, 200), Dashed = true, X = new[] { curB } });
                p.Chart.Invalidate();
            }
            if (double.IsNaN(curA) && double.IsNaN(curB)) { lblCursor.Visible = false; return; }
            var sets = data == null ? new List<RunSet>() : Checked;
            var lines = new List<string>();
            string E(double v) => double.IsNaN(v) ? "-" : ErrorBudget.Eng(v);
            lines.Add($"{"",-28} {"A at " + E(curA) + "s",16} {"B at " + E(curB) + "s",16} {"B - A",12}" + (!double.IsNaN(curA) && !double.IsNaN(curB) ? $"   Δt = {E(curB - curA)}s" : ""));
            foreach (var p in panes)
                foreach (var sig in p.Signals)
                    foreach (var tr in lastTraces.Where(t => t.Y.ContainsKey(sig)).GroupBy(t => t.Set).Select(g => g.First()))
                    {
                        double a = At(tr, sig, curA), b = At(tr, sig, curB);
                        string name = sig + (sets.Count > 1 ? " · " + Distinct(sets, tr.Set) : "");
                        if (name.Length > 28) name = name.Substring(0, 27) + "…";
                        lines.Add($"{name,-28} {E(a),16} {E(b),16} {(double.IsNaN(a) || double.IsNaN(b) ? "" : E(b - a)),12}");
                    }
            lblCursor.Text = string.Join("\n", lines.Take(10));
            lblCursor.Height = Math.Min(10, lines.Count) * 13 + 8;
            lblCursor.Visible = true;
        }

        /// <summary>A signal's value at a time, between the two samples around it.</summary>
        static double At(Trace tr, string sig, double t)
        {
            if (double.IsNaN(t) || !tr.Y.TryGetValue(sig, out var y) || tr.X.Length == 0) return double.NaN;
            int i = Array.BinarySearch(tr.X, t);
            if (i >= 0) return y[i];
            i = ~i;
            if (i == 0) return y[0];
            if (i >= tr.X.Length) return y[y.Length - 1];
            double f = (t - tr.X[i - 1]) / (tr.X[i] - tr.X[i - 1]);
            return y[i - 1] + f * (y[i] - y[i - 1]);
        }

        void Follow(Win98Chart from, double stop)
        {
            if (linking) return;
            linking = true;
            try
            {
                foreach (var p in panes.Where(x => x.Chart != null && x.Chart != from))
                {
                    if (from.IsZoomed) p.Chart.LinkX(from.ViewX0, from.ViewX1);
                    else { p.Chart.LinkX(null, null); if (stop > 0) p.Chart.FixedX = new KeyValuePair<double, double>(0, stop); }
                }
                if (!from.IsZoomed && stop > 0) from.FixedX = new KeyValuePair<double, double>(0, stop);
            }
            finally { linking = false; }
        }

        void LayoutPanes()
        {
            var charts = panes.Where(p => p.Chart != null).Select(p => p.Chart).ToList();
            if (charts.Count == 0) return;
            int avail = stack.ClientSize.Height, each = Math.Max(170, (avail - 4) / charts.Count);
            int w = stack.ClientSize.Width - (each * charts.Count > avail ? SystemInformation.VerticalScrollBarWidth : 0);
            int y = -stack.VerticalScroll.Value;
            foreach (var c in charts) { c.SetBounds(0, y, Math.Max(100, w), each - 2); y += each; }
        }

        // ------------------------------------------------------------------ actions

        void OpenInLtspice()
        {
            var s = Checked.FirstOrDefault();
            if (s == null) return;
            var row = RowsOf(s).FirstOrDefault();
            var chunk = row == null ? null : RawFile.ChunkOf(RawFile.Chunks(s.Dir), row.Run, row.Temp);
            string lt = LtspicePath();
            if (chunk == null || string.IsNullOrEmpty(lt) || !File.Exists(lt)) { Win98MessageBox.Show(FindForm(), "No .raw file or LTspice.exe to open it with.", "SPICA", MessageBoxButtons.OK, Win98Icon.Information); return; }
            try { Process.Start(lt, "\"" + chunk.Path + "\""); }
            catch (Exception ex) { Win98MessageBox.Show(FindForm(), "LTspice could not be started:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); }
        }

        /// <summary>Every pane, one under the other, as one picture.</summary>
        public Bitmap Snapshot()
        {
            var charts = panes.Where(p => p.Chart != null).Select(p => p.Chart).ToList();
            if (charts.Count == 0) return null;
            int w = Math.Max(800, charts[0].Width);
            var parts = charts.Select(c => c.ToBitmap(w, Math.Max(200, c.Height))).ToList();
            var bmp = new Bitmap(w, parts.Sum(p => p.Height), PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp)) { int y = 0; foreach (var p in parts) { g.DrawImage(p, 0, y); y += p.Height; p.Dispose(); } }
            return bmp;
        }

        public int PaneCount => panes.Count;
        public int SeriesCount => panes.Where(p => p.Chart != null).Sum(p => p.Chart.Series.Count(s => s.Kind == SeriesKind.Line && !s.Dashed && s.Label != "programmed"));

        void SaveImage()
        {
            using (var d = new SaveFileDialog { Title = "Save Waveforms", Filter = "PNG image (*.png)|*.png", FileName = "waveforms.png", AutoUpgradeEnabled = false })
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                try { using (var b = Snapshot()) b?.Save(d.FileName, ImageFormat.Png); }
                catch (Exception ex) { Win98MessageBox.Show(FindForm(), "Could not save the image:\n" + ex.Message, "SPICA", MessageBoxButtons.OK, Win98Icon.Error); }
            }
        }
    }
}
