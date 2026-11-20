using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Spica.Core;
using Spica.Core.Graph;

namespace Spica.UI
{
    /// <summary>A study that ran: its campaigns, and what did not finish.</summary>
    public sealed class StudyRunData
    {
        public string Study = "";
        public DateTime Ended = DateTime.Now;
        public List<RunSet> Sets = new List<RunSet>();
        public List<string> Problems = new List<string>();
        /// <summary>The signals its Simulate blocks were asked to save (the first look plots them).</summary>
        public List<string> Saved = new List<string>();
    }

    /// <summary>
    /// Results > a study: its waveforms, in the manner of LTspice.
    /// - Left: the analyses (tick several to overlay them), the runs, and the signals of the .raw files: a click on a
    ///   signal plots it in the pane chosen (navy frame; a click on a pane chooses it).
    /// - Right: the panes, on one time axis. A right-click on a pane adds a trace by expression (2*V(out), V(a,b)),
    ///   panes above or below, deletes it; a click on a trace's name puts a cursor on it (1, then 2, then none), a
    ///   right-click on the name moves the cursors, edits or deletes the trace. The cursors are dragged.
    /// - Bottom: for every trace, its value at the cursors, and its average, RMS, min, max and peak-to-peak between
    ///   the cursors (or over the time shown).
    /// </summary>
    public sealed class WaveformView : Panel
    {
        sealed class Pane
        {
            public readonly List<string> Exprs = new List<string>();
            public WavePlot Plot;
        }

        /// <summary>A cursor: on a trace (by its pane and name), at a time.</summary>
        sealed class CursorAt
        {
            public string Name;
            public Color Color;
            public int Pane = -1;
            public string Trace;
            public double X = double.NaN;
            public bool On => Pane >= 0;
        }

        readonly CheckedListBox lbSets;
        readonly ComboBox cbRuns;
        readonly TextBox txtFilter;
        readonly ListBox lbSignals;
        readonly Panel stack;
        readonly Label lblMeasure;
        readonly Win98ListView lvMeasure;
        readonly List<Pane> panes = new List<Pane>();
        readonly CursorAt cur1 = new CursorAt { Name = "1", Color = Color.FromArgb(40, 40, 40) };
        readonly CursorAt cur2 = new CursorAt { Name = "2", Color = Color.FromArgb(110, 110, 110) };
        int active;
        bool filling, linking;
        StudyRunData data;
        double stop;                                    // the end of the simulation: the time axis runs 0 .. stop
        (double A, double B)? shownX;                    // the time range zoomed to, kept when the panes are drawn again
        List<Trace> traces = new List<Trace>();
        readonly Dictionary<string, List<string>> namesByDir = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> problems = new List<string>();
        readonly List<Color> measureColors = new List<Color>();      // the colour of each row's trace name

        public const int MaxOverlay = 50;
        // what is dragged: a signal from the list, or a trace's name from a pane ("pane|expression")
        const string SignalPrefix = "spica-signal:", TracePrefix = "spica-trace:";
        bool dragCancelled;
        static Color[] Palette => WavePlot.Palette;

        public WaveformView()
        {
            BackColor = Win98.Face;
            Dock = DockStyle.Fill;

            var left = new Panel { Dock = DockStyle.Left, Width = 250, BackColor = Win98.Face, Padding = new Padding(0, 0, 6, 0) };
            var lblSets = new Label { Text = "Analyses (tick several to overlay):", Location = new Point(2, 2), AutoSize = true };
            lbSets = new CheckedListBox { CheckOnClick = true, Font = Win98.Font, IntegralHeight = false, BorderStyle = BorderStyle.Fixed3D, HorizontalScrollbar = true };
            lbSets.ItemCheck += (s, e) => { if (!filling) BeginInvoke(new Action(() => { FillRuns(); FillSignals(); Draw(); })); };
            var lblRuns = new Label { Text = "&Runs:", AutoSize = true };
            cbRuns = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Win98.Font };
            cbRuns.SelectedIndexChanged += (s, e) => { if (!filling) Draw(); };
            var lblSig = new Label { Text = "&Signals: click one to plot it in the pane chosen (navy frame), or drag it onto a pane.", AutoSize = false, Height = 28 };
            txtFilter = new TextBox { Font = Win98.Font, BorderStyle = BorderStyle.Fixed3D };
            txtFilter.TextChanged += (s, e) => FillSignals();
            lbSignals = new ListBox { Font = Win98.Font, IntegralHeight = false, BorderStyle = BorderStyle.Fixed3D };
            // a click plots the signal in the pane chosen; a drag takes it to the pane it is dropped on
            int downAt = -1;
            Point downPoint = Point.Empty;
            bool listDragged = false;
            lbSignals.MouseDown += (s, e) => { downAt = e.Button == MouseButtons.Left ? lbSignals.IndexFromPoint(e.Location) : -1; downPoint = e.Location; listDragged = false; };
            lbSignals.MouseMove += (s, e) =>
            {
                if (downAt < 0 || e.Button != MouseButtons.Left || (Math.Abs(e.X - downPoint.X) < 5 && Math.Abs(e.Y - downPoint.Y) < 5)) return;
                string sig = (string)lbSignals.Items[downAt];
                downAt = -1;
                listDragged = true;
                lbSignals.DoDragDrop(SignalPrefix + sig, DragDropEffects.Copy);
            };
            lbSignals.MouseClick += (s, e) =>
            {
                if (listDragged) return;
                int i = lbSignals.IndexFromPoint(e.Location);
                if (i >= 0) Add((string)lbSignals.Items[i]);
            };
            lbSignals.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && lbSignals.SelectedItem is string sig) Add(sig); };
            var lblHow = new Label
            {
                AutoSize = false, Height = 70, ForeColor = Win98.Shadow, UseMnemonic = false,
                Text = "Right-click a pane: a trace by expression (2*V(out), V(a,b)), panes above and below.\nClick a trace's name: cursor 1, then 2. Drag a name to another pane, or out to delete it.",
            };
            left.Controls.AddRange(new Control[] { lblSets, lbSets, lblRuns, cbRuns, lblSig, txtFilter, lbSignals, lblHow });
            left.Resize += (s, e) =>
            {
                int w = left.Width - 10, h = left.Height;
                lbSets.SetBounds(2, 18, w, Math.Max(60, h / 5));
                lblRuns.Location = new Point(2, lbSets.Bottom + 9);
                cbRuns.SetBounds(42, lbSets.Bottom + 6, w - 40, 21);
                lblSig.SetBounds(2, cbRuns.Bottom + 8, w, 28);
                txtFilter.SetBounds(2, lblSig.Bottom, w, 20);
                lblHow.SetBounds(2, h - 70, w, 70);
                lbSignals.SetBounds(2, txtFilter.Bottom + 4, w, Math.Max(40, lblHow.Top - 4 - txtFilter.Bottom - 4));
            };

            stack = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Win98.Face };
            stack.Resize += (s, e) => LayoutPanes();

            // the measurements of the traces: at the cursors, and over the window between them (or the time shown)
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 132, BackColor = Win98.Face, Padding = new Padding(0, 4, 0, 0) };
            lblMeasure = new Label { Dock = DockStyle.Top, Height = 18, UseMnemonic = false, BackColor = Win98.Face };
            lvMeasure = new Win98ListView { MultiSelect = false };
            lvMeasure.CellColor = (row, col) => col == 0 && row < measureColors.Count ? measureColors[row] : (Color?)null;
            foreach (var (t, w) in new[] { ("Trace", 200), ("Cursor 1", 80), ("Cursor 2", 80), ("2 - 1", 80), ("Average", 80), ("RMS", 80), ("Min", 80), ("Max", 80), ("Peak-peak", 80) })
                lvMeasure.Columns.Add(t, w, t == "Trace" ? HorizontalAlignment.Left : HorizontalAlignment.Right);
            bottom.Controls.Add(new Win98Well(lvMeasure) { Dock = DockStyle.Fill });
            bottom.Controls.Add(lblMeasure);

            Controls.Add(stack);
            Controls.Add(bottom);
            Controls.Add(left);
        }

        // ------------------------------------------------------------------ what is shown

        List<RunSet> Checked => data == null ? new List<RunSet>() : lbSets.CheckedIndices.Cast<int>().Select(i => data.Sets.Where(s => s.Dir != null).ElementAt(i)).ToList();

        /// <summary>
        /// Shows a study's campaigns, <paramref name="focus"/> ticked (or the first). The panes and their traces stay as
        /// they were, so a study run again shows the same plots.
        /// </summary>
        public void ShowStudy(StudyRunData d, RunSet focus = null)
        {
            data = d;
            filling = true;
            lbSets.Items.Clear();
            var sets = d.Sets.Where(s => s.Dir != null).ToList();
            foreach (var s in sets) lbSets.Items.Add(s.Label, s == focus || (focus == null && s == sets.FirstOrDefault()));
            filling = false;
            if (panes.Count == 0) panes.Add(new Pane());
            active = Math.Min(active, panes.Count - 1);
            FillRuns();
            FillSignals();
            Draw();
        }

        /// <summary>Puts signals on the panes, one pane each (the first look at a study).</summary>
        public void Plot(IEnumerable<string> signals)
        {
            panes.Clear();
            foreach (var s in signals) { var p = new Pane(); p.Exprs.Add(s); panes.Add(p); }
            if (panes.Count == 0) panes.Add(new Pane());
            active = panes.Count - 1;
            cur1.Pane = cur2.Pane = -1;
            Draw();
        }

        /// <summary>The traces the panes show (signals and expressions).</summary>
        public List<string> Plotted => panes.SelectMany(p => p.Exprs).ToList();

        /// <summary>The curves drawn (one per run).</summary>
        public int SeriesCount => panes.Where(p => p.Plot != null).Sum(p => p.Plot.Traces.Count);

        /// <summary>A trace into the pane chosen.</summary>
        public void Add(string expr)
        {
            expr = (expr ?? "").Trim();
            if (expr.Length == 0) return;
            if (panes.Count == 0) panes.Add(new Pane());
            var p = panes[Math.Min(active, panes.Count - 1)];
            if (!p.Exprs.Contains(expr, StringComparer.OrdinalIgnoreCase)) p.Exprs.Add(expr);
            Draw();
        }

        void FillRuns()
        {
            filling = true;
            string keep = cbRuns.SelectedItem as string;
            cbRuns.Items.Clear();
            int maxRuns = Checked.Select(s => { try { return s.Results.Rows.Count(r => r.Status == RunStatus.Ok); } catch { return 0; } }).DefaultIfEmpty(0).Max();
            if (maxRuns <= 1) cbRuns.Items.Add("Run 1");
            else
            {
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

        /// <summary>The rows shown: one run (at every temperature it ran at), or the first runs of each temperature.</summary>
        List<ResultSet.Row> RowsOf(RunSet s)
        {
            var rows = s.Results.Rows.Where(r => r.Status == RunStatus.Ok).ToList();
            string pick = cbRuns.SelectedItem as string ?? "Run 1";
            if (pick.StartsWith("All")) return rows.GroupBy(r => r.Temp).SelectMany(g => g.Take(Math.Max(1, MaxOverlay / Math.Max(1, rows.Select(r => r.Temp).Distinct().Count())))).ToList();
            int n = int.TryParse(pick.Substring(4), out var k) ? k : 1;
            return rows.Where(r => r.Run == n).ToList();
        }

        List<Trace> Load(List<RunSet> sets, List<string> signals)
        {
            var list = new List<Trace>();
            foreach (var s in sets)
            {
                List<ResultSet.Row> rows;
                try { rows = RowsOf(s); } catch (Exception ex) { problems.Add($"{s.Label}: {ex.Message}"); continue; }
                var chunks = RawFile.Chunks(s.Dir);
                if (chunks.Count == 0) { problems.Add($"{s.Label}: no .raw file was kept."); continue; }
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
                            list.Add(tr);
                        }
                    }
                    catch (Exception ex) { problems.Add($"{System.IO.Path.GetFileName(g.Key)}: {ex.Message}"); }
                }
            }
            return list;
        }

        // ------------------------------------------------------------------ expressions

        /// <summary>A trace's expression made computable: the signals it reads, and how to get its value at a point.</summary>
        sealed class Compiled
        {
            public readonly List<string> Needs = new List<string>();
            public bool Direct;                          // the signal itself, no arithmetic
            public Func<Func<string, double>, double> Value;
            public string Error;
        }

        static readonly Regex SignalRef = new Regex(@"\b([VI][A-Za-z0-9]{0,2})\(([^()]*)\)", RegexOptions.IgnoreCase);

        /// <summary>
        /// "V(out)" is the signal itself; anything else is an expression of signals, as in LTspice: 2*V(out),
        /// V(out)-V(in), V(a,b) for V(a)-V(b), I(R1)*V(out), abs(), sqrt(), db(), min(), max()...
        /// </summary>
        static Compiled Compile(string expr)
        {
            var c = new Compiled();
            var m0 = SignalRef.Match(expr.Trim());
            if (m0.Success && m0.Length == expr.Trim().Length && !m0.Groups[2].Value.Contains(","))
            {
                string sig = m0.Value;
                c.Needs.Add(sig);
                c.Direct = true;
                c.Value = get => get(sig);
                return c;
            }
            var names = new List<string>();
            string Var(string sig) { int i = names.FindIndex(x => x.Equals(sig, StringComparison.OrdinalIgnoreCase)); if (i < 0) { names.Add(sig); i = names.Count - 1; } return "spica_s" + i; }
            string text = SignalRef.Replace(expr, m =>
            {
                var parts = m.Groups[2].Value.Split(',').Select(x => x.Trim()).ToList();
                if (parts.Count == 2 && m.Groups[1].Value.Equals("V", StringComparison.OrdinalIgnoreCase))
                    return $"({Var($"V({parts[0]})")} - {Var($"V({parts[1]})")})";
                return Var(m.Value);
            });
            if (!Expr.TryParse(text, out var e, out var err, true)) { c.Error = $"{expr}: {err}"; return c; }
            c.Needs.AddRange(names);
            c.Value = get =>
            {
                var v = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < names.Count; i++) v["spica_s" + i] = get(names[i]);
                return e.Eval(v);
            };
            return c;
        }

        /// <summary>An expression along a run's time points; null when a signal it reads is not in that .raw.</summary>
        static double[] Evaluate(Compiled c, Trace tr)
        {
            if (c.Value == null || c.Needs.Any(n => !tr.Y.ContainsKey(n))) return null;
            if (c.Direct) return tr.Y[c.Needs[0]];
            var y = new double[tr.X.Length];
            for (int i = 0; i < y.Length; i++) { int k = i; y[i] = c.Value(n => tr.Y[n][k]); }
            return y;
        }

        // ------------------------------------------------------------------ drawing

        void Draw()
        {
            if (data == null) return;
            Cursor = Cursors.WaitCursor;
            try { DrawCore(); }
            finally { Cursor = Cursors.Default; }
        }

        static string TempText(double t) => double.IsNaN(t) ? "" : t.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " °C";

        void DrawCore()
        {
            // every pane drawn before goes (also those taken out of the list): one left over would cover the new ones
            foreach (var c in stack.Controls.OfType<WavePlot>().ToList()) { stack.Controls.Remove(c); c.Dispose(); }
            foreach (var p in panes) p.Plot = null;
            problems.Clear();
            var sets = Checked;
            var compiled = panes.SelectMany(p => p.Exprs).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(x => x, Compile, StringComparer.OrdinalIgnoreCase);
            foreach (var c in compiled.Values.Where(c => c.Error != null)) problems.Add(c.Error);
            var needs = compiled.Values.SelectMany(c => c.Needs).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            traces = sets.Count == 0 || needs.Count == 0 ? new List<Trace>() : Load(sets, needs);
            stop = traces.Where(t => t.X.Length > 0).Select(t => t.X[t.X.Length - 1]).DefaultIfEmpty(stop).Max();
            bool manySets = sets.Count > 1;

            for (int pi = 0; pi < panes.Count; pi++)
            {
                var pane = panes[pi];
                int paneIndex = pi;
                var plot = new WavePlot { EmptyText = "Empty pane: click a signal on the left to plot it here.", Chosen = pi == active && panes.Count > 1 };
                plot.MouseDown += (s, e) => Choose(paneIndex);
                plot.MenuRequested += at => PaneMenu(paneIndex, at);
                plot.LabelClicked += (t, e) => LabelClicked(paneIndex, t, e);
                plot.CursorMoved += c => CursorDragged(c);
                plot.LabelDragged += t => DragTrace(paneIndex, (string)t.Tag);
                plot.AllowDrop = true;
                plot.DragOver += (s, e) =>
                {
                    string d = e.Data.GetData(DataFormats.Text) as string ?? "";
                    e.Effect = d.StartsWith(SignalPrefix) ? DragDropEffects.Copy : d.StartsWith(TracePrefix) ? DragDropEffects.Move : DragDropEffects.None;
                };
                plot.DragDrop += (s, e) => BeginInvoke(new Action(() => Dropped(paneIndex, e.Data.GetData(DataFormats.Text) as string ?? "")));
                plot.XViewChanged += (s, e) => Follow((WavePlot)s);
                pane.Plot = plot;
                stack.Controls.Add(plot);
                int colour = 0;
                foreach (var expr in pane.Exprs)
                {
                    var c = compiled[expr];
                    for (int k = 0; k < sets.Count; k++)
                    {
                        var mine = traces.Where(t => t.Set == sets[k]).Select(t => (t, y: Evaluate(c, t))).Where(x => x.y != null).ToList();
                        if (mine.Count == 0) { if (c.Error == null) problems.Add($"{expr} is not in the .raw of {sets[k].Label}."); continue; }
                        bool temps = mine.Select(x => x.t.Row.Temp).Distinct().Count() > 1;
                        foreach (var g in mine.GroupBy(x => x.t.Row.Temp))
                        {
                            var col = Palette[colour++ % Palette.Length];
                            int n = g.Count();
                            string label = expr + (manySets ? " · " + sets[k].Label : "") + (temps ? " · " + TempText(g.Key) : "") + (n > 1 ? $" ({n} runs)" : "");
                            bool first = true;
                            foreach (var x in g)
                            {
                                plot.Traces.Add(new PlotTrace { Label = first ? label : null, Color = col, X = x.t.X, Y = x.y, Tag = expr });
                                first = false;
                            }
                        }
                    }
                }
                plot.YLabel = pane.Exprs.Count > 0 && pane.Exprs.All(x => x.StartsWith("I(", StringComparison.OrdinalIgnoreCase)) ? "A"
                            : pane.Exprs.Count > 0 && pane.Exprs.All(x => x.StartsWith("V(", StringComparison.OrdinalIgnoreCase)) ? "V" : "";
            }
            if (panes.Count > 0 && panes.Last().Plot != null) panes.Last().Plot.XLabel = "time (s)";
            foreach (var p in panes.Where(x => x.Plot != null))
            {
                if (stop > 0) p.Plot.FixX(0, stop);
                if (shownX.HasValue) p.Plot.LinkX(shownX.Value.A, shownX.Value.B);
            }
            // a cursor on a trace that is gone goes too
            foreach (var c in new[] { cur1, cur2 })
                if (c.On && (c.Pane >= panes.Count || panes[c.Pane].Plot == null || !panes[c.Pane].Plot.Traces.Any(t => t.Label == c.Trace))) c.Pane = -1;
            LayoutPanes();
            ShowCursors();
        }

        /// <summary>A trace's name dragged: dropped on another pane, it moves there; dropped outside every pane, it goes.</summary>
        void DragTrace(int pane, string expr)
        {
            dragCancelled = false;
            var from = panes[pane].Plot;
            QueryContinueDragEventHandler esc = (s, e) => { if (e.EscapePressed) dragCancelled = true; };
            from.QueryContinueDrag += esc;
            var effect = from.DoDragDrop(TracePrefix + pane + "|" + expr, DragDropEffects.Move);
            if (!from.IsDisposed) from.QueryContinueDrag -= esc;
            if (effect == DragDropEffects.None && !dragCancelled && pane < panes.Count) { panes[pane].Exprs.Remove(expr); Draw(); }
        }

        void Dropped(int pane, string data)
        {
            if (pane >= panes.Count) return;
            if (data.StartsWith(SignalPrefix)) { Choose(pane); Add(data.Substring(SignalPrefix.Length)); return; }
            if (!data.StartsWith(TracePrefix)) return;
            var parts = data.Substring(TracePrefix.Length).Split(new[] { '|' }, 2);
            if (parts.Length < 2 || !int.TryParse(parts[0], out int from) || from == pane || from >= panes.Count) return;
            panes[from].Exprs.Remove(parts[1]);
            if (!panes[pane].Exprs.Contains(parts[1], StringComparer.OrdinalIgnoreCase)) panes[pane].Exprs.Add(parts[1]);
            active = pane;
            Draw();
        }

        void Choose(int pane)
        {
            if (active == pane) return;
            active = pane;
            for (int i = 0; i < panes.Count; i++)
                if (panes[i].Plot != null) { panes[i].Plot.Chosen = i == active && panes.Count > 1; panes[i].Plot.Invalidate(); }
        }

        /// <summary>The time range of a pane zoomed, panned or reset: every pane shows it.</summary>
        void Follow(WavePlot from)
        {
            if (linking) return;
            linking = true;
            try
            {
                shownX = from.IsZoomed ? (from.ViewX0, from.ViewX1) : ((double, double)?)null;
                foreach (var p in panes.Where(x => x.Plot != null && x.Plot != from))
                {
                    if (shownX.HasValue) p.Plot.LinkX(shownX.Value.A, shownX.Value.B);
                    else { p.Plot.LinkX(null, null); if (stop > 0) p.Plot.FixX(0, stop); }
                }
                if (!from.IsZoomed && stop > 0) from.FixX(0, stop);
            }
            finally { linking = false; }
            Measure();
        }

        /// <summary>Every pane back to the whole simulation, its values to fit.</summary>
        void ResetZoom()
        {
            shownX = null;
            foreach (var p in panes.Where(x => x.Plot != null)) { p.Plot.LinkX(null, null); if (stop > 0) p.Plot.FixX(0, stop); }
            Measure();
        }

        /// <summary>Every pane in view while they fit at a readable height; past that the stack scrolls, to the pane chosen.</summary>
        void LayoutPanes()
        {
            var plots = panes.Where(p => p.Plot != null).Select(p => p.Plot).ToList();
            if (plots.Count == 0) return;
            int avail = stack.ClientSize.Height, each = Math.Max(150, (avail - 4) / plots.Count);
            int w = stack.ClientSize.Width - (each * plots.Count > avail ? SystemInformation.VerticalScrollBarWidth : 0);
            int y = -stack.VerticalScroll.Value;
            foreach (var c in plots) { c.SetBounds(0, y, Math.Max(100, w), each - 2); y += each; }
            if (active < panes.Count && panes[active].Plot != null) stack.ScrollControlIntoView(panes[active].Plot);
        }

        // ------------------------------------------------------------------ menus

        void PaneMenu(int pane, Point at)
        {
            Choose(pane);
            var plot = panes[pane].Plot;
            var m = new ContextMenuStrip();
            m.Items.Add("&Add Trace...", null, (s, e) => { string x = Win98Prompt.Ask(FindForm(), "Add Trace", "&Expression:", "", "e.g.  2*V(out)    V(out)-V(in)    V(a,b)    abs(I(R1))"); if (x != null) Add(x); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Add Pane A&bove", null, (s, e) => { panes.Insert(pane, new Pane()); active = pane; Draw(); });
            m.Items.Add("Add Pane Be&low", null, (s, e) => { panes.Insert(pane + 1, new Pane()); active = pane + 1; Draw(); });
            m.Items.Add(new ToolStripMenuItem("&Delete Pane", null, (s, e) => { panes.RemoveAt(pane); if (panes.Count == 0) panes.Add(new Pane()); active = Math.Min(pane, panes.Count - 1); Draw(); }) { Enabled = panes.Count > 1 || panes[pane].Exprs.Count > 0 });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("&Reset Zoom", null, (s, e) => ResetZoom());
            m.Items.Add("&Copy Image", null, (s, e) => { using (var b = Snapshot()) if (b != null) Clipboard.SetImage(b); });
            m.Items.Add("&Save Image as PNG...", null, (s, e) => SaveImage());
            Show(m, plot, at);
        }

        void LabelClicked(int pane, PlotTrace t, MouseEventArgs e)
        {
            Choose(pane);
            if (e.Button == MouseButtons.Left)
            {
                // as in LTspice: a click puts cursor 1 on the trace, another cursor 2 as well, another takes them off
                bool on1 = cur1.Pane == pane && cur1.Trace == t.Label, on2 = cur2.Pane == pane && cur2.Trace == t.Label;
                if (!on1) Attach(cur1, pane, t);
                else if (!on2) Attach(cur2, pane, t);
                else { cur1.Pane = cur2.Pane = -1; }
                ShowCursors();
                return;
            }
            string expr = (string)t.Tag;
            var m = new ContextMenuStrip();
            m.Items.Add("Cursor &1 on This Trace", null, (s, a) => { Attach(cur1, pane, t); ShowCursors(); });
            m.Items.Add("Cursor &2 on This Trace", null, (s, a) => { Attach(cur2, pane, t); ShowCursors(); });
            m.Items.Add(new ToolStripMenuItem("&No Cursors", null, (s, a) => { cur1.Pane = cur2.Pane = -1; ShowCursors(); }) { Enabled = cur1.On || cur2.On });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("&Edit Expression...", null, (s, a) =>
            {
                string x = Win98Prompt.Ask(FindForm(), "Edit Trace", "&Expression:", expr, "e.g.  2*V(out)    V(out)-V(in)    V(a,b)    abs(I(R1))");
                if (x == null || x.Trim().Length == 0) return;
                var ex = panes[pane].Exprs;
                int i = ex.IndexOf(expr);
                if (i >= 0) ex[i] = x.Trim();
                Draw();
            });
            m.Items.Add("&Delete Trace", null, (s, a) => { panes[pane].Exprs.Remove(expr); Draw(); });
            Show(m, panes[pane].Plot, e.Location);
        }

        static void Show(ContextMenuStrip m, Control on, Point at)
        {
            Win98Renderer.Apply(m);
            Win98Renderer.StyleDropDowns(m.Items);
            m.Closed += (s, e) => on.BeginInvoke(new Action(m.Dispose));
            m.Show(on, at);
        }

        // ------------------------------------------------------------------ cursors and measurements

        void Attach(CursorAt c, int pane, PlotTrace t)
        {
            c.Pane = pane;
            c.Trace = t.Label;
            var plot = panes[pane].Plot;
            // a new cursor starts in the time shown: cursor 1 at a third, cursor 2 at two thirds
            if (double.IsNaN(c.X) || c.X < plot.ViewX0 || c.X > plot.ViewX1)
                c.X = plot.ViewX0 + (plot.ViewX1 - plot.ViewX0) * (c == cur1 ? 1 / 3.0 : 2 / 3.0);
        }

        void CursorDragged(PlotCursor pc)
        {
            var c = pc.Name == "1" ? cur1 : cur2;
            c.X = pc.X;
            ShowCursors();
        }

        /// <summary>The trace a cursor is on (its first run).</summary>
        PlotTrace TraceOf(CursorAt c) =>
            c.On && c.Pane < panes.Count ? panes[c.Pane].Plot?.Traces.FirstOrDefault(t => t.Label == c.Trace) : null;

        /// <summary>A trace's value at a time, between the two samples around it.</summary>
        static double At(PlotTrace t, double x)
        {
            if (t == null || double.IsNaN(x) || t.X.Length == 0) return double.NaN;
            int i = Array.BinarySearch(t.X, x);
            if (i >= 0) return t.Y[i];
            i = ~i;
            if (i == 0) return t.Y[0];
            if (i >= t.X.Length) return t.Y[t.Y.Length - 1];
            double f = (x - t.X[i - 1]) / (t.X[i] - t.X[i - 1]);
            return t.Y[i - 1] + f * (t.Y[i] - t.Y[i - 1]);
        }

        /// <summary>The cursors on every pane (the cross on the pane of the trace they are on), and the measurements.</summary>
        void ShowCursors()
        {
            for (int i = 0; i < panes.Count; i++)
            {
                var p = panes[i].Plot;
                if (p == null) continue;
                p.Cursors.Clear();
                foreach (var c in new[] { cur1, cur2 }.Where(x => x.On))
                    p.Cursors.Add(new PlotCursor { Name = c.Name, Color = c.Color, X = c.X, Y = c.Pane == i ? At(TraceOf(c), c.X) : double.NaN });
                p.Invalidate();
            }
            Measure();
        }

        /// <summary>
        /// For every named trace (its first run): its value at each cursor and the difference, and its average, RMS, min,
        /// max and peak-to-peak over the window: between the two cursors, or the time shown.
        /// </summary>
        void Measure()
        {
            var plots = panes.Where(p => p.Plot != null).Select(p => p.Plot).ToList();
            double a, b;
            bool between = cur1.On && cur2.On;
            if (between) { a = Math.Min(cur1.X, cur2.X); b = Math.Max(cur1.X, cur2.X); }
            else if (plots.Count > 0) { a = plots[0].ViewX0; b = plots[0].ViewX1; }
            else { a = b = double.NaN; }
            string F(double v) => WavePlot.Fmt(v);
            lblMeasure.ForeColor = problems.Count > 0 ? Color.FromArgb(140, 0, 0) : Color.Black;
            lblMeasure.Text = problems.Count > 0 ? "! " + string.Join("   ! ", problems.Distinct().Take(3))
                : (between ? $"Between the cursors: {F(a)}s .. {F(b)}s,  Δt = {F(b - a)}s,  1/Δt = {F(1 / (b - a))}Hz"
                           : $"Over the time shown: {F(a)}s .. {F(b)}s") + "   (click a trace's name for a cursor; drag it)";
            lvMeasure.BeginUpdate();
            lvMeasure.Items.Clear();
            measureColors.Clear();
            foreach (var p in plots)
                foreach (var t in p.Traces.Where(x => x.Label != null))
                {
                    double v1 = cur1.On ? At(t, cur1.X) : double.NaN, v2 = cur2.On ? At(t, cur2.X) : double.NaN;
                    Window(t, a, b, out double avg, out double rms, out double min, out double max);
                    lvMeasure.Items.Add(new ListViewItem(new[] { t.Label, F(v1), F(v2), double.IsNaN(v1) || double.IsNaN(v2) ? "" : F(v2 - v1), F(avg), F(rms), F(min), F(max), F(max - min) }));
                    measureColors.Add(t.Color);
                }
            lvMeasure.EndUpdate();
        }

        /// <summary>Average and RMS over time (trapezoids, the window's edges interpolated), and the extremes, of a trace from a to b.</summary>
        static void Window(PlotTrace t, double a, double b, out double avg, out double rms, out double min, out double max)
        {
            avg = rms = min = max = double.NaN;
            if (!(b > a) || t.X.Length < 2) return;
            var xs = new List<double> { a };
            var ys = new List<double> { At(t, a) };
            for (int i = 0; i < t.X.Length; i++) if (t.X[i] > a && t.X[i] < b) { xs.Add(t.X[i]); ys.Add(t.Y[i]); }
            xs.Add(b); ys.Add(At(t, b));
            double s1 = 0, s2 = 0;
            for (int i = 1; i < xs.Count; i++)
            {
                double dx = xs[i] - xs[i - 1], ya = ys[i - 1], yb = ys[i];
                s1 += dx * (ya + yb) / 2;
                s2 += dx * (ya * ya + ya * yb + yb * yb) / 3;      // exact for a straight segment
            }
            avg = s1 / (b - a);
            rms = Math.Sqrt(Math.Max(0, s2 / (b - a)));
            min = ys.Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Min();
            max = ys.Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();
        }

        // ------------------------------------------------------------------ pictures

        /// <summary>Every pane, one under the other, as one picture.</summary>
        public Bitmap Snapshot()
        {
            var plots = panes.Where(p => p.Plot != null).Select(p => p.Plot).ToList();
            if (plots.Count == 0) return null;
            int w = Math.Max(800, plots[0].Width);
            var parts = plots.Select(c => c.ToBitmap(w, Math.Max(200, c.Height))).ToList();
            var bmp = new Bitmap(w, parts.Sum(p => p.Height), PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp)) { int y = 0; foreach (var p in parts) { g.DrawImage(p, 0, y); y += p.Height; p.Dispose(); } }
            return bmp;
        }

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
