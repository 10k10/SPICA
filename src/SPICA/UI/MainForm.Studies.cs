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
    /// The Workbench: the study editor, where every analysis starts (docs/ARCHITECTURE.md, section 7). The block
    /// library is on the left (a tree with search), the canvas fills the rest. A double-click on a block opens its
    /// configurator; a block that misses something it needs to run is drawn with a red border. The studies are saved
    /// in the setup.
    /// </summary>
    public partial class MainForm
    {
        Win98TabPage pageStudies;
        StudyCanvas studyCanvas;
        ComboBox cbStudy;
        TextBox txtBlockSearch;
        TreeView tvBlocks;
        Label lblBlockInfo;
        Label lblStudyHint;
        bool fillingStudies;
        HashSet<string> runnableKinds;

        TreeView tvModel;
        bool fillingModel;
        /// <summary>Blocks changed since the study last ran (they and what they feed run again).</summary>
        readonly HashSet<int> staleBlocks = new HashSet<int>();

        Study CurrentStudy => cbStudy?.SelectedIndex >= 0 && cbStudy.SelectedIndex < project.Studies.Count ? project.Studies[cbStudy.SelectedIndex] : null;

        void BuildStudiesPage()
        {
            var p = pageStudies;
            var probe = StudyExecutors.Register(new StudyRunner(), null);
            runnableKinds = new HashSet<string>(BlockKind.All.Where(k => probe.Has(k.Name)).Select(k => k.Name), StringComparer.OrdinalIgnoreCase);

            // --- the study bar: which study, and what to do with it ------------------------------
            var bar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(2, 2, 2, 2), Font = Win98.Font };
            Win98Renderer.Apply(bar);
            cbStudy = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, Font = Win98.Font, DropDownWidth = 300 };
            cbStudy.SelectedIndexChanged += (s, e) => { if (!fillingStudies) ShowStudy(); };
            ToolStripButton B(string text, Image icon, EventHandler click, string tip)
            {
                var b = new ToolStripButton(text, icon, click) { ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, ImageScaling = ToolStripItemImageScaling.None, Margin = new Padding(1, 1, 1, 2) };
                bar.Items.Add(b);
                return b;
            }
            // the study: which one, and what to do with it
            bar.Items.Add(new ToolStripLabel("&Study:"));
            bar.Items.Add(new ToolStripControlHost(cbStudy));
            B("New", Win98.Icon("doc16"), (s, e) => NewStudy(), "A new study: blank, from a template, or from the classic tabs");
            B("Rename", Glyphs.Pencil(), (s, e) => RenameStudy(), "Rename this study");
            B("Delete", Win98.Icon("stop16"), (s, e) => DeleteStudy(), "Delete this study from the setup");
            bar.Items.Add(new ToolStripSeparator());
            // the graph
            B("Validate", Win98.Icon("check16"), (s, e) => ValidateStudy(true), "Check the study: every block set up, every input wired, the types right, no loop");
            B("Fit", Glyphs.Magnifier(), (s, e) => studyCanvas.FitAll(), "Show the whole study (Space, or double-click the empty canvas)");
            bar.Items.Add(new ToolStripSeparator());
            // running it
            B("Add to Queue", Glyphs.Play(), (s, e) => RunStudy(), "Run this study: its campaigns join the queue on this PC  (F5)");
            // the wire colours, at the right end
            var legend = new ToolStripLabel { AutoSize = false, Width = 430, Height = 18, Alignment = ToolStripItemAlignment.Right };
            legend.Paint += (s, e) =>
            {
                int x = 4;
                foreach (var kv in StudyCanvas.TypeColor)
                {
                    using (var pen = new Pen(kv.Value, 3)) e.Graphics.DrawLine(pen, x, 9, x + 12, 9);
                    TextRenderer.DrawText(e.Graphics, kv.Key.ToString(), Win98.Font, new Point(x + 14, 2), Color.FromArgb(64, 64, 64));
                    x += 18 + TextRenderer.MeasureText(kv.Key.ToString(), Win98.Font).Width;
                }
            };
            legend.ToolTipText = "Wire colours: a wire only joins ports of the same type";
            bar.Items.Add(legend);
            Win98Renderer.StyleDropDowns(bar.Items);

            // --- left: the block library --------------------------------------------------------
            // --- left: the study as a tree (by step, each block with its state), the block library under it
            var leftCol = new Panel { Dock = DockStyle.Left, Width = 250, BackColor = Win98.Face };
            var grpModel = new Win98GroupBox { Text = "Study", Dock = DockStyle.Top, Height = 300 };
            var stateImages = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            stateImages.Images.Add("ok", Win98.Icon("check16"));
            stateImages.Images.Add("bang", Glyphs.Bang());
            stateImages.Images.Add("bolt", Glyphs.Bolt());
            stateImages.Images.Add("ring", Glyphs.Ring());
            stateImages.Images.Add("folder", Win98.Icon("folder16"));
            stateImages.Images.Add("study", Win98.Icon("app16"));
            tvModel = new TreeView { Font = Win98.Font, ImageList = stateImages, BorderStyle = BorderStyle.Fixed3D, HideSelection = false, ShowLines = true, ItemHeight = 18 };
            tvModel.AfterSelect += (s, e) =>
            {
                if (fillingModel || !(e.Node?.Tag is Block b)) return;
                studyCanvas.Select(b);
                studyCanvas.Reveal(b);
            };
            var modelTip = new Label { Text = "✓ done     ! something missing\n⚡ changed: runs again     ○ not run yet", AutoSize = false, Height = 30, ForeColor = Win98.Shadow, UseMnemonic = false };
            grpModel.Controls.Add(tvModel);
            grpModel.Controls.Add(modelTip);
            grpModel.Resize += (s, e) =>
            {
                modelTip.SetBounds(8, grpModel.Height - 36, grpModel.Width - 16, 30);
                tvModel.SetBounds(8, 20, grpModel.Width - 16, modelTip.Top - 24);
            };
            leftCol.Resize += (s, e) => grpModel.Height = Math.Max(180, leftCol.Height * 45 / 100);
            var left = new Win98GroupBox { Text = "Blocks", Dock = DockStyle.Fill };
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
            tvBlocks.NodeMouseDoubleClick += (s, e) => { if (e.Node?.Tag is BlockKind k && CurrentStudy != null) { studyCanvas.AddBlock(k.Name); SetStatus($"{k.Title} added: set it up on the right, and wire its ports."); } };
            tvBlocks.ItemDrag += (s, e) => { if (e.Item is TreeNode n && n.Tag is BlockKind k) tvBlocks.DoDragDrop(k.Name, DragDropEffects.Copy); };
            lblBlockInfo = new Label { BackColor = Win98.InfoBack, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(4), UseMnemonic = false, Font = Win98.Font };
            left.Controls.AddRange(new Control[] { txtBlockSearch, tvBlocks, lblBlockInfo });
            left.Resize += (s, e) =>
            {
                int w = left.Width - 16;
                txtBlockSearch.SetBounds(8, 20, w, 20);
                lblBlockInfo.SetBounds(8, left.Height - 8 - 96, w, 96);
                tvBlocks.SetBounds(8, 44, w, lblBlockInfo.Top - 6 - 44);
            };

            // --- the canvas ---------------------------------------------------------------------
            studyCanvas = new StudyCanvas { Dock = DockStyle.Fill, Caption = BlockCaption, Problems = BlockProblems, Runs = k => runnableKinds.Contains(k) };
            studyCanvas.StudyChanged += (s, e) => StudyEdited();
            studyCanvas.Message += m => SetStatus(m);
            studyCanvas.BlockOpened += b => { studyCanvas.Select(b); ShowInspector(b); inspectorBody.Focus(); };
            studyCanvas.SelectionChanged += (s, e) => { ShowInspector(studyCanvas.Selected); SelectInModel(studyCanvas.Selected); };
            var well = new Win98Well(studyCanvas) { Dock = DockStyle.Fill };
            lblStudyHint = new Label
            {
                Dock = DockStyle.Bottom, Height = 18, ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false,
                Text = "Click a block to set it up on the right (red: something missing).  Drag from an output to an input of the same colour.  Right-click a port for a block that fits.  Space: fit.  Del: remove.",
            };

            p.Controls.Add(well);
            p.Controls.Add(BuildInspector());
            p.Controls.Add(lblStudyHint);
            leftCol.Controls.Add(left);
            leftCol.Controls.Add(Gap(DockStyle.Top));
            leftCol.Controls.Add(grpModel);
            p.Controls.Add(Gap(DockStyle.Left));
            p.Controls.Add(leftCol);
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
                foreach (var k in kinds)
                    fam.Nodes.Add(new TreeNode(k.Title + (runnableKinds.Contains(k.Name) ? "" : "  (soon)")) { Tag = k, ImageKey = k.Name, SelectedImageKey = k.Name, ForeColor = runnableKinds.Contains(k.Name) ? Color.Black : Win98.Shadow });
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
                ? "Choose a block to read what it does.\n\nDouble-click it, or drag it onto the canvas, to add it to the study; then click it there to set it up on the right."
                : $"{k.Title}\n{k.Summary}\n\nTakes: {Types(k.Inputs)}\nGives: {Types(k.Outputs)}" + (runnableKinds.Contains(k.Name) ? "" : "\n\nDoes not run yet in this version: a study skips it.");
        }

        // ------------------------------------------------------------------ studies

        /// <summary>The studies of the setup into the Workbench (called when the setup changes). A schematic opened without
        /// any study gets one, with its Schematic block, to start from.</summary>
        void ShowStudies()
        {
            if (cbStudy == null) return;
            if (project.Studies.Count == 0 && ascPath != null)
            {
                var st = new Study { Name = Path.GetFileNameWithoutExtension(ascPath) };
                st.Add(BlockKind.Schematic, 280, 120).Set("File", ascPath).Set("Name", Path.GetFileNameWithoutExtension(ascPath));
                project.Studies.Add(st);         // not marked as a change: it is only saved when the user saves
            }
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
            staleBlocks.Clear();
            if (st != null) studyCanvas.FitAll();
            FillModelTree();
        }

        void StudyEdited()
        {
            MarkDirty();
            studyCanvas.Invalidate();
            FillModelTree();
        }

        // ------------------------------------------------------------------ the study tree

        static readonly (string Title, BlockFamily[] Families)[] ModelGroups =
        {
            ("Stimuli", new[] { BlockFamily.Stimulus }),
            ("Circuit", new[] { BlockFamily.Circuit }),
            ("Conditions", new[] { BlockFamily.Variation }),
            ("Simulation", new[] { BlockFamily.Simulation }),
            ("Evaluations", new[] { BlockFamily.Collection, BlockFamily.Evaluation, BlockFamily.External }),
            ("Outputs", new[] { BlockFamily.Output }),
        };

        /// <summary>A block's state in the tree: missing something, changed since it ran, done, or not run yet.</summary>
        string ModelState(Block b)
        {
            if (BlockProblems(b).Count > 0) return "bang";
            bool ran = studyCanvas.States.TryGetValue(b.Id, out var s) && (s == BlockState.Done || s == BlockState.Cached);
            if (ran && staleBlocks.Contains(b.Id)) return "bolt";
            return ran ? "ok" : "ring";
        }

        /// <summary>The study by step, each block with its state; the steps with a problem open.</summary>
        void FillModelTree()
        {
            if (tvModel == null) return;
            var st = CurrentStudy;
            fillingModel = true;
            tvModel.BeginUpdate();
            tvModel.Nodes.Clear();
            if (st != null)
            {
                var rootNode = new TreeNode(st.Name) { ImageKey = "study", SelectedImageKey = "study", NodeFont = Win98.BoldFont };
                foreach (var (title, fams) in ModelGroups)
                {
                    var blocks = st.Blocks.Where(b => b.Spec != null && fams.Contains(b.Spec.Family)).OrderBy(b => b.Y).ThenBy(b => b.X).ToList();
                    var g = new TreeNode($"{title} ({blocks.Count})") { ImageKey = "folder", SelectedImageKey = "folder" };
                    foreach (var b in blocks)
                    {
                        string state = runnableKinds.Contains(b.Kind) ? ModelState(b) : "ring";
                        string cap = BlockCaption(b);
                        var n = new TreeNode($"{b.Spec.Title}: {cap}") { Tag = b, ImageKey = state, SelectedImageKey = state };
                        if (!runnableKinds.Contains(b.Kind)) n.ForeColor = Win98.Shadow;
                        if (state == "bang") n.ForeColor = Color.FromArgb(170, 0, 0);
                        g.Nodes.Add(n);
                    }
                    rootNode.Nodes.Add(g);
                }
                tvModel.Nodes.Add(rootNode);
                rootNode.ExpandAll();
                // the study's own text is drawn bold: set it again so it is measured with the bold font
                rootNode.Text = st.Name + " ";
            }
            tvModel.EndUpdate();
            fillingModel = false;
            SelectInModel(studyCanvas.Selected);
        }

        void SelectInModel(Block b)
        {
            if (tvModel == null) return;
            fillingModel = true;
            try
            {
                TreeNode Find(TreeNodeCollection nodes)
                {
                    foreach (TreeNode n in nodes)
                    {
                        if (n.Tag == b && b != null) return n;
                        var f = Find(n.Nodes);
                        if (f != null) return f;
                    }
                    return null;
                }
                var hit = b == null ? null : Find(tvModel.Nodes);
                tvModel.SelectedNode = hit;
            }
            finally { fillingModel = false; }
        }

        /// <summary>A block changed after the study ran: it, and every block it feeds, run again.</summary>
        void MarkStale(Block b)
        {
            var st = CurrentStudy;
            if (st == null || b == null || studyCanvas.States.Count == 0) return;
            foreach (var x in st.Blocks) if (x == b || st.Reaches(b.Id, x.Id)) staleBlocks.Add(x.Id);
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

        /// <summary>Everything that stops the study shown from running: the wiring, and what each block misses.</summary>
        List<string> StudyProblems()
        {
            var st = CurrentStudy;
            if (st == null) return new List<string>();
            var list = st.Validate().Where(i => i.Block == 0).Select(i => i.Message).ToList();
            foreach (var b in st.Blocks.OrderBy(x => x.Id))
                foreach (var m in BlockProblems(b)) list.Add($"{b.Spec?.Title ?? b.Kind} ({b.Id}): {m}");
            return list;
        }

        bool ValidateStudy(bool tell)
        {
            var st = CurrentStudy;
            if (st == null) return false;
            var problems = StudyProblems();
            studyCanvas.Invalidate();
            if (tell)
            {
                if (problems.Count == 0) SetStatus($"Study \"{st.Name}\": every block is set up and wired.");
                else Win98MessageBox.Show(this, $"Study \"{st.Name}\" cannot run yet:\n\n" + string.Join("\n", problems.Take(15).Select(x => "- " + x)) +
                                                 "\n\nThe blocks with a red border show it too: double-click one to set it up.", "SPICA", MessageBoxButtons.OK, Win98Icon.Warning);
            }
            return problems.Count == 0;
        }

        /// <summary>What a block misses to run: an input not wired, or a setting it needs (the canvas draws it red).</summary>
        List<string> BlockProblems(Block b)
        {
            var st = CurrentStudy;
            var list = st == null ? new List<string>() : st.Validate().Where(i => i.Block == b.Id).Select(i => i.Message).ToList();
            string P(string k) => b.Get(k).Trim();
            List<string> Names(string k) => P(k).Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            var lib = qualPages.Select(q => q.Lib).FirstOrDefault(l => l.Key.Equals(P("Library"), StringComparison.OrdinalIgnoreCase));
            switch (b.Kind)
            {
                case BlockKind.Schematic:
                    string file = P("File").Length > 0 ? P("File") : project.Schematic ?? "";
                    if (file.Length == 0) list.Add("no schematic: choose the .asc (File)");
                    else if (!File.Exists(file)) list.Add("the .asc is not there: " + file);
                    break;
                case BlockKind.SupplyWaveform:
                    if (lib == null) list.Add(P("Library").Length == 0 ? "choose the library (Library)" : $"no library '{P("Library")}' in the qualification folder");
                    else if (!lib.CategoryOrder.Contains(P("Category"), StringComparer.OrdinalIgnoreCase)) list.Add("choose the equipment category (Category)");
                    if (P("Net").Length == 0) list.Add("choose the net the waveform goes into (Net)");
                    if (Names("Tests").Count == 0) list.Add("choose the tests (Tests)");
                    else if (lib != null && lib.CategoryOrder.Contains(P("Category"), StringComparer.OrdinalIgnoreCase))
                        foreach (var t in Names("Tests").Where(t => !lib.TestsFor(P("Category")).Any(x => x.Name.Equals(t, StringComparison.OrdinalIgnoreCase))))
                            list.Add($"category {P("Category")} has no test '{t}'");
                    break;
                case BlockKind.Fault:
                    if (P("From").Length == 0) list.Add("choose the net the fault starts from (From)");
                    break;
                case BlockKind.Limits:
                    if (Names("Criteria").Count == 0) list.Add("choose or make the criteria to judge");
                    foreach (var n in Names("Criteria").Where(n => !project.QualCriteria.Any(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
                        list.Add($"no criterion '{n}' in the setup");
                    break;
                case BlockKind.ErrorBudget:
                    if (project.Errors.Count == 0) list.Add("no error defined yet: make one with New, in this block's configurator");
                    foreach (var n in Names("Errors").Where(n => !project.Errors.Any(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
                        list.Add($"no error '{n}' in the setup");
                    break;
                case BlockKind.Corners:
                    if (!P("Temperatures").Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Any()) list.Add("enter the temperatures (e.g. -55; 25; 85)");
                    break;
                case BlockKind.MonteCarlo:
                    if (!int.TryParse(P("Runs"), out int runs) || runs < 1) list.Add("enter the number of runs");
                    break;
                case BlockKind.Candidates:
                    if (st != null && st.Upstream(b, "candidates").Any(x => x.Get("Name").Trim().Length == 0)) list.Add("give every candidate schematic a name");
                    break;
            }
            return list;
        }

        // ------------------------------------------------------------------ captions

        string BlockCaption(Block b)
        {
            string P(string k) => b.Get(k);
            int Count(string k) => P(k).Split('|').Count(x => x.Trim().Length > 0);
            if (!runnableKinds.Contains(b.Kind)) return "does not run yet in this version";
            switch (b.Kind)
            {
                case BlockKind.Schematic:
                    return (P("Name").Length > 0 ? P("Name") + ": " : "") + (P("File").Length > 0 ? Path.GetFileName(P("File")) : "no file chosen");
                case BlockKind.SupplyWaveform:
                    return $"{(P("Category").Length > 0 ? "cat " + P("Category") + ", " : "")}{Count("Tests")} test{(Count("Tests") == 1 ? "" : "s")}";
                case BlockKind.Fault: return $"R {P("R")}Ω from {P("Start")}s" + (P("Duration").Length > 0 ? $" for {P("Duration")}s" : "");
                case BlockKind.Nominal: return "tolerances nominal" + (P("Temperature").Length > 0 ? ", " + P("Temperature") + " °C" : "");
                case BlockKind.MonteCarlo: return $"{P("Runs")} runs" + (P("Paired") == "1" ? ", paired" : "");
                case BlockKind.Corners: return P("Temperatures").Length > 0 ? P("Temperatures") + " °C" : "no corner set";
                case BlockKind.Combine: return P("Mode");
                case BlockKind.Simulate: return "on this PC (queue)";
                case BlockKind.Measure: return Count("Meas") > 0 || P("Save").Length > 0 ? $"save {P("Save")}" + (Count("Meas") > 0 ? $", {Count("Meas")} .meas" : "") : "nothing chosen";
                case BlockKind.Limits: return Count("Criteria") == 0 ? "no criterion chosen" : P("Criteria");
                case BlockKind.ErrorBudget: return Count("Errors") == 0 ? "every error of the setup" : P("Errors");
                case BlockKind.Report: return P("Title").Length > 0 ? P("Title") : P("Style") + " report";
                case BlockKind.Candidates: return $"{CurrentStudy?.Upstream(b, "candidates").Count() ?? 0} candidates";
                default:
                    var kv = b.Props.FirstOrDefault(x => x.Value.Length > 0);
                    return kv.Key == null ? (b.Spec?.Summary ?? "") : $"{kv.Key}: {kv.Value}";
            }
        }

        // ------------------------------------------------------------------ the block configurator

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
                case "Spec.Measurements": multi = true; return new[] { "all" }.Concat(netlist?.MeasNames ?? new List<string>()).ToList();
                case "Plots.Signals": case "Measure.Save": multi = true; return nets.Select(n => $"V({n})").Concat(netlist?.Currents ?? new List<string>()).ToList();
                case "Combine.Mode": return new List<string> { "product", "union" };
                case "WorstCase.Direction": return new List<string> { "min", "max" };
                case "WorstCase.Method": return new List<string> { "Auto", "Sensitivity", "Corners" };
                case "WorstCase.Objective": return (netlist?.MeasNames ?? new List<string>()).ToList();
                case "Simulate.Where": return new List<string> { "local" };
                case "Report.Style": return new List<string> { "Full", "Concise" };
                default: return null;
            }
        }

        // ------------------------------------------------------------------ the inspector (right side)

        Panel inspector, inspectorBody;
        Label inspectorTitle, inspectorKind, inspectorMissing;
        Panel inspectorHead;
        PictureBox inspectorIcon;
        Block inspected;
        /// <summary>Called by the inspector's fields after each change: marks the setup, redraws, refreshes what is missing.</summary>
        Action inspectorChanged = () => { };

        /// <summary>The inspector: on the right, only while a block is selected; its settings apply at once.</summary>
        Panel BuildInspector()
        {
            inspector = new Panel { Dock = DockStyle.Right, Width = 350, BackColor = Win98.Face, Visible = false, Padding = new Padding(4, 0, 0, 0) };
            var frame = new Panel { Dock = DockStyle.Fill, BackColor = Win98.Face };
            frame.Paint += (s, e) => Win98.Etched(e.Graphics, new Rectangle(0, 0, frame.Width, frame.Height));
            // the header: the family's colour on the left edge, the icon, the block's name and what kind of block it is
            var head = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Win98.Face };
            head.Paint += (s, e) =>
            {
                var fam = inspected?.Spec == null ? Win98.Shadow : StudyCanvas.FamilyColor[inspected.Spec.Family];
                using (var br = new SolidBrush(fam)) e.Graphics.FillRectangle(br, 2, 6, 4, head.Height - 12);
                Win98.EtchedHLine(e.Graphics, 2, head.Width - 2, head.Height - 2);
            };
            inspectorIcon = new PictureBox { Location = new Point(12, 7), Size = new Size(16, 16), BackColor = Win98.Face };
            inspectorTitle = new Label { Location = new Point(32, 6), AutoSize = false, Height = 17, ForeColor = Color.Black, BackColor = Win98.Face, Font = new Font(Win98.Font.FontFamily, 9.75f, FontStyle.Bold), UseMnemonic = false, AutoEllipsis = true };
            inspectorKind = new Label { Location = new Point(32, 23), AutoSize = false, Height = 14, ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false, AutoEllipsis = true };
            var close = new FlatGlyphButton(Glyphs.Cross()) { Size = new Size(20, 20) };
            close.Click += (s, e) => studyCanvas.Select(null);
            new ToolTip().SetToolTip(close, "Close (Esc on the canvas, or click its empty part)");
            head.Controls.AddRange(new Control[] { inspectorIcon, inspectorTitle, inspectorKind, close });
            head.Resize += (s, e) => { close.Location = new Point(head.Width - 26, 6); inspectorTitle.Width = inspectorKind.Width = close.Left - 36; };
            inspectorHead = head;
            inspectorMissing = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 0, BackColor = Color.FromArgb(255, 235, 235), ForeColor = Color.FromArgb(150, 0, 0), Padding = new Padding(6, 4, 6, 4), UseMnemonic = false, Visible = false };
            inspectorBody = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Win98.Face };
            frame.Controls.Add(inspectorBody);
            frame.Controls.Add(inspectorMissing);
            frame.Controls.Add(head);
            inspector.Controls.Add(frame);
            return inspector;
        }

        /// <summary>Shows the block selected in the inspector (or hides it when nothing is selected).</summary>
        void ShowInspector(Block b)
        {
            inspected = b;
            var st = CurrentStudy;
            if (b == null || st == null) { inspector.Visible = false; return; }
            inspectorIcon.Image = StudyCanvas.KindIcon(b.Kind);
            inspectorTitle.Text = b.Spec?.Title ?? b.Kind;
            inspectorKind.Text = $"{(b.Spec == null ? "" : BlockKind.FamilyTitle(b.Spec.Family))} block  ·  #{b.Id}";
            inspectorHead.Invalidate();
            inspectorChanged = () => { MarkDirty(); MarkStale(b); studyCanvas.Invalidate(); UpdateMissing(); RefreshModelSoon(); };
            inspector.Visible = true;
            Action rebuild = null;
            rebuild = () => BuildBlockEditor(inspectorBody, b, st, rebuild);
            rebuild();
            UpdateMissing();
        }

        bool modelRefreshQueued;

        /// <summary>The tree again after the typing stops for a moment (a field changes at every key).</summary>
        void RefreshModelSoon()
        {
            if (modelRefreshQueued) return;
            modelRefreshQueued = true;
            var t = new Timer { Interval = 400 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); modelRefreshQueued = false; FillModelTree(); };
            t.Start();
        }

        void UpdateMissing()
        {
            var m = inspected == null ? new List<string>() : BlockProblems(inspected);
            inspectorMissing.Visible = m.Count > 0;
            if (m.Count == 0) return;
            inspectorMissing.Text = "Missing before it can run:\n" + string.Join("\n", m.Select(x => "- " + x));
            inspectorMissing.Height = TextRenderer.MeasureText(inspectorMissing.Text, Win98.Font, new Size(inspector.Width - 24, 0), TextFormatFlags.WordBreak).Height + 10;
        }

        /// <summary>What each setting is called in the inspector, when its key is too terse.</summary>
        static readonly Dictionary<string, string> PropLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Rser"] = "Source R", ["R"] = "Resistance", ["KeepRaw"] = "Keep .raw", ["Meas"] = ".meas lines", ["Save"] = "Save signals",
            ["Temperatures"] = "Temperatures", ["Pessimistic"] = "Pessimistic σ", ["Sigma"] = "Design σ", ["File"] = "File", ["Signal"] = "Signal",
        };

        /// <summary>The unit shown after a short number field.</summary>
        static readonly Dictionary<string, string> PropUnits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SupplyWaveform.Rser"] = "Ω", ["Fault.R"] = "Ω", ["Fault.Start"] = "s", ["Fault.Duration"] = "s", ["MonteCarlo.Runs"] = "runs",
            ["MonteCarlo.Seed"] = "(0 = new)", ["Nominal.Temperature"] = "°C", ["ErrorBudget.Sigma"] = "σ", ["WorstCase.Sigma"] = "σ",
        };

        /// <summary>The width of a field, by what it holds: a number is short, a net or a category medium, a path or a list the whole row.</summary>
        static int FieldWidth(string kindKey, int full)
        {
            if (PropUnits.ContainsKey(kindKey)) return 80;
            switch (kindKey)
            {
                case "SupplyWaveform.Category": return 70;
                case "SupplyWaveform.Net": case "Fault.From": case "Fault.To": case "Susceptibility.Net": return 130;
                case "MonteCarlo.Temperature": return 150;
                case "WorstCase.Direction": case "WorstCase.Method": return 110;
                default: return full;
            }
        }

        /// <summary>What each setting means, shown under its field (the explanations of the classic Setup tab).</summary>
        static readonly Dictionary<string, string> PropHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Schematic.File"] = "The LTspice schematic (.asc). Empty: the one open in SPICA.",
            ["Schematic.Name"] = "Its name in the results: the candidate's name when several schematics are compared.",
            ["SupplyWaveform.Library"] = "A library of test waveforms (a .txt in the qualification folder, one per standard). Its levels and times are templates: check them against the standard.",
            ["SupplyWaveform.Category"] = "The equipment category of the standard: it sets the levels and times of every test.",
            ["SupplyWaveform.Net"] = "The supply net. The voltage source from this net to ground is taken out and the test waveform put in its place.",
            ["SupplyWaveform.Rser"] = "Source resistance in ohms (0 = an ideal source).",
            ["SupplyWaveform.Tests"] = "One case per test ticked. Click a test to see its waveform below.",
            ["Fault.From"] = "The net the fault starts from (e.g. the input).",
            ["Fault.To"] = "The net it goes to: 0 for ground, or another net (e.g. the 28 V rail).",
            ["Fault.R"] = "Resistance of the fault when closed (ohms; SPICE suffixes work: 10m, 1).",
            ["Fault.Start"] = "When the fault closes (seconds from the start of the simulation, e.g. 10m).",
            ["Fault.Duration"] = "How long it stays closed; empty or 0: to the end. The schematic's own .tran sets the length.",
            ["Measure.Save"] = "Signals kept in the .raw file, to look at on the Waveforms page. What is not saved cannot be shown.",
            ["Measure.Meas"] = "More .meas statements, one per line, e.g.  tran vpk MAX V(out) FROM=1m TO=2m",
            ["Nominal.Temperature"] = "Empty: as in the schematic. A number: every tolerance nominal, at that temperature (°C).",
            ["MonteCarlo.Runs"] = "How many units are drawn. 1000 is a good start for mean ± 6 sigma; the error budget says when more are needed.",
            ["MonteCarlo.Paired"] = "SPICA draws every random value itself (mc(), gauss(), flat()) and saves them: the same units at every temperature, per-unit drift, and the sensitivity of each tolerance. Cleared: LTspice draws them.",
            ["MonteCarlo.Seed"] = "0: a new seed for every campaign. A number: the same units again.",
            ["MonteCarlo.Temperature"] = "Empty: as in the schematic. A number (°C) for every run, or  random -40 .. 90  for a random temperature per run.",
            ["Corners.Temperatures"] = "°C, separated by ;  One condition per temperature, every tolerance nominal (combine it with Monte Carlo for N units at each).",
            ["Corners.Edges"] = "Tolerance edges (e.g.  C_bulk: min). Not used yet in this version: only the temperatures are.",
            ["WorstCase.Objective"] = "The measurement pushed (a .meas).",
            ["WorstCase.Direction"] = "min or max: which way is worse for it.",
            ["Simulate.KeepRaw"] = "Keep the .raw waveform files: the Waveforms page and the criteria plots need them.",
            ["Simulate.Save"] = "Signals saved besides what the criteria, Plots and Measure blocks need. Empty: only those.",
            ["ErrorBudget.Sigma"] = "The error must stay inside its boundaries at mean ± this many sigma (6 = very conservative, 3 = 99.73 %).",
            ["ErrorBudget.Pessimistic"] = "Sigma from n runs is itself uncertain. Ticked: its 95 % upper bound is used (about +12 % at 200 runs, +4 % at 2000).",
            ["Spec.Measurements"] = "The measurements judged against their spec limits (LSL / USL, set on Results > Summary). all: every one that has limits.",
            ["Plots.Signals"] = "Signals saved and drawn with every case on the Waveforms page.",
            ["Report.Title"] = "The heading of the report. Empty: the study's name.",
            ["Export.File"] = "The CSV of the verdicts. Empty: beside the study's campaigns.",
            ["PartsDatabase.Adapter"] = "A program of yours: it gets part numbers and answers their ratings (JSON or CSV).",
            ["Derating.Rules"] = "The rules file of the internal standard: allowed share of each rating, by part family.",
        };

        /// <summary>Settings not shown in the configurator (not used in this version, or set elsewhere).</summary>
        static readonly HashSet<string> HiddenProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "MonteCarlo.Stop", "Simulate.Where", "Report.Sections" };

        /// <summary>
        /// The inspector's fields for a block, in order: a label on the left, the field sized to what it holds (a number
        /// short, with its unit; a net medium; a path or a list the whole row), what it means under it. Some kinds have
        /// more: a waveform preview, the schematic's contents, the setup's criteria or errors. Changes apply at once.
        /// </summary>
        void BuildBlockEditor(Panel host, Block b, Study st, Action rebuild)
        {
            host.SuspendLayout();
            int scroll = host.VerticalScroll.Value;
            foreach (Control c in host.Controls.Cast<Control>().ToList()) c.Dispose();
            host.Controls.Clear();
            const int LW = 86;                                   // the label column
            int y = 6, w = host.ClientSize.Width - 14 - SystemInformation.VerticalScrollBarWidth, fx = 6 + LW, fwFull = w - LW;
            Label Text(string t, Font f = null, Color? col = null, int indent = 0)
            {
                var l = new Label { Text = t, Location = new Point(6 + indent, y), Width = w - indent, AutoSize = false, Font = f ?? Win98.Font, ForeColor = col ?? Color.Black, UseMnemonic = false };
                l.Height = TextRenderer.MeasureText(t, l.Font, new Size(w - indent - 28, 0), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 2;
                host.Controls.Add(l);
                y += l.Height + 2;
                return l;
            }
            void Section(string t)
            {
                y += 8;
                Text(t, Win98.BoldFont, Win98.Navy);
                var line = new Panel { Location = new Point(6, y), Size = new Size(w, 2), BackColor = Win98.Face };
                line.Paint += (s, e) => Win98.EtchedHLine(e.Graphics, 0, line.Width, 0);
                host.Controls.Add(line);
                y += 6;
            }
            void Hint(string key, bool underField)
            {
                if (!PropHints.TryGetValue(b.Kind + "." + key, out var h)) return;
                Text(h, null, Win98.Shadow, underField ? LW : 0);
                y += 4;
            }
            Label RowLabel(string key)
            {
                var l = new Label { Text = (PropLabels.TryGetValue(key, out var n) ? n : key) + ":", Location = new Point(6, y + 3), Width = LW - 4, AutoEllipsis = true, UseMnemonic = false };
                host.Controls.Add(l);
                return l;
            }

            var k = b.Spec;
            if (k != null) Text(k.Summary, null, Win98.Shadow);
            if (!runnableKinds.Contains(b.Kind)) Text("This block does not run yet in this version: a study skips it, and what it feeds.", null, Color.FromArgb(140, 90, 0));

            if (b.Kind == BlockKind.Schematic) y = SchematicInfo(host, y, w, b, Section, Text);

            Section("Settings");
            bool any = false;
            for (int idx = 0; idx < b.Props.Count; idx++)
            {
                int index = idx;
                string key = b.Props[idx].Key, val = b.Props[idx].Value, kk = b.Kind + "." + key;
                if (HiddenProps.Contains(kk)) continue;
                any = true;
                void SetVal(string v) { b.Props[index] = new KeyValuePair<string, string>(key, v ?? ""); inspectorChanged(); }

                if (b.Kind == BlockKind.Limits && key == "Criteria") { y = CriteriaEditor(host, y, w, b, index); continue; }
                if (b.Kind == BlockKind.ErrorBudget && key == "Errors") { y = ErrorsEditor(host, y, w, b, index); continue; }

                // a choice between a few ways: buttons, each with what it does
                if ((b.Kind == BlockKind.Combine && key == "Mode") || (b.Kind == BlockKind.Report && key == "Style"))
                {
                    Text((PropLabels.TryGetValue(key, out var nm) ? nm : key) + ":");
                    var opts = b.Kind == BlockKind.Combine
                        ? new[] { ("product", "Product: every condition of one input with every one of the others (Monte Carlo × corners = N units at each temperature)."),
                                  ("union", "Union: the conditions of every input, side by side (corners, plus the worst case found).") }
                        : new[] { ("Full", "Full: every verdict grid, the campaigns and the notes."),
                                  ("Concise", "Concise: the verdict grids only, one per criterion.") };
                    foreach (var (v, d) in opts)
                    {
                        var rb = new Win98RadioButton { Text = d, Location = new Point(14, y), Width = w - 14, Height = 32, Checked = val.Equals(v, StringComparison.OrdinalIgnoreCase) };
                        string vv = v;
                        rb.CheckedChanged += (s, e) => { if (rb.Checked) SetVal(vv); };
                        host.Controls.Add(rb);
                        y += 34;
                    }
                    continue;
                }

                if (BoolProps.Contains(key))
                {
                    RowLabel(key);
                    var cb = new Win98CheckBox { Text = "yes", Checked = val == "1", Location = new Point(fx, y + 1), AutoSize = true };
                    cb.CheckedChanged += (s, e) => SetVal(cb.Checked ? "1" : "0");
                    host.Controls.Add(cb);
                    y += 24;
                    Hint(key, true);
                    continue;
                }
                if (b.Kind == BlockKind.Measure && key == "Meas")
                {
                    Text(PropLabels[key] + ":");
                    var tm = new Win98TextField { Location = new Point(6, y), Width = w, Height = 76, Text = string.Join("\r\n", val.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0)) };
                    tm.Box.Multiline = true; tm.Box.AcceptsReturn = true; tm.Box.ScrollBars = ScrollBars.Vertical; tm.Box.Font = Win98.MonoFont;
                    tm.Box.TextChanged += (s, e) => SetVal(string.Join(" | ", tm.Text.Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0)));
                    host.Controls.Add(tm);
                    y += 82;
                    Hint(key, false);
                    continue;
                }
                var choices = PropChoices(b, key, out bool multi);
                bool browse = b.Kind == BlockKind.Schematic && key == "File" || b.Kind == BlockKind.PartsDatabase && key == "Adapter" || b.Kind == BlockKind.Derating && key == "Rules";
                if (multi && choices != null && choices.Count > 0)
                {
                    // several at once: tick them in a list across the row (what is set but not offered stays, at the end)
                    Text((PropLabels.TryGetValue(key, out var nm) ? nm : key) + ":");
                    bool spaced = key == "Save" || key == "Signals";
                    var now = (spaced ? val.Split(new[] { '|', ' ' }, StringSplitOptions.RemoveEmptyEntries) : val.Split('|')).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    var items = choices.Concat(now.Where(x => !choices.Contains(x, StringComparer.OrdinalIgnoreCase))).ToList();
                    var list = new CheckedListBox { Location = new Point(6, y), Size = new Size(w, Math.Min(160, 20 + 16 * items.Count)), CheckOnClick = true, Font = Win98.Font, IntegralHeight = false, HorizontalScrollbar = true };
                    foreach (var it in items) list.Items.Add(it, now.Contains(it, StringComparer.OrdinalIgnoreCase));
                    list.ItemCheck += (s, e) => host.BeginInvoke(new Action(() => SetVal(string.Join(spaced ? " " : " | ", list.CheckedItems.Cast<string>()))));
                    host.Controls.Add(list);
                    y += list.Height + 4;
                    Hint(key, false);
                    if (b.Kind == BlockKind.SupplyWaveform && key == "Tests") y = WavePreview(host, y, w, b, list);
                    continue;
                }

                RowLabel(key);
                int fw = Math.Min(FieldWidth(kk, fwFull), fwFull - (browse || multi ? 28 : 0));
                if (choices != null && !multi)
                {
                    var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Location = new Point(fx, y), Width = fw, Font = Win98.Font };
                    combo.Items.AddRange(choices.Cast<object>().ToArray());
                    combo.Text = val;
                    combo.TextChanged += (s, e) => SetVal(combo.Text);
                    if (b.Kind == BlockKind.SupplyWaveform && (key == "Library" || key == "Category"))
                        combo.SelectionChangeCommitted += (s, e) => host.BeginInvoke(new Action(() => { SetVal(combo.SelectedItem as string ?? combo.Text); rebuild?.Invoke(); }));
                    host.Controls.Add(combo);
                }
                else
                {
                    var tf = new Win98TextField { Location = new Point(fx, y), Width = fw, Text = val };
                    tf.Box.TextChanged += (s, e) => SetVal(tf.Text);
                    host.Controls.Add(tf);
                    if (multi || browse)
                    {
                        var more = new Win98Button { Text = "...", Location = new Point(fx + fw + 4, y - 1), Size = new Size(24, 22) };
                        more.Click += (s, e) =>
                        {
                            string got = multi ? PickSeveral(key, choices ?? new List<string>(), tf.Text) : BrowseFor(b, key, tf.Text);
                            if (got != null) { tf.Text = got; if (b.Kind == BlockKind.Schematic) rebuild?.Invoke(); }
                        };
                        host.Controls.Add(more);
                    }
                }
                if (PropUnits.TryGetValue(kk, out var unit))
                    host.Controls.Add(new Label { Text = unit, Location = new Point(fx + fw + 4, y + 3), AutoSize = true, ForeColor = Color.FromArgb(64, 64, 64) });
                y += 26;
                Hint(key, true);
            }

            if (b.Kind == BlockKind.Simulate)
            {
                any = true;
                RowLabel("Output folder");
                var tf = new Win98TextField { Location = new Point(fx, y), Width = fwFull - 28, Text = txtOut.Text };
                tf.Box.TextChanged += (s, e) => { txtOut.Text = tf.Text; inspectorChanged(); };
                var more = new Win98Button { Text = "...", Location = new Point(fx + fwFull - 24, y - 1), Size = new Size(24, 22) };
                more.Click += (s, e) => { using (var d = new FolderBrowserDialog { SelectedPath = tf.Text }) if (d.ShowDialog(this) == DialogResult.OK) tf.Text = d.SelectedPath; };
                host.Controls.Add(tf); host.Controls.Add(more);
                y += 26;
                Text("Every study of this setup puts its campaigns under <folder>\\studies\\<study>. Empty: beside the schematic. Keep it short: LTspice cannot open files past 260 characters of path.", null, Win98.Shadow, LW);
                Section("This PC");
                Text($"LTspice instances and threads: as set in Tools > Options ({(settings.AutoParallel ? "automatic: the scheduler finds the best number" : "fixed")}). " +
                     "The campaigns of a study run one after the other in the queue.", null, Win98.Shadow);
            }
            if (!any) Text("This block has no settings.", null, Win98.Shadow);

            if (k != null && (k.Inputs.Count > 0 || k.Outputs.Count > 0))
            {
                Section("Wiring");
                foreach (var i in k.Inputs)
                {
                    var from = st.Upstream(b, i.Name).Select(x => x.Spec?.Title ?? x.Kind).ToList();
                    Text($"in   {i.Name} ({i.Type}): " + (from.Count == 0 ? (i.Optional ? "-" : "not wired") : string.Join(", ", from)), null, from.Count == 0 && !i.Optional ? Color.FromArgb(160, 0, 0) : Color.Black);
                }
                foreach (var o in k.Outputs)
                {
                    var to = st.Wires.Where(x => x.From == b.Id && x.FromPort == o.Name).Select(x => st[x.To]).Where(x => x != null).Select(x => x.Spec?.Title ?? x.Kind).ToList();
                    Text($"out  {o.Name} ({o.Type}): " + (to.Count == 0 ? "-" : string.Join(", ", to)));
                }
            }
            host.Controls.Add(new Label { Location = new Point(6, y), Size = new Size(1, 10) });
            host.ResumeLayout();
            if (scroll > 0) host.AutoScrollPosition = new Point(0, scroll);
        }

        /// <summary>Schematic: what the circuit holds (nets, .meas, currents, tolerances), and a button to open it in LTspice.</summary>
        int SchematicInfo(Panel host, int y, int w, Block b, Action<string> section, Func<string, Font, Color?, int, Label> text)
        {
            string file = b.Get("File").Trim().Length > 0 ? b.Get("File").Trim() : project.Schematic ?? "";
            section("The circuit");
            if (file.Length == 0 || !File.Exists(file)) { text(file.Length == 0 ? "No schematic chosen yet." : "Not found: " + file, null, Color.FromArgb(160, 0, 0), 0); return HostY(host); }
            Netlist nl = null;
            if (netlist != null && SameFile(file, ascPath)) nl = netlist;
            if (nl == null) text(Path.GetFileName(file) + ": open it in SPICA (File > Open) to see its nets and measurements here.", null, Win98.Shadow, 0);
            else
            {
                text($"{Path.GetFileName(file)}:  {nl.Nodes.Count} nets,  {nl.Currents.Count} currents,  {nl.MeasNames.Count} .meas", null, null, 0);
                if (nl.MeasNames.Count > 0) text("Measurements: " + string.Join(", ", nl.MeasNames), null, Win98.Shadow, 0);
                text("Nets: " + string.Join(", ", nl.Nodes.Take(40)) + (nl.Nodes.Count > 40 ? ", ..." : ""), null, Win98.Shadow, 0);
            }
            int yy = HostY(host);
            var open = new Win98Button { Text = "Open in &LTspice", Location = new Point(4, yy + 2), Width = 120 };
            open.Click += (s, e) =>
            {
                string lt = settings.LtspicePath ?? "";
                try { if (File.Exists(lt)) System.Diagnostics.Process.Start(lt, "\"" + file + "\""); else System.Diagnostics.Process.Start(file); } catch { }
            };
            host.Controls.Add(open);
            return yy + 30;
        }

        static int HostY(Panel host) => host.Controls.Cast<Control>().Select(c => c.Bottom + 2).DefaultIfEmpty(4).Max();

        /// <summary>Supply Waveform: the waveform of the test chosen in the list, with its events.</summary>
        int WavePreview(Panel host, int y, int w, Block b, CheckedListBox tests)
        {
            var chart = new Win98Chart { Location = new Point(4, y + 2), Size = new Size(w, 170), EmptyText = "Click a test above to see its waveform." };
            host.Controls.Add(chart);
            void Show()
            {
                chart.Clear();
                chart.EmptyText = "Click a test above to see its waveform.";
                var lib = qualPages.Select(q => q.Lib).FirstOrDefault(l => l.Key.Equals(b.Get("Library"), StringComparison.OrdinalIgnoreCase));
                string name = tests.SelectedItem as string ?? tests.CheckedItems.Cast<string>().FirstOrDefault();
                var t = lib?.TestsFor(b.Get("Category")).FirstOrDefault(x => x.Name == name);
                if (t == null) return;
                var pts = QualLibrary.Waveform(t, lib.Variables(b.Get("Category")), out var err);
                if (err != null) { chart.EmptyText = "Cannot draw it: " + err; chart.Invalidate(); return; }
                chart.Title = $"{t.Name} (category {b.Get("Category")})";
                chart.XLabel = "time (s)"; chart.YLabel = "V";
                chart.Add(new ChartSeries { Kind = SeriesKind.Line, Label = "supply", Color = Win98Chart.Navy, Size = 2, X = pts.Select(p => p[0]).ToArray(), Y = pts.Select(p => p[1]).ToArray() });
                var ev = QualEvents.Of(pts);
                if (ev.End > ev.Start) chart.Add(new ChartSeries { Kind = SeriesKind.VLine, Label = "event start / end", Color = Win98Chart.Silver, Dashed = true, X = new[] { ev.Start, ev.End } });
            }
            tests.SelectedIndexChanged += (s, e) => Show();
            Show();
            return y + 178;
        }

        /// <summary>Error Budget: the setup's errors, ticked when this block judges them (none ticked: all), with New / Edit / Delete.</summary>
        int ErrorsEditor(Panel host, int y, int w, Block b, int index)
        {
            host.Controls.Add(new Label { Text = "Errors judged (the setup's list; none ticked: every one):", Location = new Point(4, y), Width = w, Height = 16 });
            y += 18;
            var list = new CheckedListBox { Location = new Point(4, y), Size = new Size(w - 92, 130), CheckOnClick = true, Font = Win98.Font, IntegralHeight = false, HorizontalScrollbar = true };
            var picked = new HashSet<string>(b.Props[index].Value.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
            void Fill()
            {
                list.Items.Clear();
                foreach (var e in project.Errors) list.Items.Add(new ErrItem(e), picked.Contains(e.Name));
            }
            void Store()
            {
                picked = new HashSet<string>(list.CheckedItems.Cast<ErrItem>().Select(x => x.E.Name), StringComparer.OrdinalIgnoreCase);
                b.Props[index] = new KeyValuePair<string, string>("Errors", string.Join(" | ", project.Errors.Where(e => picked.Contains(e.Name)).Select(e => e.Name)));
                inspectorChanged();
            }
            list.ItemCheck += (s, e) => host.BeginInvoke(new Action(Store));
            Fill();
            // the measurements an error is made of: the schematic's .meas, and those of the Measure blocks
            List<string> Meas() => (netlist?.MeasNames ?? new List<string>())
                .Concat(CurrentStudy?.Blocks.Where(x => x.Kind == BlockKind.Measure).SelectMany(x => x.Get("Meas").Split('|'))
                        .Select(m => m.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Where(t => t.Length > 2).Select(t => t[0].StartsWith(".") ? t[2] : t[1]) ?? new string[0])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var bNew = new Win98Button { Text = "&New...", Location = new Point(w - 82, y), Width = 84 };
            var bEdit = new Win98Button { Text = "&Edit...", Location = new Point(w - 82, y + 28), Width = 84 };
            var bDel = new Win98Button { Text = "&Delete", Location = new Point(w - 82, y + 56), Width = 84 };
            bNew.Click += (s, e) =>
            {
                using (var f = new ErrorItemForm(null, Meas(), project.Errors.Select(x => x.Name)))
                {
                    if (f.ShowDialog(this) != DialogResult.OK) return;
                    project.Errors.Add(f.Result);
                    picked.Add(f.Result.Name);
                    MarkDirty(); Fill(); Store();
                }
            };
            void Edit()
            {
                if (!(list.SelectedItem is ErrItem it)) return;
                using (var f = new ErrorItemForm(it.E, Meas(), project.Errors.Where(x => x != it.E).Select(x => x.Name)))
                {
                    if (f.ShowDialog(this) != DialogResult.OK) return;
                    bool was = picked.Remove(it.E.Name);
                    project.Errors[project.Errors.IndexOf(it.E)] = f.Result;
                    if (was) picked.Add(f.Result.Name);
                    MarkDirty(); Fill(); Store();
                }
            }
            bEdit.Click += (s, e) => Edit();
            list.DoubleClick += (s, e) => Edit();
            bDel.Click += (s, e) =>
            {
                if (!(list.SelectedItem is ErrItem it)) return;
                if (Win98MessageBox.Show(this, $"Delete the error \"{it.E.Name}\" from the setup?", "SPICA", MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;
                project.Errors.Remove(it.E);
                picked.Remove(it.E.Name);
                MarkDirty(); Fill(); Store();
            };
            host.Controls.AddRange(new Control[] { list, bNew, bEdit, bDel });
            y += 136;
            var note = new Label
            {
                Text = (netlist?.MeasNames.Count ?? 0) == 0
                    ? "The schematic has no .meas yet: an error is made of measurements (add .meas lines to the schematic, or a Measure block)."
                    : "An error is a .meas, or an output compared with a reference. New, Edit and Delete change the setup's errors at once (they serve every study).",
                Location = new Point(4, y), Width = w, Height = 30, ForeColor = (netlist?.MeasNames.Count ?? 0) == 0 ? Color.FromArgb(160, 0, 0) : Win98.Shadow,
            };
            host.Controls.Add(note);
            return y + 34;
        }

        sealed class ErrItem
        {
            public readonly ErrorItem E;
            public ErrItem(ErrorItem e) { E = e; }
            public override string ToString()
            {
                string b = E.Symmetric && !double.IsNaN(E.Upper) ? $"± {ErrorBudget.Eng(E.Upper)}"
                         : $"{(double.IsNaN(E.Lower) ? "" : ErrorBudget.Eng(E.Lower))} .. {(double.IsNaN(E.Upper) ? "" : ErrorBudget.Eng(E.Upper))}";
                return $"{E.Name}:  {(E.UsesTf ? E.Definition() : E.Y)}  within {b}" + (E.Unit == BudgetUnit.PctReading ? " % of reading" : E.Unit == BudgetUnit.PctFs ? " % of FS" : "");
            }
        }

        /// <summary>Limits: the setup's criteria, ticked when this block judges them; New / Edit / Delete act on the setup's list.</summary>
        int CriteriaEditor(Panel host, int y, int w, Block b, int index)
        {
            var title = new Label { Text = "Criteria judged (the setup's list; tick the ones this block judges):", Location = new Point(4, y), Width = w, AutoSize = false, Height = 16 };
            host.Controls.Add(title);
            y += 18;
            var list = new CheckedListBox { Location = new Point(4, y), Size = new Size(w - 92, 150), CheckOnClick = true, Font = Win98.Font, IntegralHeight = false, HorizontalScrollbar = true };
            var picked = new HashSet<string>(b.Props[index].Value.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
            void Fill()
            {
                list.Items.Clear();
                foreach (var c in project.QualCriteria)
                    list.Items.Add(new CritItem(c), picked.Contains(c.Name));
            }
            void Store()
            {
                picked = new HashSet<string>(list.CheckedItems.Cast<CritItem>().Select(x => x.C.Name), StringComparer.OrdinalIgnoreCase);
                b.Props[index] = new KeyValuePair<string, string>("Criteria", string.Join(" | ", project.QualCriteria.Where(c => picked.Contains(c.Name)).Select(c => c.Name)));
                inspectorChanged();
            }
            list.ItemCheck += (s, e) => host.BeginInvoke(new Action(Store));
            Fill();
            var bNew = new Win98Button { Text = "&New...", Location = new Point(w - 82, y), Width = 84 };
            var bEdit = new Win98Button { Text = "&Edit...", Location = new Point(w - 82, y + 28), Width = 84 };
            var bDel = new Win98Button { Text = "&Delete", Location = new Point(w - 82, y + 56), Width = 84 };
            List<string> Signals() => (netlist?.Nodes ?? new List<string>()).Select(n => $"V({n})").Concat(netlist?.Currents ?? new List<string>()).ToList();
            bNew.Click += (s, e) =>
            {
                using (var f = new QualCriterionForm(null, Signals(), project.QualCriteria.Select(x => x.Name)))
                {
                    if (f.ShowDialog(this) != DialogResult.OK) return;
                    project.QualCriteria.Add(f.Result);
                    picked.Add(f.Result.Name);
                    MarkDirty(); Fill(); Store();
                }
            };
            void Edit()
            {
                if (!(list.SelectedItem is CritItem it)) return;
                using (var f = new QualCriterionForm(it.C, Signals(), project.QualCriteria.Where(x => x != it.C).Select(x => x.Name)))
                {
                    if (f.ShowDialog(this) != DialogResult.OK) return;
                    bool was = picked.Remove(it.C.Name);
                    project.QualCriteria[project.QualCriteria.IndexOf(it.C)] = f.Result;
                    if (was) picked.Add(f.Result.Name);
                    MarkDirty(); Fill(); Store();
                }
            }
            bEdit.Click += (s, e) => Edit();
            list.DoubleClick += (s, e) => Edit();
            bDel.Click += (s, e) =>
            {
                if (!(list.SelectedItem is CritItem it)) return;
                if (Win98MessageBox.Show(this, $"Delete the criterion \"{it.C.Name}\" from the setup? Other studies that use it lose it too.", "SPICA", MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;
                project.QualCriteria.Remove(it.C);
                picked.Remove(it.C.Name);
                MarkDirty(); Fill(); Store();
            };
            host.Controls.AddRange(new Control[] { list, bNew, bEdit, bDel });
            y += 156;
            var note = new Label { Text = "New, Edit and Delete change the setup's criteria at once (they serve every study); the ticks are kept with OK.", Location = new Point(4, y), Width = w, Height = 28, ForeColor = Win98.Shadow };
            host.Controls.Add(note);
            return y + 32;
        }

        sealed class CritItem
        {
            public readonly QualCriterion C;
            public CritItem(QualCriterion c) { C = c; }
            public override string ToString() => $"{C.Name}:  {C.RuleText()},  {C.WindowText()}" + (C.Requirement.Length > 0 ? $"  [{C.Requirement}]" : "");
        }

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

    /// <summary>A block's configurator: its fields in a scrolling area, OK and Cancel.</summary>
    sealed class BlockConfigForm : Win98Form
    {
        public readonly Panel Body;

        public BlockConfigForm(string title)
        {
            Text = title;
            MinimizeBox = false; MaximizeBox = false; Resizable = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 620 + Padding.Vertical);
            Body = new Panel { Location = new Point(Padding.Left + 6, Padding.Top + 6), Size = new Size(ClientSize.Width - Padding.Horizontal - 12, ClientSize.Height - Padding.Vertical - 52), AutoScroll = true, BackColor = Win98.Face };
            Controls.Add(Body);
            int by = ClientSize.Height - Padding.Bottom - 33;
            var ok = new Win98Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(ClientSize.Width - Padding.Right - 176, by), Size = new Size(80, 23) };
            var cancel = new Win98Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(ClientSize.Width - Padding.Right - 88, by), Size = new Size(80, 23) };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }
    }

    /// <summary>Workbench > New: a name and where to start from (blank, a template, or the setup's own settings).</summary>
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
