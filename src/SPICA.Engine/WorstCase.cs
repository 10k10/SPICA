using System.Collections.Generic;
using System.Linq;

namespace Spica.Core
{
    /// <summary>
    /// Worst case by all corners, on SPICA's own parameters: instead of random values, every tolerance site is set to an
    /// edge of its range (uniform: -1 / +1, gaussian: -k / +k sigma), in every combination. Run 1 is the nominal unit
    /// (every site 0), then the 2^n corners. Exact for circuits that move one way with each tolerance.
    /// </summary>
    public static class WorstCase
    {
        /// <summary>2^14 = 16384 runs per temperature: past this, all corners is not a sensible campaign.</summary>
        public const int MaxCornerSites = 14;

        public static double Edge(SiteKind kind, int sign, double k) => sign * (kind == SiteKind.Gaussian ? k : 1);

        public static List<double[]> CornerDesign(IList<McSite> sites, double k)
        {
            int n = sites.Count;
            var rows = new List<double[]> { new double[n] };
            for (long mask = 0; mask < 1L << n; mask++)
                rows.Add(sites.Select((s, i) => Edge(s.Kind, (mask >> i & 1) == 1 ? 1 : -1, k)).ToArray());
            return rows;
        }
    }
}
