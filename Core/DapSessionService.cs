using System.Text.Json.Nodes;
using System.Threading.Channels;
using ClrDiag.Core.Dap;

namespace ClrDiag.Core;

public enum DebugSessionState
{
    Idle,
    Connecting,
    Running,
    Halted,
    Terminated,
    Failed,
}

/// <summary>一個中斷點；Verified/Message/AdapterId 由 adapter 的回應與事件回填，不是使用者輸入。</summary>
public sealed record DebugBreakpoint(string Path, int Line)
{
    public bool Verified { get; init; }
    public string? Message { get; init; }
    public int? AdapterId { get; init; }
}

/// <summary>一個監看運算式；不記錄來源（channel 或 TUI 新增的一律無法分辨，設計如此）。</summary>
public sealed record DebugWatch(string Expression);

public sealed record WatchResult(
    string Expression,
    string? Value,
    string? Type,
    bool TimedOut,
    string? Error
);

public sealed record DebugFrame(int Id, string Name, string? SourcePath, int? Line);

/// <summary>只展開一層的變數；VariablesReference 非 0 代表還有子節點，目前 UI 不做「展開子節點」。</summary>
public sealed record DebugVariable(string Name, string Value, string Type, int VariablesReference);

/// <summary>
/// 一次中斷後的完整快照：選取框架的區域變數＋全部監看運算式的求值結果。
/// Generation 是「這是第幾次真正的中斷」的序號——同一次中斷內因為新增/移除監看或切換
/// 框架而重新整理快照時沿用同一個序號，只有真正收到新的 DAP stopped 事件才會遞增；
/// 靠這個序號（而不是物件參考）才能分辨「新的中斷」與「同一次中斷的資料更新」，見
/// Program.cs 的 RunDap。
/// </summary>
public sealed record DebugHaltedState(
    int ThreadId,
    string Reason,
    IReadOnlyList<DebugFrame> Frames,
    int SelectedFrameIndex,
    IReadOnlyList<DebugVariable> Locals,
    IReadOnlyList<WatchResult> Watches,
    long Generation
);

/// <summary>
/// 驅動 netcoredbg 的 DAP 客戶端邏輯：交握、中斷點、監看運算式、中斷後的堆疊／區域變數擷取、
/// 執行控制。ClrDiag 是唯一的 DAP 客戶端——沒有代理，沒有第二個觀察者，Neovim 只送意圖過來。
///
/// 背景幫浦仿照 ProcessMonitor.SampleLoopAsync（Core/ProcessMonitor.cs:125-147）的紀律：
/// 單一佇列消費迴圈跑到階段結束為止，單一工作項目失敗不能讓幫浦停擺（同 :135 的 catch 隔離）。
/// DapClient 的事件在它自己的讀取執行緒上同步觸發，事件處理常式因此只能做輕量、非阻塞的狀態
/// 更新；真正要再送 DAP 請求的工作（stackTrace → scopes → variables → evaluate）一律排進這個
/// 佇列非同步執行——若在讀取執行緒上直接 await 這些請求會自我死結，因為回應正是要靠同一條
/// 讀取執行緒才能送回來。
/// </summary>
public sealed class DapSessionService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EvaluateTimeout = TimeSpan.FromSeconds(2);

    private readonly LogBuffer log;
    private readonly object gate = new();
    private readonly CancellationTokenSource cts = new();
    private readonly Channel<Func<CancellationToken, Task>> workQueue = Channel.CreateUnbounded<
        Func<CancellationToken, Task>
    >();

    private NetcoredbgProcess? adapter;
    private DapClient? client;
    private Task? pump;
    private bool launchMode;

    private List<DebugBreakpoint> breakpoints = new();
    private List<DebugWatch> watches = new();
    private DebugHaltedState? halted;
    // 「第幾次真正中斷」的序號，只在收到新的 stopped 事件時遞增；見 DebugHaltedState.Generation 註解
    private long haltGeneration;

    public DapSessionService(LogBuffer log) => this.log = log;

    public DebugSessionState State { get; private set; } = DebugSessionState.Idle;

    public int? DebuggeePid { get; private set; }

    public string? LastError { get; private set; }

    public bool IsLaunchMode => launchMode;

    public bool IsConnected => client is not null;

    /// <summary>
    /// 設定檔的 netcoredbg 路徑（DapAdapterPath），供懶惰啟動的自動附加使用；
    /// 明確呼叫 AttachAsync／LaunchAsync 時仍可逐次覆寫。
    /// </summary>
    public string? DefaultAdapterPath { get; set; }

    /// <summary>
    /// 除錯階段閒置、還沒手動啟動附加或啟動時，SetBreakpointAsync 用這個委派取得
    /// 「應該附加到哪個行程」——TUI 端設成回傳目前監看的 PID，讓「送出第一個中斷點」
    /// 自動觸發 spawn netcoredbg + attach（規劃書「Spawn timing」假設：懶惰啟動，
    /// 第一個除錯動作才付出 spawn 延遲）。非互動的 --dap 模式不設這個，因為它自己在
    /// 啟動時就已經明確呼叫過 AttachAsync。
    /// </summary>
    public Func<int?>? AutoAttachPidProvider { get; set; }

    /// <summary>啟動除錯階段時把新學到的行程 PID 往外送，供 DiagApp 接管 ProcessMonitor。</summary>
    public event Action<int>? ProcessStarted;

    /// <summary>
    /// 階段狀態（State／Halted 快照／中斷點清單）有變動時觸發，讓 TUI 與具名管道通道
    /// 不必輪詢。可能在幫浦執行緒或 DapClient 的讀取執行緒上觸發，訂閱端自行負責執行緒安全。
    /// </summary>
    public event Action? StateChanged;

    public IReadOnlyList<DebugBreakpoint> Breakpoints
    {
        get
        {
            lock (gate)
            {
                return breakpoints;
            }
        }
    }

    public IReadOnlyList<DebugWatch> Watches
    {
        get
        {
            lock (gate)
            {
                return watches;
            }
        }
    }

    public DebugHaltedState? Halted
    {
        get
        {
            lock (gate)
            {
                return halted;
            }
        }
    }

    /// <summary>
    /// 從設定檔載入中斷點與監看清單的初始值。只在建構後、第一次連線前呼叫一次；
    /// 與其他設定欄位一致（例如 buildConfiguration），執行期的變更不會寫回 clrdiag.json，
    /// 純手動編輯設定檔才會影響下次啟動的初始清單。
    /// </summary>
    public void SeedFromConfig(
        IEnumerable<(string Path, int Line)> initialBreakpoints,
        IEnumerable<string> initialWatches
    )
    {
        lock (gate)
        {
            breakpoints = initialBreakpoints
                .Select(b => new DebugBreakpoint(NormalizePath(b.Path), b.Line))
                .ToList();
            watches = initialWatches.Select(w => new DebugWatch(w)).ToList();
        }
    }

    /// <summary>啟動 netcoredbg 並附加到既有行程（單純附加外部既有行程，非 ClrDiag 自己啟動）。</summary>
    public Task<bool> AttachAsync(int pid, string? adapterPathOverride, CancellationToken token) =>
        ConnectAsync(
            launch: false,
            pid,
            launchProgram: null,
            launchArgs: null,
            launchCwd: null,
            adapterPathOverride,
            token
        );

    /// <summary>
    /// 啟動 netcoredbg 並附加到既有行程；ownedByThisTool 表示這個行程其實是 ClrDiag 自己（間接）
    /// 啟動的——例如 wrapper 型 serveCommand（`dotnet run`）展開出的子行程，見
    /// ServerService.StartUnderDebuggerAsync。雖然對 netcoredbg 送的仍是 DAP `attach`（沒有既有
    /// PID 送不了 `launch`），但語意上等同「啟動模式」：IsLaunchMode／狀態列要顯示啟動模式，
    /// DisconnectAsync 也要連帶終止目標，跟直接 launch 的行為一致；不能沿用單純附加外部既有行程
    /// 時「斷線不動它」的規則。
    /// </summary>
    public Task<bool> AttachAsync(
        int pid,
        string? adapterPathOverride,
        bool ownedByThisTool,
        CancellationToken token
    ) =>
        ConnectAsync(
            launch: false,
            pid,
            launchProgram: null,
            launchArgs: null,
            launchCwd: null,
            adapterPathOverride,
            token,
            treatAsLaunchForTeardown: ownedByThisTool
        );

    /// <summary>啟動 netcoredbg 並在它底下啟動指定的程式——attach 搆不到的啟動路徑中斷點要靠這個。</summary>
    public Task<bool> LaunchAsync(
        string program,
        IReadOnlyList<string> args,
        string cwd,
        string? adapterPathOverride,
        CancellationToken token
    ) => ConnectAsync(launch: true, pid: null, program, args, cwd, adapterPathOverride, token);

    private async Task<bool> ConnectAsync(
        bool launch,
        int? pid,
        string? launchProgram,
        IReadOnlyList<string>? launchArgs,
        string? launchCwd,
        string? adapterPathOverride,
        CancellationToken token,
        bool? treatAsLaunchForTeardown = null
    )
    {
        // 「State 允許起新階段嗎」與「State 改成 Connecting」要在同一個鎖底下原子完成，
        // 且要搶在任何行程 spawn 之前：Neovim 的第一個 setBreakpoint（EnsureAutoAttachedAsync）
        // 跟 TUI 的 Shift+S→s，或單純兩個具名管道客戶端，都可能同時看到 Idle 而各自往下跑，
        // 若中間沒有互斥，兩邊都會 spawn/attach netcoredbg，互踩 client/adapter/pump（後寫的贏），
        // 輸家的 netcoredbg 沒有任何參照留下可以 Dispose，直接洩漏。
        lock (gate)
        {
            if (State is DebugSessionState.Connecting or DebugSessionState.Running or DebugSessionState.Halted)
            {
                log.Add("dap", LogKind.Warning, "除錯階段已在進行中");
                return false;
            }

            State = DebugSessionState.Connecting;
        }

        string? exePath = NetcoredbgProcess.ResolveExecutable(adapterPathOverride);
        if (exePath is null)
        {
            State = DebugSessionState.Failed;
            LastError = "找不到 netcoredbg，可在 clrdiag.json 設定 dapAdapterPath，或安裝到 PATH／mason 預設路徑";
            log.Add("dap", LogKind.Error, LastError);
            return false;
        }

        // 一般情況下「是否送 launch」與「斷線要不要連帶終止目標」是同一件事；
        // wrapper attach（treatAsLaunchForTeardown）是例外——DAP 動詞是 attach，
        // 但這個行程是我們自己間接啟動的，語意與收尾規則都要比照 launch。
        launchMode = treatAsLaunchForTeardown ?? launch;
        LastError = null;
        log.Add("dap", LogKind.Info, $"啟動除錯轉接器: {exePath}");

        NetcoredbgProcess proc;
        try
        {
            proc = NetcoredbgProcess.Start(exePath);
        }
        catch (Exception ex)
        {
            State = DebugSessionState.Failed;
            LastError = $"啟動 netcoredbg 失敗: {ex.Message}";
            log.Add("dap", LogKind.Error, LastError);
            return false;
        }

        proc.ErrorLineReceived += line => log.Add("dap", LogKind.Warning, line);
        proc.Exited += exitCode =>
        {
            log.Add("dap", LogKind.Warning, $"netcoredbg 已結束（結束碼 {exitCode}）");
            HandleAdapterGone();
        };

        var dap = new DapClient(proc.StandardOutput, proc.StandardInput);
        dap.EventReceived += OnEvent;
        dap.Start();

        adapter = proc;
        client = dap;
        EnsurePump();

        var initializedTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        void OnInitialized(DapEvent evt)
        {
            if (evt.Name == "initialized")
            {
                initializedTcs.TrySetResult();
            }
        }

        dap.EventReceived += OnInitialized;
        try
        {
            await dap.RequestAsync(
                    "initialize",
                    new
                    {
                        clientID = "clrdiag",
                        adapterID = "coreclr",
                        linesStartAt1 = true,
                        columnsStartAt1 = true,
                        pathFormat = "path",
                    },
                    RequestTimeout,
                    token
                )
                .ConfigureAwait(false);

            Task launchOrAttach = launch
                ? dap.RequestAsync(
                    "launch",
                    new
                    {
                        program = launchProgram,
                        cwd = launchCwd,
                        args = launchArgs ?? Array.Empty<string>(),
                        stopAtEntry = false,
                    },
                    RequestTimeout,
                    token
                )
                : dap.RequestAsync("attach", new { processId = pid }, RequestTimeout, token);

            await initializedTcs.Task.WaitAsync(RequestTimeout, token).ConfigureAwait(false);

            await SendAllBreakpointsAsync(token).ConfigureAwait(false);
            await dap.RequestAsync("configurationDone", null, RequestTimeout, token)
                .ConfigureAwait(false);
            await launchOrAttach.ConfigureAwait(false);

            lock (gate)
            {
                if (State == DebugSessionState.Connecting)
                {
                    State = DebugSessionState.Running;
                }
            }

            if (!launch && pid is not null)
            {
                DebuggeePid = pid;
            }

            log.Add("dap", LogKind.Success, launch ? "已在除錯器下啟動目標" : $"已附加到 PID {pid}");
            StateChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            State = DebugSessionState.Failed;
            LastError = ex.Message;
            log.Add("dap", LogKind.Error, $"除錯交握失敗: {ex.Message}");
            await TeardownAsync(terminateDebuggee: launch, CancellationToken.None).ConfigureAwait(false);
            StateChanged?.Invoke();
            return false;
        }
        finally
        {
            dap.EventReceived -= OnInitialized;
        }
    }

    // pump 是跨連線週期重用的單一背景幫浦（見類別註解），pump ??= 這個賦值本身也要在 gate
    // 底下做，理由跟 ConnectAsync 的原子化一樣：不能讓兩個並發呼叫都通過 null 檢查各建一個。
    private void EnsurePump()
    {
        lock (gate)
        {
            pump ??= Task.Run(() => PumpLoopAsync(cts.Token));
        }
    }

    private async Task PumpLoopAsync(CancellationToken token)
    {
        await foreach (Func<CancellationToken, Task> work in workQueue.Reader.ReadAllAsync(token))
        {
            try
            {
                await work(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 階段結束或請求逾時，略過
            }
            catch (Exception ex)
            {
                // 單一工作項目失敗不能讓幫浦停擺（同 ProcessMonitor.cs:135 的紀律）
                LastError = ex.Message;
                log.Add("dap", LogKind.Error, $"除錯工作失敗: {ex.Message}");
            }
        }
    }

    private void Enqueue(Func<CancellationToken, Task> work) => workQueue.Writer.TryWrite(work);

    private void OnEvent(DapEvent evt)
    {
        switch (evt.Name)
        {
            case "process":
                if ((int?)evt.Body?["systemProcessId"] is { } sysPid)
                {
                    DebuggeePid = sysPid;
                    ProcessStarted?.Invoke(sysPid);
                }

                break;

            case "breakpoint":
                UpdateBreakpointFromEvent(evt.Body);
                break;

            case "stopped":
                HandleStopped(evt.Body);
                break;

            case "continued":
                lock (gate)
                {
                    State = DebugSessionState.Running;
                    halted = null;
                }

                StateChanged?.Invoke();
                break;

            case "output":
                string category = (string?)evt.Body?["category"] ?? "stdout";
                string text = (string?)evt.Body?["output"] ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    log.Add(
                        "dap",
                        category == "stderr" ? LogKind.Warning : LogKind.Output,
                        text.TrimEnd()
                    );
                }

                break;

            case "terminated":
            case "exited":
                lock (gate)
                {
                    State = DebugSessionState.Terminated;
                    halted = null;
                }

                log.Add("dap", LogKind.Info, "除錯階段已結束");
                StateChanged?.Invoke();
                break;
        }
    }

    private void HandleAdapterGone()
    {
        lock (gate)
        {
            if (State == DebugSessionState.Terminated)
            {
                return; // 已經是我們主動要求的結束，不必再標記一次
            }

            State = DebugSessionState.Failed;
            LastError = "netcoredbg 中途結束";
            halted = null;
        }

        StateChanged?.Invoke();
    }

    private void HandleStopped(JsonNode? body)
    {
        int threadId = (int?)body?["threadId"] ?? 0;
        string reason = (string?)body?["reason"] ?? "unknown";
        long generation;

        lock (gate)
        {
            State = DebugSessionState.Halted;
            // 真正的新中斷才遞增；同一次中斷後續的監看/框架刷新沿用呼叫端帶來的舊序號
            generation = ++haltGeneration;
        }

        log.Add("dap", LogKind.Info, $"已中斷: {reason}（執行緒 {threadId}）");
        StateChanged?.Invoke();

        // 讀取執行緒上不能直接 await 後續請求，排進幫浦佇列非同步處理
        Enqueue(token => RefreshHaltedStateAsync(threadId, reason, frameIndex: 0, generation, token));
    }

    /// <summary>中斷後的完整流程：stackTrace → scopes → variables（一層） → 逐一 evaluate 監看運算式。</summary>
    private async Task RefreshHaltedStateAsync(
        int threadId,
        string reason,
        int frameIndex,
        long generation,
        CancellationToken token
    )
    {
        DapClient? dap = client;
        if (dap is null)
        {
            return;
        }

        JsonNode? stackBody = await dap.RequestAsync(
                "stackTrace",
                new { threadId, startFrame = 0 },
                RequestTimeout,
                token
            )
            .ConfigureAwait(false);

        var frames = new List<DebugFrame>();
        if (stackBody?["stackFrames"] is JsonArray frameArray)
        {
            foreach (JsonNode? f in frameArray)
            {
                if (f is null)
                {
                    continue;
                }

                frames.Add(
                    new DebugFrame(
                        Id: (int?)f["id"] ?? 0,
                        Name: (string?)f["name"] ?? "?",
                        SourcePath: (string?)f["source"]?["path"],
                        Line: (int?)f["line"]
                    )
                );
            }
        }

        frameIndex = frames.Count == 0 ? 0 : Math.Clamp(frameIndex, 0, frames.Count - 1);
        List<DebugVariable> locals =
            frames.Count > 0
                ? await FetchLocalsAsync(dap, frames[frameIndex].Id, token).ConfigureAwait(false)
                : new List<DebugVariable>();

        List<WatchResult> watchResults = await EvaluateWatchesAsync(
                dap,
                frames.Count > 0 ? frames[frameIndex].Id : (int?)null,
                token
            )
            .ConfigureAwait(false);

        var snapshot = new DebugHaltedState(
            threadId,
            reason,
            frames,
            frameIndex,
            locals,
            watchResults,
            generation
        );
        bool stored;
        lock (gate)
        {
            // 中斷後可能已經被 continue（例如使用者手快）；此時丟掉這份遲到的快照
            stored = State == DebugSessionState.Halted;
            if (stored)
            {
                halted = snapshot;
            }
        }

        if (stored)
        {
            StateChanged?.Invoke();
        }
    }

    private async Task<List<DebugVariable>> FetchLocalsAsync(
        DapClient dap,
        int frameId,
        CancellationToken token
    )
    {
        try
        {
            JsonNode? scopesBody = await dap.RequestAsync(
                    "scopes",
                    new { frameId },
                    RequestTimeout,
                    token
                )
                .ConfigureAwait(false);

            if (scopesBody?["scopes"] is not JsonArray scopes || scopes.Count == 0)
            {
                return new List<DebugVariable>();
            }

            // 一般以第一個非 expensive 的 scope 當作區域變數（多半就是 "Locals"）
            JsonNode scope = scopes.FirstOrDefault(s => (bool?)s?["expensive"] != true) ?? scopes[0]!;
            int variablesReference = (int?)scope["variablesReference"] ?? 0;
            return variablesReference == 0
                ? new List<DebugVariable>()
                : await FetchVariablesAsync(dap, variablesReference, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Add("dap", LogKind.Warning, $"讀取區域變數失敗: {ex.Message}");
            return new List<DebugVariable>();
        }
    }

    private static async Task<List<DebugVariable>> FetchVariablesAsync(
        DapClient dap,
        int variablesReference,
        CancellationToken token
    )
    {
        JsonNode? body = await dap.RequestAsync(
                "variables",
                new { variablesReference },
                RequestTimeout,
                token
            )
            .ConfigureAwait(false);

        var result = new List<DebugVariable>();
        if (body?["variables"] is JsonArray vars)
        {
            // 只展開一層（children on demand），先訂 200 筆上限避免超大集合把畫面塞爆
            foreach (JsonNode? v in vars.Take(200))
            {
                if (v is null)
                {
                    continue;
                }

                result.Add(
                    new DebugVariable(
                        Name: (string?)v["name"] ?? "?",
                        Value: (string?)v["value"] ?? string.Empty,
                        Type: (string?)v["type"] ?? string.Empty,
                        VariablesReference: (int?)v["variablesReference"] ?? 0
                    )
                );
            }
        }

        return result;
    }

    private async Task<List<WatchResult>> EvaluateWatchesAsync(
        DapClient dap,
        int? frameId,
        CancellationToken token
    )
    {
        List<DebugWatch> current;
        lock (gate)
        {
            current = watches;
        }

        var results = new List<WatchResult>(current.Count);
        foreach (DebugWatch watch in current)
        {
            results.Add(await EvaluateOneAsync(dap, watch.Expression, frameId, token).ConfigureAwait(false));
        }

        return results;
    }

    private static async Task<WatchResult> EvaluateOneAsync(
        DapClient dap,
        string expression,
        int? frameId,
        CancellationToken token
    )
    {
        try
        {
            JsonNode? body = await dap.RequestAsync(
                    "evaluate",
                    new { expression, frameId = frameId ?? 0, context = "watch" },
                    EvaluateTimeout,
                    token
                )
                .ConfigureAwait(false);

            return new WatchResult(
                expression,
                (string?)body?["result"],
                (string?)body?["type"],
                TimedOut: false,
                Error: null
            );
        }
        catch (TimeoutException)
        {
            return new WatchResult(expression, null, null, TimedOut: true, Error: null);
        }
        catch (DapRequestException ex)
        {
            return new WatchResult(expression, null, null, TimedOut: false, Error: ex.Message);
        }
        catch (Exception ex)
        {
            return new WatchResult(expression, null, null, TimedOut: false, Error: ex.Message);
        }
    }

    /// <summary>新增中斷點；冪等（同檔案同行已存在就視為成功）。已連線時立即送給 adapter，未連線時只留在清單。</summary>
    public async Task<bool> SetBreakpointAsync(string path, int line, CancellationToken token)
    {
        path = NormalizePath(path);
        lock (gate)
        {
            if (breakpoints.Any(b => PathsEqual(b.Path, path) && b.Line == line))
            {
                return true;
            }

            breakpoints = new List<DebugBreakpoint>(breakpoints) { new DebugBreakpoint(path, line) };
        }

        await EnsureAutoAttachedAsync(token).ConfigureAwait(false);
        await SendBreakpointsForPathAsync(path, token).ConfigureAwait(false);
        return true;
    }

    /// <summary>階段仍是閒置狀態時，用 AutoAttachPidProvider 找目標並自動附加——見該屬性的說明。</summary>
    private async Task EnsureAutoAttachedAsync(CancellationToken token)
    {
        if (State is not (DebugSessionState.Idle or DebugSessionState.Failed or DebugSessionState.Terminated))
        {
            return; // 已經連線中或連線過，不用再自動附加一次
        }

        int? pid = AutoAttachPidProvider?.Invoke();
        if (pid is null)
        {
            return; // 沒有可附加的目標；中斷點仍會留著，之後手動啟動或有目標時再套用
        }

        await ConnectAsync(
                launch: false,
                pid,
                launchProgram: null,
                launchArgs: null,
                launchCwd: null,
                DefaultAdapterPath,
                token
            )
            .ConfigureAwait(false);
    }

    /// <summary>移除中斷點；回傳是否真的有一筆被移除。</summary>
    public async Task<bool> ClearBreakpointAsync(string path, int line, CancellationToken token)
    {
        path = NormalizePath(path);
        bool removed;
        lock (gate)
        {
            var updated = breakpoints.Where(b => !(PathsEqual(b.Path, path) && b.Line == line)).ToList();
            removed = updated.Count != breakpoints.Count;
            breakpoints = updated;
        }

        if (removed)
        {
            await SendBreakpointsForPathAsync(path, token).ConfigureAwait(false);
        }

        return removed;
    }

    // Windows 路徑不分大小寫；斜線方向已經在 NormalizePath 統一過，這裡只需要忽略大小寫
    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把路徑的 / 一律換成 \。實測發現：PDB（Portable PDB）內嵌的原始碼路徑一律是 Windows
    /// 原生的 \，但 Neovim 的絕對路徑、本文件 README 的設定範例都習慣用 /——兩種寫法混用時，
    /// SendBreakpointsForPathAsync 送給 netcoredbg 的 source.path 跟 PDB 對不起來，中斷點會
    /// 永遠停在 pending／unverified，「已中斷」永遠不會發生，卻沒有任何錯誤訊息可看
    /// （netcoredbg 就是回一句「pending, will be resolved when debugging starts」，沒有再更新）。
    /// 在 SetBreakpointAsync／ClearBreakpointAsync／SeedFromConfig 這些寫入點統一正規化，
    /// 才能讓 PathsEqual 的比對與送給 netcoredbg 的路徑都是同一種寫法。
    /// </summary>
    private static string NormalizePath(string path) => path.Replace('/', '\\');

    private async Task SendBreakpointsForPathAsync(string path, CancellationToken token)
    {
        DapClient? dap = client;
        if (
            dap is null
            || State is DebugSessionState.Idle or DebugSessionState.Failed or DebugSessionState.Terminated
        )
        {
            return; // 尚無連線的階段：中斷點留在清單裡，下次連線由 SendAllBreakpointsAsync 一併送出
        }

        List<DebugBreakpoint> forPath;
        lock (gate)
        {
            forPath = breakpoints.Where(b => PathsEqual(b.Path, path)).ToList();
        }

        try
        {
            JsonNode? body = await dap.RequestAsync(
                    "setBreakpoints",
                    new
                    {
                        source = new { path },
                        breakpoints = forPath.Select(b => new { line = b.Line }).ToArray(),
                    },
                    RequestTimeout,
                    token
                )
                .ConfigureAwait(false);

            JsonArray? results = body?["breakpoints"] as JsonArray;

            lock (gate)
            {
                var updated = new List<DebugBreakpoint>(breakpoints);
                for (int i = 0; i < forPath.Count; i++)
                {
                    JsonNode? r = results is not null && i < results.Count ? results[i] : null;
                    int index = updated.FindIndex(b => PathsEqual(b.Path, path) && b.Line == forPath[i].Line);
                    if (index < 0)
                    {
                        continue;
                    }

                    updated[index] = updated[index] with
                    {
                        Verified = (bool?)r?["verified"] ?? false,
                        Message = (string?)r?["message"],
                        AdapterId = (int?)r?["id"],
                    };
                }

                breakpoints = updated;
            }
        }
        catch (Exception ex)
        {
            log.Add("dap", LogKind.Warning, $"設定中斷點失敗（{path}）: {ex.Message}");
        }
    }

    private void UpdateBreakpointFromEvent(JsonNode? body)
    {
        JsonNode? bp = body?["breakpoint"];
        if (bp is null)
        {
            return;
        }

        int? id = (int?)bp["id"];
        if (id is null)
        {
            return;
        }

        lock (gate)
        {
            int index = breakpoints.FindIndex(b => b.AdapterId == id);
            if (index < 0)
            {
                return;
            }

            var updated = new List<DebugBreakpoint>(breakpoints);
            updated[index] = updated[index] with
            {
                Verified = (bool?)bp["verified"] ?? false,
                Message = (string?)bp["message"],
            };
            breakpoints = updated;
        }
    }

    private async Task SendAllBreakpointsAsync(CancellationToken token)
    {
        List<string> paths;
        lock (gate)
        {
            paths = breakpoints.Select(b => b.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        foreach (string path in paths)
        {
            await SendBreakpointsForPathAsync(path, token).ConfigureAwait(false);
        }
    }

    /// <summary>新增監看運算式；已在中斷狀態時立即求值一次。</summary>
    public void AddWatch(string expression)
    {
        lock (gate)
        {
            if (watches.Any(w => w.Expression == expression))
            {
                return;
            }

            watches = new List<DebugWatch>(watches) { new DebugWatch(expression) };
        }

        RefreshWatchesIfHalted();
    }

    public void RemoveWatch(string expression)
    {
        lock (gate)
        {
            watches = watches.Where(w => w.Expression != expression).ToList();
        }

        RefreshWatchesIfHalted();
    }

    private void RefreshWatchesIfHalted()
    {
        DebugHaltedState? current;
        lock (gate)
        {
            current = halted;
        }

        if (current is null)
        {
            return;
        }

        // 監看新增/移除不是新的中斷，沿用同一個 Generation——RunDap 靠這個序號分辨
        // 「同一次中斷的資料更新」與「真正的下一次中斷」
        Enqueue(
            token => RefreshHaltedStateAsync(
                current.ThreadId,
                current.Reason,
                current.SelectedFrameIndex,
                current.Generation,
                token
            )
        );
    }

    /// <summary>切換目前檢視的呼叫堆疊框架；重新擷取該框架的區域變數與監看結果。</summary>
    public void SelectFrame(int frameIndex)
    {
        DebugHaltedState? current;
        lock (gate)
        {
            current = halted;
        }

        if (current is null)
        {
            return;
        }

        // 切換框架同樣不是新的中斷，沿用同一個 Generation（理由同 RefreshWatchesIfHalted）
        Enqueue(
            token => RefreshHaltedStateAsync(current.ThreadId, current.Reason, frameIndex, current.Generation, token)
        );
    }

    public Task ContinueAsync(CancellationToken token) => SendControlAsync("continue", token);

    public Task StepOverAsync(CancellationToken token) => SendControlAsync("next", token);

    public Task StepInAsync(CancellationToken token) => SendControlAsync("stepIn", token);

    public Task StepOutAsync(CancellationToken token) => SendControlAsync("stepOut", token);

    public async Task PauseAsync(CancellationToken token)
    {
        DapClient? dap = client;
        if (dap is null)
        {
            return;
        }

        int? threadId;
        lock (gate)
        {
            threadId = halted?.ThreadId;
        }

        try
        {
            // pause 不需要特定執行緒也能送；沒有已知執行緒就傳 0，adapter 會暫停所有執行緒
            await dap.RequestAsync("pause", new { threadId = threadId ?? 0 }, RequestTimeout, token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Add("dap", LogKind.Warning, $"暫停失敗: {ex.Message}");
        }
    }

    private async Task SendControlAsync(string command, CancellationToken token)
    {
        DapClient? dap = client;
        if (dap is null)
        {
            log.Add("dap", LogKind.Warning, "尚無除錯階段可操作");
            return;
        }

        int threadId;
        lock (gate)
        {
            if (halted is null)
            {
                log.Add("dap", LogKind.Warning, "目前不在中斷狀態");
                return;
            }

            threadId = halted.ThreadId;
        }

        try
        {
            await dap.RequestAsync(command, new { threadId }, RequestTimeout, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Add("dap", LogKind.Warning, $"{command} 失敗: {ex.Message}");
        }
    }

    /// <summary>結束除錯階段：DAP disconnect（依模式決定是否連帶終止目標），再強制砍掉 adapter 子行程。</summary>
    public Task DisconnectAsync(CancellationToken token) =>
        TeardownAsync(terminateDebuggee: launchMode, token);

    // 保護 TeardownAsync 的單次進入：失敗的 ConnectAsync 用 CancellationToken.None 呼叫這個
    // （見上面 catch 區塊），可能跟 Dispose() 自己的收尾同時執行，兩邊都在 client/adapter
    // 被 null 掉之前讀到同一個非 null 的 DapClient，各自呼叫一次 dap.Dispose()——第二次
    // 會丟例外並中斷收尾，包含真正砍掉 netcoredbg 的 proc?.Dispose() 那一行都不會跑到。
    private readonly SemaphoreSlim teardownGate = new(1, 1);

    private async Task TeardownAsync(bool terminateDebuggee, CancellationToken token)
    {
        // 一律用 CancellationToken.None 等鎖：收尾本身不該因為呼叫端的 token 被取消，
        // 就放棄互斥、讓兩個收尾同時跑起來。
        await teardownGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            DapClient? dap = client;
            NetcoredbgProcess? proc = adapter;
            client = null;
            adapter = null;

            // 讀取與清空 client/adapter 現在在同一個鎖底下完成：拿到鎖時兩者皆已是 null，
            // 代表另一邊已經收尾過了，這裡自然變成不做事的空跑（冪等）。
            if (dap is not null)
            {
                try
                {
                    await dap.RequestAsync(
                            "disconnect",
                            new { terminateDebuggee },
                            TimeSpan.FromSeconds(3),
                            token
                        )
                        .ConfigureAwait(false);
                }
                catch
                {
                    // 逾時或已斷線都無妨，下面一定會強制砍掉 adapter 行程，目標不會被留下
                }
                finally
                {
                    dap.Dispose();
                }
            }

            proc?.Dispose(); // 一定會嘗試 Kill；不留殘留的 netcoredbg 行程

            lock (gate)
            {
                State = DebugSessionState.Terminated;
                halted = null;
            }

            DebuggeePid = null;
            StateChanged?.Invoke();
        }
        finally
        {
            teardownGate.Release();
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        workQueue.Writer.TryComplete();

        try
        {
            DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // 結束流程不能因為斷線失敗而卡住，下面仍會處理殘留的行程物件
        }

        adapter?.Dispose();
        client?.Dispose();
        cts.Dispose();
    }
}
