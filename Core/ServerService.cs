using System.Diagnostics;
using System.Net.Security;
using System.Text.RegularExpressions;

namespace ClrDiag.Core;

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    External,
    Stopping,
    // 由 DapSessionService 啟動或附加的除錯目標；與 Running/External 分開是為了讓 serve 面板
    // 標示「(debug)」，提醒使用者這個行程目前掛著除錯器（例如 x 鍵停止時走的是 DAP terminate）。
    Debug,
}

public readonly record struct ProbeResult(
    DateTime TimeStamp,
    bool Ok,
    int StatusCode,
    double ElapsedMs,
    string? Error
);

/// <summary>
/// 控制開發伺服器。啟動方式完全由設定檔的 serveCommand / serveArguments 決定
/// （IIS Express 腳本、dotnet run、自訂 script 都可以），未設定時只做附加監看。
/// 伺服器健康狀態以 HTTP 探測取得，不依賴 ASP.NET 效能計數器（很多機器上沒有執行個體）。
///
/// 哪個行程是伺服器，啟動與接管都由 ServerLocator 辨識（監聽設定連接埠的受控行程）；
/// 停止只依 PID（連同後代行程）進行，絕不依映像名稱，避免停到同名的無關行程。
///
/// 狀態欄位：state、serverPid、serverStart、port、lastFailure、lastStartedPid 的寫入都在 stateLock 內
/// （TryBeginStart、SetTarget、RestoreAfterFailedStart、StopAsync、Refresh、State 的 setter 與各屬性的 setter）。
/// serveProcess 由啟動流程在 stateLock 內寫入；清除則用 Interlocked 交換，
/// 讓同一個行程只被結束與釋放一次：啟動它的那一次呼叫失敗時（FinishStart）、CleanupStartedProcess、
/// StopAsync 成功後（StopAsync 以 PID 結束行程，之後才釋放物件）。TryBeginStart 也會釋放已結束的舊行程。
/// </summary>
public sealed class ServerService : IDisposable
{
    private static readonly TimeSpan StartPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly DiagConfig config;
    private readonly LogBuffer log;
    private readonly HttpClient probeClient;
    private readonly RingBuffer<ProbeResult> probes = new(120);
    private readonly object gate = new();

    private readonly LocatorProbes locatorProbes;
    private readonly ProcessControl control;

    private readonly object stateLock = new();
    private Process? serveProcess;
    private CancellationTokenSource? startCts;
    private int port;
    private ServerFailure lastFailure;
    private int? lastStartedPid;
    private string? lastStartError;

    // 啟動的行程輸出中第一行例外訊息（「XxxException: 訊息」）與第一行錯誤
    // （stderr，或含 ERR／FTL／fail:／crit: 的 stdout 行）；由輸出事件執行緒寫入，
    // 啟動失敗時附在失敗原因後面，例外訊息優先
    private volatile string? firstAppExceptionLine;
    private volatile string? firstAppErrorLine;
    private ServerState state = ServerState.Stopped;
    private int? serverPid;
    private DateTime? serverStart;

    public ServerService(
        DiagConfig config,
        LogBuffer log,
        int port,
        LocatorProbes? probes = null,
        ProcessControl? control = null
    )
    {
        this.config = config;
        this.log = log;
        locatorProbes = probes ?? LocatorProbes.Default;
        this.control = control ?? ProcessControl.Default;
        this.port = port;

        var handler = new SocketsHttpHandler
        {
            // 開發伺服器多半用本機自簽憑證，探測時不驗證憑證鏈
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
            AllowAutoRedirect = false,
        };

        probeClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    public int Port
    {
        get
        {
            lock (stateLock)
            {
                return port;
            }
        }
        private set
        {
            lock (stateLock)
            {
                port = value;
            }
        }
    }

    /// <summary>連接埠已有人監聽之後，等待辨識出伺服器行程的上限；測試縮短它以免等滿 30 秒。</summary>
    internal TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>除錯啟動等待 wrapper 產生子行程的上限。</summary>
    internal TimeSpan DebugChildTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>停止時等待行程結束的輪詢間隔；測試縮短它以免每輪等 500 毫秒。</summary>
    internal TimeSpan StopPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>送出正常關閉訊號後，最多輪詢幾次等行程結束。</summary>
    internal int StopSignalAttempts { get; init; } = 20;

    /// <summary>強制結束後，最多輪詢幾次等行程結束。</summary>
    internal int StopForceAttempts { get; init; } = 3;

    public ServerState State
    {
        get
        {
            lock (stateLock)
            {
                return state;
            }
        }
        private set
        {
            lock (stateLock)
            {
                state = value;
            }
        }
    }

    /// <summary>目前監看的行程 PID（可能是本工具啟動的，也可能是外部既有的）。</summary>
    public int? ServerPid
    {
        get
        {
            lock (stateLock)
            {
                return serverPid;
            }
        }
    }

    /// <summary>
    /// 是否還持有本工具啟動的行程物件（serveCommand 啟動的行程，不論它是否已結束）；為 true 時 StopAsync
    /// 才會結束並釋放它，所以再啟動前要先停止。
    /// </summary>
    public bool HasStartedProcess
    {
        get
        {
            lock (stateLock)
            {
                return serveProcess is not null;
            }
        }
    }

    /// <summary>最近一次辨識、啟動或停止失敗的原因類別；成功時為 None。</summary>
    public ServerFailure LastFailure
    {
        get
        {
            lock (stateLock)
            {
                return lastFailure;
            }
        }
        private set
        {
            lock (stateLock)
            {
                lastFailure = value;
            }
        }
    }

    /// <summary>最近一次啟動失敗的原因（與 6 記錄的失敗訊息相同）；啟動成功或尚未啟動為 null。</summary>
    public string? LastStartError
    {
        get
        {
            lock (stateLock)
            {
                return lastStartError;
            }
        }
        private set
        {
            lock (stateLock)
            {
                lastStartError = value;
            }
        }
    }

    /// <summary>最近一次 StartAsync 啟動的行程（啟動指令本身）的 PID；沒有啟動過為 null。</summary>
    public int? LastStartedPid
    {
        get
        {
            lock (stateLock)
            {
                return lastStartedPid;
            }
        }
    }

    public bool ProbeEnabled { get; set; } = true;

    public ProbeResult? LastProbe
    {
        get
        {
            lock (gate)
            {
                return probes.TryGetLast(out ProbeResult last) ? last : null;
            }
        }
    }

    public ProbeResult[] ProbeHistory
    {
        get
        {
            lock (gate)
            {
                return probes.TakeLast(probes.Count);
            }
        }
    }

    public string Url => config.ExpandProbeUrl(Port);

    /// <summary>是否有人在該連接埠監聽（不論是誰啟動的）。</summary>
    public bool IsPortListening() => PortOwnerFinder.IsListening(Port);

    /// <summary>
    /// 找出要接管的既有行程，規則見 ServerLocator：只接管監聽設定連接埠的受控行程（設定了
    /// processNames 時名稱也要符合）；沒有人監聽、擁有者不明或有多個候選就不接管，原因寫進 6 記錄。
    /// </summary>
    public int? FindExistingServer() => LocateExisting().Pid;

    private LocateResult LocateExisting()
    {
        LocateResult result = ServerLocator.Locate(Port, config.ProcessNames, startedRootPid: null, locatorProbes);
        LastFailure = result.Failure;
        if (result.Pid is null && result.Reason is not null)
        {
            log.Add("serve", result.Kind, result.Reason);
        }

        return result;
    }

    /// <summary>
    /// 掛上外部既有的行程（attach-only 模式）。伺服器正在啟動或停止中時由該操作維護 PID 與狀態，
    /// 不接受接管並回傳 false。
    /// </summary>
    public bool AdoptExisting(int pid)
    {
        if (!TryAdopt(pid, ServerState.External))
        {
            return false;
        }

        log.Add("serve", LogKind.Info, $"接管既有行程 PID {pid}（非本工具啟動）");
        return true;
    }

    /// <summary>
    /// 掛上由 DapSessionService 啟動或附加的除錯目標；serve 面板會標示「(debug)」。
    /// 伺服器正在啟動或停止中時不接受並回傳 false。
    /// </summary>
    public bool AdoptDebuggee(int pid)
    {
        if (!TryAdopt(pid, ServerState.Debug))
        {
            return false;
        }

        log.Add("serve", LogKind.Info, $"接管除錯目標 PID {pid}（掛著除錯器）");
        return true;
    }

    private bool TryAdopt(int pid, ServerState adoptedState)
    {
        bool stopping;
        lock (stateLock)
        {
            if (state is not (ServerState.Starting or ServerState.Stopping))
            {
                SetTarget(pid, adoptedState);
                return true;
            }

            stopping = state == ServerState.Stopping;
        }

        // 啟動中收到的除錯目標事件是正常情形：啟動流程成功後自己會設定目標，不必記警告
        if (stopping)
        {
            log.Add("serve", LogKind.Warning, $"伺服器正在停止中，未接管 PID {pid}");
        }

        return false;
    }

    /// <summary>
    /// 設定監看目標與狀態，並記下行程建立時間：PID 被別的行程重複使用後不會把它當成原本的伺服器。
    /// </summary>
    private void SetTarget(int pid, ServerState newState)
    {
        lock (stateLock)
        {
            serverPid = pid;
            serverStart = control.StartTime(pid);
            state = newState;
        }
    }

    /// <summary>
    /// 開始啟動前的共用檢查，通過時把狀態改成 Starting。回傳 false 表示被拒絕（原因已寫進記錄），
    /// existingPid 是啟動中或執行中的 PID，供 StartAsync 回報。
    /// 除錯階段進行中（Debug）與前一次啟動的行程仍在執行時也拒絕：兩者的行程由別的流程持有，
    /// 再啟動會讓兩個流程搶同一個 serveProcess。
    /// </summary>
    private bool TryBeginStart(out ServerState previous, out int? existingPid)
    {
        lock (stateLock)
        {
            previous = state;
            existingPid = null;
            switch (state)
            {
                case ServerState.Stopping:
                    log.Add("serve", LogKind.Warning, "伺服器正在停止中");
                    existingPid = serverPid;
                    return false;
                case ServerState.Starting:
                    log.Add("serve", LogKind.Warning, "伺服器正在啟動中");
                    existingPid = serverPid;
                    return false;
                case ServerState.Running:
                    log.Add("serve", LogKind.Warning, "伺服器已在執行中");
                    existingPid = serverPid;
                    return false;
                case ServerState.Debug:
                    lastFailure = ServerFailure.DebugSessionActive;
                    log.Add(
                        "serve",
                        LogKind.Warning,
                        $"除錯階段進行中（PID {serverPid}），請先停止除錯階段（x）再啟動伺服器"
                    );
                    return false;
            }

            if (serveProcess is { } held)
            {
                if (!HasExited(held))
                {
                    lastFailure = ServerFailure.StartedProcessStillRunning;
                    log.Add(
                        "serve",
                        LogKind.Warning,
                        $"前一次啟動的行程（PID {held.Id}）仍在執行，請先按 x 停止再啟動"
                    );
                    return false;
                }

                serveProcess = null;
                held.Dispose();
            }

            state = ServerState.Starting;
            return true;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 啟動流程的共用收尾。失敗時只結束「這一次呼叫啟動」的行程（started），別的流程持有的
    /// serveProcess 不動；連接埠與狀態還原成啟動前。成功時狀態已由呼叫端設好，不做任何事。
    /// </summary>
    private void FinishStart(bool succeeded, ServerState previous, int previousPort, Process? started)
    {
        if (succeeded)
        {
            return;
        }

        Port = previousPort;
        if (
            started is not null
            && ReferenceEquals(Interlocked.CompareExchange(ref serveProcess, null, started), started)
        )
        {
            KillTree(started);
        }

        RestoreAfterFailedStart(previous);
    }

    /// <summary>
    /// 啟動失敗後還原狀態：原本已接管行程（External）時維持原狀，其他情形回到 Stopped
    /// 並清掉 PID 與建立時間，不留下殘留的 PID。
    /// </summary>
    private void RestoreAfterFailedStart(ServerState previous)
    {
        lock (stateLock)
        {
            if (serverPid is not null && previous == ServerState.External)
            {
                state = previous;
                return;
            }

            serverPid = null;
            serverStart = null;
            state = ServerState.Stopped;
        }
    }

    /// <summary>
    /// 取消進行中的啟動（走 StartCancelled 路徑，結束啟動的行程樹）。回傳 false 表示目前沒有進行中的啟動。
    /// </summary>
    public bool CancelStart()
    {
        CancellationTokenSource? source;
        lock (stateLock)
        {
            source = state == ServerState.Starting ? startCts : null;
        }

        if (source is null)
        {
            return false;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 啟動伺服器並回傳新行程的 PID。失敗原因見 LastFailure。
    /// 行程活著而連接埠還沒有人監聽時一直等（應用程式可能在遷移資料庫等），直到行程結束、
    /// 發生無法辨識的致命失敗或被 CancelStart／token 取消；progress 每秒回報一次等待進度。
    /// </summary>
    public async Task<int?> StartAsync(int? port, CancellationToken token, Action<string>? progress = null)
    {
        if (!TryBeginStart(out ServerState previous, out int? existingPid))
        {
            return existingPid;
        }

        using var startSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (stateLock)
        {
            startCts = startSource;
        }

        token = startSource.Token;

        int previousPort = Port;
        Process? started = null;
        bool succeeded = false;
        LastFailure = ServerFailure.None;
        LastStartError = null;
        firstAppExceptionLine = null;
        firstAppErrorLine = null;
        try
        {
            if (
                !CheckCanServe($"設定檔未指定 serveCommand，無法啟動伺服器（可在 {DiagConfig.FileName} 設定，或自行啟動後由本工具附加）")
            )
            {
                return null;
            }

            if (port is not null)
            {
                Port = port.Value;
            }

            bool portHeldByHttpSys = false;
            if (IsPortListening())
            {
                log.Add("serve", LogKind.Warning, $"連接埠 {Port} 已被監聽，嘗試接管既有行程");
                LocateResult existing = LocateExisting();
                if (existing.Pid is { } existingServerPid)
                {
                    SetTarget(existingServerPid, ServerState.External);
                    succeeded = true;
                    log.Add("serve", LogKind.Info, $"接管既有行程 PID {existingServerPid}（非本工具啟動）");
                    return existingServerPid;
                }

                // 監聽記在 HTTP.sys 名下（例如本機 IIS 佔著 80），而且沒有名稱符合、服務此連接埠的行程：
                // 沒有任何既有的伺服器可接管，照常啟動
                if (existing.Failure != ServerFailure.NoServingProcess)
                {
                    return null;
                }

                log.Add("serve", LogKind.Info, $"沒有既有的行程服務連接埠 {Port}，照常啟動");
                LastFailure = ServerFailure.None;
                portHeldByHttpSys = true;
            }

            started = LaunchServeProcess("啟動失敗");
            if (started is null)
            {
                return null;
            }

            Process process = started;
            // 行程還活著時先讀好建立時間：根結束後 Process.GetProcessById 讀不到，持有的物件才讀得到
            DateTime? processStart = ChildProcessFinder.SafeStartTime(process);
            log.Add(
                "serve",
                LogKind.Info,
                $"{Path.GetFileName(process.StartInfo.FileName)}（PID {process.Id}）啟動中，連接埠 {Port}"
            );

            // 每輪只查連接埠擁有者與行程樹（毫秒級），不掃描全部行程的模組
            var elapsed = Stopwatch.StartNew();
            Stopwatch? listening = null;
            int reportedSeconds = -1;
            bool longWaitLogged = false;
            LocateResult last;
            while (true)
            {
                await Task.Delay(StartPollInterval, token).ConfigureAwait(false);
                bool exited = process.HasExited;
                last = ServerLocator.Locate(Port, config.ProcessNames, process.Id, locatorProbes, processStart);
                if (last.Pid is { } found)
                {
                    SetTarget(found, ServerState.Running);
                    succeeded = true;
                    log.Add("serve", LogKind.Success, $"PID {found} 已啟動 → {Url}");
                    if (portHeldByHttpSys)
                    {
                        log.Add(
                            "serve",
                            LogKind.Warning,
                            $"連接埠 {Port} 啟動前已由 HTTP.sys 佔用（例如本機 IIS），探測網址可能連到別的站台，探測結果不一定代表這個行程"
                        );
                    }

                    return found;
                }

                if (ServerLocator.IsFatal(last.Failure))
                {
                    LastFailure = last.Failure;
                    LastStartError = $"無法辨識伺服器行程：{last.Reason}";
                    log.Add("serve", LogKind.Error, LastStartError);
                    return null;
                }

                if (exited)
                {
                    break;
                }

                if (last.Failure == ServerFailure.NotListening)
                {
                    int seconds = (int)elapsed.Elapsed.TotalSeconds;
                    if (seconds != reportedSeconds)
                    {
                        reportedSeconds = seconds;
                        progress?.Invoke($"等待應用程式監聽連接埠 {Port}（已 {seconds} 秒，按 x 取消）");
                    }

                    if (!longWaitLogged && elapsed.Elapsed >= StartTimeout)
                    {
                        longWaitLogged = true;
                        log.Add(
                            "serve",
                            LogKind.Info,
                            $"應用程式啟動超過 {StartTimeout.TotalSeconds:F0} 秒仍未監聽連接埠 {Port}，繼續等待（按 x 取消）"
                        );
                    }

                    continue;
                }

                listening ??= Stopwatch.StartNew();
                if (listening.Elapsed >= StartTimeout)
                {
                    break;
                }
            }

            bool commandExited = process.HasExited;
            LastFailure = commandExited ? ServerFailure.StartCommandExited : ServerFailure.StartTimedOut;
            string cause;
            if (!commandExited)
            {
                cause = $"連接埠 {Port} 已有人監聽，但等了 {listening?.Elapsed.TotalSeconds ?? 0:F0} 秒仍未辨識出伺服器行程";
            }
            else if (last.Failure == ServerFailure.NotListening)
            {
                // 結束碼 0 也算失敗：應用程式在監聽前就結束，常見是啟動例外被應用程式自己攔下後正常結束
                cause = $"應用程式在監聽連接埠 {Port} 之前就結束了（結束碼 {process.ExitCode}）";
            }
            else
            {
                cause = $"啟動指令已結束（結束碼 {process.ExitCode}），未辨識出伺服器行程";
            }

            string message = last.Reason is null || last.Failure == ServerFailure.NotListening
                ? cause
                : $"{cause}：{last.Reason}";
            if ((firstAppExceptionLine ?? firstAppErrorLine) is { } appError)
            {
                message = $"{message}；應用程式輸出的錯誤：{appError}";
            }

            LastStartError = message;
            log.Add("serve", LogKind.Error, message);
            return null;
        }
        catch (OperationCanceledException)
        {
            LastFailure = ServerFailure.StartCancelled;
            log.Add("serve", LogKind.Warning, "啟動已取消，已清除啟動的行程");
            return null;
        }
        catch (Exception ex)
        {
            LastFailure = ServerFailure.StartError;
            log.Add("serve", LogKind.Error, $"啟動失敗: {ex.Message}");
            return null;
        }
        finally
        {
            lock (stateLock)
            {
                startCts = null;
            }

            FinishStart(succeeded, previous, previousPort, started);
        }
    }

    /// <summary>
    /// 在除錯器下啟動伺服器：專治 serveCommand 是 `dotnet run` 這類 wrapper 的情形——netcoredbg
    /// 的 `launch` 只會附加到 wrapper 本身，wrapper 另外開的子行程（真正的 app）完全不受除錯器
    /// 控制，中斷點永遠不會命中（DiagConfig.IsWrapperServeCommand 的說明）。
    ///
    /// 做法：wrapper 行程照 StartAsync 原樣啟動（不受除錯器控制），但找子行程改用
    /// ChildProcessFinder.DirectChildrenOf（Toolhelp32Snapshot 直接查 wrapper 的子行程）。
    /// StartAsync 等的是「連接埠有人監聽」，要等伺服器開始監聽才看得到；除錯啟動要在
    /// 子行程一出現（遠早於開始監聽）就接手，才趕得上啟動路徑上的中斷點。
    /// ChildProcessFinder 只讀行程清單的 PID／PPID，不開行程控制代碼、不列舉模組，
    /// 可以用 20ms 的間隔輪詢，把「行程出現」到「除錯器接手」的間隔壓到最短。
    ///
    /// 這個間隔仍然不是零——不是行程建立時的強制暫停（Win32 CREATE_SUSPENDED 那一類機制），
    /// 只是盡快追上去。極早、只有一兩行就執行完的啟動路徑仍可能撲空；但 DI 期間、
    /// ConfigureServices 這類實際會設中斷點的位置，執行到那裡所需的時間遠大於輪詢間隔，
    /// 可穩定命中（見 README「除錯」一節）。
    ///
    /// 找不到子行程、wrapper 提前結束、或 attach 本身失敗，一律視為失敗、清掉 wrapper 行程並在
    /// 「6 記錄」印出明確原因——絕不會悄悄退化成「附加到 wrapper、中斷點全部不會命中」的狀態。
    /// 啟動前的狀態檢查與 StartAsync 相同（TryBeginStart）：已有除錯階段、啟動中或執行中，
    /// 或前一次啟動的行程仍在執行（尚未按 x 停止）都拒絕。
    /// </summary>
    public async Task<int?> StartUnderDebuggerAsync(
        DapSessionService dap,
        string? adapterPathOverride,
        CancellationToken token
    )
    {
        if (!TryBeginStart(out ServerState previous, out _))
        {
            return null;
        }

        int previousPort = Port;
        Process? started = null;
        bool succeeded = false;
        LastFailure = ServerFailure.None;
        try
        {
            if (!CheckCanServe("設定檔未指定 serveCommand，無法在除錯器下啟動"))
            {
                return null;
            }

            if (IsPortListening())
            {
                LastFailure = ServerFailure.PortAlreadyListening;
                log.Add(
                    "serve",
                    LogKind.Error,
                    $"連接埠 {Port} 已被監聽，無法在除錯器下啟動新行程（先按 x 停止既有行程，除錯啟動需要一個全新的行程才能命中啟動路徑上的中斷點）"
                );
                return null;
            }

            started = LaunchServeProcess("啟動 wrapper 失敗");
            if (started is null)
            {
                return null;
            }

            Process wrapper = started;
            log.Add(
                "serve",
                LogKind.Info,
                $"{Path.GetFileName(wrapper.StartInfo.FileName)}（wrapper）啟動中，等待實際 app 子行程…"
            );

            int? childPid;
            try
            {
                childPid = await WaitForDirectChildAsync(
                        wrapper,
                        pollInterval: TimeSpan.FromMilliseconds(20),
                        DebugChildTimeout,
                        token
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LastFailure = ServerFailure.StartCancelled;
                log.Add("serve", LogKind.Warning, "除錯啟動已取消，已清除 wrapper 行程");
                return null;
            }

            if (childPid is null)
            {
                bool wrapperExited = wrapper.HasExited;
                LastFailure = wrapperExited ? ServerFailure.StartCommandExited : ServerFailure.StartTimedOut;
                string reason = wrapperExited
                    ? $"wrapper 已結束（結束碼 {wrapper.ExitCode}），未偵測到子行程"
                    : $"等待 wrapper 產生子行程逾時（{DebugChildTimeout.TotalSeconds:F0} 秒）";
                log.Add(
                    "serve",
                    LogKind.Error,
                    $"除錯啟動失敗：{reason}。serveCommand/serveArguments 可能不是會再開子行程的 wrapper，"
                        + "或子行程啟動太慢；也可考慮改指向建置好的組件本身（例如 \"dotnet\", [\"bin/Debug/net8.0/MyApp.dll\"]）"
                );
                return null;
            }

            log.Add("serve", LogKind.Info, $"偵測到子行程 PID {childPid}，附加除錯器中…");
            bool attached = await dap.AttachAsync(childPid.Value, adapterPathOverride, ownedByThisTool: true, token)
                .ConfigureAwait(false);

            if (!attached)
            {
                LastFailure = ServerFailure.StartError;
                // 附加失敗時子行程可能已經在跑（甚至佔著連接埠）；它在 wrapper 的行程樹內，
                // 連同 wrapper 一起清乾淨，不留下「沒有除錯器、也沒人知道」的殭屍行程
                log.Add(
                    "serve",
                    LogKind.Error,
                    $"除錯啟動失敗：附加到 PID {childPid} 失敗（{dap.LastError}）——子行程與 wrapper 行程已一併清除，"
                        + "不會留下沒有除錯器附加、卻繼續佔用連接埠的殭屍行程"
                );
                return null;
            }

            SetTarget(childPid.Value, ServerState.Debug);
            succeeded = true;
            log.Add("serve", LogKind.Info, $"接管除錯目標 PID {childPid}（掛著除錯器）");
            log.Add(
                "serve",
                LogKind.Success,
                $"已在除錯器下啟動：wrapper → 子行程 PID {childPid}（等待中斷點，按 8 看偵錯分頁）"
            );
            return childPid;
        }
        finally
        {
            FinishStart(succeeded, previous, previousPort, started);
        }
    }

    /// <summary>
    /// 結束本工具啟動的行程（serveCommand 啟動的 wrapper 或伺服器本身）與它的後代並釋放它。
    /// 沒有啟動過行程（例如直接 launch、或接管既有行程）時直接略過，呼叫也安全。
    /// 除錯階段結束時（DisconnectAsync 已經連帶終止子行程）、重建重啟與離開程式時都會呼叫這個方法收尾。
    /// </summary>
    public void CleanupStartedProcess()
    {
        Process? root = Interlocked.Exchange(ref serveProcess, null);
        if (root is not null)
        {
            KillTree(root);
        }
    }

    /// <summary>
    /// 強制結束 root 與它的後代並釋放 root；只動這棵樹，不碰其他行程。
    /// 後代以行程快照裡的父 PID 連結找出（ChildProcessFinder.DescendantsOf，已核對每一層父子的建立時間）：
    /// 根結束後，它的直接子行程仍記著根的 PID，找得到也會結束；中間行程先結束的孫行程不在連結上，找不到。
    /// 根的建立時間從持有的行程物件讀（根結束後只有這個物件讀得到）；讀不到時根底下的連結都不採用（根還活著時，Kill 的整棵樹結束仍會動到它的後代）。
    /// </summary>
    private static void KillTree(Process root)
    {
        HashSet<int> descendants = new();
        try
        {
            descendants = ChildProcessFinder.DescendantsOf(root.Id, ChildProcessFinder.SafeStartTime(root));
            if (!root.HasExited)
            {
                root.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 行程可能已經自行結束（子行程退出後很多 wrapper 會跟著結束），略過
        }
        finally
        {
            root.Dispose();
        }

        foreach (int pid in descendants)
        {
            try
            {
                using Process child = Process.GetProcessById(pid);
                child.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已經結束，略過
            }
        }
    }

    /// <summary>設定檔有 serveCommand 才回傳 true；否則記下原因類別與 message 並回傳 false。</summary>
    private bool CheckCanServe(string message)
    {
        if (config.CanServe)
        {
            return true;
        }

        LastFailure = ServerFailure.NoServeCommand;
        log.Add("serve", LogKind.Error, message);
        return false;
    }

    /// <summary>
    /// 啟動 serveCommand 的行程，成功時發佈為 serveProcess 並記下 LastStartedPid；失敗時記下 StartError 與
    /// 「{failurePrefix}: 原因」並回傳 null。StartAsync 與 StartUnderDebuggerAsync 共用。
    /// </summary>
    private Process? LaunchServeProcess(string failurePrefix)
    {
        Process process;
        try
        {
            process = StartWrapperProcess(BuildServeStartInfo());
        }
        catch (Exception ex)
        {
            LastFailure = ServerFailure.StartError;
            log.Add("serve", LogKind.Error, $"{failurePrefix}: {ex.Message}");
            return null;
        }

        lock (stateLock)
        {
            serveProcess = process;
            lastStartedPid = process.Id;
        }

        return process;
    }

    /// <summary>組出啟動伺服器用的 ProcessStartInfo；StartAsync 與 StartUnderDebuggerAsync 共用。</summary>
    private ProcessStartInfo BuildServeStartInfo()
    {
        var psi = new ProcessStartInfo(config.Expand(config.ServeCommand!, port: Port))
        {
            WorkingDirectory = config.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 標準輸入一定要導向管道：不導向的話子行程（腳本 → IIS Express）會沿用本工具的
            // 主控台輸入緩衝區，把使用者的按鍵吃掉，TUI 從此收不到 1/2/3/4 等按鍵。
            // 管道刻意保持開啟不寫入也不關閉：關閉會讓伺服器讀到 EOF 而可能自行結束。
            RedirectStandardInput = true,
            // 但光導向 stdin 擋不住 IIS Express：它的「按 Q 結束」讀取器是直接開 CONIN$
            // 讀鍵盤，不透過標準輸入 handle，所以子行程必須完全沒有主控台可開，
            // 按鍵才會留在本工具的 TUI（等同 Visual Studio 啟動 IIS Express 的方式）。
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        foreach (string arg in config.ServeArguments ?? Array.Empty<string>())
        {
            psi.ArgumentList.Add(config.Expand(arg, port: Port));
        }

        return psi;
    }

    /// <summary>啟動伺服器／wrapper 行程並接上輸出攔截；StartAsync 與 StartUnderDebuggerAsync 共用。</summary>
    private Process StartWrapperProcess(ProcessStartInfo psi)
    {
        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            string line = e.Data ?? string.Empty;
            NoteAppErrorLine(line, fromStdErr: false);
            log.Add("serve", LogKind.Output, line);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            string line = e.Data ?? string.Empty;
            NoteAppErrorLine(line, fromStdErr: true);
            log.Add("serve", LogKind.Error, line);
        };

        process.Start();
        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            // 行程已經啟動卻接不上輸出：不留下沒人持有的行程
            KillTree(process);
            throw;
        }

        return process;
    }

    // 例外訊息行：「Namespace.XxxException: 訊息」或「XxxException (0x80131904): 訊息」
    private static readonly Regex ExceptionLinePattern = new(
        @"\b[\w.]*Exception(\s*\(0x[0-9A-Fa-f]+\))?:\s*\S",
        RegexOptions.Compiled
    );

    // Serilog 的 [.. ERR] / [.. FTL]，以及 Microsoft.Extensions.Logging 主控台格式的 fail: / crit:
    private static readonly Regex ErrorLevelPattern = new(
        @"\b(ERR|FTL)\]|^\s*(fail|crit):",
        RegexOptions.Compiled
    );

    /// <summary>記下啟動行程輸出中的第一行例外訊息與第一行錯誤，啟動失敗時附在原因後面。</summary>
    private void NoteAppErrorLine(string line, bool fromStdErr)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        string trimmed = line.Trim();
        if (firstAppExceptionLine is null && ExceptionLinePattern.IsMatch(trimmed))
        {
            firstAppExceptionLine = trimmed;
        }

        if (firstAppErrorLine is null && (fromStdErr || ErrorLevelPattern.IsMatch(trimmed)))
        {
            firstAppErrorLine = trimmed;
        }
    }

    /// <summary>
    /// 輪詢 wrapper 的直接子行程（見 StartUnderDebuggerAsync／ChildProcessFinder 為什麼不能掃描
    /// 全部行程的模組找 CLR 的說明）。實測 `dotnet run` 會先開一個 conhost.exe
    /// （終端機主控台的輔助行程，即使 CreateNoWindow/重新導向三個串流也還是會出現），
    /// 幾秒後才輪到真正的 app 子行程——如果不排除 conhost，會在 app 還沒起來前就誤判
    /// conhost 是目標並嘗試附加，白白浪費一次啟動機會（attach 到非受控行程，
    /// configurationDone 會直接失敗）。真的同時出現多個非 conhost 候選時挑 PID 最小的
    /// （最先建立的）——寧可有一個明確、可預期的行為，也不要隨機挑到不確定是哪個的行程。
    /// </summary>
    private async Task<int?> WaitForDirectChildAsync(
        Process wrapperProcess,
        TimeSpan pollInterval,
        TimeSpan timeout,
        CancellationToken token
    )
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            await Task.Delay(pollInterval, token).ConfigureAwait(false);

            List<int> children = ChildProcessFinder
                .DirectChildrenOf(wrapperProcess.Id)
                .Where(pid => !IsConhost(pid))
                .ToList();
            if (children.Count > 0)
            {
                return children.Min();
            }

            if (wrapperProcess.HasExited)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 判斷 PID 是不是 conhost.exe（終端機主控台的輔助行程，不是我們要附加的目標）。
    /// 查不到（行程可能已經結束）就當作「不是」，交由呼叫端下一輪輪詢或附加失敗處理，
    /// 不要在這裡誤判把一個真正還活著的候選行程排除掉。
    /// </summary>
    private static bool IsConhost(int pid) =>
        string.Equals(LocatorProbes.ProcessNameOf(pid), "conhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 停止伺服器：只依 PID（本工具啟動的行程、ServerPid）連同後代行程（/T）結束，絕不依映像名稱。
    /// 先送正常關閉訊號，因為以 IIS Express 為例，強制砍掉會讓 http.sys 留下 URL 註冊，
    /// 下次啟動同一個埠就會出現 0x800700b7；逾時沒結束才強制結束同一批 PID。
    /// 結束前一律核對行程建立時間：與記錄不符（PID 已被別的行程重複使用）視為原本的行程已結束，不動它；
    /// 讀不到建立時間（例如沒有權限）時無法確認是原本的行程，不動它並警告。
    /// 回傳 true 表示行程確實都結束了（或本來就沒有可停止的行程）；
    /// false 表示行程仍在執行或正在啟動／停止中，狀態與 PID 保持原樣並記錄警告。
    /// </summary>
    public async Task<bool> StopAsync(CancellationToken token)
    {
        ServerState previous;
        var targets = new List<ProcIdent>();
        lock (stateLock)
        {
            if (state is ServerState.Starting or ServerState.Stopping)
            {
                log.Add("serve", LogKind.Warning, "伺服器正在啟動或停止中，請稍後再試");
                return false;
            }

            if (serverPid is null && serveProcess is null)
            {
                log.Add("serve", LogKind.Warning, "沒有可停止的伺服器");
                return true;
            }

            previous = state;
            state = ServerState.Stopping;
            if (serveProcess is { } started)
            {
                targets.Add(new ProcIdent(started.Id, ChildProcessFinder.SafeStartTime(started)));
            }

            if (serverPid is { } pid && targets.All(t => t.Pid != pid))
            {
                targets.Add(new ProcIdent(pid, serverStart));
            }
        }

        bool stopped = false;
        var warned = new HashSet<int>();
        try
        {
            targets = targets.Where(IsProcessAlive).ToList();

            // taskkill 送出關閉訊號才回傳 0；沒有視窗的行程（dotnet run 的 app 等）送不出去，不必空等
            bool signalSent = false;
            foreach (ProcIdent target in targets)
            {
                signalSent |= SendKill(target, force: false, warned);
            }

            if (signalSent)
            {
                await WaitUntilGoneAsync(targets, StopSignalAttempts, token).ConfigureAwait(false);
            }

            List<ProcIdent> alive = targets.Where(IsProcessAlive).ToList();
            if (alive.Count > 0)
            {
                string pids = string.Join("、", alive.Select(t => t.Pid));
                log.Add(
                    "serve",
                    LogKind.Warning,
                    signalSent
                        ? $"PID {pids} 未回應關閉訊號，改為強制結束（IIS Express 可能留下 URL 註冊）"
                        : $"PID {pids} 沒有視窗可送關閉訊號，改為強制結束"
                );
                foreach (ProcIdent target in alive)
                {
                    SendKill(target, force: true, warned);
                }

                await WaitUntilGoneAsync(alive, StopForceAttempts, token).ConfigureAwait(false);
            }

            List<ProcIdent> survivors = targets.Where(IsProcessAlive).ToList();
            if (survivors.Count > 0)
            {
                LastFailure = ServerFailure.StopFailed;
                log.Add(
                    "serve",
                    LogKind.Warning,
                    $"PID {string.Join("、", survivors.Select(t => t.Pid))} 仍在執行，未能停止（可能沒有權限）；已保留監看，請手動結束"
                );
                return false;
            }

            try
            {
                Interlocked.Exchange(ref serveProcess, null)?.Dispose();
            }
            catch
            {
                // 略過
            }

            lock (stateLock)
            {
                serverPid = null;
                serverStart = null;
                state = ServerState.Stopped;
            }

            stopped = true;
            LastFailure = ServerFailure.None;
            log.Add("serve", LogKind.Info, "伺服器已停止");
            return true;
        }
        finally
        {
            if (!stopped)
            {
                State = previous;
            }
        }
    }

    private async Task WaitUntilGoneAsync(List<ProcIdent> targets, int attempts, CancellationToken token)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            await Task.Delay(StopPollInterval, token).ConfigureAwait(false);
            if (!targets.Any(IsProcessAlive))
            {
                return;
            }
        }
    }

    /// <summary>對設定的探測網址做一次請求，回報狀態碼與延遲。</summary>
    public async Task ProbeAsync(CancellationToken token)
    {
        if (!ProbeEnabled || ServerPid is null)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await probeClient
                .GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            bool ok = (int)response.StatusCode < 500;
            AddProbe(
                new ProbeResult(
                    DateTime.Now,
                    ok,
                    (int)response.StatusCode,
                    sw.Elapsed.TotalMilliseconds,
                    null
                )
            );
        }
        catch (OperationCanceledException)
        {
            // 關閉中
        }
        catch (Exception ex)
        {
            AddProbe(
                new ProbeResult(
                    DateTime.Now,
                    false,
                    0,
                    sw.Elapsed.TotalMilliseconds,
                    ex.GetBaseException().Message
                )
            );
        }
    }

    /// <summary>
    /// 回報目標行程是否還活著，供 UI 偵測伺服器意外結束。啟動或停止進行中不檢查：
    /// 那段期間 PID 與狀態由該操作自己維護。
    /// </summary>
    public void Refresh()
    {
        lock (stateLock)
        {
            if (state is ServerState.Starting or ServerState.Stopping)
            {
                return;
            }

            if (serverPid is { } pid && !IsProcessAlive(new ProcIdent(pid, serverStart)))
            {
                log.Add("serve", LogKind.Warning, $"監看的行程 PID {pid} 已結束");
                serverPid = null;
                serverStart = null;
                state = ServerState.Stopped;
            }
        }
    }

    private void AddProbe(ProbeResult result)
    {
        lock (gate)
        {
            probes.Add(result);
        }
    }

    /// <summary>行程識別：PID 加上建立時間（讀不到建立時間時為 null）。</summary>
    private readonly record struct ProcIdent(int Pid, DateTime? Start);

    /// <summary>
    /// 該識別的行程是否還活著；PID 已被建立時間不同的行程使用時，原本的行程視為已結束。
    /// 建立時間讀不到（例如沒有權限）時當作還活著，寧可回報停不掉，也不要誤報已停止。
    /// </summary>
    private bool IsProcessAlive(ProcIdent target)
    {
        if (!control.IsRunning(target.Pid))
        {
            return false;
        }

        if (target.Start is not { } expected)
        {
            return true;
        }

        DateTime? actual = control.StartTime(target.Pid);
        return actual is null || actual == expected;
    }

    /// <summary>
    /// 以 PID 結束行程與它的後代行程；force 為 false 時只送正常關閉訊號。回傳是否成功送出。
    /// 建立時間與記錄不符時 PID 已被別的行程使用，原本的行程視為已結束，不動它也不警告；
    /// 讀不到建立時間時無法確認它還是原本的行程，不動它，同一個 PID 只警告一次（warned）。
    /// </summary>
    private bool SendKill(ProcIdent target, bool force, HashSet<int> warned)
    {
        // 送出前再核對一次：等待期間 PID 可能已被別的行程使用；建立時間只讀一次
        if (!control.IsRunning(target.Pid))
        {
            return false;
        }

        DateTime? actual = control.StartTime(target.Pid);
        if (target.Start is { } expected && actual is { } current && current != expected)
        {
            return false;
        }

        if (target.Start is null || actual is null)
        {
            if (warned.Add(target.Pid))
            {
                log.Add(
                    "serve",
                    LogKind.Warning,
                    $"PID {target.Pid} 讀不到建立時間（可能沒有權限），無法確認是原本的行程，未送出結束指令；請確認後自行結束"
                );
            }

            return false;
        }

        string arguments = $"/PID {target.Pid} /T{(force ? " /F" : string.Empty)}";
        try
        {
            bool sent = control.Kill(target.Pid, force);
            log.Add("serve", LogKind.Info, $"taskkill {arguments}");
            return sent;
        }
        catch (Exception ex)
        {
            log.Add("serve", LogKind.Error, $"taskkill 失敗: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        probeClient.Dispose();
        serveProcess?.Dispose();
    }
}
