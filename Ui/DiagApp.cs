using ClrDiag.Core;
using ClrDiag.Core.Dap;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ClrDiag.Ui;

public enum DiagView
{
    Memory,
    Heap,
    Threads,
    Log,
    Output,
    Debug,
}

public enum HeapSort
{
    Size,
    Count,
    SizeDelta,
    CountDelta,
}

/// <summary>
/// 主控台儀表板：上方是建置 / 伺服器 / 行程三個狀態面板，中間是可切換的分頁檢視，
/// 最下面是單列的按鍵與狀態列。
/// 所有耗時工作（建置、快照、根參考搜尋）都在背景執行，UI 迴圈只負責繪製與收鍵。
/// </summary>
public sealed partial class DiagApp : IDisposable
{
    private readonly DiagConfig config;
    private readonly LogBuffer log = new();
    private readonly ProcessMonitor monitor = new();
    private readonly HeapSnapshotService snapshots;
    private readonly AppCodeMatcher appCode;
    private readonly BuildService build;
    private readonly ServerService server;
    private readonly DebugOutputListener debugOutput = new();
    private readonly DebugOutputBuffer outputBuffer = new();
    private readonly DapSessionService dap;
    private DebugCommandServer? dapPipe;
    private readonly CancellationTokenSource cts = new();

    // 快照在背景執行緒完成、UI 執行緒讀取，因此採用「複製後整批換掉」的方式：
    // 直接對同一個 List 新增會讓正在繪製的 UI 執行緒讀到改動中的集合而丟例外。
    private List<DiagSnapshot> heapSnapshots = new();
    private DiagSnapshot? threadsSnapshot;
    private int baselineIndex = -1;

    private DiagView view = DiagView.Memory;

    // 面板編號：0–2 是上排的 build / serve / process，3–7 對應中間的五個檢視。
    // 換號＝選取（回到分割版面），同號再按一次＝放大該面板（隱藏上排、佔滿畫面）。
    private const int FirstViewPane = 3;
    private int selectedPane = FirstViewPane;
    private bool zoomed;

    // 放大上排面板時的捲動位移；兩者都是由上往下（0 = 第一列）
    private int buildScroll;
    private int probeScroll;

    private HeapSort heapSort = HeapSort.Size;
    private string filter = string.Empty;
    private bool filterMode;
    private int heapCursor;
    private int threadCursor;
    private int logScroll;
    private int outputScroll;
    private int debugFrameCursor;
    private bool outputAllPids;
    private string buildConfiguration = "Debug";
    private bool autoSnapshot;
    private bool debugArmed;
    private bool watchInputMode;
    private string watchInput = string.Empty;
    private DateTime lastAutoSnapshot = DateTime.MinValue;
    private string status = "就緒";
    private string? busy;
    private List<RootPath>? rootPaths;
    private string? rootPathsType;
    private Task? backgroundWork;
    private DateTime lastProbe = DateTime.MinValue;

    public DiagApp(DiagConfig config, int port)
    {
        this.config = config;
        build = new BuildService(config, log);
        server = new ServerService(config, log, port);
        snapshots = new HeapSnapshotService(config.AppNamespaces);
        appCode = new AppCodeMatcher(config.AppNamespaces);
        buildConfiguration = config.Configurations.FirstOrDefault() ?? "Debug";
        debugOutput.LineReceived += line => outputBuffer.Add(line);

        dap = new DapSessionService(log);
        dap.SeedFromConfig(config.ParsedDapBreakpoints, config.DapWatches);
        dap.DefaultAdapterPath = config.DapAdapterPath;
        // 除錯階段閒置時，送出第一個中斷點會自動附加到目前監看的行程（懶惰啟動，見規劃書
        // 「Spawn timing」假設）；沒有監看目標就先讓中斷點留在清單裡，不強行啟動。
        dap.AutoAttachPidProvider = () => monitor.TargetPid;
        dap.ProcessStarted += OnDebuggeeProcessStarted;
    }

    /// <summary>
    /// 除錯目標的行程 id 出現時（launch 模式的 process 事件，或 attach 目標本身），自動把
    /// ProcessMonitor 切到這個 PID——鎖定決策「Auto-switch to debuggee」：不做還原，
    /// 使用者要換回別的行程就按 p。
    /// </summary>
    private void OnDebuggeeProcessStarted(int pid)
    {
        monitor.Attach(pid);
        server.AdoptDebuggee(pid);
        status = $"除錯目標 PID {pid}（已自動切換監看）";
        log.Add("dap", LogKind.Info, $"自動切換監看至除錯目標 PID {pid}");
    }

    /// <summary>啟動 DBWIN 監聽並把「無法攔截」的原因寫進訊息記錄；其餘功能不受影響。</summary>
    private void StartDebugOutputListener()
    {
        debugOutput.Start();
        if (debugOutput.Unavailable is { } reason)
        {
            log.Add(
                "diag",
                LogKind.Warning,
                $"無法攔截應用程式輸出（OutputDebugString）: {reason}"
            );
        }
    }

    /// <summary>
    /// 具名管道指令通道只在互動模式開；--render/--dap 等批次模式不需要常駐通道。
    /// 設定 dapEnabled:false 可完全關閉除錯功能（不開管道，dap 欄位仍存在但永遠是 Idle）。
    /// </summary>
    private void StartDebugSubsystem()
    {
        if (!config.DapEnabled)
        {
            log.Add("dap", LogKind.Info, "除錯功能已停用（dapEnabled:false）");
            return;
        }

        dapPipe = new DebugCommandServer(dap, log, DebugCommandServer.PipeNameFor(config.Root));
        dapPipe.Start();
    }

    public void Run(int? attachPid)
    {
        monitor.Start();
        StartDebugOutputListener();
        StartDebugSubsystem();

        if (attachPid is not null)
        {
            server.AdoptExisting(attachPid.Value);
            monitor.Attach(attachPid.Value);
        }
        else if (server.FindExistingServer() is { } existing)
        {
            server.AdoptExisting(existing);
            monitor.Attach(existing);
        }
        else
        {
            log.Add(
                "diag",
                LogKind.Info,
                config.CanServe
                    ? "尚未偵測到可監看的受控行程，按 [s] 啟動開發伺服器"
                    : "尚未偵測到可監看的受控行程；自行啟動應用程式後按 [p] 選擇，或以 --pid 指定"
            );
        }

        LogStartupInfo();

        Layout layout = BuildLayout();

        AnsiConsole
            .Live(layout)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Crop)
            .Start(ctx =>
            {
                while (!cts.IsCancellationRequested)
                {
                    Tick();
                    Render(layout);
                    ctx.Refresh();

                    // 100ms 內優先反應按鍵，其餘時間等待下一次繪製
                    // 標準輸入被重新導向時（例如管線或 CI）沒有按鍵可讀，只做定時更新
                    for (int i = 0; i < 5 && !cts.IsCancellationRequested; i++)
                    {
                        if (!Console.IsInputRedirected && Console.KeyAvailable)
                        {
                            HandleKey(Console.ReadKey(intercept: true));
                            break;
                        }

                        Thread.Sleep(100);
                    }
                }
            });
    }

    /// <summary>把設定來源與解析結果寫進訊息記錄，方便判斷工具「以為」自己在哪個專案。</summary>
    private void LogStartupInfo()
    {
        log.Add(
            "diag",
            LogKind.Info,
            $"設定檔: {config.ConfigFile ?? "無（自動偵測）"}   根目錄: {config.Root}"
        );

        log.Add(
            "diag",
            LogKind.Info,
            config.CanBuild
                ? $"建置目標: {config.ResolvedBuildProject}（{(config.BuildCommand ?? (config.IsSdkProject ? "dotnet build" : config.MsBuildPath ?? "MSBuild 未解析"))}）"
                : $"無法建置: {config.BuildToolError}"
        );

        log.Add(
            "diag",
            LogKind.Info,
            config.CanServe
                ? $"啟動指令: {config.ServeCommand} {string.Join(' ', config.ServeArguments ?? Array.Empty<string>())}"
                : "未設定啟動指令，只能附加到既有行程"
        );

        log.Add(
            "diag",
            LogKind.Info,
            $"監看對象: {(config.ProcessNames.Length > 0 ? string.Join(", ", config.ProcessNames) : "所有載入 CLR 的行程")}"
                + $"   自己的命名空間: {(config.AppNamespaces.Length > 0 ? string.Join(", ", config.AppNamespaces) : "未設定（以非框架判斷）")}"
        );
    }

    private static Layout BuildLayout() =>
        new Layout("root").SplitRows(
            new Layout("top")
                .Size(8)
                .SplitColumns(new Layout("build"), new Layout("serve"), new Layout("process")),
            new Layout("body"),
            new Layout("footer").Size(3)
        );

    /// <summary>每次繪製前的狀態維護：偵測伺服器存活、健康探測、自動快照。</summary>
    private void Tick()
    {
        server.Refresh();

        if (server.ServerPid is { } pid && monitor.TargetPid != pid)
        {
            monitor.Attach(pid);
        }
        else if (
            server.ServerPid is null
            && monitor.TargetPid is not null
            && !monitor.IsTargetAlive
        )
        {
            monitor.Attach(null);
        }

        if (server.ServerPid is not null && DateTime.Now - lastProbe > TimeSpan.FromSeconds(5))
        {
            lastProbe = DateTime.Now;
            _ = server.ProbeAsync(cts.Token);
        }

        if (
            autoSnapshot
            && !snapshots.IsBusy
            && server.ServerPid is not null
            && DateTime.Now - lastAutoSnapshot > TimeSpan.FromMinutes(5)
        )
        {
            lastAutoSnapshot = DateTime.Now;
            StartSnapshot(includeTypes: true, reason: "自動");
        }

        if (backgroundWork is { IsCompleted: true })
        {
            backgroundWork = null;
            busy = null;
        }
    }

    private void Render(Layout layout)
    {
        // 放大時整列上排隱藏，空間全部讓給 body；上排面板此時也不必再繪製
        layout["top"].IsVisible = !zoomed;

        if (!zoomed)
        {
            layout["build"].Update(RenderBuildPanel());
            layout["serve"].Update(RenderServePanel());
            layout["process"].Update(RenderProcessPanel());
        }

        // 放大中間檢視不需要另一套繪製函式：檢視本來就依 BodyHeight() 決定高度，
        // 上排隱藏後 BodyHeight() 變大，內容自動填滿。
        layout["body"]
            .Update(
                ShowViewTabs
                    ? new Rows(RenderViewTabs(), RenderBodyView())
                    : RenderZoom(selectedPane)
            );
        layout["footer"].Update(RenderFooter());
    }

    /// <summary>
    /// 分頁列只在主區顯示檢視時出現。放大上排面板（build / serve / process）時主區是那個面板的
    /// 內容，掛一排檢視分頁在上面會指向看不到的東西，所以連同它佔的那一列一起讓出來。
    /// </summary>
    private bool ShowViewTabs => !(zoomed && selectedPane < FirstViewPane);

    private IRenderable RenderBodyView() =>
        view switch
        {
            DiagView.Memory => RenderMemoryView(),
            DiagView.Heap => RenderHeapView(),
            DiagView.Threads => RenderThreadsView(),
            DiagView.Output => RenderOutputView(),
            DiagView.Debug => RenderDebugView(),
            _ => RenderLogView(),
        };

    private int BodyHeight()
    {
        int windowHeight;
        try
        {
            windowHeight = overrideHeight ?? Console.WindowHeight;
        }
        catch
        {
            // 沒有實體主控台（重新導向、CI）時給一個合理預設值
            windowHeight = 40;
        }

        // 扣掉上排（放大時為 0）、footer 三列（一列內容加上下框線）、
        // 分頁列一列（放大上排面板時沒有）、body 面板自己的上下框線
        return Math.Max(6, windowHeight - (zoomed ? 0 : 8) - 3 - (ShowViewTabs ? 1 : 0) - 2);
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        if (watchInputMode)
        {
            HandleWatchInputKey(key);
            return;
        }

        if (filterMode)
        {
            HandleFilterKey(key);
            return;
        }

        // 數字鍵優先於其他按鍵處理，主鍵盤上排與數字鍵盤都吃
        if (PaneDigit(key) is { } digit)
        {
            SelectPane(digit);
            return;
        }

        switch (key.Key)
        {
            case ConsoleKey.Q when key.Modifiers == 0:
                cts.Cancel();
                return;
            case ConsoleKey.Escape:
                // 放大中先還原版面，再按一次才離開
                if (zoomed)
                {
                    zoomed = false;
                    status = "已還原分割版面";
                    return;
                }

                cts.Cancel();
                return;
            case ConsoleKey.UpArrow:
                MoveCursor(-1);
                return;
            case ConsoleKey.DownArrow:
                MoveCursor(1);
                return;
            case ConsoleKey.PageUp:
                MoveCursor(-BodyHeight() / 2);
                return;
            case ConsoleKey.PageDown:
                MoveCursor(BodyHeight() / 2);
                return;
            case ConsoleKey.Home:
                MoveCursor(int.MinValue / 2);
                return;
            case ConsoleKey.End:
                MoveCursor(int.MaxValue / 2);
                return;
            // 執行控制走功能鍵（VS/VS Code 慣用手感），與字元按鍵表分開處理：
            // Console.ReadKey 回報這些鍵時 KeyChar 通常是 '\0'，混進字元表徒增混淆。
            case ConsoleKey.F5:
                _ = dap.ContinueAsync(cts.Token);
                status = "續行";
                return;
            case ConsoleKey.F10:
                _ = dap.StepOverAsync(cts.Token);
                status = "下一步";
                return;
            case ConsoleKey.F11 when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
                _ = dap.StepOutAsync(cts.Token);
                status = "跳出函式";
                return;
            case ConsoleKey.F11:
                _ = dap.StepInAsync(cts.Token);
                status = "進入函式";
                return;
            case ConsoleKey.F6:
                _ = dap.PauseAsync(cts.Token);
                status = "暫停於下一個可中斷點";
                return;
        }

        switch (char.ToLowerInvariant(key.KeyChar))
        {
            case 'm':
                FocusPane(PaneOf(DiagView.Memory));
                break;
            case 'h':
                FocusPane(PaneOf(DiagView.Heap));
                break;
            case 'l':
                FocusPane(PaneOf(DiagView.Log));
                break;
            case 'j':
                MoveCursor(1);
                break;
            case 'k':
                MoveCursor(-1);
                break;
            case 'b':
                StartBuild();
                break;
            case 'c':
                // 可選設定來自設定檔的 configurations，沒設定時就是 Debug / Release
                string[] configurations =
                    config.Configurations.Length > 0
                        ? config.Configurations
                        : new[] { "Debug", "Release" };
                int current = Array.IndexOf(configurations, buildConfiguration);
                buildConfiguration = configurations[(current + 1) % configurations.Length];
                status = $"建置設定切換為 {buildConfiguration}";
                break;
            case 's':
                if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                {
                    ToggleDebugArm();
                }
                else
                {
                    StartServer();
                }

                break;
            case 'x':
                StopServer();
                break;
            case 'w':
                watchInputMode = true;
                status = "輸入監看運算式，Enter 新增（已存在則移除）· Esc 取消";
                break;
            case 'r':
                RestartWithBuild();
                break;
            case 'n':
                StartSnapshot(includeTypes: true, reason: "手動");
                break;
            case 'a':
                autoSnapshot = !autoSnapshot;
                status = autoSnapshot ? "自動快照已開啟（每 5 分鐘）" : "自動快照已關閉";
                break;
            case 'd':
                SetBaseline(clear: key.Modifiers.HasFlag(ConsoleModifiers.Shift));
                break;
            case 'o':
                heapSort = heapSort switch
                {
                    HeapSort.Size => HeapSort.SizeDelta,
                    HeapSort.SizeDelta => HeapSort.Count,
                    HeapSort.Count => HeapSort.CountDelta,
                    _ => HeapSort.Size,
                };
                status = $"排序: {heapSort}";
                break;
            case 'e':
                ExportReport();
                break;
            case 'p':
                CycleTargetProcess();
                break;
            case '/':
                filterMode = true;
                status = "輸入型別關鍵字，Enter 套用 / Esc 取消";
                break;
            case 't':
                if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                {
                    StartSnapshot(includeTypes: false, reason: "執行緒");
                }
                else
                {
                    FocusPane(PaneOf(DiagView.Threads));
                }

                break;
            case 'f':
                FindRootPaths();
                break;
            case 'g':
                outputAllPids = !outputAllPids;
                status = outputAllPids
                    ? "輸出範圍切換為: 全部行程"
                    : "輸出範圍切換為: 只看附加的 PID";
                break;
            case '?':
                FocusPane(PaneOf(DiagView.Log));
                log.Add(
                    "diag",
                    LogKind.Info,
                    "按鍵: 0/1/2 選 build/serve/process 面板 · 3/4/5/6/7/8 切換主區分頁（記憶體/堆疊/執行緒/記錄/輸出/偵錯，分頁列在主區上方） · 同號鍵再按一次放大（Esc 還原） · b 建置 · c 設定 · s 啟動 · Shift+S 準備下次以除錯器啟動 · x 停止 · r 重建並重啟 · n 快照 · T 只更新執行緒 · d 設基準(D 清除) · o 排序 · / 過濾 · f 找根參考 · e 匯出 · a 自動快照 · p 換行程 · g 輸出檢視的 PID 範圍 · w 新增/移除監看運算式 · F5 續行 · F10 下一步 · F11 進入 · Shift+F11 跳出 · F6 暫停 · q 離開"
                );
                break;
        }
    }

    private void HandleFilterKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                filter = string.Empty;
                filterMode = false;
                status = "已清除過濾條件";
                return;
            case ConsoleKey.Enter:
                filterMode = false;
                status = string.IsNullOrEmpty(filter) ? "已清除過濾條件" : $"過濾: {filter}";
                return;
            case ConsoleKey.Backspace:
                if (filter.Length > 0)
                {
                    filter = filter[..^1];
                }

                return;
        }

        if (!char.IsControl(key.KeyChar))
        {
            filter += key.KeyChar;
        }
    }

    /// <summary>新增／移除監看運算式的行內輸入，與 HandleFilterKey 同一種模式（Enter 套用、Esc 取消）。</summary>
    private void HandleWatchInputKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                watchInput = string.Empty;
                watchInputMode = false;
                status = "已取消";
                return;
            case ConsoleKey.Enter:
                watchInputMode = false;
                string expression = watchInput.Trim();
                watchInput = string.Empty;
                if (expression.Length == 0)
                {
                    return;
                }

                // 已存在就移除，不存在就新增——同一個按鍵做雙向切換，不必另外配一顆刪除鍵
                if (dap.Watches.Any(w => w.Expression == expression))
                {
                    dap.RemoveWatch(expression);
                    status = $"已移除監看: {expression}";
                }
                else
                {
                    dap.AddWatch(expression);
                    status = $"已新增監看: {expression}";
                }

                return;
            case ConsoleKey.Backspace:
                if (watchInput.Length > 0)
                {
                    watchInput = watchInput[..^1];
                }

                return;
        }

        if (!char.IsControl(key.KeyChar))
        {
            watchInput += key.KeyChar;
        }
    }

    /// <summary>
    /// 面板編號的按鍵解析。主鍵盤上排（D0–D8）與數字鍵盤（NumPad0–NumPad8）都接受；
    /// 兩者都沒對上時再看字元，涵蓋回報方式不一樣的終端機。只認 0–8，其餘交給後面的按鍵處理。
    /// </summary>
    private static int? PaneDigit(ConsoleKeyInfo key)
    {
        if (key.Modifiers != 0)
        {
            return null;
        }

        return key.Key switch
        {
            >= ConsoleKey.D0 and <= ConsoleKey.D8 => key.Key - ConsoleKey.D0,
            >= ConsoleKey.NumPad0 and <= ConsoleKey.NumPad8 => key.Key - ConsoleKey.NumPad0,
            _ => key.KeyChar is >= '0' and <= '8' ? key.KeyChar - '0' : null,
        };
    }

    private static int PaneOf(DiagView target) => FirstViewPane + (int)target;

    /// <summary>數字鍵：同號再按一次切換放大，換號則選取該面板並回到分割版面。</summary>
    private void SelectPane(int pane)
    {
        if (pane == selectedPane)
        {
            zoomed = !zoomed;
            status = zoomed
                ? $"放大 {PaneName(pane)}（再按 {pane} 或 Esc 還原）"
                : "已還原分割版面";
            return;
        }

        FocusPane(pane);
    }

    /// <summary>選取面板並回到分割版面；字母捷徑（m/h/l/t）與數字鍵共用。</summary>
    private void FocusPane(int pane)
    {
        selectedPane = pane;
        zoomed = false;

        if (pane >= FirstViewPane)
        {
            view = (DiagView)(pane - FirstViewPane);
        }
    }

    private void MoveCursor(int delta)
    {
        // 選到上排面板時，捲動鍵作用在該面板的內容上（放大後才有足夠高度看出差別）
        switch (selectedPane)
        {
            case 0:
                // 錯誤清單由上往下讀，往下捲＝位移變大
                buildScroll = Math.Max(0, buildScroll + delta);
                return;
            case 1:
                // 探測記錄最新的在最下面，與訊息記錄同樣是往上捲看更早的
                probeScroll = Math.Max(0, probeScroll - delta);
                return;
            case 2:
                // process 面板的內容有界，不需要捲動
                return;
        }

        switch (view)
        {
            case DiagView.Heap:
                heapCursor = Math.Max(
                    0,
                    Math.Min(heapCursor + delta, Math.Max(0, CurrentHeapRows().Count - 1))
                );
                break;
            case DiagView.Threads:
                threadCursor = Math.Max(
                    0,
                    Math.Min(threadCursor + delta, Math.Max(0, CurrentThreads().Count - 1))
                );
                break;
            case DiagView.Log:
                logScroll = Math.Max(0, logScroll - delta);
                break;
            case DiagView.Output:
                outputScroll = Math.Max(0, outputScroll - delta);
                break;
            case DiagView.Debug:
                IReadOnlyList<DebugFrame> frames = dap.Halted?.Frames ?? Array.Empty<DebugFrame>();
                if (frames.Count > 0)
                {
                    debugFrameCursor = Math.Max(0, Math.Min(debugFrameCursor + delta, frames.Count - 1));
                    dap.SelectFrame(debugFrameCursor);
                }

                break;
        }
    }

    /// <summary>供 Ctrl+C 處理常式呼叫：走一般的收尾流程（等同按 q），不做任何強制動作。</summary>
    public void RequestExit() => cts.Cancel();

    public void Dispose()
    {
        cts.Cancel();
        dapPipe?.Dispose();
        dap.Dispose();
        // 跟 StopServer／RestartWithBuild 同一個收尾順序：wrapper 啟動（dotnet run 類）才會
        // 留下 wrapper 行程，直接 launch 沒有 wrapper 可清，這裡呼叫也安全。少了這一行，
        // 用 q／Ctrl+C 結束時 wrapper 型 serveCommand 的 dotnet run 行程會被留下孤兒行程。
        server.CleanupDebugWrapper();
        monitor.Dispose();
        server.Dispose();
        debugOutput.Dispose();
        cts.Dispose();
    }
}
