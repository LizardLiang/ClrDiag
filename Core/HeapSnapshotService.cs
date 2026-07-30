using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

namespace ClrDiag.Core;

/// <summary>
/// 以 ClrMD 對目標行程取得受控堆疊快照。
/// 使用 CreateSnapshotAndAttach（Windows PSS 行程複本）而非除錯器附加：
/// 目標行程不會被暫停成除錯狀態，本工具異常結束也不會把目標行程一起帶走。
/// 快照結果會全部轉成 POCO 後立即釋放複本，避免長期占用記憶體。
/// </summary>
public sealed class HeapSnapshotService
{
    private readonly AppCodeMatcher appCode;
    private int snapshotCounter;

    /// <param name="appNamespaces">視為自己程式碼的命名空間前綴；空的話用「非框架」近似判斷。</param>
    public HeapSnapshotService(IReadOnlyList<string>? appNamespaces = null) =>
        appCode = new AppCodeMatcher(appNamespaces ?? Array.Empty<string>());

    /// <summary>執行緒標記是依使用者設定的命名空間，還是「非框架」近似判斷。</summary>
    public bool HasExplicitAppNamespaces => appCode.HasExplicitPrefixes;

    /// <summary>快照是否正在進行中（UI 用來顯示忙碌狀態並避免重入）。</summary>
    public bool IsBusy { get; private set; }

    /// <summary>取得一次完整快照：型別直方圖 + 受控執行緒堆疊。</summary>
    public DiagSnapshot Capture(
        int processId,
        bool includeTypes,
        bool includeThreads,
        CancellationToken token
    )
    {
        IsBusy = true;
        try
        {
            var sw = Stopwatch.StartNew();
            using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(processId);
            ClrInfo clrInfo =
                dataTarget.ClrVersions.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "目標行程沒有載入 CLR，無法取得受控堆疊快照"
                );

            using ClrRuntime runtime = clrInfo.CreateRuntime();

            long objectCount = 0;
            ulong totalSize = 0;

            // 以 MethodTable 位址（而非型別名稱字串）當索引鍵：Type.Name 每次都要讀 metadata
            // 並組出字串，在數百萬物件的堆疊上是主要瓶頸，名稱留到最後再解析一次即可。
            var histogram = new Dictionary<ulong, (long Count, ulong Size, ClrType Type)>();
            long unknownCount = 0;
            string? walkWarning = null;

            if (includeTypes)
            {
                // carefully: true —— 遇到無法走訪的記憶體區塊時仍繼續走完該 segment。
                // 預設 false 會在第一個壞掉的物件就停下，該 segment 後面的物件全部漏掉，直方圖會缺一大塊。
                using IEnumerator<ClrObject> walker = runtime
                    .Heap.EnumerateObjects(carefully: true)
                    .GetEnumerator();

                int sinceCancelCheck = 0;
                while (true)
                {
                    if (++sinceCancelCheck >= 4096)
                    {
                        sinceCancelCheck = 0;
                        token.ThrowIfCancellationRequested();
                    }

                    ClrObject obj;
                    try
                    {
                        if (!walker.MoveNext())
                        {
                            break;
                        }

                        obj = walker.Current;
                    }
                    catch (Exception ex)
                    {
                        // 走訪本身失步（不是單一物件的問題）：保留已統計的結果，並讓上層知道這份快照不完整。
                        walkWarning = $"堆疊走訪中斷，統計不完整：{ex.Message}";
                        break;
                    }

                    objectCount++;

                    // 必須先判型別再碰 Size：這是對「執行中」行程做 PSS 複本，
                    // 堆疊上必然有剛配置或正被 GC 搬移、method table 尚未成形的物件，
                    // 而 ClrObject.Size 在 Type 為 null 時會直接丟
                    // 「Object {addr} is corrupted, could not determine type.」。
                    if (obj.Type is not { } type)
                    {
                        // 沒有型別就無從得知大小，只能計數，不計入總大小。
                        unknownCount++;
                        continue;
                    }

                    ulong size = obj.Size;
                    totalSize += size;

                    histogram.TryGetValue(
                        type.MethodTable,
                        out (long Count, ulong Size, ClrType Type) entry
                    );
                    histogram[type.MethodTable] = (entry.Count + 1, entry.Size + size, type);
                }
            }

            List<HeapTypeStat> types = histogram
                .Select(kv => new HeapTypeStat(
                    kv.Value.Type.Name ?? "<unnamed>",
                    kv.Value.Count,
                    kv.Value.Size
                ))
                .ToList();

            if (unknownCount > 0)
            {
                types.Add(new HeapTypeStat("<unknown>", unknownCount, 0));
            }

            types.Sort((left, right) => right.TotalSize.CompareTo(left.TotalSize));

            List<ManagedThreadInfo> threads = includeThreads
                ? CollectThreads(runtime, token)
                : new List<ManagedThreadInfo>();

            return new DiagSnapshot(
                Index: ++snapshotCounter,
                TakenAt: DateTime.Now,
                Duration: sw.Elapsed,
                ClrVersion: $"{clrInfo.Flavor} {clrInfo.Version}",
                ObjectCount: objectCount,
                TotalSize: totalSize,
                SegmentCount: runtime.Heap.Segments.Length,
                Types: types,
                Threads: threads,
                WalkWarning: walkWarning
            );
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>比較兩次快照的型別差異；baseline 為 null 時只回報目前值。</summary>
    public static List<HeapTypeDelta> Diff(DiagSnapshot current, DiagSnapshot? baseline)
    {
        Dictionary<string, HeapTypeStat>? baselineMap = baseline?.Types.ToDictionary(
            t => t.TypeName,
            StringComparer.Ordinal
        );

        var result = new List<HeapTypeDelta>(current.Types.Count);
        foreach (HeapTypeStat type in current.Types)
        {
            long countDelta = 0;
            long sizeDelta = 0;
            if (
                baselineMap is not null
                && baselineMap.TryGetValue(type.TypeName, out HeapTypeStat? old)
            )
            {
                countDelta = type.Count - old.Count;
                sizeDelta = (long)type.TotalSize - (long)old.TotalSize;
            }
            else if (baselineMap is not null)
            {
                countDelta = type.Count;
                sizeDelta = (long)type.TotalSize;
            }

            result.Add(
                new HeapTypeDelta(type.TypeName, type.Count, countDelta, type.TotalSize, sizeDelta)
            );
        }

        // 只在基準快照裡出現、現在已消失的型別也要列出（負成長）
        if (baselineMap is not null)
        {
            var currentNames = current
                .Types.Select(t => t.TypeName)
                .ToHashSet(StringComparer.Ordinal);
            foreach (HeapTypeStat old in baselineMap.Values)
            {
                if (!currentNames.Contains(old.TypeName))
                {
                    result.Add(
                        new HeapTypeDelta(old.TypeName, 0, -old.Count, 0, -(long)old.TotalSize)
                    );
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 找出指定型別的物件被誰握住：從 GC 根做廣度優先搜尋，回報前幾條參考鏈。
    /// 這是「記憶體沒被釋放」時最關鍵的資訊，等同 Visual Studio 的 Paths to Root。
    /// 有時間與造訪數上限，避免在大型堆疊上卡住。
    /// </summary>
    public List<RootPath> FindRootPaths(
        int processId,
        string typeName,
        int maxPaths,
        TimeSpan budget,
        CancellationToken token
    )
    {
        IsBusy = true;
        try
        {
            var sw = Stopwatch.StartNew();
            using DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(processId);
            ClrInfo clrInfo =
                dataTarget.ClrVersions.FirstOrDefault()
                ?? throw new InvalidOperationException("目標行程沒有載入 CLR");
            using ClrRuntime runtime = clrInfo.CreateRuntime();

            var parents = new Dictionary<ulong, ulong>();
            var rootLabels = new Dictionary<ulong, string>();
            var typeNames = new Dictionary<ulong, string>();
            var queue = new Queue<ulong>();
            var visited = new HashSet<ulong>();

            foreach (ClrRoot root in runtime.Heap.EnumerateRoots())
            {
                token.ThrowIfCancellationRequested();
                ClrObject rootObject = root.Object;
                if (!rootObject.IsValid || !visited.Add(rootObject.Address))
                {
                    continue;
                }

                rootLabels[rootObject.Address] =
                    $"{root.RootKind} → {rootObject.Type?.Name ?? "?"}";
                typeNames[rootObject.Address] = rootObject.Type?.Name ?? "?";
                queue.Enqueue(rootObject.Address);
            }

            var found = new List<RootPath>();
            long examined = 0;
            bool budgetExhausted = false;

            while (queue.Count > 0 && found.Count < maxPaths)
            {
                token.ThrowIfCancellationRequested();
                if (sw.Elapsed > budget)
                {
                    budgetExhausted = true;
                    break;
                }

                ulong address = queue.Dequeue();
                examined++;

                var current = runtime.Heap.GetObject(address);
                if (!current.IsValid || current.Type is null)
                {
                    continue;
                }

                typeNames[address] = current.Type.Name ?? "?";

                if (string.Equals(current.Type.Name, typeName, StringComparison.Ordinal))
                {
                    found.Add(BuildPath(address, parents, rootLabels, typeNames));
                    continue; // 不再往該物件下層展開
                }

                foreach (
                    ClrObject reference in current.EnumerateReferences(
                        carefully: true,
                        considerDependantHandles: true
                    )
                )
                {
                    if (!reference.IsValid || !visited.Add(reference.Address))
                    {
                        continue;
                    }

                    parents[reference.Address] = address;
                    queue.Enqueue(reference.Address);
                }
            }

            if (found.Count == 0)
            {
                // 走完整個可達圖仍找不到，代表該型別的物件目前沒有任何 GC 根，
                // 也就是「等待回收的垃圾」而不是洩漏；這與「時間不夠沒搜完」是完全不同的結論，必須分開講。
                string message = budgetExhausted
                    ? $"已達 {budget.TotalSeconds:N0} 秒上限，走訪 {examined:N0} 個物件仍未找到 {typeName} 的參考鏈（結果不完整）"
                    : $"已走完全部 {examined:N0} 個可達物件，{typeName} 沒有任何 GC 根 → 屬於等待回收的垃圾，不是洩漏";

                found.Add(new RootPath(message, Array.Empty<string>()));
            }

            return found;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static RootPath BuildPath(
        ulong target,
        Dictionary<ulong, ulong> parents,
        Dictionary<ulong, string> rootLabels,
        Dictionary<ulong, string> typeNames
    )
    {
        var chain = new List<string>();
        ulong cursor = target;
        var guard = new HashSet<ulong>();

        while (guard.Add(cursor))
        {
            string type = typeNames.TryGetValue(cursor, out string? name) ? name : "?";
            chain.Add($"0x{cursor:x} {type}");

            if (!parents.TryGetValue(cursor, out ulong parent))
            {
                break;
            }

            cursor = parent;
        }

        chain.Reverse();
        string rootLabel = rootLabels.TryGetValue(cursor, out string? label)
            ? label
            : "unknown root";
        return new RootPath(rootLabel, chain);
    }

    private List<ManagedThreadInfo> CollectThreads(ClrRuntime runtime, CancellationToken token)
    {
        var result = new List<ManagedThreadInfo>();

        foreach (ClrThread thread in runtime.Threads)
        {
            token.ThrowIfCancellationRequested();

            var frames = new List<string>();
            try
            {
                foreach (ClrStackFrame frame in thread.EnumerateStackTrace())
                {
                    frames.Add(DescribeFrame(frame));
                    if (frames.Count >= 60)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // 個別執行緒的堆疊可能無法讀取，保留已取得的框架
            }

            string topFrame =
                frames.FirstOrDefault(f => !f.StartsWith("[", StringComparison.Ordinal))
                ?? "(no managed frames)";

            result.Add(
                new ManagedThreadInfo(
                    OsThreadId: thread.OSThreadId,
                    ManagedThreadId: thread.ManagedThreadId,
                    IsFinalizer: thread.IsFinalizer,
                    IsGcThread: thread.IsGc,
                    IsAlive: thread.IsAlive,
                    PendingException: thread.CurrentException is { } ex
                        ? $"{ex.Type?.Name}: {ex.Message}"
                        : null,
                    State: ClassifyState(frames),
                    TopFrame: topFrame,
                    Frames: frames,
                    IsApplicationThread: frames.Any(appCode.IsAppFrame)
                )
            );
        }

        // 排序原則：有例外的最優先，其次是含自己程式碼的執行緒，
        // 再來才是有受控框架的；已結束的執行緒（OSThreadId 0、無堆疊）一律排到最後。
        return result
            .OrderByDescending(t => t.PendingException is not null)
            .ThenByDescending(t => t.IsApplicationThread)
            .ThenByDescending(t => t.State != "no-stack")
            .ThenByDescending(t => t.State != "native")
            .ThenByDescending(t => t.IsAlive)
            .ThenBy(t => t.OsThreadId)
            .ToList();
    }

    private static string DescribeFrame(ClrStackFrame frame)
    {
        if (frame.Method is { } method)
        {
            string type = method.Type?.Name ?? "?";
            return $"{type}.{method.Name}";
        }

        return string.IsNullOrEmpty(frame.FrameName) ? "[native]" : $"[{frame.FrameName}]";
    }

    /// <summary>
    /// 依堆疊最上層的框架推測執行緒狀態。
    /// ClrMD 4 不再提供 BlockingObjects，因此改以等待型 API 的特徵字串判斷。
    /// </summary>
    private static string ClassifyState(IReadOnlyList<string> frames)
    {
        if (frames.Count == 0)
        {
            return "no-stack";
        }

        // 只有 [native] / [GCFrame] 這類方括號框架，代表當下沒有受控程式碼在執行
        if (frames.All(f => f.StartsWith("[", StringComparison.Ordinal)))
        {
            return "native";
        }

        string top = string.Join(" | ", frames.Take(4));

        if (
            top.Contains("Monitor.Wait", StringComparison.Ordinal)
            || top.Contains("Monitor.Enter", StringComparison.Ordinal)
            || top.Contains("Monitor.ReliableEnter", StringComparison.Ordinal)
        )
        {
            return "lock-wait";
        }

        if (
            top.Contains("WaitHandle.Wait", StringComparison.Ordinal)
            || top.Contains("ManualResetEvent", StringComparison.Ordinal)
            || top.Contains("SemaphoreSlim", StringComparison.Ordinal)
            || top.Contains("CountdownEvent", StringComparison.Ordinal)
        )
        {
            return "event-wait";
        }

        if (
            top.Contains("Task.Wait", StringComparison.Ordinal)
            || top.Contains("GetAwaiter", StringComparison.Ordinal)
            || top.Contains("TaskAwaiter", StringComparison.Ordinal)
        )
        {
            return "task-wait";
        }

        if (top.Contains("Thread.Sleep", StringComparison.Ordinal))
        {
            return "sleep";
        }

        if (
            top.Contains("SqlClient", StringComparison.Ordinal)
            || top.Contains("OracleClient", StringComparison.Ordinal)
            || top.Contains("NHibernate", StringComparison.Ordinal)
        )
        {
            return "db";
        }

        if (
            top.Contains("HttpClient", StringComparison.Ordinal)
            || top.Contains("HttpWebRequest", StringComparison.Ordinal)
            || top.Contains("Socket", StringComparison.Ordinal)
        )
        {
            return "network";
        }

        return "running";
    }
}
