using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace ClrDiag.Core;

/// <summary>應用程式透過 OutputDebugString 寫出的一行訊息（沒有偵錯器時原本會直接消失）。</summary>
public readonly record struct DebugOutputLine(DateTime TimeStamp, int Pid, string Text);

/// <summary>
/// DBWIN 監聽器：攔截 OutputDebugString（Debug.WriteLine / Trace.WriteLine 走的路徑）。
/// 對「本機工作階段」與「Global\」兩組命名各建立一組監聽（DBWIN_BUFFER / DBWIN_BUFFER_READY /
/// DBWIN_DATA_READY），任一組成功即可用；兩組都失敗才視為無法攔截。
/// 這三個具名物件照慣例由「監聽端」建立、由「產生端」（OutputDebugString 呼叫者）開啟寫入；
/// 若建立時發現已存在，代表已有其他監聽者（例如 DebugView 或另一個 clrdiag 實例）在跑，
/// 同一時間只能有一個監聽者，這是 Windows 這組具名物件的既有限制，非本工具能改。
/// </summary>
public sealed class DebugOutputListener : IDisposable
{
    private const int BufferSize = 4096;
    private const int MaxMessageBytes = BufferSize - 4;

    private readonly List<Session> sessions = new();
    private bool disposed;

    public event Action<DebugOutputLine>? LineReceived;

    /// <summary>無法攔截的原因；null 表示至少有一組（本機或 Global\）監聽成功。</summary>
    public string? Unavailable { get; private set; }

    public void Start()
    {
        Encoding encoding = ResolveAnsiEncoding();
        var failureReasons = new List<string>();

        TryStartSession(string.Empty, "本機工作階段", encoding, failureReasons);
        TryStartSession(@"Global\", "Global", encoding, failureReasons);

        Unavailable = sessions.Count > 0 ? null : string.Join("；", failureReasons);
    }

    /// <summary>
    /// 取得系統 ANSI 字碼頁對應的編碼；DBWIN 傳的是 ANSI 位元組而非 UTF-8，
    /// 硬解 UTF-8 會讓繁體中文訊息全部變亂碼。取不到就退回 UTF-8。
    /// </summary>
    private static Encoding ResolveAnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(NativeMethods.GetACP());
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private void TryStartSession(
        string prefix,
        string label,
        Encoding encoding,
        List<string> failureReasons
    )
    {
        var session = new Session(prefix, encoding);
        if (session.TryOpen(out string? reason))
        {
            session.LineReceived += OnLineReceived;
            session.Start();
            sessions.Add(session);
        }
        else
        {
            failureReasons.Add($"{label}: {reason}");
            session.Dispose();
        }
    }

    private void OnLineReceived(DebugOutputLine line) => LineReceived?.Invoke(line);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (Session session in sessions)
        {
            session.LineReceived -= OnLineReceived;
            session.Dispose();
        }

        sessions.Clear();
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern int GetACP();
    }

    /// <summary>一組 DBWIN 具名物件（本機或 Global\）與讀取這組物件的背景執行緒。</summary>
    private sealed class Session : IDisposable
    {
        private readonly string prefix;
        private readonly Encoding encoding;
        private readonly ManualResetEvent stopSignal = new(false);

        private MemoryMappedFile? mapping;
        private MemoryMappedViewAccessor? accessor;
        private EventWaitHandle? bufferReady;
        private EventWaitHandle? dataReady;
        private Thread? thread;

        public Session(string prefix, Encoding encoding)
        {
            this.prefix = prefix;
            this.encoding = encoding;
        }

        public event Action<DebugOutputLine>? LineReceived;

        /// <summary>建立這一組的三個具名物件；任何一個「已存在」都視為被其他監聽者占用。</summary>
        public bool TryOpen(out string? reason)
        {
            mapping = OpenOrCreateMapping(prefix + "DBWIN_BUFFER", out reason);
            if (mapping is null)
            {
                return false;
            }

            try
            {
                accessor = mapping.CreateViewAccessor(0, BufferSize, MemoryMappedFileAccess.Read);
            }
            catch (Exception ex)
            {
                reason = $"無法對應共用記憶體: {ex.Message}";
                return false;
            }

            bufferReady = OpenOrCreateEvent(prefix + "DBWIN_BUFFER_READY", out reason);
            if (bufferReady is null)
            {
                return false;
            }

            dataReady = OpenOrCreateEvent(prefix + "DBWIN_DATA_READY", out reason);
            if (dataReady is null)
            {
                return false;
            }

            reason = null;
            return true;
        }

        public void Start()
        {
            thread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = $"DBWIN-{(prefix.Length == 0 ? "local" : "global")}",
            };
            thread.Start();

            // 告知潛在的產生端（OutputDebugString 呼叫者）緩衝區已可使用
            bufferReady!.Set();
        }

        private void ReadLoop()
        {
            var buffer = new byte[BufferSize];
            var handles = new WaitHandle[] { dataReady!, stopSignal };

            while (true)
            {
                int signaled;
                try
                {
                    signaled = WaitHandle.WaitAny(handles);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (signaled != 0)
                {
                    return;
                }

                try
                {
                    accessor!.ReadArray(0, buffer, 0, BufferSize);

                    int pid = BitConverter.ToInt32(buffer, 0);

                    // 訊息從第 4 byte 開始，最多 4092 bytes；找不到結尾 NUL 就以緩衝區尾端為界
                    int nul = Array.IndexOf(buffer, (byte)0, 4, MaxMessageBytes);
                    int length = (nul < 0 ? BufferSize : nul) - 4;

                    string text = encoding
                        .GetString(buffer, 4, Math.Max(0, length))
                        .TrimEnd('\r', '\n');

                    LineReceived?.Invoke(new DebugOutputLine(DateTime.Now, pid, text));
                }
                catch (ObjectDisposedException)
                {
                    // Dispose() 正在關閉這一組資源（accessor 可能剛好在這個當下被釋放），
                    // 結束讀取迴圈而不是讓背景執行緒帶著未處理的例外死掉
                    return;
                }
                finally
                {
                    try
                    {
                        // 讓下一個產生端可以寫入；關閉期間 bufferReady 也可能已被釋放
                        bufferReady!.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                        // 同上，Dispose() 已經在收尾，這裡不需要再處理
                    }
                }
            }
        }

        public void Dispose()
        {
            stopSignal.Set();
            thread?.Join(1000);

            accessor?.Dispose();
            mapping?.Dispose();
            bufferReady?.Dispose();
            dataReady?.Dispose();
            stopSignal.Dispose();
        }

        private static MemoryMappedFile? OpenOrCreateMapping(string name, out string? reason)
        {
            try
            {
                MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read).Dispose();
                reason =
                    $"共用記憶體 {name} 已存在（可能已有其他監聽者，例如 DebugView 或另一個 clrdiag）";
                return null;
            }
            catch (FileNotFoundException)
            {
                // 尚未有人建立，往下由我們建立
            }
            catch (UnauthorizedAccessException ex)
            {
                reason = $"開啟共用記憶體 {name} 失敗: {ex.Message}";
                return null;
            }

            try
            {
                MemoryMappedFile created = MemoryMappedFile.CreateNew(
                    name,
                    BufferSize,
                    MemoryMappedFileAccess.ReadWrite
                );
                reason = null;
                return created;
            }
            catch (Exception ex)
            {
                reason = $"建立共用記憶體 {name} 失敗: {ex.Message}";
                return null;
            }
        }

        private static EventWaitHandle? OpenOrCreateEvent(string name, out string? reason)
        {
            try
            {
                EventWaitHandle.OpenExisting(name).Dispose();
                reason =
                    $"事件 {name} 已存在（可能已有其他監聽者，例如 DebugView 或另一個 clrdiag）";
                return null;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 尚未有人建立，往下由我們建立
            }
            catch (UnauthorizedAccessException ex)
            {
                reason = $"開啟事件 {name} 失敗: {ex.Message}";
                return null;
            }

            try
            {
                var handle = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    name,
                    out bool createdNew
                );
                if (!createdNew)
                {
                    // 極短時間內被別人搶先建立（競態），視同占用
                    handle.Dispose();
                    reason = $"事件 {name} 建立時已被其他監聽者搶先建立";
                    return null;
                }

                reason = null;
                return handle;
            }
            catch (Exception ex)
            {
                reason = $"建立事件 {name} 失敗: {ex.Message}";
                return null;
            }
        }
    }
}
