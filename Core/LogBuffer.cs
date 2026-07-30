namespace ClrDiag.Core;

public enum LogKind
{
    Info,
    Output,
    Warning,
    Error,
    Success,
}

public readonly record struct LogLine(DateTime TimeStamp, LogKind Kind, string Source, string Text);

/// <summary>集中收集建置 / 伺服器 / 工具訊息的環形記錄，UI 的 log 檢視直接讀這裡。</summary>
public sealed class LogBuffer
{
    private readonly object gate = new();
    private readonly RingBuffer<LogLine> lines;

    public LogBuffer(int capacity = 2000) => lines = new RingBuffer<LogLine>(capacity);

    public void Add(string source, LogKind kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (gate)
        {
            lines.Add(new LogLine(DateTime.Now, kind, source, text.TrimEnd()));
        }
    }

    public LogLine[] TakeLast(int count)
    {
        lock (gate)
        {
            return lines.TakeLast(count);
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
