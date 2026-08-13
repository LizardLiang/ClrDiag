using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClrDiag.Core.Dap;

/// <summary>一則 DAP 事件的最小封裝：事件名稱與原始 body，欄位由訂閱端自行解析。</summary>
public sealed record DapEvent(string Name, JsonNode? Body);

/// <summary>DAP 請求收到 success:false 回應時丟出，帶對方回報的訊息（若有）。</summary>
public sealed class DapRequestException : Exception
{
    public DapRequestException(string command, string? message)
        : base($"DAP 請求 {command} 失敗: {message ?? "(無訊息)"}") { }
}

/// <summary>
/// DAP（Debug Adapter Protocol）的 Content-Length 訊框讀寫器，架在任意一組輸入／輸出 Stream 上
///（實務上是 adapter 子行程的 stdout / stdin）。這是全專案唯一需要解析訊框式 JSON 的地方：
/// 既有的子行程輸出攔截（Core/ServerService.cs:179-180）是逐行文字，套用不到這裡。
///
/// 內容一律以位元組長度讀取（Content-Length 是位元組數，不是字元數）：標頭以 ASCII 逐位元組
/// 掃到換行為止，內容則先湊滿指定位元組數再一次解成 UTF-8 字串。若改用 StreamReader 把標頭與
/// 內容混在一起解碼，內容裡出現多位元組字元（例如變數值含中文）時，位元組數就不等於字元數，
/// 訊框邊界會讀錯。
/// </summary>
public sealed class DapTransport : IDisposable
{
    private readonly Stream input;
    private readonly Stream output;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    // 讀取端自己緩衝一段位元組，避免逐一位元組呼叫 Stream.ReadAsync 造成大量系統呼叫
    private readonly byte[] readBuffer = new byte[8192];
    private int bufferStart;
    private int bufferLength;

    public DapTransport(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
    }

    /// <summary>寫出一則訊息。執行緒安全，可從多個呼叫端並行呼叫。</summary>
    public async Task WriteAsync(JsonNode message, CancellationToken token)
    {
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header, token).ConfigureAwait(false);
            await output.WriteAsync(body, token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>讀一則完整訊息；對方關閉輸入串流時回傳 null（呼叫端應視為連線結束）。</summary>
    public async Task<JsonNode?> ReadAsync(CancellationToken token)
    {
        int contentLength = -1;

        while (true)
        {
            string? headerLine = await ReadHeaderLineAsync(token).ConfigureAwait(false);
            if (headerLine is null)
            {
                return null;
            }

            if (headerLine.Length == 0)
            {
                break; // 空白列＝標頭結束，接下來是內容
            }

            int colon = headerLine.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            string name = headerLine[..colon].Trim();
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                contentLength = int.Parse(
                    headerLine[(colon + 1)..].Trim(),
                    CultureInfo.InvariantCulture
                );
            }
        }

        if (contentLength < 0)
        {
            throw new InvalidDataException("DAP 訊框缺少 Content-Length 標頭");
        }

        byte[] body = await ReadExactAsync(contentLength, token).ConfigureAwait(false);
        return JsonNode.Parse(body);
    }

    /// <summary>讀一行 ASCII 標頭（以 \n 結尾，容忍前面的 \r）。串流關閉且無資料可讀時回傳 null。</summary>
    private async Task<string?> ReadHeaderLineAsync(CancellationToken token)
    {
        var line = new List<byte>(64);
        while (true)
        {
            byte? b = await ReadByteAsync(token).ConfigureAwait(false);
            if (b is null)
            {
                return line.Count == 0 ? null : Encoding.ASCII.GetString(line.ToArray());
            }

            if (b == (byte)'\n')
            {
                if (line.Count > 0 && line[^1] == (byte)'\r')
                {
                    line.RemoveAt(line.Count - 1);
                }

                return Encoding.ASCII.GetString(line.ToArray());
            }

            line.Add(b.Value);
        }
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken token)
    {
        var result = new byte[count];
        int total = 0;
        while (total < count)
        {
            int fromBuffer = Math.Min(bufferLength - bufferStart, count - total);
            if (fromBuffer > 0)
            {
                Array.Copy(readBuffer, bufferStart, result, total, fromBuffer);
                bufferStart += fromBuffer;
                total += fromBuffer;
                continue;
            }

            if (!await FillBufferAsync(token).ConfigureAwait(false))
            {
                throw new EndOfStreamException("DAP 串流在訊息讀到一半時關閉");
            }
        }

        return result;
    }

    private async Task<byte?> ReadByteAsync(CancellationToken token)
    {
        if (bufferStart >= bufferLength && !await FillBufferAsync(token).ConfigureAwait(false))
        {
            return null;
        }

        return readBuffer[bufferStart++];
    }

    private async Task<bool> FillBufferAsync(CancellationToken token)
    {
        bufferStart = 0;
        bufferLength = await input.ReadAsync(readBuffer, token).ConfigureAwait(false);
        return bufferLength > 0;
    }

    public void Dispose() => writeLock.Dispose();
}

/// <summary>
/// 在 DapTransport 之上做請求／回應關聯（依 seq）與事件分派。
/// 背景讀取迴圈遵循與 ProcessMonitor.SampleLoopAsync（Core/ProcessMonitor.cs:125-147）
/// 同樣的紀律：單一迴圈跑到串流關閉為止，單一訊息處理失敗不能讓迴圈整個死掉
///（同 Core/ProcessMonitor.cs:135 的 catch 空隔離）。
/// </summary>
public sealed class DapClient : IDisposable
{
    private readonly DapTransport transport;
    private readonly object gate = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonNode>> pending = new();
    private readonly CancellationTokenSource cts = new();
    private int nextSeq = 1;
    private Task? readLoop;

    /// <summary>對方傳來的每一則事件；在背景讀取執行緒上觸發，訂閱端自行負責執行緒安全。</summary>
    public event Action<DapEvent>? EventReceived;

    /// <summary>讀取迴圈結束時觸發一次（對方關閉連線或不可恢復的錯誤）；正常關閉時 error 為 null。</summary>
    public event Action<Exception?>? Disconnected;

    public DapClient(Stream input, Stream output) => transport = new DapTransport(input, output);

    public void Start() => readLoop ??= Task.Run(() => ReadLoopAsync(cts.Token));

    /// <summary>送出一個請求並等待回應；success:false 時丟出 DapRequestException，逾時丟出 TimeoutException。</summary>
    public async Task<JsonNode?> RequestAsync(
        string command,
        object? arguments,
        TimeSpan timeout,
        CancellationToken token
    )
    {
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        int seq;
        lock (gate)
        {
            seq = nextSeq++;
            pending[seq] = tcs;
        }

        var message = new JsonObject
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = command,
        };
        if (arguments is not null)
        {
            message["arguments"] = JsonSerializer.SerializeToNode(arguments);
        }

        try
        {
            await transport.WriteAsync(message, token).ConfigureAwait(false);
        }
        catch
        {
            lock (gate)
            {
                pending.Remove(seq);
            }

            throw;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);
        using CancellationTokenRegistration registration = timeoutCts.Token.Register(
            () => tcs.TrySetCanceled(timeoutCts.Token)
        );

        try
        {
            JsonNode response = await tcs.Task.ConfigureAwait(false);
            bool success = response["success"]?.GetValue<bool>() ?? false;
            if (!success)
            {
                throw new DapRequestException(command, response["message"]?.GetValue<string>());
            }

            return response["body"];
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"DAP 請求 {command} 逾時（{timeout}）");
        }
        finally
        {
            lock (gate)
            {
                pending.Remove(seq);
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                JsonNode? node;
                try
                {
                    node = await transport.ReadAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (node is null)
                {
                    break; // 對方關閉連線
                }

                try
                {
                    Dispatch(node);
                }
                catch (Exception ex)
                {
                    // 單一訊息處理失敗不能拖垮整個讀取迴圈
                    failure = ex;
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            // 迴圈結束：讓所有還在等待的請求收到例外，不留下永遠不完成的 Task
            lock (gate)
            {
                foreach (TaskCompletionSource<JsonNode> waiting in pending.Values)
                {
                    waiting.TrySetException(new IOException("DAP 連線已結束"));
                }

                pending.Clear();
            }

            Disconnected?.Invoke(failure);
        }
    }

    private void Dispatch(JsonNode node)
    {
        string? type = node["type"]?.GetValue<string>();
        if (type == "response")
        {
            int requestSeq = node["request_seq"]!.GetValue<int>();
            TaskCompletionSource<JsonNode>? tcs;
            lock (gate)
            {
                pending.Remove(requestSeq, out tcs);
            }

            tcs?.TrySetResult(node);
        }
        else if (type == "event")
        {
            string name = node["event"]!.GetValue<string>();
            EventReceived?.Invoke(new DapEvent(name, node["body"]));
        }

        // type == "request"（reverse request，例如 runInTerminal）目前不支援，直接忽略：
        // 子行程以重新導向的管道啟動（無主控台），netcoredbg 不需要編輯器代開終端機。
    }

    public void Dispose()
    {
        cts.Cancel();
        transport.Dispose();
        cts.Dispose();
    }
}
