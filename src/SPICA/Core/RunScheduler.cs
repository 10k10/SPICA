using System;

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
    /// Decides how many LTspice instances run at once and how many Monte Carlo runs go into each launch,
    /// with one fixed rule:
    ///
    ///   instances  N = physical cores (or the manual value), never more than free RAM allows
    ///   x          = seconds one run takes (measured: every instance first simulates one run alone)
    ///   chunk      k = runs that fit in ChunkSeconds (k * x ~ 2 min), at most an equal share of
    ///                  what is left (ceil(left / N)) so all instances finish together
    ///   launches   z = ceil(runs / k)
    ///
    /// A 2-minute chunk keeps LTspice start-up (~1 s) below 1 % of the work and still saves progress often.
    /// </summary>
    public sealed class RunScheduler
    {
        public const double ChunkSeconds = 120;

        public struct Config : IEquatable<Config>
        {
            public int Instances, Threads;
            public Config(int i, int t) { Instances = i; Threads = t; }
            public bool Equals(Config o) => Instances == o.Instances && Threads == o.Threads;
            public override bool Equals(object obj) => obj is Config c && Equals(c);
            public override int GetHashCode() => Instances * 397 ^ Threads;
            public override string ToString() => Threads == 1 ? $"{Instances} inst x 1 thread" : $"{Instances} inst x {Threads} threads";
        }

        public enum Phase { Measuring, Running, Manual }

        readonly HardwareInfo hw;
        readonly Action<string> log;
        readonly bool manual;
        int lastPlanChunk;

        public Config Current { get; private set; }
        public Config Best => Current;
        public int Epoch { get; private set; }
        public Phase State { get; private set; }

        double overheadEma = double.NaN;     // seconds of LTspice start-up per launch
        double perRunEma = double.NaN;       // simulation seconds per run (x)
        double rawPerRunEma = double.NaN;    // bytes of .raw per run
        public long MaxPeakMemory { get; private set; }

        public double OverheadSeconds => overheadEma;
        public double SecondsPerRun => perRunEma;
        public double RawBytesPerRun => rawPerRunEma;

        /// <summary>Runs per minute across all instances at the measured x.</summary>
        public double BestThroughputPerMinute => double.IsNaN(perRunEma) || perRunEma <= 0 ? 0 : Current.Instances * 60 / perRunEma;

        public RunScheduler(HardwareInfo hw, int totalRuns, ProfileStore.Profile warm, Config? manualConfig, Action<string> log)
        {
            this.hw = hw;
            this.log = log ?? (_ => { });
            manual = manualConfig.HasValue;
            var wanted = manualConfig ?? new Config(Math.Max(1, hw.PhysicalCores), 1);
            wanted.Instances = Math.Max(1, Math.Min(wanted.Instances, Math.Max(1, totalRuns)));
            Current = wanted;

            if (warm != null && warm.SecondsPerRun > 0)
            {
                perRunEma = warm.SecondsPerRun;
                overheadEma = warm.OverheadSeconds;
            }
            State = manual ? Phase.Manual : double.IsNaN(perRunEma) ? Phase.Measuring : Phase.Running;
            this.log(manual ? $"Manual parallelism: {Current}."
                            : $"{Current}: one instance per physical core ({hw.PhysicalCores} cores, {hw.LogicalProcessors} threads).");
            if (!double.IsNaN(perRunEma))
                this.log($"Run time from the last campaign of this circuit on this machine: {perRunEma:0.###} s/run.");
            else
                this.log("Measuring the run time: each instance simulates one run first.");
        }

        static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

        // ------------------------------------------------------------------ chunk sizing

        /// <param name="unallocated">Runs not yet handed to any instance.</param>
        public int NextChunkSize(int unallocated)
        {
            if (unallocated <= 0) return 0;
            if (double.IsNaN(perRunEma) || perRunEma <= 0) return 1;      // measuring x

            int n = Math.Max(1, (int)(ChunkSeconds / perRunEma));
            int share = (int)Math.Ceiling(unallocated / (double)Math.Max(1, Current.Instances));
            n = Math.Min(n, share);
            if (!double.IsNaN(rawPerRunEma) && rawPerRunEma > 0)
                n = Math.Min(n, Math.Max(1, (int)(1.5e9 / rawPerRunEma)));   // keep each .raw < ~1.5 GB
            n = Clamp(n, 1, unallocated);

            // log the plan when it is first known, and again when the chunk size moves by more than 25 %
            // (except in the tail, where the equal-share cap shrinks it on purpose)
            if (lastPlanChunk == 0 || (share > n && Math.Abs(n - lastPlanChunk) > 0.25 * lastPlanChunk))
            {
                LogPlan(n, unallocated);
                lastPlanChunk = n;
            }
            return n;
        }

        void LogPlan(int k, int left)
        {
            int inst = Math.Max(1, Current.Instances);
            double cpu = left * perRunEma, wall = cpu / inst;
            log($"Plan: {left} runs x {perRunEma:0.##} s = {Hours(cpu)} of simulation / {inst} instances = about {Hours(wall)}; " +
                $"chunks of {k} runs (~{k * perRunEma:0} s) -> {(left + k - 1) / k} launches.");
        }

        static string Hours(double s) => s < 120 ? $"{s:0} s" : s < 7200 ? $"{s / 60:0} min" : $"{s / 3600:0.0} h";

        // ------------------------------------------------------------------ feedback

        public void OnChunkDone(ChunkStat s, int unallocated)
        {
            if (s.Failed || s.Runs <= 0) return;

            if (s.PeakMemory > MaxPeakMemory) MaxPeakMemory = s.PeakMemory;
            if (s.RawBytes > 0) rawPerRunEma = Ema(rawPerRunEma, s.RawBytes / (double)s.Runs, 0.3);
            bool hasSim = !double.IsNaN(s.SimSeconds) && s.SimSeconds > 0;
            if (hasSim) overheadEma = Ema(overheadEma, Math.Max(0, s.WallSeconds - s.SimSeconds), 0.3);

            // x under the real load, from every chunk (instances share caches and memory bandwidth)
            bool first = double.IsNaN(perRunEma);
            perRunEma = Ema(perRunEma, (hasSim ? s.SimSeconds : s.WallSeconds) / s.Runs, 0.35);
            if (first)
            {
                log($"Measured {perRunEma:0.###} s per run.");
                if (State == Phase.Measuring) State = Phase.Running;
            }
        }

        /// <summary>Memory guard, called periodically by the engine: instances = min(N, free RAM / peak RAM per instance).
        /// It only ever lowers the count, so it cannot oscillate.</summary>
        public void OnMemorySample(long availableBytes, long ourWorkingSet)
        {
            if (MaxPeakMemory <= 0) return;
            long usable = (long)((availableBytes + ourWorkingSet) * 0.85);
            int cap = Math.Max(1, (int)(usable / MaxPeakMemory));
            if (cap >= Current.Instances) return;
            log($"Memory guard: each instance peaks at {MaxPeakMemory / 1048576.0:0} MB, limiting to {cap} instances.");
            Current = new Config(cap, Current.Threads); Epoch++;
        }

        public ProfileStore.Profile ToProfile() => new ProfileStore.Profile
        {
            Instances = Current.Instances,
            Threads = Current.Threads,
            SecondsPerRun = double.IsNaN(perRunEma) ? 0 : perRunEma,
            OverheadSeconds = double.IsNaN(overheadEma) ? 0 : overheadEma,
        };

        static double Ema(double prev, double x, double a) => double.IsNaN(prev) ? x : prev + a * (x - prev);
    }
}
