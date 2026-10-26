using System;
using System.Collections.Generic;
using System.Linq;

namespace Spica.Core
{
    /// <summary>Statistics of one finished chunk, fed back into the scheduler.</summary>
    public sealed class ChunkStat
    {
        public int Runs;
        public double WallSeconds;        // process start -> exit
        public double SimSeconds;         // "Total elapsed time" reported by LTspice
        public int Instances, Threads;    // configuration when the chunk started
        public int Epoch;                 // configuration epoch at start
        public bool Clean;                // configuration did not change while it ran
        public long PeakMemory;
        public long RawBytes;
        public bool Failed;
    }

    /// <summary>
    /// Decides how many LTspice instances run at once, how many threads each may use, and how many
    /// Monte Carlo runs go into each LTspice launch.
    ///
    /// 1. Start from the hardware (one instance per physical core, threads=1, capped by RAM) or from
    ///    the best configuration remembered for this machine + circuit.
    /// 2. Measure real throughput (runs/s across all instances) per configuration, from chunks that
    ///    ran entirely under it.
    /// 3. Hill-climb: try more instances, fewer instances, and fewer instances with more threads
    ///    each; keep a change only when throughput improves by more than 3 %.
    /// 4. Size chunks so LTspice start-up stays below ~5 % of the work, cap their duration so the
    ///    controller can react, and shrink them near the end (guided self-scheduling) so every
    ///    instance finishes at about the same time.
    /// 5. Memory guard: never run more instances than free RAM allows at the observed peak usage.
    /// </summary>
    public sealed class AdaptiveScheduler
    {
        public struct Config : IEquatable<Config>
        {
            public int Instances, Threads;
            public Config(int i, int t) { Instances = i; Threads = t; }
            public bool Equals(Config o) => Instances == o.Instances && Threads == o.Threads;
            public override bool Equals(object obj) => obj is Config c && Equals(c);
            public override int GetHashCode() => Instances * 397 ^ Threads;
            public override string ToString() => Threads == 1 ? $"{Instances} inst x 1 thread" : $"{Instances} inst x {Threads} threads";
        }

        sealed class Score
        {
            public int Chunks, Runs, SimChunks;
            public double Wall, Sim;

            /// <summary>
            /// Runs per second across all instances. Based on LTspice's own simulation time when
            /// available, which makes it independent of chunk size (start-up cost is amortised
            /// separately by chunk sizing); otherwise falls back to wall time.
            /// </summary>
            public double Throughput(int instances)
            {
                if (SimChunks == Chunks && Sim > 0) return instances * Runs / Sim;
                return Wall > 0 ? instances * Runs / Wall : 0;
            }
        }

        public enum Phase { WarmUp, Probing, Converged, Manual }

        readonly HardwareInfo hw;
        readonly int totalRuns;
        readonly Action<string> log;
        readonly Dictionary<Config, Score> scores = new Dictionary<Config, Score>();
        readonly HashSet<Config> tried = new HashSet<Config>();
        readonly Queue<Config> candidates = new Queue<Config>();

        public Config Current { get; private set; }
        public Config Best { get; private set; }
        double bestThroughput;
        public int Epoch { get; private set; }
        public Phase State { get; private set; }

        int memCap = int.MaxValue;
        double overheadEma = double.NaN;     // seconds of LTspice start-up per launch
        double perRunEma = double.NaN;       // simulation seconds per run under the current config
        double rawPerRunEma = double.NaN;    // bytes of .raw per run
        public long MaxPeakMemory { get; private set; }

        public double OverheadSeconds => overheadEma;
        public double SecondsPerRun => perRunEma;
        public double RawBytesPerRun => rawPerRunEma;

        public AdaptiveScheduler(HardwareInfo hw, int totalRuns, ProfileStore.Profile warm, Config? manual, Action<string> log)
        {
            this.hw = hw;
            this.totalRuns = totalRuns;
            this.log = log ?? (_ => { });

            if (manual.HasValue)
            {
                Current = Best = manual.Value;
                State = Phase.Manual;
                this.log($"Manual parallelism: {Current}.");
                return;
            }

            Config start;
            if (warm != null)
            {
                start = new Config(Clamp(warm.Instances, 1, hw.LogicalProcessors), Clamp(warm.Threads, 1, hw.LogicalProcessors));
                perRunEma = warm.SecondsPerRun;
                overheadEma = warm.OverheadSeconds;
                this.log($"Warm start from saved profile: {start} ({warm.SecondsPerRun:0.###} s/run).");
            }
            else
            {
                start = new Config(Math.Max(1, hw.PhysicalCores), 1);
                this.log($"Starting point from hardware: {start} ({hw.PhysicalCores} physical cores, {hw.LogicalProcessors} threads).");
            }
            start.Instances = Math.Min(start.Instances, Math.Max(1, totalRuns));
            Current = Best = start;
            tried.Add(start);
            State = Phase.WarmUp;
        }

        static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

        int MaxInstances => Math.Max(1, Math.Min(hw.LogicalProcessors, memCap));

        // ------------------------------------------------------------------ chunk sizing

        /// <param name="unallocated">Runs not yet handed to any instance.</param>
        public int NextChunkSize(int unallocated)
        {
            if (unallocated <= 0) return 0;
            int inst = Math.Max(1, Current.Instances);

            // Guided self-scheduling: never take more than half of an equal share of what is left,
            // so the tail is split finely and all instances finish together.
            int tail = (int)Math.Ceiling(unallocated / (2.0 * inst));

            if (double.IsNaN(perRunEma) || perRunEma <= 0)
                return Clamp(Math.Min(2, tail), 1, unallocated);   // probe chunk: learn the timings cheaply

            double overhead = double.IsNaN(overheadEma) ? 1.0 : Math.Max(0.05, overheadEma);
            int forEfficiency = (int)Math.Ceiling(overhead / (0.05 * perRunEma));     // start-up <= ~5 %

            double maxChunkSeconds = State == Phase.WarmUp || State == Phase.Probing ? 15 : 90;
            int byDuration = Math.Max(1, (int)(maxChunkSeconds / perRunEma));

            // The tail stops shrinking once a chunk would simulate for less than one start-up:
            // smaller chunks cost more in launches than they save in end-of-campaign imbalance.
            int tailFloor = Math.Max(1, (int)Math.Ceiling(overhead / perRunEma));

            int n = Math.Min(forEfficiency, byDuration);
            n = Math.Min(n, Math.Max(tail, tailFloor));
            if (!double.IsNaN(rawPerRunEma) && rawPerRunEma > 0)
                n = Math.Min(n, Math.Max(1, (int)(1.5e9 / rawPerRunEma)));          // keep each .raw < ~1.5 GB
            return Clamp(n, 1, unallocated);
        }

        // ------------------------------------------------------------------ feedback

        public void OnChunkDone(ChunkStat s, int unallocated)
        {
            if (s.Failed || s.Runs <= 0) return;

            if (s.PeakMemory > MaxPeakMemory) MaxPeakMemory = s.PeakMemory;
            if (s.RawBytes > 0) rawPerRunEma = Ema(rawPerRunEma, s.RawBytes / (double)s.Runs, 0.3);

            if (!double.IsNaN(s.SimSeconds) && s.SimSeconds >= 0)
            {
                double ov = Math.Max(0, s.WallSeconds - s.SimSeconds);
                overheadEma = Ema(overheadEma, ov, 0.3);
            }

            var cfg = new Config(s.Instances, s.Threads);
            if (s.Clean && s.Epoch == Epoch)
                perRunEma = Ema(perRunEma, (!double.IsNaN(s.SimSeconds) && s.SimSeconds > 0 ? s.SimSeconds : s.WallSeconds) / s.Runs, 0.35);

            if (!s.Clean) return;
            if (!scores.TryGetValue(cfg, out var sc)) scores[cfg] = sc = new Score();
            sc.Chunks++; sc.Runs += s.Runs; sc.Wall += s.WallSeconds;
            if (!double.IsNaN(s.SimSeconds) && s.SimSeconds > 0) { sc.Sim += s.SimSeconds; sc.SimChunks++; }

            if (State == Phase.Manual || State == Phase.Converged) return;
            if (!cfg.Equals(Current) || s.Epoch != Epoch) return;

            int need = Math.Max(3, Current.Instances);
            if (sc.Chunks < need) return;

            double thr = sc.Throughput(Current.Instances);
            if (State == Phase.WarmUp)
            {
                bestThroughput = thr;
                Best = Current;
                log($"Baseline {Current}: capacity {thr * 60:0.0} runs/min of pure simulation.");
                BuildCandidates(null);
                NextCandidateOrConverge(unallocated);
                return;
            }

            // Probing: compare against best.
            if (thr > bestThroughput * 1.03)
            {
                var prev = Best;
                log($"Better: {Current} gives {thr * 60:0.0} runs/min (was {bestThroughput * 60:0.0}). Keeping it.");
                Best = Current;
                bestThroughput = thr;
                BuildCandidates(prev);
            }
            else
            {
                log($"No gain: {Current} gives {thr * 60:0.0} runs/min vs best {bestThroughput * 60:0.0}.");
            }
            NextCandidateOrConverge(unallocated);
        }

        /// <summary>Neighbours of the best configuration, continuing in the direction that just helped.</summary>
        void BuildCandidates(Config? previousBest)
        {
            candidates.Clear();
            var b = Best;
            int step = Math.Max(1, (int)Math.Round(b.Instances * 0.25));
            var list = new List<Config>();

            var up = new Config(Math.Min(b.Instances + step, Math.Max(1, MaxInstances / b.Threads)), b.Threads);
            var down = new Config(Math.Max(1, b.Instances - step), b.Threads);
            var thr = new Config(Math.Max(1, b.Instances / 2), b.Threads * 2);

            bool cameFromBelow = previousBest.HasValue && previousBest.Value.Instances < b.Instances && previousBest.Value.Threads == b.Threads;
            bool cameFromAbove = previousBest.HasValue && previousBest.Value.Instances > b.Instances && previousBest.Value.Threads == b.Threads;

            if (cameFromAbove) { list.Add(down); list.Add(thr); }
            else if (cameFromBelow) { list.Add(up); list.Add(thr); }
            else { list.Add(up); list.Add(down); list.Add(thr); }

            foreach (var c in list)
            {
                if (c.Equals(b) || tried.Contains(c)) continue;
                if (c.Instances * c.Threads > hw.LogicalProcessors * 1) continue;
                if (c.Instances > MaxInstances) continue;
                candidates.Enqueue(c);
            }
        }

        void NextCandidateOrConverge(int unallocated)
        {
            // Probing only pays off while plenty of work remains.
            double remainingSeconds = double.IsNaN(perRunEma) ? double.PositiveInfinity
                : unallocated * perRunEma / Math.Max(1, Current.Instances);
            bool worthIt = unallocated > totalRuns * 0.25 && remainingSeconds > 20;

            if (candidates.Count > 0 && worthIt)
            {
                var c = candidates.Dequeue();
                tried.Add(c);
                Apply(c, Phase.Probing);
                log($"Probing {c}...");
                return;
            }
            Apply(Best, Phase.Converged);
            log(worthIt || candidates.Count == 0
                ? $"Converged on {Best} ({bestThroughput * 60:0.0} runs/min)."
                : $"Not enough work left to keep probing; using {Best}.");
        }

        void Apply(Config c, Phase p)
        {
            c.Instances = Math.Min(c.Instances, MaxInstances);
            // perRunEma is kept as a prior for chunk sizing; clean chunks of the new epoch refine it.
            if (!c.Equals(Current)) { Current = c; Epoch++; }
            State = p;
        }

        /// <summary>Memory guard, called periodically by the engine.</summary>
        public void OnMemorySample(long availableBytes, long ourWorkingSet)
        {
            if (MaxPeakMemory <= 0) return;
            long usable = (long)((availableBytes + ourWorkingSet) * 0.85);
            int cap = Math.Max(1, (int)(usable / MaxPeakMemory));
            if (cap == memCap) return;
            memCap = cap;
            if (Current.Instances > cap)
            {
                log($"Memory guard: each instance peaks at {MaxPeakMemory / 1048576.0:0} MB, limiting to {cap} instances.");
                var c = Current; c.Instances = cap;
                Current = c; Epoch++;
                if (Best.Instances > cap) { var b = Best; b.Instances = cap; Best = b; }
            }
        }

        public ProfileStore.Profile ToProfile() => new ProfileStore.Profile
        {
            Instances = Best.Instances,
            Threads = Best.Threads,
            SecondsPerRun = double.IsNaN(perRunEma) ? 0 : perRunEma,
            OverheadSeconds = double.IsNaN(overheadEma) ? 0 : overheadEma,
        };

        public double BestThroughputPerMinute => bestThroughput * 60;

        static double Ema(double prev, double x, double a) => double.IsNaN(prev) ? x : prev + a * (x - prev);
    }
}
