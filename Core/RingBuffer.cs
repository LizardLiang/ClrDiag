namespace ClrDiag.Core;

/// <summary>固定容量的環形緩衝區，滿了之後覆寫最舊的項目。索引 0 為最舊、Count-1 為最新。</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] items;
    private int start;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        items = new T[capacity];
    }

    public int Capacity => items.Length;

    public int Count { get; private set; }

    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return items[(start + index) % items.Length];
        }
    }

    public bool TryGetLast(out T value)
    {
        if (Count == 0)
        {
            value = default!;
            return false;
        }

        value = this[Count - 1];
        return true;
    }

    public void Add(T item)
    {
        if (Count < items.Length)
        {
            items[(start + Count) % items.Length] = item;
            Count++;
            return;
        }

        items[start] = item;
        start = (start + 1) % items.Length;
    }

    /// <summary>取出最後 count 筆（不足則全部），依舊→新排列。</summary>
    public T[] TakeLast(int count)
    {
        int take = Math.Min(count, Count);
        var result = new T[take];
        for (int i = 0; i < take; i++)
        {
            result[i] = this[Count - take + i];
        }

        return result;
    }
}
