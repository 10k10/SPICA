using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Spica.Core
{
    /// <summary>
    /// Finds the model and library files a schematic refers to (.lib, .inc) the way LTspice does: in the schematic's
    /// folder, LTspice's own library and its search paths.
    /// </summary>
    internal sealed class LibraryResolver
    {
        readonly string ascDir, lib;
        readonly List<string> libPaths;
        readonly Dictionary<string, string> deep = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public LibraryResolver(string ascDir)
        {
            this.ascDir = ascDir;
            lib = Ltspice.LibDir();
            libPaths = Ltspice.SearchPaths("LibrarySearchPath");
        }

        public string Model(string reference, string baseDir)
        {
            try
            {
                reference = Environment.ExpandEnvironmentVariables(reference);
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
}
