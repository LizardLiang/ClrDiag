using System.Diagnostics;

namespace ClrDiag.Core;

/// <summary>
/// 依 PID 綁定 .NET CLR 效能計數器。
/// CLR 計數器的執行個體名稱是「行程名稱」加序號（iisexpress、iisexpress#1…），
/// 序號會隨行程啟動順序改變，因此每次都以 "Process ID" 計數器驗證是否仍指向同一個 PID。
/// </summary>
public sealed class ClrCounterSet : IDisposable
{
    private const string MemoryCategory = ".NET CLR Memory";
    private const string ExceptionCategory = ".NET CLR Exceptions";
    private const string LockCategory = ".NET CLR LocksAndThreads";

    private readonly int processId;
    private readonly Dictionary<string, PerformanceCounter> counters = new(StringComparer.Ordinal);
    private string? instanceName;
    private DateTime nextResolveAttempt = DateTime.MinValue;

    public ClrCounterSet(int processId) => this.processId = processId;

    /// <summary>最近一次無法讀取計數器的原因，供 UI 顯示。</summary>
    public string? LastError { get; private set; }

    public bool IsAvailable => instanceName is not null;

    /// <summary>確認（必要時重新解析）計數器執行個體是否仍對應目標 PID。</summary>
    public bool EnsureResolved()
    {
        if (instanceName is not null && MatchesPid(instanceName))
        {
            return true;
        }

        ResetCounters();

        // 解析失敗時不要每秒重試，避免列舉大量執行個體造成負擔
        if (DateTime.UtcNow < nextResolveAttempt)
        {
            return false;
        }

        nextResolveAttempt = DateTime.UtcNow.AddSeconds(5);

        try
        {
            var category = new PerformanceCounterCategory(MemoryCategory);
            foreach (string candidate in category.GetInstanceNames())
            {
                if (MatchesPid(candidate))
                {
                    instanceName = candidate;
                    LastError = null;
                    return true;
                }
            }

            LastError = "找不到對應 PID 的 .NET CLR Memory 執行個體（目標可能尚未載入 CLR）";
        }
        catch (Exception ex)
        {
            LastError = $"無法列舉 CLR 計數器: {ex.Message}";
        }

        return false;
    }

    public double? ReadMemory(string counterName) => Read(MemoryCategory, counterName);

    public double? ReadExceptions(string counterName) => Read(ExceptionCategory, counterName);

    public double? ReadLocks(string counterName) => Read(LockCategory, counterName);

    /// <summary>讀取原始值（計數類計數器用，不需要兩次取樣）。</summary>
    public long? ReadMemoryRaw(string counterName)
    {
        PerformanceCounter? counter = GetCounter(MemoryCategory, counterName);
        if (counter is null)
        {
            return null;
        }

        try
        {
            return counter.RawValue;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    private double? Read(string category, string counterName)
    {
        PerformanceCounter? counter = GetCounter(category, counterName);
        if (counter is null)
        {
            return null;
        }

        try
        {
            return counter.NextValue();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    private PerformanceCounter? GetCounter(string category, string counterName)
    {
        if (instanceName is null)
        {
            return null;
        }

        string key = category + "|" + counterName;
        if (counters.TryGetValue(key, out PerformanceCounter? existing))
        {
            return existing;
        }

        try
        {
            var counter = new PerformanceCounter(
                category,
                counterName,
                instanceName,
                readOnly: true
            );
            _ = counter.NextValue(); // 速率型計數器需要先取一次基準值
            counters[key] = counter;
            return counter;
        }
        catch (Exception ex)
        {
            LastError = $"{category}\\{counterName}: {ex.Message}";
            return null;
        }
    }

    private bool MatchesPid(string candidate)
    {
        try
        {
            using var pidCounter = new PerformanceCounter(
                MemoryCategory,
                "Process ID",
                candidate,
                readOnly: true
            );
            return (int)pidCounter.RawValue == processId;
        }
        catch
        {
            return false;
        }
    }

    private void ResetCounters()
    {
        foreach (PerformanceCounter counter in counters.Values)
        {
            counter.Dispose();
        }

        counters.Clear();
        instanceName = null;
    }

    public void Dispose() => ResetCounters();
}
