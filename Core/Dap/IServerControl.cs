namespace ClrDiag.Core.Dap;

/// <summary>伺服器控制指令的名稱，同時也是管道指令的 cmd 值與回覆 result.command 的值。</summary>
public static class ServerCommands
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Restart = "restart";

    public static bool IsServerCommand(string cmd) => cmd is Start or Stop or Restart;
}

/// <summary>伺服器目前所處的階段；值就是 JSON 欄位 server.phase 的字串。</summary>
public static class ServerPhases
{
    public const string Stopped = "stopped";
    public const string Building = "building";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Stopping = "stopping";
    public const string BuildFailed = "buildFailed";
    public const string StartFailed = "startFailed";
}

/// <summary>一次 start／stop／restart 跑完的結果；Errors 在建置失敗時放編譯錯誤摘要。</summary>
public sealed record ServerCommandOutcome(
    string Command,
    bool Ok,
    string Phase,
    string Message,
    int? Pid,
    IReadOnlyList<string> Errors
);

/// <summary>
/// 指令是否被接受。Accepted 為 false 表示已有背景工作在跑而拒絕（沒有任何東西被啟動）；
/// Completion 在接受時代表整段流程的結果，只是取消啟動這類立即生效的動作則為 null。
/// </summary>
public sealed record ServerCommandTicket(
    bool Accepted,
    string Message,
    Task<ServerCommandOutcome>? Completion
);

/// <summary>伺服器目前的狀態快照，供 status 與每則回覆附帶。</summary>
public sealed record ServerControlSnapshot(
    string Phase,
    int? Pid,
    string? Url,
    string? Busy,
    ServerCommandOutcome? Last
);

/// <summary>
/// 伺服器的建置／啟動／停止入口。互動介面的 s／x／r 鍵與管道的 start／stop／restart
/// 呼叫同一組方法，管道端不另寫第二份流程。
/// </summary>
public interface IServerControl
{
    ServerControlSnapshot Snapshot();

    ServerCommandTicket Start();

    ServerCommandTicket Stop();

    ServerCommandTicket Restart();
}
