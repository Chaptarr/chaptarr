using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace NzbDrone.Core.Instrumentation
{
    public static class MemorySnapshot
    {
        public static string Capture()
        {
            var managedBytes = GC.GetTotalMemory(false);

            using var process = Process.GetCurrentProcess();

            return $"managed={FormatBytes(managedBytes)}, workingSet={FormatBytes(process.WorkingSet64)}, private={FormatBytes(process.PrivateMemorySize64)}";
        }

        public static string CaptureDetailed()
        {
            var managedBytes = GC.GetTotalMemory(false);
            var gcInfo = GC.GetGCMemoryInfo();

            using var process = Process.GetCurrentProcess();
            var privateBytes = process.PrivateMemorySize64;
            var nonGcPrivateBytes = Math.Max(0, privateBytes - gcInfo.TotalCommittedBytes);

            return $"managed={FormatBytes(managedBytes)}, " +
                   $"gcHeap={FormatBytes(gcInfo.HeapSizeBytes)}, " +
                   $"gcCommitted={FormatBytes(gcInfo.TotalCommittedBytes)}, " +
                   $"gcFragmented={FormatBytes(gcInfo.FragmentedBytes)}, " +
                   $"nonGcPrivate={FormatBytes(nonGcPrivateBytes)}, " +
                   $"totalAllocated={FormatBytes(GC.GetTotalAllocatedBytes(precise: false))}, " +
                   $"memoryLoad={FormatBytes(gcInfo.MemoryLoadBytes)}, " +
                   $"totalAvailable={FormatBytes(gcInfo.TotalAvailableMemoryBytes)}, " +
                   $"workingSet={FormatBytes(process.WorkingSet64)}, " +
                   $"private={FormatBytes(privateBytes)}, " +
                   $"gen0={GC.CollectionCount(0)}, " +
                   $"gen1={GC.CollectionCount(1)}, " +
                   $"gen2={GC.CollectionCount(2)}, " +
                   $"serverGC={GCSettings.IsServerGC}, " +
                   $"latency={GCSettings.LatencyMode}";
        }

        public static void CollectFullCompacting()
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }

        public static bool TryTrimNativeHeap()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return false;
            }

            try
            {
                return MallocTrim(UIntPtr.Zero) == 1;
            }
            catch
            {
                return false;
            }
        }

        public static bool ReleaseUnusedMemory()
        {
            CollectFullCompacting();
            return TryTrimNativeHeap();
        }

        private static string FormatBytes(long bytes)
        {
            const double mib = 1024d * 1024d;
            return $"{bytes / mib:0.0} MiB";
        }

        [DllImport("libc", EntryPoint = "malloc_trim")]
        private static extern int MallocTrim(UIntPtr pad);
    }
}
