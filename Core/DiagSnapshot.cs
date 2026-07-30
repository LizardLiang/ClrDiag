namespace ClrDiag.Core;

/// <summary>受控堆疊中某個型別的統計。</summary>
public sealed record HeapTypeStat(string TypeName, long Count, ulong TotalSize);

/// <summary>快照當下某個受控執行緒的狀態與呼叫堆疊。</summary>
public sealed record ManagedThreadInfo(
    uint OsThreadId,
    int ManagedThreadId,
    bool IsFinalizer,
    bool IsGcThread,
    bool IsAlive,
    string? PendingException,
    string State,
    string TopFrame,
    IReadOnlyList<string> Frames,
    bool IsApplicationThread
);

/// <summary>
/// 判斷某個框架是否屬於「自己的程式碼」。
/// 設定檔給了命名空間前綴就照它判斷；沒給就以「不是框架命名空間」作為近似值，
/// 這樣換到任何專案都還有可用的標記，只是會把第三方套件也算進來。
/// </summary>
public sealed class AppCodeMatcher
{
    private static readonly string[] FrameworkPrefixes =
    {
        "System.",
        "Microsoft.",
        "mscorlib",
        "Internal.",
        "Interop",
        "netstandard",
        "Windows.",
        "Newtonsoft.",
        "[",
    };

    private readonly string[] prefixes;

    public AppCodeMatcher(IReadOnlyList<string> appNamespaces) =>
        prefixes = appNamespaces.ToArray();

    /// <summary>是否採用使用者設定的前綴（UI 用來說明標記依據）。</summary>
    public bool HasExplicitPrefixes => prefixes.Length > 0;

    public bool IsAppFrame(string frame)
    {
        if (prefixes.Length > 0)
        {
            return prefixes.Any(prefix =>
                frame.StartsWith(prefix, StringComparison.Ordinal)
                || frame.Contains('.' + prefix, StringComparison.Ordinal)
            );
        }

        return !FrameworkPrefixes.Any(prefix => frame.StartsWith(prefix, StringComparison.Ordinal));
    }
}

/// <summary>一次記憶體快照的完整結果。取完即與目標行程脫離，不持有 PSS 複本。</summary>
public sealed record DiagSnapshot(
    int Index,
    DateTime TakenAt,
    TimeSpan Duration,
    string ClrVersion,
    long ObjectCount,
    ulong TotalSize,
    int SegmentCount,
    IReadOnlyList<HeapTypeStat> Types,
    IReadOnlyList<ManagedThreadInfo> Threads,
    // WalkWarning：堆疊走訪中途失敗時的說明；非 null 代表這份統計不完整
    string? WalkWarning = null
)
{
    public double TotalSizeMb => TotalSize / 1024.0 / 1024.0;

    public string Label => $"#{Index} {TakenAt:HH:mm:ss}";
}

/// <summary>兩次快照之間單一型別的差異。</summary>
public sealed record HeapTypeDelta(
    string TypeName,
    long Count,
    long CountDelta,
    ulong TotalSize,
    long SizeDelta
);

/// <summary>從 GC 根到目標物件的一條參考鏈。</summary>
public sealed record RootPath(string RootDescription, IReadOnlyList<string> Chain);
