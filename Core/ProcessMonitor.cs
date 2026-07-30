using System.Diagnostics;

namespace ClrDiag.Core;

/// <summary>
/// 以固定間隔取樣目標行程的記憶體 / CPU / CLR 指標，並保留歷史值供走勢圖使用。
/// 取樣執行緒獨立於 UI，UI 只讀取快照結果。
/// </summary>
public sealed class ProcessMonitor : IDisposable
{
    private readonly object gate = new();
    private readonly RingBuffer<MetricSample> history;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource cts = new();

    private Process? target;
    private ClrCounterSet? clrCounters;
    private Task? loop;
    private TimeSpan lastCpuTime;
    private DateTime lastCpuStamp;

    public ProcessMonitor(int historyCapacity = 900, TimeSpan? interval = null)
    {
        history = new RingBuffer<MetricSample>(historyCapacity);
        this.interval = interval ?? TimeSpan.FromSeconds(1);
    }

    public int? TargetPid { get; private set; }

    public string? TargetName { get; private set; }

    public DateTime? TargetStartTime { get; private set; }

    /// <summary>目標行程是否仍存活；沒有目標時為 false。</summary>
    public bool IsTargetAlive
    {
        get
        {
            lock (gate)
            {
                return target is { HasExited: false };
            }
        }
    }

    public string? CounterStatus
    {
        get
        {
            lock (gate)
            {
                if (clrCounters is null)
                {
                    return null;
                }

                return clrCounters.IsAvailable ? null : clrCounters.LastError;
            }
        }
    }

    public void Start()
    {
        loop ??= Task.Run(() => SampleLoopAsync(cts.Token));
    }

    /// <summary>切換監看目標；傳入 null 表示解除監看。歷史資料會清空重新累積。</summary>
    public void Attach(int? processId)
    {
        lock (gate)
        {
            target?.Dispose();
            clrCounters?.Dispose();
            target = null;
            clrCounters = null;
            TargetPid = null;
            TargetName = null;
            TargetStartTime = null;
            lastCpuTime = TimeSpan.Zero;
            lastCpuStamp = default;

            if (processId is null)
            {
                return;
            }

            try
            {
                Process process = Process.GetProcessById(processId.Value);
                target = process;
                TargetPid = process.Id;
                TargetName = process.ProcessName;
                TargetStartTime = process.StartTime;
                clrCounters = new ClrCounterSet(process.Id);
            }
            catch
            {
                // 行程可能剛結束，維持未附加狀態
            }
        }
    }

    public MetricSample[] History
    {
        get
        {
            lock (gate)
            {
                return history.TakeLast(history.Count);
            }
        }
    }

    public MetricSample? Latest
    {
        get
        {
            lock (gate)
            {
                return history.TryGetLast(out MetricSample last) ? last : null;
            }
        }
    }

    private async Task SampleLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                SampleOnce();
            }
            catch
            {
                // 取樣失敗不應中斷迴圈（行程結束、計數器暫時不可用等）
            }

            try
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void SampleOnce()
    {
        lock (gate)
        {
            if (target is null)
            {
                return;
            }

            if (target.HasExited)
            {
                target.Dispose();
                clrCounters?.Dispose();
                target = null;
                clrCounters = null;
                return;
            }

            target.Refresh();

            DateTime now = DateTime.Now;
            TimeSpan cpuTime = SafeCpuTime(target);
            double cpuPercent = 0;
            if (lastCpuStamp != default)
            {
                double wallMs = (now - lastCpuStamp).TotalMilliseconds;
                if (wallMs > 0)
                {
                    cpuPercent =
                        (cpuTime - lastCpuTime).TotalMilliseconds
                        / wallMs
                        / Environment.ProcessorCount
                        * 100.0;
                }
            }

            lastCpuTime = cpuTime;
            lastCpuStamp = now;

            bool countersReady = clrCounters?.EnsureResolved() == true;

            var sample = new MetricSample
            {
                TimeStamp = now,
                WorkingSetMb = target.WorkingSet64 / 1024.0 / 1024.0,
                PrivateMb = target.PrivateMemorySize64 / 1024.0 / 1024.0,
                CpuPercent = Math.Clamp(cpuPercent, 0, 100 * Environment.ProcessorCount),
                ThreadCount = SafeThreadCount(target),
                HandleCount = SafeHandleCount(target),
                Gen0Mb = countersReady ? ToMb(clrCounters!.ReadMemoryRaw("Gen 0 heap size")) : null,
                Gen1Mb = countersReady ? ToMb(clrCounters!.ReadMemoryRaw("Gen 1 heap size")) : null,
                Gen2Mb = countersReady ? ToMb(clrCounters!.ReadMemoryRaw("Gen 2 heap size")) : null,
                LohMb = countersReady
                    ? ToMb(clrCounters!.ReadMemoryRaw("Large Object Heap size"))
                    : null,
                AllHeapsMb = countersReady
                    ? ToMb(clrCounters!.ReadMemoryRaw("# Bytes in all Heaps"))
                    : null,
                CommittedMb = countersReady
                    ? ToMb(clrCounters!.ReadMemoryRaw("# Total committed Bytes"))
                    : null,
                Gen0Collections = countersReady
                    ? clrCounters!.ReadMemoryRaw("# Gen 0 Collections")
                    : null,
                Gen1Collections = countersReady
                    ? clrCounters!.ReadMemoryRaw("# Gen 1 Collections")
                    : null,
                Gen2Collections = countersReady
                    ? clrCounters!.ReadMemoryRaw("# Gen 2 Collections")
                    : null,
                TimeInGcPercent = countersReady ? clrCounters!.ReadMemory("% Time in GC") : null,
                PinnedObjects = countersReady
                    ? clrCounters!.ReadMemoryRaw("# of Pinned Objects")
                    : null,
                ExceptionsPerSec = countersReady
                    ? clrCounters!.ReadExceptions("# of Exceps Thrown / sec")
                    : null,
                ContentionPerSec = countersReady
                    ? clrCounters!.ReadLocks("Contention Rate / sec")
                    : null,
            };

            history.Add(sample);
        }
    }

    private static double? ToMb(long? bytes) =>
        bytes is null ? null : bytes.Value / 1024.0 / 1024.0;

    private static TimeSpan SafeCpuTime(Process process)
    {
        try
        {
            return process.TotalProcessorTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static int SafeThreadCount(Process process)
    {
        try
        {
            return process.Threads.Count;
        }
        catch
        {
            return 0;
        }
    }

    private static int SafeHandleCount(Process process)
    {
        try
        {
            return process.HandleCount;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        lock (gate)
        {
            target?.Dispose();
            clrCounters?.Dispose();
        }

        cts.Dispose();
    }
}
