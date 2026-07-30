namespace ClrDiag.Core;

/// <summary>單次取樣的行程 / CLR 記憶體指標。null 代表該來源當下無法取得。</summary>
public readonly record struct MetricSample
{
    public DateTime TimeStamp { get; init; }

    // --- 來自 System.Diagnostics.Process ---
    public double WorkingSetMb { get; init; }
    public double PrivateMb { get; init; }
    public double CpuPercent { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }

    // --- 來自 .NET CLR 效能計數器（取不到時為 null） ---
    public double? Gen0Mb { get; init; }
    public double? Gen1Mb { get; init; }
    public double? Gen2Mb { get; init; }
    public double? LohMb { get; init; }
    public double? AllHeapsMb { get; init; }
    public double? CommittedMb { get; init; }
    public long? Gen0Collections { get; init; }
    public long? Gen1Collections { get; init; }
    public long? Gen2Collections { get; init; }
    public double? TimeInGcPercent { get; init; }
    public double? ExceptionsPerSec { get; init; }
    public double? ContentionPerSec { get; init; }
    public long? PinnedObjects { get; init; }
}
