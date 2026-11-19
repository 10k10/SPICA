using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Spica.Core
{
    /// <summary>Everything that talks to the LTspice executable outside of a campaign run.</summary>
    public static class Ltspice
    {
        public static string Locate()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidates = new[]
            {
                Path.Combine(local, @"Programs\ADI\LTspice\LTspice.exe"),
                Path.Combine(pf, @"ADI\LTspice\LTspice.exe"),
                Path.Combine(pf, @"LTC\LTspiceXVII\XVIIx64.exe"),
                @"C:\Program Files\ADI\LTspice\LTspice.exe",
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        public static string Version(string exe)
        {
            try { return FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Trim() ?? "?"; }
            catch { return "?"; }
        }

        // ------------------------------------------------------------------ private .ini

        /// <summary>
        /// Builds a clean, private LTspice.ini for the campaign. The user's own settings are copied
        /// (solver, tolerances, search paths...) so results match the GUI, then the keys SPICA needs
        /// are forced: clock reseeding of the MC generator, and no first-run / analytics / web-sync
        /// dialogs that would block a batch instance. The user's real ini is never modified.
        /// </summary>
        public static string WritePrivateIni(string path, bool clockReseed)
        {
            var sections = ReadUserIni();
            if (!sections.TryGetValue("Options", out var opt))
            {
                opt = new List<KeyValuePair<string, string>>();
                sections["Options"] = opt;
            }
            void Set(string k, string v)
            {
                opt.RemoveAll(kv => kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
                opt.Add(new KeyValuePair<string, string>(k, v));
            }
            Set("CaptureAnalytics", "false");
            Set("LastWebUpdateQuery", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            Set("UseClocktoReseedMC", clockReseed ? "true" : "false");

            var sb = new StringBuilder();
            foreach (var sec in sections)
            {
                sb.Append('[').Append(sec.Key).Append("]\r\n");
                foreach (var kv in sec.Value) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
            }
            File.WriteAllText(path, sb.ToString(), new UnicodeEncoding(false, true));
            return path;
        }

        public static string UserIniPath()
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var name in new[] { "LTspice.ini", "LTspiceXVII.ini" })
            {
                var p = Path.Combine(roaming, name);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>The user's LTspice library folder (lib\cmp, lib\sub, lib\sym), or null.</summary>
        public static string LibDir()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return new[] { Path.Combine(local, @"LTspice\lib"), Path.Combine(docs, @"LTspiceXVII\lib") }.FirstOrDefault(Directory.Exists);
        }

        /// <summary>
        /// The extra folders set in LTspice's Control Panel > Search Paths (LibrarySearchPath or SymbolSearchPath),
        /// existing folders only. The value is read tolerantly: one or several lines, separated by ; | or newlines.
        /// </summary>
        public static List<string> SearchPaths(string key)
        {
            var found = new List<string>();
            var path = UserIniPath();
            if (path == null) return found;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); } catch { return found; }
            var texts = new List<string> { Encoding.Unicode.GetString(bytes) };
            if (bytes.Length > 3) texts.Add(Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1));
            texts.Add(Encoding.Default.GetString(bytes));
            var offRx = new Regex(@"^\s*" + Regex.Escape(key) + @"Disabled\s*=\s*(true|1)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (texts.Any(t => offRx.IsMatch(t))) return found;     // unticked in LTspice's Control Panel
            var keyRx = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=(.*)$", RegexOptions.IgnoreCase);
            var pathLine = new Regex(@"^\s*""?([A-Za-z]:\\|\\\\|%)");
            var split = new Regex(@"[;|\t]+|\\n(?=[A-Za-z]:\\|\\\\)|(?<=\S)(?=[A-Za-z]:\\)");
            foreach (var text in texts)
            {
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim('﻿', '\0', ' ')).ToList();
                for (int i = 0; i < lines.Count; i++)
                {
                    var m = keyRx.Match(lines[i]);
                    if (!m.Success) continue;
                    var parts = new List<string> { m.Groups[1].Value };
                    for (int j = i + 1; j < lines.Count && pathLine.IsMatch(lines[j]); j++) parts.Add(lines[j]);   // a value spread over lines
                    foreach (var part in parts)
                        foreach (var raw in split.Split(part))
                        {
                            string p = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"').Trim());
                            try { if (p.Length > 2 && Directory.Exists(p) && !found.Contains(p, StringComparer.OrdinalIgnoreCase)) found.Add(Path.GetFullPath(p)); }
                            catch { }
                        }
                }
            }
            return found;
        }

        static readonly Regex SectionRx = new Regex(@"^\[([A-Za-z0-9 _]+)\]$");
        static readonly Regex KeyRx = new Regex(@"^([A-Za-z0-9_ ]{1,64})=([\x20-\x7E]*)$");

        /// <summary>
        /// Reads the user ini tolerantly. LTspice writes UTF-16 but files are seen in the wild with a
        /// section that is shifted by one byte, so both alignments are decoded and only sane
        /// "key=value" lines are kept. Later occurrences win.
        /// </summary>
        static Dictionary<string, List<KeyValuePair<string, string>>> ReadUserIni()
        {
            var result = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
            var path = UserIniPath();
            if (path == null) return result;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); } catch { return result; }

            var texts = new List<string>();
            bool utf16 = bytes.Length > 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || bytes[1] == 0);
            if (utf16)
            {
                texts.Add(Encoding.Unicode.GetString(bytes));
                if (bytes.Length > 3) texts.Add(Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1));
            }
            else texts.Add(Encoding.Default.GetString(bytes));

            // Each decoding yields a run of valid lines; merge them in file order per decoding.
            foreach (var text in texts)
            {
                string section = null;
                foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = raw.Trim('\uFEFF', ' ', '\0');
                    var m = SectionRx.Match(line);
                    if (m.Success) { section = m.Groups[1].Value; continue; }
                    if (section == null) continue;
                    var k = KeyRx.Match(line);
                    if (!k.Success) { continue; }
                    if (!result.TryGetValue(section, out var list))
                        result[section] = list = new List<KeyValuePair<string, string>>();
                    string key = k.Groups[1].Value;
                    list.RemoveAll(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                    list.Add(new KeyValuePair<string, string>(key, k.Groups[2].Value));
                }
            }
            return result;
        }

        // ------------------------------------------------------------------ schematic -> netlist

        /// <summary>
        /// Runs "LTspice -netlist" on the schematic and returns the netlist text. LTspice writes the
        /// .net next to the .asc; if the user already had a file with that name it is restored.
        /// </summary>
        public static string ConvertSchematic(string exe, string ascPath, string privateIni, out Encoding encoding)
        {
            string netPath = Path.ChangeExtension(ascPath, ".net");
            byte[] backup = File.Exists(netPath) ? File.ReadAllBytes(netPath) : null;
            DateTime before = File.Exists(netPath) ? File.GetLastWriteTimeUtc(netPath) : DateTime.MinValue;
            try
            {
                var args = $"-netlist \"{ascPath}\"";
                if (privateIni != null) args += $" -ini \"{privateIni}\"";
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(ascPath),
                };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(60000))
                    {
                        try { p.Kill(); } catch { }
                        throw new Exception("LTspice did not finish converting the schematic within 60 s.");
                    }
                }
                if (!File.Exists(netPath) || File.GetLastWriteTimeUtc(netPath) == before && backup != null)
                    throw new Exception("LTspice did not produce a netlist. Open the schematic in LTspice and check it for errors.");
                return ReadText(netPath, out encoding);
            }
            finally
            {
                try
                {
                    if (backup != null) File.WriteAllBytes(netPath, backup);
                    else if (File.Exists(netPath)) File.Delete(netPath);
                }
                catch { }
            }
        }

        /// <summary>Reads an LTspice text file whatever its encoding (UTF-16 with/without BOM, UTF-8, ANSI).</summary>
        public static string ReadText(string path, out Encoding encoding)
        {
            var b = File.ReadAllBytes(path);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) { encoding = Encoding.Unicode; return Encoding.Unicode.GetString(b, 2, b.Length - 2); }
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { encoding = new UTF8Encoding(false); return Encoding.UTF8.GetString(b, 3, b.Length - 3); }
            if (b.Length >= 2 && b[1] == 0 && b[0] != 0) { encoding = Encoding.Unicode; return Encoding.Unicode.GetString(b); }
            encoding = Encoding.Default;
            return Encoding.Default.GetString(b);
        }

        public static string ReadText(string path) => ReadText(path, out _);
    }
}
