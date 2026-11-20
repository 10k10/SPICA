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
    /// The Workbench: the study editor, where every analysis starts. On the left, the study shown (chosen, made,
    /// renamed), its blocks as a tree with their state, and the block library; in the middle the canvas; on the right,
    /// while a block is selected, its settings. A block that misses something it needs to run has a red border.
    /// </summary>
    public partial class MainForm
    {
        StudyCanvas studyCanvas;
        ComboBox cbStudy;
        TreeView tvBlocks, tvModel;
        Label lblBlockInfo, lblNoStudy;
        bool fillingStudies, fillingModel;
        /// <summary>Blocks changed since the study last ran (they and what they feed run again).</summary>
        readonly HashSet<int> staleBlocks = new HashSet<int>();

        Study CurrentStudy => cbStudy?.SelectedIndex >= 0 && cbStudy.SelectedIndex < project.Studies.Count ? project.Studies[cbStudy.SelectedIndex] : null;

        void BuildStudiesPage()
        {
            var p = pageStudies;
            var tip = new ToolTip();

            // --- left: the study shown (and New / Rename / Delete beside it), its tree, the block library
            var leftCol = new Panel { Dock = DockStyle.Left, Width = 250, BackColor = Win98.Face };
            var studyRow = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Win98.Face };
            var lblStudy = new Label { Text = "&Study:", Location = new Point(0, 6), AutoSize = true };
            cbStudy = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Win98.Font };
            cbStudy.SelectedIndexChanged += (s, e) => { if (!fillingStudies) ShowStudy(); };
            // the list opens as wide as its longest name
            cbStudy.DropDown += (s, e) =>
                cbStudy.DropDownWidth = Math.Max(cbStudy.Width, cbStudy.Items.Cast<object>().Select(x => TextRenderer.MeasureText(x.ToString(), cbStudy.Font).Width + 24).DefaultIfEmpty(0).Max());
            Win98Button Small(Image img, string hint, Action click)
            {
                var b = new Win98Button { Image = img, Size = new Size(23, 22) };
                b.Click += (s, e) => click();
                tip.SetToolTip(b, hint);
                studyRow.Controls.Add(b);
                return b;
            }
            var bNew = Small(Win98.Icon("doc16"), "New study (Ctrl+N)", NewStudy);
            var bRename = Small(Glyphs.Pencil(), "Rename the study", RenameStudy);
            var bDelete = Small(Glyphs.Cross(), "Delete the study", DeleteStudy);
            studyRow.Controls.Add(lblStudy);
            studyRow.Controls.Add(cbStudy);
            studyRow.Resize += (s, e) =>
            {
                int x = studyRow.Width;
                foreach (var b in new[] { bDelete, bRename, bNew }) { x -= b.Width; b.Location = new Point(x, 1); x -= 1; }
                cbStudy.SetBounds(lblStudy.Right + 4, 2, x - 3 - lblStudy.Right - 4, 21);
            };

            var grpModel = new Win98GroupBox { Text = "Study", Dock = DockStyle.Top, Height = 240 };
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
            leftCol.Resize += (s, e) => grpModel.Height = Math.Max(180, leftCol.Height * 40 / 100);

            var library = new Win98GroupBox { Text = "Blocks", Dock = DockStyle.Fill };
            var images = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            foreach (var k in BlockKind.All) images.Images.Add(k.Name, StudyCanvas.KindIcon(k.Name));
            images.Images.Add("folder", Win98.Icon("folder16"));
            tvBlocks = new TreeView { Font = Win98.Font, ImageList = images, BorderStyle = BorderStyle.Fixed3D, HideSelection = false, ShowLines = true, ItemHeight = 18 };
            tvBlocks.AfterSelect += (s, e) => ShowBlockInfo(e.Node?.Tag as BlockKind);
            tvBlocks.NodeMouseDoubleClick += (s, e) => { if (e.Node?.Tag is BlockKind k && CurrentStudy != null) { studyCanvas.AddBlock(k.Name); SetStatus($"{k.Title} added: set it up on the right, and wire its ports."); } };
            tvBlocks.ItemDrag += (s, e) => { if (e.Item is TreeNode n && n.Tag is BlockKind k) tvBlocks.DoDragDrop(k.Name, DragDropEffects.Copy); };
            lblBlockInfo = new Label { BackColor = Win98.InfoBack, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(4), UseMnemonic = false, Font = Win98.Font };
            library.Controls.AddRange(new Control[] { tvBlocks, lblBlockInfo });
            library.Resize += (s, e) =>
            {
                int w = library.Width - 16;
                lblBlockInfo.SetBounds(8, library.Height - 8 - 96, w, 96);
                tvBlocks.SetBounds(8, 20, w, lblBlockInfo.Top - 6 - 20);
            };

            // --- the canvas -----------------------------------------------------------------------
            studyCanvas = new StudyCanvas { Dock = DockStyle.Fill, Caption = BlockCaption, Problems = BlockProblems };
            studyCanvas.StudyChanged += (s, e) => StudyEdited();
            studyCanvas.Message += m => SetStatus(m);
            studyCanvas.BlockOpened += b => { studyCanvas.Select(b); ShowInspector(b); inspectorBody.Focus(); };
            studyCanvas.SelectionChanged += (s, e) => { ShowInspector(studyCanvas.Selected); SelectInModel(studyCanvas.Selected); };
            lblNoStudy = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Win98.Shadow, BackColor = Color.White, UseMnemonic = false,
                Text = "Open an LTspice schematic (File > Open, or drop it here).\n\nSPICA makes a study of it, ready to run: the schematic, every tolerance nominal, simulated.",
            };
            studyCanvas.Controls.Add(lblNoStudy);
            var well = new Win98Well(studyCanvas) { Dock = DockStyle.Fill };
            var hint = new Label
            {
                Dock = DockStyle.Bottom, Height = 20, ForeColor = Win98.Shadow, BackColor = Win98.Face, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                // the wire colours take the right end
                Padding = new Padding(0, 0, StudyCanvas.TypeColor.Sum(kv => 18 + TextRenderer.MeasureText(kv.Key.ToString(), Win98.Font).Width) + 16, 0),
                Text = "Click a block to set it up (red: something missing).  Drag an output to an input of its colour.  Right-click a port for a block that fits.  Space: fit.  Del: remove.",
            };
            hint.Paint += (s, e) =>
            {
                int w = StudyCanvas.TypeColor.Sum(kv => 18 + TextRenderer.MeasureText(kv.Key.ToString(), Win98.Font).Width);
                int x = hint.Width - w - 4;
                using (var bg = new SolidBrush(Win98.Face)) e.Graphics.FillRectangle(bg, x - 8, 0, w + 12, hint.Height);
                foreach (var kv in StudyCanvas.TypeColor)
                {
                    using (var pen = new Pen(kv.Value, 3)) e.Graphics.DrawLine(pen, x, 10, x + 12, 10);
                    TextRenderer.DrawText(e.Graphics, kv.Key.ToString(), Win98.Font, new Point(x + 14, 3), Color.FromArgb(64, 64, 64));
                    x += 18 + TextRenderer.MeasureText(kv.Key.ToString(), Win98.Font).Width;
                }
            };
            p.Controls.Add(well);
            p.Controls.Add(BuildInspector());
            p.Controls.Add(hint);
            leftCol.Controls.Add(library);
            leftCol.Controls.Add(Gap(DockStyle.Top));
            leftCol.Controls.Add(grpModel);
            leftCol.Controls.Add(Gap(DockStyle.Top, 4));
            leftCol.Controls.Add(studyRow);
            p.Controls.Add(Gap(DockStyle.Left));
            p.Controls.Add(leftCol);
            p.Controls.Add(Gap(DockStyle.Top, 4));

            FillBlockTree();
            ShowBlockInfo(null);
        }

        // ------------------------------------------------------------------ library

        void FillBlockTree()
        {
            tvBlocks.BeginUpdate();
            tvBlocks.Nodes.Clear();
            foreach (BlockFamily f in Enum.GetValues(typeof(BlockFamily)))
            {
                var fam = new TreeNode(f.ToString()) { ImageKey = "folder", SelectedImageKey = "folder" };
                foreach (var k in BlockKind.All.Where(k => k.Family == f))
                    fam.Nodes.Add(new TreeNode(k.Title) { Tag = k, ImageKey = k.Name, SelectedImageKey = k.Name });
                tvBlocks.Nodes.Add(fam);
                fam.Expand();
            }
            tvBlocks.EndUpdate();
        }

        static string Types(List<PortSpec> ports) =>
            ports.Count == 0 ? "nothing" : string.Join(", ", ports.Select(p => p.Type.ToString() + (p.Many ? " (several)" : "")).Distinct());

        void ShowBlockInfo(BlockKind k)
        {
            lblBlockInfo.Text = k == null
                ? "Choose a block to read what it does.\n\nDouble-click it, or drag it onto the canvas, to add it to the study; then click it there to set it up."
                : $"{k.Title}\n{k.Summary}\n\nTakes: {Types(k.Inputs)}\nGives: {Types(k.Outputs)}";
        }

        // ------------------------------------------------------------------ studies

        /// <summary>The project's studies into the Workbench (when the project changes).</summary>
        void ShowStudies()
        {
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
            lblNoStudy.Visible = st == null;
            if (st != null) studyCanvas.FitAll();
            FillModelTree();
        }

        void StudyEdited()
        {
            MarkDirty();
            studyCanvas.Invalidate();
            FillModelTree();
        }

        void NewStudy()
        {
            if (project.Schematic.Length == 0 && project.Studies.Count == 0) { BrowseOpen(); return; }
            string name = Win98Prompt.Ask(this, "New Study", "&Name:", UniqueStudyName(project.Schematic.Length > 0 ? Path.GetFileNameWithoutExtension(project.Schematic) : "Study"));
            if (name == null) return;
            var st = project.Schematic.Length > 0 ? DefaultStudy(project.Schematic) : new Study();
            st.Name = UniqueStudyName(name);
            project.Studies.Add(st);
            MarkDirty();
            ShowStudies();
            cbStudy.SelectedIndex = project.Studies.IndexOf(st);
            SetStatus($"Study \"{st.Name}\" added: the schematic, nominal, simulated. Add or change blocks from the library.");
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
            fillingStudies = true;
            cbStudy.Items[cbStudy.SelectedIndex] = st.Name;
            fillingStudies = false;
            MarkDirty();
            FillModelTree();
        }

        void DeleteStudy()
        {
            var st = CurrentStudy;
            if (st == null) return;
            if (Win98MessageBox.Show(this, $"Delete the study \"{st.Name}\" from the project?\n(Its results stay on disk.)", "SPICA", MessageBoxButtons.YesNo, Win98Icon.Question) != DialogResult.Yes) return;
            project.Studies.Remove(st);
            MarkDirty();
            ShowStudies();
        }

        // ------------------------------------------------------------------ the study tree

        static readonly (string Title, BlockFamily Family)[] ModelGroups =
        {
            ("Circuit", BlockFamily.Circuit), ("Analyses", BlockFamily.Analysis), ("Simulation", BlockFamily.Simulation),
        };

        /// <summary>A block's state in the tree: missing something, changed since it ran, done, or not run yet.</summary>
        string ModelState(Block b)
        {
            if (BlockProblems(b).Count > 0) return "bang";
            bool ran = studyCanvas.States.TryGetValue(b.Id, out var s) && (s == BlockState.Done || s == BlockState.Cached);
            if (ran && staleBlocks.Contains(b.Id)) return "bolt";
            return ran ? "ok" : "ring";
        }

        /// <summary>The study by step, each block with its state.</summary>
        void FillModelTree()
        {
            var st = CurrentStudy;
            fillingModel = true;
            tvModel.BeginUpdate();
            tvModel.Nodes.Clear();
            if (st != null)
            {
                var rootNode = new TreeNode(st.Name) { ImageKey = "study", SelectedImageKey = "study", NodeFont = Win98.BoldFont };
                foreach (var (title, fam) in ModelGroups)
                {
                    var blocks = st.Blocks.Where(b => b.Spec?.Family == fam).OrderBy(b => b.Y).ThenBy(b => b.X).ToList();
                    var g = new TreeNode($"{title} ({blocks.Count})") { ImageKey = "folder", SelectedImageKey = "folder" };
                    foreach (var b in blocks)
                    {
                        string state = ModelState(b);
                        var n = new TreeNode($"{b.Spec.Title}: {BlockCaption(b)}") { Tag = b, ImageKey = state, SelectedImageKey = state };
                        if (state == "bang") n.ForeColor = Color.FromArgb(170, 0, 0);
                        g.Nodes.Add(n);
                    }
                    rootNode.Nodes.Add(g);
                }
                // blocks this version does not know (a project from another version): listed, so they can be removed
                foreach (var b in st.Blocks.Where(x => x.Spec == null))
                    rootNode.Nodes.Add(new TreeNode($"{b.Kind}: unknown block") { Tag = b, ImageKey = "bang", SelectedImageKey = "bang", ForeColor = Color.FromArgb(170, 0, 0) });
                tvModel.Nodes.Add(rootNode);
                rootNode.ExpandAll();
                // the study's own text is drawn bold: set it again so it is measured with the bold font
                rootNode.Text = st.Name + " ";
            }
            tvModel.EndUpdate();
            fillingModel = false;
            SelectInModel(studyCanvas.Selected);
            UpdateCommandState();
        }

        void SelectInModel(Block b)
        {
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
                tvModel.SelectedNode = b == null ? null : Find(tvModel.Nodes);
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

        // ------------------------------------------------------------------ what is missing

        /// <summary>Everything that stops the study shown from running: the wiring, and what each block misses.</summary>
        List<string> StudyProblems()
        {
            var st = CurrentStudy;
            if (st == null) return new List<string>();
            var list = st.Validate().Where(i => i.Block == 0).Select(i => i.Message).ToList();
            if (!st.Blocks.Any(b => b.Kind == BlockKind.Simulate)) list.Add("no Simulate block, so nothing would run: add one (Simulation, in the library), with an analysis (e.g. Nominal) wired into it");
            foreach (var b in st.Blocks.OrderBy(x => x.Id))
                foreach (var m in BlockProblems(b)) list.Add($"{b.Spec?.Title ?? b.Kind}: {m}");
            return list;
        }

        /// <summary>The schematic a Schematic block stands for (its own file, else the project's).</summary>
        string FileOf(Block b) => b.Get("File").Trim().Length > 0 ? b.Get("File").Trim() : project.Schematic;

        /// <summary>The netlist of the schematic a study simulates, when it is the one read (null otherwise).</summary>
        Netlist NetlistOf(Study st)
        {
            var sch = st?.Blocks.FirstOrDefault(b => b.Kind == BlockKind.Schematic);
            return sch != null && netlist != null && SameFile(FileOf(sch), ascPath) ? netlist : null;
        }

        int sitesOf = -1;
        Netlist sitesNetlist;

        /// <summary>The tolerances SPICA can drive in a netlist (worst case sets them to their edges).</summary>
        int ToleranceSites(Netlist nl)
        {
            if (nl != sitesNetlist) { sitesNetlist = nl; sitesOf = McPlan.Build(nl.Lines, nl.SourcePath).Sites.Count; }
            return sitesOf;
        }

        /// <summary>What a block misses to run: an input not wired, or a setting it needs (the canvas draws it red).</summary>
        List<string> BlockProblems(Block b)
        {
            var st = CurrentStudy;
            var list = st == null ? new List<string>() : st.Validate().Where(i => i.Block == b.Id).Select(i => i.Message).ToList();
            void Check(Action a) { try { a(); } catch (Exception ex) { list.Add(ex.Message); } }
            var nl = NetlistOf(st);
            switch (b.Kind)
            {
                case BlockKind.Schematic:
                    string file = FileOf(b);
                    if (file.Length == 0) list.Add("no schematic: choose the .asc (File)");
                    else if (!File.Exists(file)) list.Add("the .asc is not there: " + file);
                    break;
                case BlockKind.Nominal:
                    Check(() => StudyExecutors.Temps(b.Get("Temperature")));
                    break;
                case BlockKind.MonteCarlo:
                    double runs = StudyExecutors.Num(b.Get("Runs"));
                    if (!(runs >= 1) || runs != Math.Floor(runs) || runs > 10_000_000) list.Add("enter the number of runs (a whole number, 1 or more)");
                    double seed = StudyExecutors.Num(b.Get("Seed"), 0);
                    if (!(seed >= 0) || seed != Math.Floor(seed) || seed > uint.MaxValue) list.Add("the seed must be a whole number, 0 or more");
                    Check(() => StudyExecutors.Temps(b.Get("Temperature")));
                    break;
                case BlockKind.WorstCase:
                    double k = StudyExecutors.Num(b.Get("Sigma"), 3);
                    if (!(k > 0) || k > 10) list.Add("sigma must be above 0 and at most 10");
                    Check(() => StudyExecutors.Temps(b.Get("Temperature")));
                    if (nl != null)
                    {
                        int n = ToleranceSites(nl);
                        if (n == 0) list.Add("the schematic has no tolerance SPICA can drive (mc(), gauss() or flat() outside .subckt)");
                        else if (n > WorstCase.MaxCornerSites) list.Add($"{n} tolerances: 2^{n} runs per temperature is too many; use Monte Carlo");
                    }
                    break;
                case BlockKind.Simulate:
                    if (nl != null)
                    {
                        var known = new HashSet<string>(nl.Nodes.Select(x => $"V({x})").Concat(nl.Currents), StringComparer.OrdinalIgnoreCase);
                        foreach (var s in SaveList(b).Where(s => !known.Contains(s))) list.Add($"the schematic has no signal {s} (Save)");
                    }
                    break;
            }
            return list;
        }

        static List<string> SaveList(Block b) => b.Get("Save").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        // ------------------------------------------------------------------ captions

        string BlockCaption(Block b)
        {
            string P(string k) => b.Get(k).Trim();
            string At() => P("Temperature").Length > 0 ? ", " + P("Temperature") + " °C" : "";
            switch (b.Kind)
            {
                case BlockKind.Schematic:
                    string file = FileOf(b);
                    return file.Length > 0 ? Path.GetFileName(file) : "no file chosen";
                case BlockKind.Nominal: return "tolerances nominal" + At();
                case BlockKind.MonteCarlo: return $"{P("Runs")} runs" + (P("Paired") == "1" ? ", paired" : "") + At();
                case BlockKind.WorstCase: return $"tolerance edges, ±{(P("Sigma").Length > 0 ? P("Sigma") : "3")}σ" + At();
                case BlockKind.Simulate:
                    int n = SaveList(b).Count;
                    return n == 0 ? "saves every signal" : $"saves {n} signal{(n == 1 ? "" : "s")}";
                default: return b.Spec?.Summary ?? "unknown block";
            }
        }

        // ------------------------------------------------------------------ the inspector (right side)

        Panel inspector, inspectorBody, inspectorHead;
        Label inspectorTitle, inspectorKind, inspectorMissing;
        PictureBox inspectorIcon;
        Block inspected;
        /// <summary>Called by the inspector's fields after each change: marks the project, redraws, refreshes what is missing.</summary>
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
            inspectorKind.Text = $"{(b.Spec == null ? "Unknown" : b.Spec.Family.ToString())} block  ·  #{b.Id}";
            inspectorHead.Invalidate();
            inspectorChanged = () => { MarkDirty(); MarkStale(b); studyCanvas.Invalidate(); UpdateMissing(); RefreshModelSoon(); };
            bool opening = !inspector.Visible;
            inspector.Visible = true;
            // the panel takes room from the canvas: keep the block in view
            if (opening) BeginInvoke(new Action(() => studyCanvas.Reveal(b)));
            Action rebuild = null;
            rebuild = () => BuildBlockEditor(inspectorBody, b, st, rebuild);
            rebuild();
            UpdateMissing();
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
            ["Save"] = "Save signals", ["Sigma"] = "Sigma", ["Temperature"] = "Temperature",
        };

        /// <summary>The unit shown after a short field.</summary>
        static readonly Dictionary<string, string> PropUnits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MonteCarlo.Runs"] = "runs", ["MonteCarlo.Seed"] = "(0 = new)", ["WorstCase.Sigma"] = "σ",
            ["Nominal.Temperature"] = "°C", ["MonteCarlo.Temperature"] = "°C", ["WorstCase.Temperature"] = "°C",
        };

        static readonly HashSet<string> BoolProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Paired" };

        /// <summary>What each setting means, shown under its field.</summary>
        static readonly Dictionary<string, string> PropHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Schematic.File"] = "The LTspice schematic (.asc). Empty: the project's own.",
            ["Nominal.Temperature"] = "Empty: as in the schematic. Several, separated by ;  (e.g. -55; 25; 85): one run at each.",
            ["MonteCarlo.Runs"] = "How many units are drawn from the tolerances.",
            ["MonteCarlo.Seed"] = "0: new units every time. A number: the same units again.",
            ["MonteCarlo.Paired"] = "SPICA draws the random values itself (mc(), gauss(), flat()): the same units at every temperature, the same again with the same seed. Cleared: LTspice draws them.",
            ["MonteCarlo.Temperature"] = "Empty: as in the schematic. Several, separated by ;  (e.g. -55; 25; 85): every run at each.",
            ["WorstCase.Sigma"] = "Gaussian tolerances are pushed to ± this many sigma; flat ones to their limits.",
            ["WorstCase.Temperature"] = "Empty: as in the schematic. Several, separated by ;  (e.g. -55; 25; 85): every combination at each.",
            ["Simulate.Save"] = "The signals kept in the .raw files, to plot on the Results tab. The ones the schematic's .meas read are always kept. None other ticked: every signal (larger files).",
        };

        /// <summary>
        /// The inspector's fields for a block, in order: a label on the left, the field sized to what it holds (a number
        /// short, with its unit; a path the whole row; a choice of signals as a list), what it means under it.
        /// Changes apply at once.
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
            void RowLabel(string key) =>
                host.Controls.Add(new Label { Text = (PropLabels.TryGetValue(key, out var n) ? n : key) + ":", Location = new Point(6, y + 3), Width = LW - 4, AutoEllipsis = true, UseMnemonic = false });

            var k = b.Spec;
            if (k == null) Text($"SPICA {AppVersion} has no block \"{b.Kind}\" (a project from another version): remove it (Del).", null, Color.FromArgb(160, 0, 0));
            else Text(k.Summary, null, Win98.Shadow);
            if (b.Kind == BlockKind.Schematic) SchematicInfo(b, Section, s => Text(s, null, Win98.Shadow), ref y, host);
            if (b.Kind == BlockKind.WorstCase && NetlistOf(st) is Netlist wn)
            {
                int n = ToleranceSites(wn);
                Text(n == 0 ? "The schematic has no tolerance SPICA can drive." : $"{n} tolerance{(n == 1 ? "" : "s")}: 1 + 2^{n} = {1 + (1L << Math.Min(n, 40)):N0} runs per temperature.", null, n == 0 || n > WorstCase.MaxCornerSites ? Color.FromArgb(160, 0, 0) : Win98.Shadow);
            }

            if (k != null && b.Props.Count > 0) Section("Settings");
            for (int idx = 0; idx < b.Props.Count; idx++)
            {
                int index = idx;
                string key = b.Props[idx].Key, val = b.Props[idx].Value, kk = b.Kind + "." + key;
                void SetVal(string v) { b.Props[index] = new KeyValuePair<string, string>(key, v ?? ""); inspectorChanged(); }

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
                if (kk == "Simulate.Save" && NetlistOf(st) is Netlist sn)
                {
                    // the circuit's signals, ticked when saved: first the ones its .meas read, always saved (ticked, cannot be
                    // cleared), then the others; what is set but not in the circuit stays, at the end
                    Text(PropLabels[key] + ":");
                    const string Forced = "   (.meas)";
                    var now = val.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    var forced = sn.RequiredSignals.ToList();
                    var choices = sn.Nodes.Select(n => $"V({n})").Concat(sn.Currents).Where(x => !forced.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
                    var ticked = now.Where(x => !forced.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
                    var items = forced.Select(x => x + Forced).Concat(ticked).Concat(choices.Where(x => !ticked.Contains(x, StringComparer.OrdinalIgnoreCase))).ToList();
                    var list = new CheckedListBox { Location = new Point(6, y), Size = new Size(w, Math.Min(200, 20 + 16 * items.Count)), CheckOnClick = true, Font = Win98.Font, IntegralHeight = false, HorizontalScrollbar = true };
                    foreach (var it in items) list.Items.Add(it, it.EndsWith(Forced) || now.Contains(it, StringComparer.OrdinalIgnoreCase));
                    list.ItemCheck += (s, e) =>
                    {
                        if (((string)list.Items[e.Index]).EndsWith(Forced)) { e.NewValue = CheckState.Checked; return; }
                        host.BeginInvoke(new Action(() => SetVal(string.Join(" ", list.CheckedItems.Cast<string>().Where(x => !x.EndsWith(Forced))))));
                    };
                    host.Controls.Add(list);
                    y += list.Height + 4;
                    Hint(key, false);
                    continue;
                }

                RowLabel(key);
                bool browse = kk == "Schematic.File";
                int fw = PropUnits.ContainsKey(kk) ? (key == "Temperature" ? 150 : 80) : fwFull - (browse ? 28 : 0);
                var tf = new Win98TextField { Location = new Point(fx, y), Width = fw, Text = val };
                tf.Box.TextChanged += (s, e) => SetVal(tf.Text);
                host.Controls.Add(tf);
                if (browse)
                {
                    var more = new Win98Button { Text = "...", Location = new Point(fx + fw + 4, y - 1), Size = new Size(24, 22) };
                    more.Click += (s, e) =>
                    {
                        using (var d = new OpenFileDialog { Filter = "LTspice schematic (*.asc)|*.asc", AutoUpgradeEnabled = false })
                        {
                            try { if (FileOf(b).Length > 0) d.InitialDirectory = Path.GetDirectoryName(FileOf(b)); } catch { }
                            if (d.ShowDialog(this) == DialogResult.OK) { tf.Text = d.FileName; rebuild?.Invoke(); }
                        }
                    };
                    host.Controls.Add(more);
                }
                if (PropUnits.TryGetValue(kk, out var unit))
                    host.Controls.Add(new Label { Text = unit, Location = new Point(fx + fw + 4, y + 3), AutoSize = true, ForeColor = Color.FromArgb(64, 64, 64) });
                y += 26;
                Hint(key, true);
            }

            if (b.Kind == BlockKind.Simulate)
            {
                Section("This PC");
                string folder = StudyFolder(st);
                Text($"LTspice: {(settings.AutoParallel ? "as many instances as the scheduler finds best" : $"{ManualConfig()?.Instances} instances x {ManualConfig()?.Threads} threads")}. " +
                     $"Results in {(folder ?? "(the folder of the project, once saved)")}. Change them in Tools > Options.", null, Win98.Shadow);
            }

            if (k != null && (k.Inputs.Count > 0 || k.Outputs.Count > 0))
            {
                Section("Wiring");
                foreach (var i in k.Inputs)
                {
                    var from = st.Upstream(b, i.Name).Select(x => x.Spec?.Title ?? x.Kind).ToList();
                    Text($"in   {i.Name} ({i.Type}): " + (from.Count == 0 ? "not wired" : string.Join(", ", from)), null, from.Count == 0 ? Color.FromArgb(160, 0, 0) : Color.Black);
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

        /// <summary>Schematic: what the circuit holds (nets, .meas, tolerances), and a button to open it in LTspice.</summary>
        void SchematicInfo(Block b, Action<string> section, Action<string> text, ref int y, Panel host)
        {
            string file = FileOf(b);
            section("The circuit");
            if (file.Length == 0 || !File.Exists(file)) { text(file.Length == 0 ? "No schematic chosen yet." : "Not found: " + file); return; }
            Netlist nl = netlist != null && SameFile(file, ascPath) ? netlist : null;
            if (nl == null) text(Path.GetFileName(file) + (SameFile(file, project.Schematic) ? ": being read by LTspice..." : ": its nets show here when it is the project's schematic."));
            else
            {
                text($"{Path.GetFileName(file)}:  {nl.Nodes.Count} nets,  {nl.Currents.Count} currents,  {nl.MeasNames.Count} .meas,  {ToleranceSites(nl)} tolerances SPICA drives");
                if (nl.Analysis.Length > 0) text("Analysis: " + nl.Analysis);
                if (nl.MeasNames.Count > 0) text("Measurements: " + string.Join(", ", nl.MeasNames));
                text("Nets: " + string.Join(", ", nl.Nodes.Take(40)) + (nl.Nodes.Count > 40 ? ", ..." : ""));
            }
            var open = new Win98Button { Text = "Open in &LTspice", Location = new Point(4, y + 2), Width = 120 };
            open.Click += (s, e) =>
            {
                string lt = settings.LtspicePath ?? "";
                try { if (File.Exists(lt)) System.Diagnostics.Process.Start(lt, "\"" + file + "\""); else System.Diagnostics.Process.Start(file); } catch { }
            };
            host.Controls.Add(open);
            y += 30;
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
