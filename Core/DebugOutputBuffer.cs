namespace ClrDiag.Core;

/// <summary>
/// 應用程式 OutputDebugString 訊息的執行緒安全環形緩衝區，容量固定 5000（不開放設定鍵）。
/// 攔截層全收，不論 PID；PID 與文字過濾都只發生在顯示層，所以切換「只看附加 PID / 全部行程」
/// 不會遺失切換前的歷史。與 LogBuffer 同形狀，但容量與用途都不同，故獨立成一個緩衝區，
/// 不與 LogBuffer 共用（避免把兩種完全不同性質的訊息混在一起，也不放大 LogBuffer 的 2000 筆容量）。
/// </summary>
public sealed class DebugOutputBuffer
{
    private const int Capacity = 5000;
    private readonly object gate = new();
    private readonly RingBuffer<DebugOutputLine> lines = new(Capacity);

    private long droppedCount;

    /// <summary>因環形緩衝區覆寫最舊項目而遺失的筆數，顯示在檢視標頭提醒使用者。</summary>
    public long DroppedCount
    {
        get
        {
            lock (gate)
            {
                return droppedCount;
            }
        }
    }

    public void Add(DebugOutputLine line)
    {
        lock (gate)
        {
            if (lines.Count == lines.Capacity)
            {
                droppedCount++;
            }

            lines.Add(line);
        }
    }

    public DebugOutputLine[] TakeLast(int count)
    {
        lock (gate)
        {
            return lines.TakeLast(count);
        }
    }

    /// <summary>
    /// 由最新往最舊走訪，套用 predicate，最多收集 take 筆（依舊→新排列回傳）。
    /// 一般情況（沒有捲動）只需要碰到「可見行數」那麼多筆就能停下，不必走訪整個緩衝區，
    /// 也不會把不會顯示的訊息拿去做昂貴的字串格式化。
    /// </summary>
    public DebugOutputLine[] TakeLastMatching(int take, Func<DebugOutputLine, bool> predicate)
    {
        lock (gate)
        {
            var result = new List<DebugOutputLine>(Math.Min(take, lines.Count));
            for (int i = lines.Count - 1; i >= 0 && result.Count < take; i--)
            {
                DebugOutputLine line = lines[i];
                if (predicate(line))
                {
                    result.Add(line);
                }
            }

            result.Reverse();
            return result.ToArray();
        }
    }

    /// <summary>
    /// 符合 predicate 的總筆數，只給畫面標頭顯示用；不做任何格式化，比逐行組字串便宜很多，
    /// 但仍是整個緩衝區的一次走訪 —— 之所以能接受，是因為真正的熱路徑（格式化可見行）
    /// 已由 <see cref="TakeLastMatching"/> 限制在可見範圍內。
    /// </summary>
    public int CountMatching(Func<DebugOutputLine, bool> predicate)
    {
        lock (gate)
        {
            int count = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                if (predicate(lines[i]))
                {
                    count++;
                }
            }

            return count;
        }
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return lines.Count;
            }
        }
    }
}
