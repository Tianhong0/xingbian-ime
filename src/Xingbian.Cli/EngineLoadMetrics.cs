using System;
using System.Diagnostics;
using System.Globalization;

namespace Xingbian.Cli
{
    /// <summary>一次引擎加载的耗时与内存占用记录（在 selftest / bench 之间传递）。</summary>
    internal sealed class EngineLoadMetrics
    {
        private EngineLoadMetrics()
        {
        }

        public double Milliseconds { get; private set; }

        public long WorkingSetBefore { get; private set; }

        public long WorkingSetAfter { get; private set; }

        public long ManagedBytesBefore { get; private set; }

        public long ManagedBytesAfter { get; private set; }

        /// <summary>常驻内存增量（字节，负数按 0 计）。</summary>
        public long WorkingSetDelta
        {
            get
            {
                long delta = WorkingSetAfter - WorkingSetBefore;
                return delta > 0 ? delta : 0;
            }
        }

        /// <summary>托管堆增量（字节，负数按 0 计）。</summary>
        public long ManagedDelta
        {
            get
            {
                long delta = ManagedBytesAfter - ManagedBytesBefore;
                return delta > 0 ? delta : 0;
            }
        }

        public double WorkingSetDeltaMegabytes
        {
            get { return WorkingSetDelta / 1048576.0; }
        }

        public double ManagedDeltaMegabytes
        {
            get { return ManagedDelta / 1048576.0; }
        }

        public static double ToMegabytes(long bytes)
        {
            return bytes / 1048576.0;
        }

        public static string FormatMegabytes(long bytes)
        {
            return ToMegabytes(bytes).ToString("F1", CultureInfo.InvariantCulture);
        }

        /// <summary>包住一次加载，记录耗时与内存增量。</summary>
        public static T Measure<T>(Func<T> loader, out EngineLoadMetrics metrics)
        {
            EngineLoadMetrics m = new EngineLoadMetrics();
            metrics = m;

            Process process = Process.GetCurrentProcess();
            process.Refresh();
            m.WorkingSetBefore = process.WorkingSet64;
            m.ManagedBytesBefore = GC.GetTotalMemory(true);

            Stopwatch stopwatch = Stopwatch.StartNew();
            T result;
            try
            {
                result = loader();
            }
            finally
            {
                stopwatch.Stop();
            }

            m.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
            m.ManagedBytesAfter = GC.GetTotalMemory(false);
            process.Refresh();
            m.WorkingSetAfter = process.WorkingSet64;

            return result;
        }
    }
}
