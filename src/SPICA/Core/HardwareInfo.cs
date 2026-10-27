using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Spica.Core
{
    /// <summary>Topology of the machine: cores, SMT, hybrid P/E cores, NUMA nodes, memory.</summary>
    public sealed class HardwareInfo
    {
        public string CpuName = "Unknown CPU";
        public int LogicalProcessors;
        public int PhysicalCores;
        public int SmtCores;              // physical cores exposing more than one logical processor
        public int PerformanceCores;      // hybrid CPUs: cores of the highest efficiency class
        public int EfficiencyCores;
        public int NumaNodes = 1;
        public int ProcessorGroups = 1;
        public long TotalMemory;
        public long AvailableMemory;

        public bool IsHybrid => EfficiencyCores > 0 && PerformanceCores > 0;
        public bool HasSmt => SmtCores > 0 && LogicalProcessors > PhysicalCores;

        /// <summary>Stable identity of the machine for scheduler profiles.</summary>
        public string MachineKey => $"{CpuName.Trim()}|{PhysicalCores}C{LogicalProcessors}T|{TotalMemory / (1L << 30)}GB";

        public static HardwareInfo Detect()
        {
            var hw = new HardwareInfo();
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                    hw.CpuName = (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? hw.CpuName;
            }
            catch { }

            hw.LogicalProcessors = Environment.ProcessorCount;
            try { ReadTopology(hw); } catch { }
            if (hw.PhysicalCores <= 0) hw.PhysicalCores = Math.Max(1, hw.LogicalProcessors / 2);
            if (hw.LogicalProcessors < hw.PhysicalCores) hw.LogicalProcessors = hw.PhysicalCores;

            RefreshMemory(hw);
            return hw;
        }

        public static void RefreshMemory(HardwareInfo hw)
        {
            var ms = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(ms))
            {
                hw.TotalMemory = (long)ms.ullTotalPhys;
                hw.AvailableMemory = (long)ms.ullAvailPhys;
            }
        }

        public static long AvailableMemoryNow()
        {
            var ms = new MEMORYSTATUSEX();
            return GlobalMemoryStatusEx(ms) ? (long)ms.ullAvailPhys : 0;
        }

        /// <summary>Physical memory in use, 0..100 %.</summary>
        public static double MemoryLoadPercent()
        {
            var ms = new MEMORYSTATUSEX();
            return GlobalMemoryStatusEx(ms) && ms.ullTotalPhys > 0 ? 100.0 * (ms.ullTotalPhys - ms.ullAvailPhys) / ms.ullTotalPhys : double.NaN;
        }

        static long lastIdle, lastBusy;

        /// <summary>Whole-machine CPU use since the previous call, 0..100 % (NaN on the first call).
        /// GetSystemTimes instead of performance counters, whose names are localized.</summary>
        public static double CpuLoadPercent()
        {
            if (!GetSystemTimes(out long idle, out long kernel, out long user)) return double.NaN;
            long busy = kernel + user - idle;              // kernel time includes idle time
            long dIdle = idle - lastIdle, dBusy = busy - lastBusy;
            bool first = lastIdle == 0 && lastBusy == 0;
            lastIdle = idle; lastBusy = busy;
            if (first || dIdle + dBusy <= 0) return double.NaN;
            return 100.0 * dBusy / (dIdle + dBusy);
        }

        static void ReadTopology(HardwareInfo hw)
        {
            // RelationAll = 0xFFFF
            uint len = 0;
            GetLogicalProcessorInformationEx(0xFFFF, IntPtr.Zero, ref len);
            if (len == 0) return;
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(0xFFFF, buf, ref len)) return;
                int cores = 0, smt = 0, numa = 0, groups = 1, logical = 0;
                var effClasses = new List<byte>();
                long offset = 0;
                while (offset < len)
                {
                    IntPtr p = new IntPtr(buf.ToInt64() + offset);
                    int relationship = Marshal.ReadInt32(p, 0);
                    int size = Marshal.ReadInt32(p, 4);
                    if (size <= 0) break;
                    switch (relationship)
                    {
                        case 0: // RelationProcessorCore -> PROCESSOR_RELATIONSHIP at +8
                            {
                                cores++;
                                byte flags = Marshal.ReadByte(p, 8);
                                byte eff = Marshal.ReadByte(p, 9);
                                if ((flags & 1) != 0) smt++;
                                effClasses.Add(eff);
                                break;
                            }
                        case 1: // RelationNumaNode
                            numa++;
                            break;
                        case 4: // RelationGroup -> GROUP_RELATIONSHIP at +8: MaximumGroupCount, ActiveGroupCount
                            {
                                groups = Marshal.ReadInt16(p, 10);
                                int baseOff = 8 + 4 + 20; // GROUP_RELATIONSHIP header
                                for (int gidx = 0; gidx < groups; gidx++)
                                {
                                    // PROCESSOR_GROUP_INFO: Maximum(1) Active(1) Reserved(38) ActiveProcessorMask(8 on x64)
                                    int infoOff = baseOff + gidx * (IntPtr.Size == 8 ? 48 : 44);
                                    logical += Marshal.ReadByte(p, infoOff + 1);
                                }
                                break;
                            }
                    }
                    offset += size;
                }
                hw.PhysicalCores = cores;
                hw.SmtCores = smt;
                hw.NumaNodes = Math.Max(1, numa);
                hw.ProcessorGroups = Math.Max(1, groups);
                if (logical > 0) hw.LogicalProcessors = logical;

                if (effClasses.Count > 0)
                {
                    byte max = 0, min = 255;
                    foreach (var e in effClasses) { if (e > max) max = e; if (e < min) min = e; }
                    if (max != min)
                        foreach (var e in effClasses) { if (e == max) hw.PerformanceCores++; else hw.EfficiencyCores++; }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public override string ToString()
        {
            var s = $"{CpuName}: {PhysicalCores} cores / {LogicalProcessors} threads";
            if (IsHybrid) s += $" ({PerformanceCores}P + {EfficiencyCores}E)";
            if (NumaNodes > 1) s += $", {NumaNodes} NUMA nodes";
            s += $", {TotalMemory / (double)(1L << 30):0.0} GB RAM";
            return s;
        }

        // ------------------------------------------------------------------ P/Invoke

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);
    }
}
