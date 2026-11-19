using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Spica.Core;
using Spica.Core.Graph;

namespace Spica.UI
{
    /// <summary>
    /// The Studies tab: the study editor (docs/ARCHITECTURE.md, section 7). The block library is on the left (a tree
    /// with search), the canvas in the middle, the properties of the selected block on the right. The studies are
    /// saved in the setup. Blocks cannot run yet: the main toolbar runs the Setup tab's campaign as before.
    /// </summary>
    public partial class MainForm
    {
        Win98TabPage pageStudies;
        StudyCanvas studyCanvas;
        ComboBox cbStudy;
        TextBox txtBlockSearch;
        TreeView tvBlocks;
        Label lblBlockInfo;
        Panel studyProps;
        Label lblStudyHint;
        bool fillingStudies;

        Study CurrentStudy => cbStudy?.SelectedIndex >= 0 && cbStudy.SelectedIndex < project.Studies.Count ? project.Studies[cbStudy.SelectedIndex] : null;

        void BuildStudiesPage()
        {
            var p = pageStudies;

            // --- the study bar: which study, and what to do with the graph ----------------------
            var bar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(2, 2, 2, 2), Font = Win98.Font };
            Win98Renderer.Apply(bar);
            cbStudy = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Font = Win98.Font };
            cbStudy.SelectedIndexChanged += (s, e) => { if (!fillingStudies) ShowStudy(); };
            ToolStripButton B(string text, string icon, EventHandler click, string tip)
            {
                var b = new ToolStripButton(text, icon == null ? null : Win98.Icon(icon), click) { ToolTipText = tip, DisplayStyle = icon == null ? ToolStripItemDisplayStyle.Text : ToolStripItemDisplayStyle.ImageAndText };
                bar.Items.Add(b);
                return b;
            }
            bar.Items.Add(new ToolStripLabel("&Study:"));
            bar.Items.Add(new ToolStripControlHost(cbStudy));
            B("New...", "doc16", (s, e) => NewStudy(), "A new study, blank or from a template");
            B("Rename...", null, (s, e) => RenameStudy(), "Rename this study");
            B("Delete", "stop16", (s, e) => DeleteStudy(), "Delete this study from the setup");
            bar.Items.Add(new ToolStripSeparator());
            B("Validate", "check16", (s, e) => ValidateStudy(true), "Check the wiring: every input wired, the types right, no loop");
            B("Arrange", "queue16", (s, e) => { if (CurrentStudy == null) return; CurrentStudy.AutoLayout(); studyCanvas.FitAll(); StudyEdited(); }, "Place the blocks in columns, from the stimuli to the outputs");
            B("Fit", null, (s, e) => studyCanvas.FitAll(), "Show the whole study (or double-click the empty canvas)");
            bar.Items.Add(new ToolStripSeparator());
            B("Results...", "results16", (s, e) =>
            {
                if (lastVerdicts.Count == 0 && studyRunner == null) { SetStatus("No study has run yet: Run Here (F5) on this tab runs the one shown."); return; }
                ShowStudyResults(studyRunner?.Study ?? CurrentStudy, new List<string>());
            }, "The verdicts of the last study run");
            bar.Items.Add(new ToolStripSeparator());
            var legend = new ToolStripLabel { AutoSize = false, Width = 470, Height = 18 };
            legend.Paint += (s, e) =>
            {
                int x = 4;
                foreach (var kv in StudyCanvas.TypeColor)
                {
                    using (var pen = new Pen(kv.Value, 3)) e.Graphics.DrawLine(pen, x, 9, x + 14, 9);
                    TextRenderer.DrawText(e.Graphics, kv.Key.ToString(), Win98.Font, new Point(x + 17, 2), Color.Black);
                    x += 22 + TextRenderer.MeasureText(kv.Key.ToString(), Win98.Font).Width;
                }
            };
            legend.ToolTipText = "Wire colours: a wire only joins ports of the same type";
            bar.Items.Add(legend);
            Win98Renderer.StyleDropDowns(bar.Items);

            // --- left: the block library --------------------------------------------------------
            var left = new Win98GroupBox { Text = "Blocks", Dock = DockStyle.Left, Width = 214 };
            txtBlockSearch = new TextBox { Font = Win98.Font, BorderStyle = BorderStyle.Fixed3D };
            var searchHint = "Search blocks...";
            txtBlockSearch.Text = searchHint; txtBlockSearch.ForeColor = Win98.Shadow;
            txtBlockSearch.GotFocus += (s, e) => { if (txtBlockSearch.Text == searchHint) { txtBlockSearch.Text = ""; txtBlockSearch.ForeColor = Color.Black; } };
            txtBlockSearch.LostFocus += (s, e) => { if (txtBlockSearch.Text.Length == 0) { txtBlockSearch.Text = searchHint; txtBlockSearch.ForeColor = Win98.Shadow; } };
            txtBlockSearch.TextChanged += (s, e) => FillBlockTree(txtBlockSearch.Text == searchHint ? "" : txtBlockSearch.Text);
            var images = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            foreach (var k in BlockKind.All) images.Images.Add(k.Name, StudyCanvas.KindIcon(k.Name));
            images.Images.Add("folder", Win98.Icon("folder16"));
            tvBlocks = new TreeView { Font = Win98.Font, ImageList = images, BorderStyle = BorderStyle.Fixed3D, HideSelection = false, ShowLines = true, ItemHeight = 18 };
            tvBlocks.AfterSelect += (s, e) => ShowBlockInfo(e.Node?.Tag as BlockKind);
            tvBlocks.NodeMouseDoubleClick += (s, e) => { if (e.Node?.Tag is BlockKind k && CurrentStudy != null) { studyCanvas.AddBlock(k.Name); SetStatus($"{k.Title} added: wire its ports."); } };
            tvBlocks.ItemDrag += (s, e) => { if (e.Item is TreeNode n && n.Tag is BlockKind k) tvBlocks.DoDragDrop(k.Name, DragDropEffects.Copy); };
            lblBlockInfo = new Label { BackColor = Win98.InfoBack, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(4), UseMnemonic = false, Font = Win98.Font };
            left.Controls.AddRange(new Control[] { txtBlockSearch, tvBlocks, lblBlockInfo });
            left.Resize += (s, e) =>
            {
                int w = left.Width - 16;
                txtBlockSearch.SetBounds(8, 20, w, 20);
                lblBlockInfo.SetBounds(8, left.Height - 8 - 118, w, 118);
                tvBlocks.SetBounds(8, 44, w, lblBlockInfo.Top - 6 - 44);
            };

            // --- right: the properties ----------------------------------------------------------
            var right = new Win98GroupBox { Text = "Properties", Dock = DockStyle.Right, Width = 290 };
            studyProps = new Panel { AutoScroll = true, BackColor = Win98.Face };
            right.Controls.Add(studyProps);
            right.Resize += (s, e) => studyProps.SetBounds(6, 18, right.Width - 12, right.Height - 24);

            // --- middle: the canvas -------------------------------------------------------------
            studyCanvas = new StudyCanvas { Dock = DockStyle.Fill, Caption = BlockCaption };
            studyCanvas.SelectionChanged += (s, e) => ShowStudyProps();
            studyCanvas.StudyChanged += (s, e) => StudyEdited();
            studyCanvas.Message += m => SetStatus(m);
            var well = new Win98Well(studyCanvas) { Dock = DockStyle.Fill };
            lblStudyHint = new Label
            {
                Dock = DockStyle.Bottom, Height = 18, ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false,
                Text = "Drag from an output (right) to an input (left) of the same colour.  Right-click a port for a block that fits.  Wheel: zoom.  Drag the canvas: pan.  Del: remove.",
            };

            p.Controls.Add(well);
            p.Controls.Add(lblStudyHint);
            p.Controls.Add(Gap(DockStyle.Right));
            p.Controls.Add(right);
            p.Controls.Add(Gap(DockStyle.Left));
            p.Controls.Add(left);
            p.Controls.Add(Gap(DockStyle.Top, 4));
            p.Controls.Add(bar);

            FillBlockTree("");
            ShowBlockInfo(null);
        }

        // ------------------------------------------------------------------ library

        void FillBlockTree(string filter)
        {
            filter = (filter ?? "").Trim();
            tvBlocks.BeginUpdate();
            tvBlocks.Nodes.Clear();
            foreach (BlockFamily f in Enum.GetValues(typeof(BlockFamily)))
            {
                var kinds = BlockKind.All.Where(k => k.Family == f && (filter.Length == 0 ||
                    k.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || k.Summary.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                if (kinds.Count == 0) continue;
                var fam = new TreeNode(BlockKind.FamilyTitle(f)) { ImageKey = "folder", SelectedImageKey = "folder" };
                foreach (var k in kinds) fam.Nodes.Add(new TreeNode(k.Title) { Tag = k, ImageKey = k.Name, SelectedImageKey = k.Name });
                tvBlocks.Nodes.Add(fam);
            }
            if (filter.Length > 0) tvBlocks.ExpandAll();
            else foreach (TreeNode n in tvBlocks.Nodes) n.Expand();
            tvBlocks.EndUpdate();
        }

        static string Types(List<PortSpec> ports) =>
            ports.Count == 0 ? "nothing" : string.Join(", ", ports.Select(p => p.Type.ToString() + (p.Many ? " (several)" : "") + (p.Optional ? " (optional)" : "")).Distinct());

        void ShowBlockInfo(BlockKind k)
        {
            lblBlockInfo.Text = k == null
                ? "Choose a block to read what it does.\n\nDouble-click it, or drag it onto the canvas, to add it to the study."
                : $"{k.Title}\n{k.Summary}\n\nTakes: {Types(k.Inputs)}\nGives: {Types(k.Outputs)}";
        }

        // ------------------------------------------------------------------ studies

        /// <summary>The studies of the setup into the tab (called when the setup changes).</summary>
        void ShowStudies()
        {
            if (cbStudy == null) return;
            fillingStudies = true;
            try
            {
                var keep = CurrentStudy;
                cbStudy.Items.Clear();
                foreach (var s in project.Studies) cbStudy.Items.Add(s.Name);
                int i = keep == null ? -1 : project.Studies.IndexOf(keep);
                cbStudy.SelectedIndex = project.Studies.Count == 0 ? -1 : Math.Max(0, i);
            }
            finally { fillingStudies = false; }
            ShowStudy();
        }

        void ShowStudy()
        {
            var st = CurrentStudy;
            studyCanvas.Study = st;
            studyCanvas.States.Clear();
            if (st != null) studyCanvas.FitAll();
            ShowStudyProps();
        }

        void StudyEdited()
        {
            MarkDirty();
            if (studyCanvas.Selected == null) ShowStudyProps();
            else RefreshIssues();
        }

        void NewStudy()
        {
            using (var f = new NewStudyForm(project.Studies.Count == 0))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                var added = new List<Study>();
                if (f.FromSetup) added.AddRange(LegacyStudies.From(project));
                else added.Add(f.Template.Make(project));
                foreach (var s in added)
                {
                    if (f.StudyName.Length > 0 && added.Count == 1) s.Name = f.StudyName;
                    s.Name = UniqueStudyName(s.Name);
                    project.Studies.Add(s);
                }
                MarkDirty();
                fillingStudies = true;
                cbStudy.Items.Clear();
                foreach (var s in project.Studies) cbStudy.Items.Add(s.Name);
                cbStudy.SelectedIndex = project.Studies.IndexOf(added[0]);
                fillingStudies = false;
                ShowStudy();
                SetStatus(added.Count == 1 ? $"Study \"{added[0].Name}\" added." : $"{added.Count} studies added from the setup.");
            }
        }

        string UniqueStudyName(string name, Study except = null)
        {
            name = string.IsNullOrWhiteSpace(name) ? "Study" : name.Trim().Replace("[", "(").Replace("]", ")");
            string n = name;
            for (int i = 2; project.Studies.Any(s => s != except && s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); i++) n = $"{name} {i}";
            return n;
        }

        void RenameStudy()
        {
            var st = CurrentStudy;
            if (st == null) return;
            string name = Win98Prompt.Ask(this, "Rename Study", "&Name:", st.Name);
            if (name == null) return;
            st.Name = UniqueStudyName(name, st);
            cbStudy.Items[cbStudy.SelectedIndex] = st.Name;
            MarkDirty();
            ShowStudyProps();
        }

        void DeleteStudy()
        {
            var st = CurrentStudy;
            if (st == null) return;
            if (Win98MessageBox.Show(this, $"Delete the study \"{st.Name}\" from the setup?", "SPICA", MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;
            project.Studies.Remove(st);
            MarkDirty();
            ShowStudies();
        }

        List<StudyIssue> ValidateStudy(bool tell)
        {
            var st = CurrentStudy;
            if (st == null) return new List<StudyIssue>();
            var issues = st.Validate();
            if (tell)
            {
                if (issues.Count == 0) SetStatus($"Study \"{st.Name}\": the wiring is complete.");
                else Win98MessageBox.Show(this, $"Study \"{st.Name}\":\n\n" + string.Join("\n", issues.Take(15).Select(i => "- " + IssueText(st, i))), "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
            }
            ShowStudyProps();
            return issues;
        }

        static string IssueText(Study st, StudyIssue i) =>
            i.Block > 0 && st[i.Block] is Block b ? $"{b.Spec?.Title ?? b.Kind} ({b.Id}): {i.Message}" : i.Message;

        // ------------------------------------------------------------------ captions

        string BlockCaption(Block b)
        {
            string P(string k) => b.Get(k);
            int Count(string k) => P(k).Split('|').Count(x => x.Trim().Length > 0);
            switch (b.Kind)
            {
                case BlockKind.Schematic:
                    return (P("Name").Length > 0 ? P("Name") + ": " : "") + (P("File").Length > 0 ? Path.GetFileName(P("File")) : "no file chosen");
                case BlockKind.SupplyWaveform:
                    return $"{(P("Category").Length > 0 ? "cat " + P("Category") + ", " : "")}{Count("Tests")} test{(Count("Tests") == 1 ? "" : "s")}";
                case BlockKind.Fault: return $"R {P("R")}Ω from {P("Start")}s" + (P("Duration").Length > 0 ? $" for {P("Duration")}s" : "");
                case BlockKind.Susceptibility: return P("Signal").Length > 0 ? P("Signal") : "signal not set";
                case BlockKind.Nominal: return "tolerances nominal" + (P("Temperature").Length > 0 ? ", " + P("Temperature") + " °C" : "");
                case BlockKind.MonteCarlo: return $"{P("Runs")} runs" + (P("Paired") == "1" ? ", paired" : "");
                case BlockKind.Corners: return P("Temperatures").Length > 0 ? P("Temperatures") + " °C" : "no corner set";
                case BlockKind.WorstCase: return P("Objective").Length > 0 ? $"{P("Direction")} {P("Objective")}" : "objective not set";
                case BlockKind.Combine: return P("Mode");
                case BlockKind.Simulate: return P("Where") == "remote" ? "on the remote PC" : "on this PC (queue)";
                case BlockKind.Limits: return Count("Criteria") == 0 ? "no criterion chosen" : P("Criteria");
                case BlockKind.ErrorBudget: return Count("Errors") == 0 ? "no error chosen" : P("Errors");
                case BlockKind.Report: return P("Title").Length > 0 ? P("Title") : P("Style") + " report";
                case BlockKind.PartsDatabase: return P("Adapter").Length > 0 ? Path.GetFileName(P("Adapter")) : "adapter not set";
                case BlockKind.Candidates: return $"{CurrentStudy?.Upstream(b, "candidates").Count() ?? 0} candidates";
                default:
                    var kv = b.Props.FirstOrDefault(x => x.Value.Length > 0);
                    return kv.Key == null ? (b.Spec?.Summary ?? "") : $"{kv.Key}: {kv.Value}";
            }
        }

        // ------------------------------------------------------------------ properties

        static readonly HashSet<string> BoolProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Paired", "KeepRaw", "Pessimistic" };

        /// <summary>What a property can be: a list to choose from (several at once when multi), or free text.</summary>
        List<string> PropChoices(Block b, string key, out bool multi)
        {
            multi = false;
            var nets = netlist?.Nodes ?? new List<string>();
            switch (b.Kind + "." + key)
            {
                case "SupplyWaveform.Library": return qualPages.Select(q => q.Lib.Key).ToList();
                case "SupplyWaveform.Category":
                    return qualPages.FirstOrDefault(q => q.Lib.Key.Equals(b.Get("Library"), StringComparison.OrdinalIgnoreCase))?.Lib.CategoryOrder.ToList() ?? new List<string>();
                case "SupplyWaveform.Tests":
                    multi = true;
                    var lib = qualPages.FirstOrDefault(q => q.Lib.Key.Equals(b.Get("Library"), StringComparison.OrdinalIgnoreCase))?.Lib;
                    return lib == null ? new List<string>() : lib.TestsFor(b.Get("Category")).Select(t => t.Name).ToList();
                case "SupplyWaveform.Net": case "Susceptibility.Net": case "Fault.From": return nets.ToList();
                case "Fault.To": return new[] { "0" }.Concat(nets).ToList();
                case "Limits.Criteria": multi = true; return project.QualCriteria.Select(c => c.Name).ToList();
                case "ErrorBudget.Errors": multi = true; return project.Errors.Select(e => e.Name).ToList();
                case "Spec.Measurements": multi = true; return new[] { "all" }.Concat(netlist?.MeasNames ?? new List<string>()).ToList();
                case "Plots.Signals": case "Measure.Save": multi = true; return nets.Select(n => $"V({n})").Concat(netlist?.Currents ?? new List<string>()).ToList();
                case "Combine.Mode": return new List<string> { "product", "union" };
                case "WorstCase.Direction": return new List<string> { "min", "max" };
                case "WorstCase.Method": return new List<string> { "Auto", "Sensitivity", "Corners" };
                case "WorstCase.Objective": return (netlist?.MeasNames ?? new List<string>()).ToList();
                case "Simulate.Where": return new List<string> { "local", "remote" };
                case "Report.Style": return new List<string> { "Full", "Concise" };
                default: return null;
            }
        }

        void ShowStudyProps()
        {
            if (studyProps == null) return;
            studyProps.SuspendLayout();
            foreach (Control c in studyProps.Controls.Cast<Control>().ToList()) c.Dispose();
            studyProps.Controls.Clear();
            int y = 2, w = Math.Max(160, studyProps.ClientSize.Width - 8 - SystemInformation.VerticalScrollBarWidth);
            Label Text(string t, Font f = null, Color? col = null, int h = 0)
            {
                var l = new Label { Text = t, Location = new Point(2, y), Width = w, AutoSize = false, Font = f ?? Win98.Font, ForeColor = col ?? Color.Black, UseMnemonic = false };
                l.Height = h > 0 ? h : TextRenderer.MeasureText(t, l.Font, new Size(w, 0), TextFormatFlags.WordBreak).Height + 2;
                studyProps.Controls.Add(l);
                y += l.Height + 2;
                return l;
            }

            var st = CurrentStudy;
            var b = studyCanvas.Selected;
            if (st == null)
            {
                Text("No study yet.", Win98.BoldFont);
                Text("A study is a graph of blocks: stimuli into a schematic, variations, the simulation, evaluations and outputs.\n\n" +
                     "Use New... in the bar above: blank, a template, or the studies the Setup and Qualification tabs stand for.", null, Win98.Shadow);
                var btn = new Win98Button { Text = "&New Study...", Location = new Point(2, y + 4), Width = 110 };
                btn.Click += (s, e) => NewStudy();
                studyProps.Controls.Add(btn);
                studyProps.ResumeLayout();
                return;
            }
            if (b == null)
            {
                // the study itself
                Text(st.Name, Win98.BoldFont);
                Text($"{st.Blocks.Count} blocks, {st.Wires.Count} wires", null, Win98.Shadow);
                y += 4;
                Text("Note:");
                var note = new Win98TextField { Location = new Point(2, y), Width = w, Height = 64, Text = st.Note };
                note.Box.Multiline = true; note.Box.ScrollBars = ScrollBars.Vertical; note.Box.AcceptsReturn = true;
                note.Box.TextChanged += (s, e) => { st.Note = note.Text; MarkDirty(); };
                studyProps.Controls.Add(note);
                y += 70;
                var issues = st.Validate();
                y += 4;
                Text(issues.Count == 0 ? "The wiring is complete." : $"{issues.Count} thing{(issues.Count == 1 ? "" : "s")} to fix before it can run:",
                     Win98.BoldFont, issues.Count == 0 ? Color.FromArgb(0, 100, 0) : Color.FromArgb(160, 0, 0));
                foreach (var i in issues.Take(20)) Text("- " + IssueText(st, i), null, Color.FromArgb(128, 0, 0));
                y += 6;
                Text("Run Here (F5) runs this study while this tab is open: its campaigns join the queue, and the verdicts open in their own window.", null, Win98.Shadow);
                studyProps.ResumeLayout();
                return;
            }

            var k = b.Spec;
            studyProps.Controls.Add(new PictureBox { Image = StudyCanvas.KindIcon(b.Kind), Location = new Point(2, y), Size = new Size(16, 16) });
            var head = Text($"{k?.Title ?? b.Kind}  (block {b.Id})", Win98.BoldFont);
            head.Left = 22; head.Width = w - 20;
            if (k != null) Text(k.Summary, null, Win98.Shadow);
            y += 4;

            // the properties, in order (a repeated key is one row each)
            for (int idx = 0; idx < b.Props.Count; idx++)
            {
                int index = idx;
                string key = b.Props[idx].Key, val = b.Props[idx].Value;
                Text(key + ":");
                void SetVal(string v)
                {
                    b.Props[index] = new KeyValuePair<string, string>(key, v ?? "");
                    MarkDirty();
                    studyCanvas.Invalidate();
                }
                if (BoolProps.Contains(key))
                {
                    var cb = new Win98CheckBox { Text = "yes", Checked = val == "1", Location = new Point(2, y - 2), AutoSize = true };
                    cb.CheckedChanged += (s, e) => SetVal(cb.Checked ? "1" : "0");
                    studyProps.Controls.Add(cb);
                    y += 22;
                    continue;
                }
                var choices = PropChoices(b, key, out bool multi);
                bool browse = b.Kind == BlockKind.Schematic && key == "File" || b.Kind == BlockKind.PartsDatabase && key == "Adapter" || b.Kind == BlockKind.Derating && key == "Rules";
                int fw = multi || browse ? w - 28 : w;
                if (choices != null && !multi)
                {
                    var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(2, y), Width = fw, Font = Win98.Font };
                    combo.Items.AddRange(choices.Cast<object>().ToArray());
                    combo.Text = val;
                    combo.TextChanged += (s, e) => SetVal(combo.Text);
                    // a category or a library changes the lists below it
                    if (b.Kind == BlockKind.SupplyWaveform && (key == "Library" || key == "Category")) combo.SelectionChangeCommitted += (s, e) => BeginInvoke(new Action(ShowStudyProps));
                    studyProps.Controls.Add(combo);
                }
                else
                {
                    var tf = new Win98TextField { Location = new Point(2, y), Width = fw, Text = val };
                    tf.Box.TextChanged += (s, e) => SetVal(tf.Text);
                    studyProps.Controls.Add(tf);
                    if (multi || browse)
                    {
                        var more = new Win98Button { Text = "...", Location = new Point(fw + 6, y - 1), Size = new Size(24, 22) };
                        more.Click += (s, e) =>
                        {
                            string got = multi ? PickSeveral(key, choices, tf.Text) : BrowseFor(b, key, tf.Text);
                            if (got != null) tf.Text = got;
                        };
                        studyProps.Controls.Add(more);
                    }
                }
                y += 26;
            }
            if (b.Props.Count == 0) Text("This block has no settings.", null, Win98.Shadow);

            // its wiring
            if (k != null)
            {
                y += 6;
                Text("Wiring", Win98.BoldFont);
                foreach (var i in k.Inputs)
                {
                    var from = st.Upstream(b, i.Name).Select(x => x.Spec?.Title ?? x.Kind).ToList();
                    Text($"in  {i.Name} ({i.Type}): " + (from.Count == 0 ? (i.Optional ? "-" : "not wired") : string.Join(", ", from)), null, from.Count == 0 && !i.Optional ? Color.FromArgb(160, 0, 0) : Color.Black);
                }
                foreach (var o in k.Outputs)
                {
                    var to = st.Wires.Where(x => x.From == b.Id && x.FromPort == o.Name).Select(x => st[x.To]).Where(x => x != null).Select(x => x.Spec?.Title ?? x.Kind).ToList();
                    Text($"out {o.Name} ({o.Type}): " + (to.Count == 0 ? "-" : string.Join(", ", to)));
                }
            }
            var mine = st.Validate().Where(i => i.Block == b.Id).ToList();
            if (mine.Count > 0) { y += 4; foreach (var i in mine) Text("! " + i.Message, null, Color.FromArgb(160, 0, 0)); }
            studyProps.ResumeLayout();
        }

        void RefreshIssues() { if (studyCanvas.Selected != null) ShowStudyProps(); }

        string PickSeveral(string what, List<string> choices, string current)
        {
            var now = current.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            using (var f = new PickForm(what, choices.Union(now, StringComparer.OrdinalIgnoreCase).ToList(), now))
                return f.ShowDialog(this) == DialogResult.OK ? string.Join(" | ", f.Picked) : null;
        }

        string BrowseFor(Block b, string key, string current)
        {
            string filter = key == "File" ? "LTspice schematic (*.asc)|*.asc" : key == "Rules" ? "Rules (*.txt)|*.txt|All files (*.*)|*.*" : "Programs (*.exe;*.bat;*.cmd;*.py)|*.exe;*.bat;*.cmd;*.py|All files (*.*)|*.*";
            using (var d = new OpenFileDialog { Filter = filter, AutoUpgradeEnabled = false })
            {
                try { if (current.Length > 0) d.InitialDirectory = Path.GetDirectoryName(current); } catch { }
                return d.ShowDialog(this) == DialogResult.OK ? d.FileName : null;
            }
        }
    }

    /// <summary>Studies > New...: a name and where to start from (blank, a template, or the setup's own settings).</summary>
    sealed class NewStudyForm : Win98Form
    {
        readonly Win98TextField txtName;
        readonly ListBox list;
        readonly Label lblDesc;
        const string FromSetupText = "From the Setup and Qualification tabs";

        public string StudyName => txtName.Text.Trim();
        public StudyTemplates.Template Template => list.SelectedItem as StudyTemplates.Template;
        public bool FromSetup => list.SelectedItem as string == FromSetupText;

        public NewStudyForm(bool firstOne)
        {
            Text = "New Study";
            MinimizeBox = false; MaximizeBox = false; Resizable = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 300 + Padding.Vertical);
            int x = Padding.Left + 10, y = Padding.Top + 10;
            Controls.Add(new Label { Text = "&Start from:", Location = new Point(x, y), AutoSize = true });
            list = new ListBox { Location = new Point(x, y + 18), Size = new Size(400, 140), Font = Win98.Font, IntegralHeight = false };
            list.Items.Add(FromSetupText);
            foreach (var t in StudyTemplates.All) list.Items.Add(t);
            lblDesc = new Label { Location = new Point(x, y + 164), Size = new Size(400, 44), ForeColor = Win98.Shadow, UseMnemonic = false };
            Controls.Add(new Label { Text = "&Name (empty: the template's):", Location = new Point(x, y + 214), AutoSize = true });
            txtName = new Win98TextField { Location = new Point(x, y + 232), Width = 260 };
            Controls.AddRange(new Control[] { list, lblDesc, txtName });
            list.SelectedIndexChanged += (s, e) =>
                lblDesc.Text = FromSetup ? "The studies the setup's tabs stand for: the tolerance campaign and, when tests are ticked, the qualification. The tabs stay as they are."
                             : Template?.Description ?? "";
            list.SelectedIndex = firstOne ? 0 : 1;
            list.DoubleClick += (s, e) => { DialogResult = DialogResult.OK; };
            int by = ClientSize.Height - Padding.Bottom - 33;
            var ok = new Win98Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(ClientSize.Width - Padding.Right - 176, by), Size = new Size(80, 23) };
            var cancel = new Win98Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(ClientSize.Width - Padding.Right - 88, by), Size = new Size(80, 23) };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }
    }

    /// <summary>Several values out of a list (tests, criteria, signals...), with the ones already chosen ticked.</summary>
    sealed class PickForm : Win98Form
    {
        readonly CheckedListBox list;
        public List<string> Picked => list.CheckedItems.Cast<string>().ToList();

        public PickForm(string what, List<string> choices, List<string> now)
        {
            Text = "Choose: " + what;
            MinimizeBox = false; MaximizeBox = false; Resizable = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(380, 330 + Padding.Vertical);
            int x = Padding.Left + 10, y = Padding.Top + 10;
            list = new CheckedListBox { Location = new Point(x, y), Size = new Size(360, 270), CheckOnClick = true, Font = Win98.Font, IntegralHeight = false };
            foreach (var c in choices) list.Items.Add(c, now.Contains(c, StringComparer.OrdinalIgnoreCase));
            if (choices.Count == 0) Controls.Add(new Label { Text = "Nothing to choose from yet (open a schematic, or define them first).", Location = new Point(x + 6, y + 6), AutoSize = true, ForeColor = Win98.Shadow, BackColor = Color.White });
            Controls.Add(list);
            int by = ClientSize.Height - Padding.Bottom - 33;
            var ok = new Win98Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(ClientSize.Width - Padding.Right - 176, by), Size = new Size(80, 23) };
            var cancel = new Win98Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(ClientSize.Width - Padding.Right - 88, by), Size = new Size(80, 23) };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            if (choices.Count == 0) list.SendToBack();
        }
    }

    /// <summary>A one-line question (a name), Win98 style; null when cancelled.</summary>
    static class Win98Prompt
    {
        public static string Ask(IWin32Window owner, string title, string label, string value)
        {
            using (var f = new Win98Form { Text = title, MinimizeBox = false, MaximizeBox = false, Resizable = false, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent })
            {
                f.ClientSize = new Size(330, 92 + f.Padding.Vertical);
                int x = f.Padding.Left + 10, y = f.Padding.Top + 10;
                f.Controls.Add(new Label { Text = label, Location = new Point(x, y + 3), AutoSize = true });
                var tf = new Win98TextField { Location = new Point(x + 50, y), Width = 250, Text = value };
                f.Controls.Add(tf);
                int by = f.ClientSize.Height - f.Padding.Bottom - 33;
                var ok = new Win98Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(f.ClientSize.Width - f.Padding.Right - 176, by), Size = new Size(80, 23) };
                var cancel = new Win98Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(f.ClientSize.Width - f.Padding.Right - 88, by), Size = new Size(80, 23) };
                f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? tf.Text.Trim() : null;
            }
        }
    }
}
