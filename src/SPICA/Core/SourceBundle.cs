using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Spica.Core
{
    /// <summary>
    /// "&lt;schematic&gt;_sources.zip" in a campaign folder: a snapshot, taken when the campaign starts, of everything it
    /// simulated (schematic, sub-sheets, symbols, model libraries, the netlist, the setup) and of where it ran (machine,
    /// Windows, LTspice and its library). Unzip it and open the schematic in SPICA: its setup loads with it, and the
    /// netlist's library references are redirected to the copies in the bundle, so the same circuit is simulated again.
    /// </summary>
    public static class SourceBundle
    {
        public const string Manifest = "SPICA_bundle.txt";
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string PathFor(string folder, string ascPath) =>
            Path.Combine(folder, Path.GetFileNameWithoutExtension(ascPath) + "_sources.zip");

        /// <summary>The bundle of a campaign folder, or null.</summary>
        public static string Find(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            return Directory.GetFiles(folder, "*_sources.zip").OrderBy(f => f.Length).FirstOrDefault();
        }

        public static string AppVersion =>
            (typeof(SourceBundle).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
             ?? typeof(SourceBundle).Assembly.GetName().Version.ToString()).Split('+')[0];

        public static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        public static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        }

        // ------------------------------------------------------------------ environment snapshot

        /// <summary>Where and with what a campaign ran, as "key = value" pairs in a fixed order.</summary>
        public static List<KeyValuePair<string, string>> Environment(string ltspiceExe, HardwareInfo hw)
        {
            var e = new List<KeyValuePair<string, string>>();
            void Add(string k, string v) { if (!string.IsNullOrEmpty(v)) e.Add(new KeyValuePair<string, string>(k, v)); }
            Add("Computer", System.Environment.MachineName);
            Add("User", System.Environment.UserName);
            Add("Windows", WindowsVersion());
            if (hw != null)
            {
                Add("CPU", hw.CpuName.Trim());
                Add("Cores", $"{hw.PhysicalCores} cores / {hw.LogicalProcessors} threads" + (hw.IsHybrid ? $" ({hw.PerformanceCores}P + {hw.EfficiencyCores}E)" : ""));
                Add("RAM", $"{hw.TotalMemory / (double)(1L << 30):0.0} GB");
            }
            Add("SPICA", AppVersion);
            if (!string.IsNullOrEmpty(ltspiceExe))
            {
                Add("LTspice", Ltspice.Version(ltspiceExe));
                Add("LTspice exe", ltspiceExe);
            }
            Add("LTspice library", LibraryVersion(ltspiceExe));
            Add("LTspice lib folder", Ltspice.LibDir());
            var lp = Ltspice.SearchPaths("LibrarySearchPath");
            var sp = Ltspice.SearchPaths("SymbolSearchPath");
            if (lp.Count > 0) Add("Library search paths", string.Join(" ; ", lp));
            if (sp.Count > 0) Add("Symbol search paths", string.Join(" ; ", sp));
            return e;
        }

        static string WindowsVersion()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string name = k?.GetValue("ProductName") as string ?? "Windows";
                    string build = k?.GetValue("CurrentBuildNumber") as string ?? "";
                    object ubr = k?.GetValue("UBR");
                    string disp = k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string ?? "";
                    // Windows 11 still calls itself "Windows 10" in ProductName
                    if (int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
                    return $"{name} {disp} (build {build}{(ubr != null ? "." + ubr : "")}, {(System.Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})".Replace("  ", " ");
                }
            }
            catch { return System.Environment.OSVersion.VersionString; }
        }

        /// <summary>The date of LTspice's own model library (LTspice.json "Models", else the newest library change log entry).</summary>
        static string LibraryVersion(string exe)
        {
            try
            {
                string json = exe != null ? Path.Combine(Path.GetDirectoryName(exe), "LTspice.json") : null;
                if (json != null && File.Exists(json))
                {
                    var m = Regex.Match(File.ReadAllText(json), @"""Models""\s*:\s*""([^""]+)""");
                    if (m.Success) return "models of " + m.Groups[1].Value;
                }
                string lib = Ltspice.LibDir();
                string log = lib != null ? Path.Combine(Path.GetDirectoryName(lib), "ChangeLog.txt") : null;
                if (log != null && File.Exists(log))
                {
                    var first = File.ReadLines(log).FirstOrDefault(l => l.Trim().Length > 0);
                    if (first != null) return "change log of " + first.Trim().Split(' ')[0];
                }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------------ writing

        sealed class Entry { public string Kind, Zip, Source, Sha; public long Bytes; }

        /// <summary>
        /// Writes the bundle. Returns a one-line summary for the log; problems that do not stop the bundle
        /// (a library that cannot be found) are added to <paramref name="warnings"/>.
        /// </summary>
        public static string Write(string zipPath, CampaignOptions opt, HardwareInfo hw, DateTime started, List<string> warnings)
        {
            string asc = Path.GetFullPath(opt.AscPath);
            string ascDir = Path.GetDirectoryName(asc);
            string stem = Path.GetFileNameWithoutExtension(asc);
            var r = new Resolver(ascDir, opt.LtspiceExe);
            var entries = new List<Entry>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);     // zip names
            var bySource = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var redirect = new List<KeyValuePair<string, string>>();

            Entry Add(string kind, string source, string zipName)
            {
                if (bySource.TryGetValue(source, out var have)) return have;
                zipName = zipName.Replace('\\', '/').TrimStart('/');
                string baseName = zipName;
                for (int i = 2; !used.Add(zipName); i++)
                    zipName = Path.ChangeExtension(baseName, null) + "_" + i + Path.GetExtension(baseName);
                var en = new Entry { Kind = kind, Zip = zipName, Source = source };
                entries.Add(en);
                bySource[source] = en;
                return en;
            }
            string Inside(string full) =>
                full.StartsWith(ascDir + "\\", StringComparison.OrdinalIgnoreCase) ? full.Substring(ascDir.Length + 1) : null;

            // schematic, its sub-sheets and the symbols they use
            Add("schematic", asc, Path.GetFileName(asc));
            var sheets = new Queue<string>();
            sheets.Enqueue(asc);
            var seenSheets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { asc };
            while (sheets.Count > 0)
            {
                string sheet = sheets.Dequeue();
                string text;
                try { text = Ltspice.ReadText(sheet); } catch { continue; }
                foreach (var line in text.Split('\n'))
                {
                    var l = line.Trim();
                    if (!l.StartsWith("SYMBOL ", StringComparison.OrdinalIgnoreCase)) continue;
                    var tok = l.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tok.Length < 2) continue;
                    string name = tok[1];
                    foreach (var f in r.Symbol(name, Path.GetDirectoryName(sheet)))
                    {
                        if (r.IsStock(f)) continue;           // ships with every LTspice install
                        string inside = Inside(f);
                        bool sub = f.EndsWith(".asc", StringComparison.OrdinalIgnoreCase);
                        Add(sub ? "sheet" : "symbol", f, inside ?? name + Path.GetExtension(f));
                        if (sub && seenSheets.Add(f)) sheets.Enqueue(f);
                    }
                }
            }

            // model libraries, include files and PWL files referenced by the netlist (then by those files)
            var models = new Queue<KeyValuePair<string, int>>();
            foreach (var line in opt.Netlist.Lines)
                foreach (var refName in References(line))
                {
                    string f = r.Model(refName, ascDir);
                    if (f == null) { warnings?.Add($"{refName}: not found, not in the bundle"); continue; }
                    string inside = Inside(f);
                    var en = Add("model", f, inside ?? "libs/" + Path.GetFileName(f));
                    if (!redirect.Any(kv => kv.Key.Equals(refName, StringComparison.OrdinalIgnoreCase)))
                        redirect.Add(new KeyValuePair<string, string>(refName, en.Zip));
                    models.Enqueue(new KeyValuePair<string, int>(f, 1));
                }
            var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (models.Count > 0)
            {
                var kv = models.Dequeue();
                if (kv.Value > 4 || !scanned.Add(kv.Key)) continue;
                try { if (new FileInfo(kv.Key).Length > 20 << 20) continue; } catch { continue; }
                string text;
                try { text = Ltspice.ReadText(kv.Key); } catch { continue; }
                foreach (var line in text.Split('\n'))
                {
                    var m = Netlist.IncludeRx.Match(line.Trim());
                    if (!m.Success) continue;
                    string refName = m.Groups[2].Value.Trim('"');
                    string f = r.Model(refName, Path.GetDirectoryName(kv.Key));
                    if (f == null) { warnings?.Add($"{refName} (included by {Path.GetFileName(kv.Key)}): not found, not in the bundle"); continue; }
                    string inside = Inside(f);
                    Add("model", f, inside ?? "libs/" + Path.GetFileName(f));
                    models.Enqueue(new KeyValuePair<string, int>(f, kv.Value + 1));
                }
            }

            // the netlist SPICA simulates and the setup (named like the schematic, so SPICA loads it with it)
            string netText = string.Join("\r\n", opt.Netlist.Lines) + "\r\n";
            byte[] setupBytes = null;
            if (opt.Setup != null)
            {
                string tmp = Path.Combine(Path.GetTempPath(), "SPICA", "bundle_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);
                try
                {
                    var copy = opt.Setup.Clone();
                    copy.Seed = opt.Seed;
                    copy.Schematic = Path.Combine(tmp, Path.GetFileName(asc));   // saved as a bare name: the schematic beside it
                    string sp = Path.Combine(tmp, stem + ".spica");
                    copy.Save(sp);
                    setupBytes = File.ReadAllBytes(sp);
                }
                finally { try { Directory.Delete(tmp, true); } catch { } }
            }

            // the files' bytes, read once (hashed and zipped from the same copy)
            var data = new Dictionary<Entry, byte[]>();
            foreach (var en in entries.ToList())
            {
                try { data[en] = File.ReadAllBytes(en.Source); }
                catch (Exception ex) { warnings?.Add($"{en.Source}: {ex.Message}"); entries.Remove(en); continue; }
                en.Sha = Sha256(data[en]);
                en.Bytes = data[en].LongLength;
            }

            var man = new StringBuilder();
            man.Append("; SPICA source bundle: what this campaign simulated, and where.\r\n");
            man.Append($"; To simulate it again: unzip, open {Path.GetFileName(asc)} in SPICA (the setup {stem}.spica loads with it).\r\n");
            man.Append("; SPICA redirects the library references listed under [Redirect] to the copies in this bundle.\r\n");
            man.Append("[Campaign]\r\n");
            man.Append($"Started={started.ToString("yyyy-MM-dd HH:mm:ss", Inv)}\r\n");
            man.Append($"Schematic={asc}\r\n");
            man.Append($"SchematicSha256={entries[0].Sha}\r\n");
            man.Append($"NetlistSha256={Sha256(opt.Netlist.Encoding.GetBytes(netText))}\r\n");
            man.Append($"Seed={opt.Seed}\r\n");
            man.Append($"Generator={(opt.Mc != null ? "SPICA paired samples (reproducible from the seed)" : opt.ClockReseed ? "LTspice mc(), reseeded from the clock (not reproducible run by run)" : "LTspice mc()")}\r\n");
            man.Append("[Environment]\r\n");
            foreach (var kv in Environment(opt.LtspiceExe, hw)) man.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
            man.Append("[Files]\r\n; kind | file in this bundle | bytes | sha256 | original\r\n");
            foreach (var en in entries) man.Append($"{en.Kind}|{en.Zip}|{en.Bytes}|{en.Sha}|{en.Source}\r\n");
            if (setupBytes != null) man.Append($"setup|{stem}.spica|{setupBytes.Length}|{Sha256(setupBytes)}|{opt.Setup.FilePath}\r\n");
            man.Append("netlist|netlist.net|||\r\n");
            man.Append("[Redirect]\r\n; reference as written in the netlist = file in this bundle\r\n");
            foreach (var kv in redirect) man.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
            if (warnings != null && warnings.Count > 0)
            {
                man.Append("[Missing]\r\n");
                foreach (var w in warnings) man.Append(w).Append("\r\n");
            }

            string part = zipPath + ".part";
            using (var fs = new FileStream(part, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                void Put(string name, byte[] bytes, DateTime when)
                {
                    var z = zip.CreateEntry(name, CompressionLevel.Optimal);
                    z.LastWriteTime = when;
                    using (var s = z.Open()) s.Write(bytes, 0, bytes.Length);
                }
                foreach (var en in entries) Put(en.Zip, data[en], File.GetLastWriteTime(en.Source));
                if (setupBytes != null) Put(stem + ".spica", setupBytes, started);
                Put("netlist.net", opt.Netlist.Encoding.GetBytes(netText), started);
                Put(Manifest, new UTF8Encoding(false).GetBytes(man.ToString()), started);
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(part, zipPath);

            int nModel = entries.Count(e => e.Kind == "model"), nSym = entries.Count(e => e.Kind == "symbol" || e.Kind == "sheet");
            return $"{Path.GetFileName(zipPath)}: schematic, setup, netlist, {nModel} model file{(nModel == 1 ? "" : "s")}, {nSym} symbol{(nSym == 1 ? "" : "s")}/sheet{(nSym == 1 ? "" : "s")}";
        }

        static IEnumerable<string> References(string line)
        {
            var t = line.Trim();
            if (t.Length == 0 || t[0] == '*' || t[0] == ';') yield break;
            var m = Netlist.IncludeRx.Match(t);
            if (m.Success) { yield return m.Groups[2].Value.Trim('"'); yield break; }
            if (t.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0)
                foreach (Match mm in Netlist.FileArgRx.Matches(t)) yield return mm.Groups[2].Value.Trim('"');
        }

        /// <summary>Finds files the way LTspice does: given folder, schematic folder, LTspice's library, the user's search paths.</summary>
        internal sealed class Resolver
        {
            readonly string ascDir, lib;
            readonly List<string> libPaths, symPaths;
            readonly HashSet<string> stock = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            readonly Dictionary<string, string> deep = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public Resolver(string ascDir, string exe)
            {
                this.ascDir = ascDir;
                lib = Ltspice.LibDir();
                libPaths = Ltspice.SearchPaths("LibrarySearchPath");
                symPaths = Ltspice.SearchPaths("SymbolSearchPath");
                // the symbols LTspice ships: its lib.zip (beside the exe, or beside the library folder)
                foreach (var z in new[] { exe != null ? Path.Combine(Path.GetDirectoryName(exe), "lib.zip") : null,
                                          lib != null ? Path.Combine(Path.GetDirectoryName(lib), "lib.zip") : null })
                {
                    if (z == null || !File.Exists(z)) continue;
                    try
                    {
                        using (var a = ZipFile.OpenRead(z))
                            foreach (var en in a.Entries)
                                if (en.FullName.StartsWith("lib/sym/", StringComparison.OrdinalIgnoreCase)) stock.Add(en.FullName.Substring(8).Replace('/', '\\'));
                        break;
                    }
                    catch { }
                }
            }

            public bool IsStock(string file)
            {
                if (lib == null || stock.Count == 0) return false;
                string sym = Path.Combine(lib, "sym") + "\\";
                return file.StartsWith(sym, StringComparison.OrdinalIgnoreCase) && stock.Contains(file.Substring(sym.Length));
            }

            /// <summary>The .asy of a symbol and, for a hierarchical block, its .asc sheet.</summary>
            public IEnumerable<string> Symbol(string name, string sheetDir)
            {
                name = name.Replace('/', '\\');
                var dirs = new List<string> { sheetDir, ascDir };
                if (lib != null) dirs.Add(Path.Combine(lib, "sym"));
                dirs.AddRange(symPaths);
                string asy = null, sheet = null;
                foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (asy == null) asy = Exists(Path.Combine(d, name + ".asy"));
                    if (sheet == null && (d.Equals(sheetDir, StringComparison.OrdinalIgnoreCase) || d.Equals(ascDir, StringComparison.OrdinalIgnoreCase)))
                        sheet = Exists(Path.Combine(d, name + ".asc"));
                }
                if (asy == null) asy = Deep(Path.GetFileName(name) + ".asy", symPaths);
                if (asy != null) yield return asy;
                if (sheet != null) yield return sheet;
            }

            public string Model(string reference, string baseDir)
            {
                try
                {
                    reference = System.Environment.ExpandEnvironmentVariables(reference);
                    if (Path.IsPathRooted(reference)) return Exists(reference);
                    var dirs = new List<string> { baseDir, ascDir };   // LTspice does not look beside the symbol
                    if (lib != null) { dirs.Add(Path.Combine(lib, "sub")); dirs.Add(Path.Combine(lib, "cmp")); }
                    dirs.AddRange(libPaths);
                    foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var f = Exists(Path.Combine(d, reference));
                        if (f != null) return f;
                    }
                    var roots = new List<string>(libPaths);
                    if (lib != null) roots.Add(Path.Combine(lib, "sub"));
                    return Deep(Path.GetFileName(reference), roots);
                }
                catch { return null; }
            }

            static string Exists(string p)
            {
                try { var f = Path.GetFullPath(p); return File.Exists(f) ? f : null; }
                catch { return null; }
            }

            /// <summary>A file by name anywhere below the given folders (first match).</summary>
            string Deep(string fileName, List<string> roots)
            {
                if (deep.TryGetValue(fileName, out var hit)) return hit;
                hit = null;
                foreach (var root in roots)
                {
                    try { hit = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault(); }
                    catch { }
                    if (hit != null) break;
                }
                return deep[fileName] = hit;
            }
        }

        // ------------------------------------------------------------------ reading

        /// <summary>The manifest of an unzipped bundle beside a schematic, or of a bundle zip, as sections of key/value lines.</summary>
        public static Dictionary<string, List<KeyValuePair<string, string>>> ReadManifest(string zipOrFile)
        {
            string text;
            if (zipOrFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using (var a = ZipFile.OpenRead(zipOrFile))
                {
                    var en = a.GetEntry(Manifest);
                    if (en == null) return null;
                    using (var s = new StreamReader(en.Open(), Encoding.UTF8, true)) text = s.ReadToEnd();
                }
            }
            else text = File.ReadAllText(zipOrFile, Encoding.UTF8);
            var result = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, string>> cur = null;
            foreach (var raw in text.Split('\n'))
            {
                var l = raw.TrimEnd('\r');
                if (l.Length == 0 || l[0] == ';') continue;
                if (l[0] == '[' && l.EndsWith("]")) { result[l.Substring(1, l.Length - 2)] = cur = new List<KeyValuePair<string, string>>(); continue; }
                if (cur == null) continue;
                int eq = l.IndexOf('=');
                cur.Add(eq < 0 ? new KeyValuePair<string, string>(l, "") : new KeyValuePair<string, string>(l.Substring(0, eq), l.Substring(eq + 1)));
            }
            return result;
        }

        public static string Value(Dictionary<string, List<KeyValuePair<string, string>>> man, string section, string key) =>
            man != null && man.TryGetValue(section, out var list) ? list.LastOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value : null;

        /// <summary>
        /// When the schematic was unzipped from a bundle, points the netlist's library references at the bundle's copies
        /// (by the reference as written, else by file name). Returns how many lines were redirected.
        /// </summary>
        public static int Redirect(Netlist nl)
        {
            string dir = Path.GetDirectoryName(nl.SourcePath ?? "");
            string man = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, Manifest);
            if (man == null || !File.Exists(man)) return 0;
            var m = ReadManifest(man);
            if (m == null || !m.TryGetValue("Redirect", out var list)) return 0;
            var exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in list) exact[kv.Key.Trim()] = Path.GetFullPath(Path.Combine(dir, kv.Value.Trim().Replace('/', '\\')));
            var byName = exact.Values.GroupBy(v => Path.GetFileName(v), StringComparer.OrdinalIgnoreCase)
                              .Where(g => g.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                              .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            string Target(string reference)
            {
                string bare = reference.Trim('"');
                if (exact.TryGetValue(bare, out var t) && File.Exists(t)) return t;
                try { if (byName.TryGetValue(Path.GetFileName(bare), out t) && File.Exists(t)) return t; } catch { }
                return null;
            }
            int n = 0;
            for (int i = 0; i < nl.Lines.Count; i++)
            {
                string line = nl.Lines[i], t = line.TrimStart();
                if (t.Length == 0 || t[0] == '*') continue;
                var im = Netlist.IncludeRx.Match(t);
                string fixedLine = null;
                if (im.Success)
                {
                    var tgt = Target(im.Groups[2].Value);
                    if (tgt != null) fixedLine = im.Groups[1].Value + "\"" + tgt + "\"" + im.Groups[3].Value;
                }
                else if (t.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string r = Netlist.FileArgRx.Replace(t, mm => { var tgt = Target(mm.Groups[2].Value); return tgt != null ? mm.Groups[1].Value + "\"" + tgt + "\"" : mm.Value; });
                    if (r != t) fixedLine = r;
                }
                if (fixedLine != null && fixedLine != t) { nl.Lines[i] = fixedLine; n++; }
            }
            return n;
        }
    }
}
