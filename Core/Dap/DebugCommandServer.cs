using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ClrDiag.Core.Dap;

/// <summary>
/// 專案範圍的具名管道指令通道：\\.\pipe\clrdiag-&lt;專案根目錄雜湊&gt;。
/// Neovim（或任何送 --send 的客戶端）以換行分隔的 JSON 送指令過來，每個指令都會收到一則
/// 目前的階段狀態回覆；階段狀態改變（中斷／恢復／結束）時也會不待請求主動推播給所有已連線
/// 的客戶端，客戶端不需要自己輪詢。
///
/// 一個 NamedPipeServerStream 只服務一個客戶端，因此用「接受迴圈」模式：每次都開一個新的
/// server stream 等待連線，連上後把它丟給獨立的處理工作，迴圈立刻回頭開下一個等待連線
///（同時支援多個客戶端，例如 Neovim 常駐連線＋一次性的 --send 呼叫並存）。
///
/// 命名管道本身沒有網路曝露面——這是選它而非 TCP loopback 的理由，見規劃書的
/// 「攻擊者」分析：netcoredbg 自己的 --server 模式會綁 0.0.0.0，這個設計完全不用它。
/// </summary>
public sealed class DebugCommandServer : IDisposable
{
    /// <summary>
    /// 一個已連線客戶端的寫入端＋專屬互斥鎖。HandleClientAsync 的逐指令回覆與 Broadcast()
    /// 的主動推播都可能對同一個 writer 送 WriteLineAsync——StreamWriter 不是執行緒安全的，
    /// continue/stepOver 這類指令幾乎立刻會觸發 continued 事件→StateChanged→Broadcast()，
    /// 這時回覆可能還沒寫完，交錯的 bytes 會打斷 nvim 那端靠換行分隔 JSON 的框架解析。
    /// 兩條寫入路徑共用同一把鎖，確保同一個 writer 任何時候只有一次 WriteLineAsync 在跑。
    /// </summary>
    private sealed class ClientConnection(StreamWriter writer)
    {
        public StreamWriter Writer { get; } = writer;
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    private readonly DapSessionService session;
    private readonly LogBuffer log;
    private readonly CancellationTokenSource cts = new();
    private readonly object clientsGate = new();
    private readonly List<ClientConnection> clients = new();
    private readonly Action<int> onProcessStarted;

    private Task? acceptLoop;

    public DebugCommandServer(DapSessionService session, LogBuffer log, string pipeName)
    {
        this.session = session;
        this.log = log;
        PipeName = pipeName;
        onProcessStarted = _ => Broadcast();
    }

    public string PipeName { get; }

    /// <summary>依專案根目錄推導穩定的管道名稱；同一個專案每次啟動都會得到同一個名字。</summary>
    public static string PipeNameFor(string projectRoot)
    {
        string normalized = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar)
            .ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "clrdiag-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    public void Start()
    {
        acceptLoop ??= Task.Run(() => AcceptLoopAsync(cts.Token));
        session.StateChanged += Broadcast;
        session.ProcessStarted += onProcessStarted;
        log.Add("dap", LogKind.Info, $"除錯指令管道: \\\\.\\pipe\\{PipeName}");
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous
                );
            }
            catch (Exception ex)
            {
                // 開不出新的 server stream 實例（罕見，多半是資源耗盡），稍等再試，不讓整個接受迴圈死掉
                log.Add("dap", LogKind.Error, $"具名管道建立失敗: {ex.Message}");
                await Task.Delay(1000, token).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (Exception ex)
            {
                log.Add("dap", LogKind.Warning, $"具名管道連線失敗: {ex.Message}");
                pipe.Dispose();
                continue;
            }

            // 交給獨立工作處理，接受迴圈立刻回頭開下一個實例等待下一個客戶端
            _ = Task.Run(() => HandleClientAsync(pipe, token), token);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        var connection = new ClientConnection(writer);

        lock (clientsGate)
        {
            clients.Add(connection);
        }

        try
        {
            // 一連上就送一次目前狀態，客戶端不必等下一個事件才知道現況
            await WriteStateAsync(connection, BuildStateMessage(), token).ConfigureAwait(false);

            while (!token.IsCancellationRequested && pipe.IsConnected)
            {
                string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                {
                    break; // 客戶端關閉連線
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonObject reply = await HandleCommandAsync(line, token).ConfigureAwait(false);
                await WriteStateAsync(connection, reply, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 階段結束，略過
        }
        catch (Exception ex)
        {
            log.Add("dap", LogKind.Warning, $"具名管道客戶端處理失敗: {ex.Message}");
        }
        finally
        {
            lock (clientsGate)
            {
                clients.Remove(connection);
            }

            // WriteLock 刻意不在這裡 Dispose：Broadcast() 可能正好對同一個 connection
            // 進行中的推播還持有這把鎖，跟原本 writer 也不在這裡明確 Dispose 一致，
            // 交給 GC 回收；連線關閉後續寫入本來就會因 pipe.Dispose() 而失敗並被
            // Broadcast 的 ContinueWith 容錯移除。
            try
            {
                pipe.Dispose();
            }
            catch
            {
                // 略過
            }
        }
    }

    /// <summary>階段狀態改變時主動推播給所有已連線的客戶端；單一客戶端寫入失敗不影響其他人。</summary>
    private void Broadcast()
    {
        JsonObject message = BuildStateMessage();
        List<ClientConnection> targets;
        lock (clientsGate)
        {
            if (clients.Count == 0)
            {
                return;
            }

            targets = new List<ClientConnection>(clients);
        }

        foreach (ClientConnection connection in targets)
        {
            _ = WriteStateAsync(connection, message, cts.Token)
                .ContinueWith(
                    t =>
                    {
                        if (t.IsFaulted)
                        {
                            lock (clientsGate)
                            {
                                clients.Remove(connection);
                            }
                        }
                    },
                    TaskScheduler.Default
                );
        }
    }

    /// <summary>
    /// 逐指令回覆（HandleClientAsync）與主動推播（Broadcast）都可能同時對同一個 writer 呼叫
    /// 這個方法；用該連線專屬的 WriteLock 序列化，避免兩段 JSON 交錯寫進同一個 stream。
    /// </summary>
    private static async Task WriteStateAsync(
        ClientConnection connection,
        JsonObject message,
        CancellationToken token
    )
    {
        await connection.WriteLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await connection
                .Writer.WriteLineAsync(message.ToJsonString().AsMemory(), token)
                .ConfigureAwait(false);
        }
        finally
        {
            connection.WriteLock.Release();
        }
    }

    private async Task<JsonObject> HandleCommandAsync(string line, CancellationToken token)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (Exception ex)
        {
            return ErrorState($"無法解析指令: {ex.Message}");
        }

        string? cmd = (string?)node?["cmd"];
        if (string.IsNullOrEmpty(cmd))
        {
            return ErrorState("指令缺少 cmd 欄位");
        }

        try
        {
            switch (cmd)
            {
                case "setBreakpoint":
                    await session
                        .SetBreakpointAsync(RequireString(node!, "path"), RequireInt(node!, "line"), token)
                        .ConfigureAwait(false);
                    break;

                case "clearBreakpoint":
                    await session
                        .ClearBreakpointAsync(RequireString(node!, "path"), RequireInt(node!, "line"), token)
                        .ConfigureAwait(false);
                    break;

                case "addWatch":
                    session.AddWatch(RequireString(node!, "expression"));
                    break;

                case "removeWatch":
                    session.RemoveWatch(RequireString(node!, "expression"));
                    break;

                case "continue":
                    await session.ContinueAsync(token).ConfigureAwait(false);
                    break;

                case "stepOver":
                    await session.StepOverAsync(token).ConfigureAwait(false);
                    break;

                case "stepIn":
                    await session.StepInAsync(token).ConfigureAwait(false);
                    break;

                case "stepOut":
                    await session.StepOutAsync(token).ConfigureAwait(false);
                    break;

                case "pause":
                    await session.PauseAsync(token).ConfigureAwait(false);
                    break;

                default:
                    return ErrorState($"未知指令: {cmd}");
            }
        }
        catch (Exception ex)
        {
            return ErrorState($"指令 {cmd} 執行失敗: {ex.Message}");
        }

        return BuildStateMessage();
    }

    private static string RequireString(JsonNode node, string field) =>
        (string?)node[field] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"缺少欄位 {field}");

    private static int RequireInt(JsonNode node, string field) =>
        (int?)node[field] ?? throw new InvalidOperationException($"缺少欄位 {field}");

    private JsonObject ErrorState(string message)
    {
        JsonObject state = BuildStateMessage();
        state["error"] = message;
        return state;
    }

    /// <summary>目前的階段狀態物件：運行/中斷、停止原因、位置、執行緒 id，加上中斷點與監看清單。</summary>
    public JsonObject BuildStateMessage()
    {
        DebugHaltedState? halted = session.Halted;
        var msg = new JsonObject
        {
            ["type"] = "state",
            ["sessionState"] = session.State.ToString(),
            ["pid"] = session.DebuggeePid,
            ["launchMode"] = session.IsLaunchMode,
        };

        if (halted is not null)
        {
            msg["threadId"] = halted.ThreadId;
            msg["stopReason"] = halted.Reason;

            DebugFrame? top =
                halted.Frames.Count > 0 ? halted.Frames[halted.SelectedFrameIndex] : null;
            if (top is not null)
            {
                msg["location"] = new JsonObject { ["path"] = top.SourcePath, ["line"] = top.Line };
            }

            msg["watchResults"] = new JsonArray(
                halted
                    .Watches.Select(w =>
                        (JsonNode)new JsonObject
                        {
                            ["expression"] = w.Expression,
                            ["value"] = w.Value,
                            ["timedOut"] = w.TimedOut,
                            ["error"] = w.Error,
                        }
                    )
                    .ToArray()
            );
        }

        msg["breakpoints"] = new JsonArray(
            session
                .Breakpoints.Select(b =>
                    (JsonNode)new JsonObject
                    {
                        ["path"] = b.Path,
                        ["line"] = b.Line,
                        ["verified"] = b.Verified,
                        ["message"] = b.Message,
                    }
                )
                .ToArray()
        );

        msg["watches"] = new JsonArray(
            session.Watches.Select(w => (JsonNode)JsonValue.Create(w.Expression)).ToArray()
        );

        return msg;
    }

    public void Dispose()
    {
        session.StateChanged -= Broadcast;
        session.ProcessStarted -= onProcessStarted;
        cts.Cancel();

        List<ClientConnection> targets;
        lock (clientsGate)
        {
            targets = new List<ClientConnection>(clients);
            clients.Clear();
        }

        foreach (ClientConnection connection in targets)
        {
            try
            {
                connection.Writer.Dispose();
            }
            catch
            {
                // 略過
            }
        }

        cts.Dispose();
    }
}
