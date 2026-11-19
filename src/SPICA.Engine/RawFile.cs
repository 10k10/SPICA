using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Spica.Core
{
    /// <summary>
    /// Reader of LTspice's binary .raw waveform files (transient): the header names the variables, then each point is
    /// the time as a double and every other variable as a float (all doubles with the "double" flag). A stepped file
    /// holds its steps one after the other; a step starts where the time goes back.
    /// </summary>
    public sealed class RawFile
    {
        public string Path;
        public readonly List<string> Names = new List<string>();     // 0 = time
        public int Points;
        public bool Double, Stepped;
        long dataOffset;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static RawFile Open(string path)
        {
            var r = new RawFile { Path = path };
            byte[] head;
            using (var fs = File.OpenRead(path))
            {
                head = new byte[(int)Math.Min(fs.Length, 1 << 20)];
                int got = 0;
                while (got < head.Length) { int n = fs.Read(head, got, head.Length - got); if (n <= 0) break; got += n; }
            }
            int skip = head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE ? 2 : 0;
            bool wide = head.Length > skip + 1 && head[skip + 1] == 0;
            var enc = wide ? Encoding.Unicode : Encoding.ASCII;
            string text = enc.GetString(head, skip, head.Length - skip);
            int bin = text.IndexOf("Binary:\n", StringComparison.Ordinal);
            if (bin < 0)
            {
                if (text.IndexOf("Values:", StringComparison.Ordinal) >= 0) throw new InvalidDataException("ASCII .raw files are not supported.");
                throw new InvalidDataException("Not an LTspice .raw file (no Binary: section).");
            }
            r.dataOffset = skip + enc.GetByteCount(text.Substring(0, bin + 8));

            int nVars = 0;
            bool inVars = false, real = true, fast = false;
            foreach (var line in text.Substring(0, bin).Split('\n'))
            {
                if (inVars)
                {
                    if (line.StartsWith("\t") || line.StartsWith(" "))
                    {
                        var p = line.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 2) r.Names.Add(p[1]);
                        continue;
                    }
                    inVars = false;
                }
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string key = line.Substring(0, colon).Trim().ToLowerInvariant(), val = line.Substring(colon + 1).Trim();
                switch (key)
                {
                    case "no. variables": int.TryParse(val, NumberStyles.Integer, Inv, out nVars); break;
                    case "no. points": int.TryParse(val, NumberStyles.Integer, Inv, out r.Points); break;
                    case "flags":
                        var f = val.ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        r.Double = f.Contains("double"); r.Stepped = f.Contains("stepped");
                        real = !f.Contains("complex"); fast = f.Contains("fastaccess");
                        break;
                    case "variables": inVars = true; break;
                }
            }
            if (!real) throw new InvalidDataException("Only transient (real) .raw files can be plotted.");
            if (fast) throw new InvalidDataException("\"fastaccess\" .raw files are not supported.");
            if (r.Names.Count == 0 || (nVars > 0 && r.Names.Count != nVars)) throw new InvalidDataException("The variable list of the .raw file could not be read.");
            return r;
        }

        int RecordBytes => Double ? 8 * Names.Count : 8 + 4 * (Names.Count - 1);

        /// <summary>Index of a variable: V(out) and v(OUT) match; a bare net name means its voltage.</summary>
        public int Find(string signal)
        {
            string s = (signal ?? "").Trim();
            int i = Names.FindIndex(n => n.Equals(s, StringComparison.OrdinalIgnoreCase));
            if (i < 0 && !s.Contains("(")) i = Names.FindIndex(n => n.Equals($"V({s})", StringComparison.OrdinalIgnoreCase));
            return i;
        }

        public sealed class Step
        {
            public double[] X;
            public double[][] Y;     // Y[k] = variable vars[k], null when the file does not have it
        }

        /// <summary>
        /// The time and the given variables of some steps (0-based), in one pass over the file: x = time,
        /// Y[k] = variable vars[k]. Steps the file does not have are left out.
        /// </summary>
        public Dictionary<int, Step> ReadSteps(IList<int> vars, ICollection<int> steps)
        {
            var res = new Dictionary<int, Step>();
            int last = steps.Count == 0 ? -1 : steps.Max();
            List<double> t = null;
            List<double>[] cols = null;
            int rec = RecordBytes, cur = 0;
            void Close()
            {
                if (t != null) res[cur] = new Step { X = t.ToArray(), Y = cols.Select(c => c?.ToArray()).ToArray() };
                t = null;
            }
            void Begin()
            {
                if (!steps.Contains(cur)) return;
                t = new List<double>();
                cols = vars.Select(v => v >= 0 && v < Names.Count ? new List<double>() : null).ToArray();
            }
            var buf = new byte[rec];
            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
            {
                fs.Position = dataOffset;
                double prev = double.NegativeInfinity;
                Begin();
                for (int p = 0; p < Points; p++)
                {
                    if (fs.Read(buf, 0, rec) != rec) break;
                    double time = Math.Abs(BitConverter.ToDouble(buf, 0));     // LTspice uses the sign bit as a flag
                    if (p > 0 && time < prev)
                    {
                        Close();
                        if (++cur > last) break;
                        Begin();
                    }
                    prev = time;
                    if (t == null) continue;
                    t.Add(time);
                    for (int k = 0; k < cols.Length; k++)
                    {
                        if (cols[k] == null) continue;
                        int v = vars[k];
                        cols[k].Add(v == 0 ? time : Double ? BitConverter.ToDouble(buf, 8 * v) : BitConverter.ToSingle(buf, 8 + 4 * (v - 1)));
                    }
                }
                Close();
            }
            return res;
        }

        /// <summary>The chunk .raw files of a campaign folder: runs first..last, at a corner temperature or not.</summary>
        public sealed class Chunk
        {
            public string Path;
            public int Id, First, Last;
            public double Temp = double.NaN;
        }

        static readonly Regex ChunkName = new Regex(@"^chunk(\d+)_r(\d+)-(\d+)(?:_T(-?[0-9.]+))?\.raw$", RegexOptions.IgnoreCase);

        public static List<Chunk> Chunks(string campaignDir)
        {
            var list = new List<Chunk>();
            string dir = System.IO.Path.Combine(campaignDir, "chunks");
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "chunk*.raw"))
            {
                var m = ChunkName.Match(System.IO.Path.GetFileName(f));
                if (!m.Success) continue;
                list.Add(new Chunk
                {
                    Path = f, Id = int.Parse(m.Groups[1].Value, Inv), First = int.Parse(m.Groups[2].Value, Inv), Last = int.Parse(m.Groups[3].Value, Inv),
                    Temp = m.Groups[4].Success ? double.Parse(m.Groups[4].Value, Inv) : double.NaN,
                });
            }
            return list;
        }

        /// <summary>The .raw holding a run (at a corner temperature, if the campaign had corners): the newest chunk that ran it.</summary>
        public static Chunk ChunkOf(List<Chunk> chunks, int run, double temp)
        {
            return chunks.Where(c => run >= c.First && run <= c.Last &&
                                     (double.IsNaN(c.Temp) || double.IsNaN(temp) || Math.Abs(c.Temp - temp) < 1e-6))
                         .OrderByDescending(c => c.Id).FirstOrDefault();
        }
    }
}
