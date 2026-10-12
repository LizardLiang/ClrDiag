using System.Globalization;
using System.Text;
using ClrDiag.Core;
using ClrDiag.Core.Dap;

namespace ClrDiag.Ui;

public sealed partial class DiagApp
{
    /// <summary>
    /// 在 workGate 內檢查並指派背景工作：按鍵（UI 執行緒）與管道指令（管道執行緒）
    /// 都經過這裡，同一時間最多只有一個背景工作，不會同時跑兩次建置。
    /// </summary>
    private bool TryBeginWork(string busyText, Func<Task> work) =>
        TryBeginTask(busyText, () => Task.Run(work));

    private bool TryBeginWork(string busyText, Action work) =>
        TryBeginTask(busyText, () => Task.Run(work));

    private bool TryBeginTask(string busyText, Func<Task> start)
    {
        lock (workGate)
        {
            if (backgroundWork is { IsCompleted: false })
            {
                return false;
            }

            busy = busyText;
            backgroundWork = start();
            return true;
        }
    }

    /// <summary>
    /// 伺服器控制流程（s／x／r 鍵與管道的 start／stop／restart 共用）的進入點：同樣由 workGate
    /// 保證只有一個背景工作。流程結束時，最後一則訊息成為狀態列文字，結果記入 lastServerOutcome。
    /// </summary>
    private ServerCommandTicket BeginServerWork(
        string command,
        string busyText,
        string phase,
        Func<Task<ServerCommandOutcome>> flow
    )
    {
        lock (workGate)
        {
            if (backgroundWork is { IsCompleted: false })
            {
                return new ServerCommandTicket(false, $"已有背景工作進行中：{busy}", null);
            }

            busy = busyText;
            serverPhase = phase;
            lastServerOutcome = null;
            Task<ServerCommandOutcome> completion = Task.Run(async () =>
            {
                ServerCommandOutcome outcome;
                try
                {
                    outcome = await flow().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.Add("serve", LogKind.Error, ex.ToString());
                    outcome = ServerOutcome(
                        command,
                        false,
                        ServerPhases.StartFailed,
                        $"{command} 執行失敗：{ex.Message}"
                    );
                }

                status = outcome.Message;
                lastServerOutcome = outcome;
                serverPhase = null;
                return outcome;
            });
            backgroundWork = completion;
            return new ServerCommandTicket(true, busyText, completion);
        }
    }

    private static ServerCommandOutcome ServerOutcome(
        string command,
        bool ok,
        string phase,
        string message,
        int? pid = null,
        IReadOnlyList<string>? errors = null
    ) => new(command, ok, phase, message, pid, errors ?? Array.Empty<string>());

    ServerCommandTicket IServerControl.Start() => StartServer();

    ServerCommandTicket IServerControl.Stop() => StopServer();

    ServerCommandTicket IServerControl.Restart() => RestartWithBuild();

    ServerControlSnapshot IServerControl.Snapshot()
    {
        string? transientPhase = serverPhase;
        ServerCommandOutcome? last = lastServerOutcome;
        string? busyText;
        lock (workGate)
        {
            busyText = backgroundWork is { IsCompleted: false } ? busy : null;
        }

        ServerState state = server.State;
        bool alive = state is ServerState.Running or ServerState.External or ServerState.Debug;
        string phase =
            transientPhase
            ?? state switch
            {
                ServerState.Starting => ServerPhases.Starting,
                ServerState.Stopping => ServerPhases.Stopping,
                _ when alive => ServerPhases.Running,
                _ => last?.Phase is ServerPhases.BuildFailed or ServerPhases.StartFailed
                    ? last.Phase
                    : ServerPhases.Stopped,
            };

        return new ServerControlSnapshot(
            phase,
            alive ? server.ServerPid : null,
            alive ? server.Url : null,
            busyText,
            last
        );
    }

    private void StartBuild()
    {
        bool started = TryBeginWork(
            $"建置 {buildConfiguration}",
            async () =>
            {
                status = $"建置 {buildConfiguration} 執行中…";
                BuildResult result = await build
                    .BuildAsync(buildConfiguration, cts.Token)
                    .ConfigureAwait(false);
                status = result.Success
                    ? $"建置成功（{Format.Duration(result.Duration)}，警告 {result.WarningCount}）"
                    : $"建置失敗：{result.Errors.Count} 個錯誤（按 6 看完整記錄）";
            }
        );
        if (!started)
        {
            status = "已有背景工作進行中";
        }
    }

    /// <summary>按鍵被拒絕時（已有背景工作、缺 serveCommand）把原因顯示在狀態列。</summary>
    private void ShowRefusal(ServerCommandTicket ticket)
    {
        if (!ticket.Accepted)
        {
            status = ticket.Message;
        }
    }

    private ServerCommandTicket StartServer()
    {
        if (debugArmed)
        {
            return StartServerUnderDebugger();
        }

        return BeginServerWork(
            ServerCommands.Start,
            "啟動伺服器",
            ServerPhases.Starting,
            async () =>
            {
                status = $"啟動開發伺服器（連接埠 {server.Port}）…";
                return await StartServerProcessAsync(
                        ServerCommands.Start,
                        pid => $"伺服器已啟動 PID {pid} → {server.Url}",
                        "伺服器啟動已取消",
                        "伺服器啟動失敗"
                    )
                    .ConfigureAwait(false);
            }
        );
    }

    /// <summary>
    /// 一般（非除錯）啟動並接上監看；s 鍵與 r 鍵的最後一步共用，差別只在三則訊息。
    /// 啟動失敗時 failurePrefix 後面接 LastStartError，沒有原因時接「按 6 看記錄」。
    /// </summary>
    private async Task<ServerCommandOutcome> StartServerProcessAsync(
        string command,
        Func<int, string> okMessage,
        string cancelledMessage,
        string failurePrefix
    )
    {
        int? pid = await server.StartAsync(null, cts.Token, text => status = text).ConfigureAwait(false);
        if (pid is not null)
        {
            monitor.Attach(pid.Value);
            return ServerOutcome(command, true, ServerPhases.Running, okMessage(pid.Value), pid);
        }

        if (server.LastFailure == ServerFailure.StartCancelled)
        {
            return ServerOutcome(command, false, ServerPhases.Stopped, cancelledMessage);
        }

        string message = server.LastStartError is { } reason
            ? $"{failurePrefix}：{reason}"
            : $"{failurePrefix}（按 6 看記錄）";
        return ServerOutcome(command, false, ServerPhases.StartFailed, message);
    }

    /// <summary>Shift+S 準備／取消「下次啟動走除錯器」；s 與 r 讀這個旗標決定路徑。</summary>
    private void ToggleDebugArm()
    {
        debugArmed = !debugArmed;
        status = debugArmed
            ? "已準備：下次按 s 或 r 會在除錯器下啟動（再按 Shift+S 取消）"
            : "已取消除錯器啟動準備";
    }

    /// <summary>
    /// 啟動模式的除錯：serveCommand/serveArguments/port 原樣重用，不走 ServerService.StartAsync
    /// 的一般啟動路徑，這樣才能命中啟動路徑上的中斷點（attach 搆不到）。實際走哪條路由
    /// LaunchServerUnderDebuggerAsync 依 serveCommand 是不是 wrapper 決定：
    /// 直接對 serveCommand 送 DAP launch，或啟動 wrapper 再 attach 到它的子行程。
    /// 目標行程的 PID 由 DapSessionService 的 process 事件回填（見 OnDebuggeeProcessStarted）
    /// 或（wrapper 路徑）ServerService.AdoptDebuggee 直接設定，不像一般啟動要輪詢等新行程出現。
    /// </summary>
    private ServerCommandTicket StartServerUnderDebugger()
    {
        if (!config.CanServe)
        {
            return new ServerCommandTicket(
                false,
                $"設定檔未指定 {DiagConfig.FileName} 的 serveCommand，無法在除錯器下啟動",
                null
            );
        }

        ServerCommandTicket ticket = BeginServerWork(
            ServerCommands.Start,
            "啟動伺服器（除錯模式）",
            ServerPhases.Starting,
            async () =>
            {
                status = "在除錯器下啟動開發伺服器…";
                (bool ok, string message) = await LaunchServerUnderDebuggerAsync().ConfigureAwait(false);
                return ServerOutcome(
                    ServerCommands.Start,
                    ok,
                    ok ? ServerPhases.Running : ServerPhases.StartFailed,
                    message,
                    server.ServerPid
                );
            }
        );
        if (ticket.Accepted)
        {
            debugArmed = false;
        }

        return ticket;
    }

    /// <summary>
    /// 實際執行「在除錯器下啟動伺服器」，StartServerUnderDebugger 與 RestartWithBuild 共用。
    /// serveCommand 若是 `dotnet run` 這類 wrapper（DiagConfig.IsWrapperServeCommand），直接對
    /// wrapper 送 DAP launch 只會附加到 wrapper 本身，中斷點永遠不會命中——改走
    /// ServerService.StartUnderDebuggerAsync（啟動 wrapper → 找子行程 → attach，見該方法說明）。
    /// 不是 wrapper（例如已經指向建置好的組件本身）維持原本直接 DAP launch 的路徑。
    /// </summary>
    private async Task<(bool Ok, string Message)> LaunchServerUnderDebuggerAsync()
    {
        if (config.IsWrapperServeCommand)
        {
            int? pid = await server
                .StartUnderDebuggerAsync(dap, config.DapAdapterPath, cts.Token)
                .ConfigureAwait(false);
            return pid is not null
                ? (true, $"已在除錯器下啟動伺服器 PID {pid}（等待中斷點，按 8 看偵錯分頁）")
                : (false, "除錯啟動失敗（按 6 看記錄）");
        }

        string program = config.Expand(config.ServeCommand!, port: server.Port);
        string[] args = (config.ServeArguments ?? Array.Empty<string>())
            .Select(a => config.Expand(a, port: server.Port))
            .ToArray();

        bool ok = await dap.LaunchAsync(program, args, config.Root, config.DapAdapterPath, cts.Token)
            .ConfigureAwait(false);
        return ok
            ? (true, "已在除錯器下啟動伺服器（等待中斷點，按 8 看偵錯分頁）")
            : (false, $"除錯啟動失敗: {dap.LastError}（按 6 看記錄）");
    }

    private ServerCommandTicket StopServer()
    {
        if (server.State == ServerState.Starting && server.CancelStart())
        {
            status = "正在取消伺服器啟動…";
            return new ServerCommandTicket(true, status, null);
        }

        // 由除錯器啟動的伺服器要走 DAP terminate，直接 taskkill 會留下沒人善後的 netcoredbg
        if (dap.IsConnected && dap.IsLaunchMode)
        {
            return BeginServerWork(
                ServerCommands.Stop,
                "停止伺服器（除錯模式）",
                ServerPhases.Stopping,
                async () =>
                {
                    await dap.DisconnectAsync(cts.Token).ConfigureAwait(false);
                    // 結束本工具啟動的行程（除錯啟動的 wrapper，或 s 啟動的伺服器）；沒有啟動過行程時略過。
                    // 除錯目標的子行程已由上面的 disconnect 連帶終止。
                    server.CleanupStartedProcess();
                    monitor.Attach(null);
                    return ServerOutcome(
                        ServerCommands.Stop,
                        true,
                        ServerPhases.Stopped,
                        "已透過除錯器停止伺服器"
                    );
                }
            );
        }

        return BeginServerWork(
            ServerCommands.Stop,
            "停止伺服器",
            ServerPhases.Stopping,
            async () =>
            {
                bool stopped = await server.StopAsync(cts.Token).ConfigureAwait(false);
                if (!stopped)
                {
                    return ServerOutcome(
                        ServerCommands.Stop,
                        false,
                        ServerPhases.Running,
                        "伺服器未能停止，已保留監看（按 6 看記錄）",
                        server.ServerPid
                    );
                }

                monitor.Attach(null);
                return ServerOutcome(ServerCommands.Stop, true, ServerPhases.Stopped, "伺服器已停止");
            }
        );
    }

    /// <summary>
    /// 完整的日常開發迴圈：停止 → 建置 → 啟動，並保留監看歷史。
    /// debugArmed 時，啟動那一步跟 s 鍵一樣改走 DAP launch（見 StartServerUnderDebugger）；
    /// 已經在除錯器下執行時，停止那一步改走 DAP disconnect，不直接 taskkill。
    /// </summary>
    private ServerCommandTicket RestartWithBuild()
    {
        bool launchUnderDebugger = debugArmed;

        ServerCommandTicket ticket = BeginServerWork(
            ServerCommands.Restart,
            "重建並重啟",
            ServerPhases.Stopping,
            async () =>
            {
                if (dap.IsConnected && dap.IsLaunchMode)
                {
                    status = "停止除錯階段…";
                    await dap.DisconnectAsync(cts.Token).ConfigureAwait(false);
                    server.CleanupStartedProcess(); // 結束本工具啟動的行程；沒有啟動過行程時略過
                    monitor.Attach(null);
                }
                else if (server.ServerPid is not null || server.HasStartedProcess)
                {
                    status = "停止伺服器…";
                    bool stopped = await server.StopAsync(cts.Token).ConfigureAwait(false);
                    if (!stopped)
                    {
                        return ServerOutcome(
                            ServerCommands.Restart,
                            false,
                            ServerPhases.Running,
                            "伺服器未能停止，已取消重建與重啟（按 6 看記錄）",
                            server.ServerPid
                        );
                    }

                    monitor.Attach(null);
                }

                serverPhase = ServerPhases.Building;
                status = $"建置 {buildConfiguration}…";
                BuildResult result = await build
                    .BuildAsync(buildConfiguration, cts.Token)
                    .ConfigureAwait(false);
                if (!result.Success)
                {
                    return ServerOutcome(
                        ServerCommands.Restart,
                        false,
                        ServerPhases.BuildFailed,
                        $"建置失敗，未重啟伺服器（{result.Errors.Count} 個錯誤）",
                        errors: result.Errors.Select(e => e.ToString()).Take(20).ToArray()
                    );
                }

                serverPhase = ServerPhases.Starting;
                if (launchUnderDebugger)
                {
                    if (!config.CanServe)
                    {
                        return ServerOutcome(
                            ServerCommands.Restart,
                            false,
                            ServerPhases.StartFailed,
                            "建置成功，但未設定 serveCommand 無法在除錯器下啟動"
                        );
                    }

                    status = "在除錯器下啟動伺服器…";
                    (bool debugOk, string debugMessage) = await LaunchServerUnderDebuggerAsync()
                        .ConfigureAwait(false);
                    return ServerOutcome(
                        ServerCommands.Restart,
                        debugOk,
                        debugOk ? ServerPhases.Running : ServerPhases.StartFailed,
                        debugOk
                            ? $"重建並在除錯器下重啟完成（建置 {Format.Duration(result.Duration)}）"
                            : $"建置成功但{debugMessage}",
                        server.ServerPid
                    );
                }

                status = "啟動伺服器…";
                return await StartServerProcessAsync(
                        ServerCommands.Restart,
                        pid => $"重建並重啟完成 PID {pid}（建置 {Format.Duration(result.Duration)}）",
                        "建置成功，伺服器啟動已取消",
                        "建置成功但伺服器啟動失敗"
                    )
                    .ConfigureAwait(false);
            }
        );
        if (ticket.Accepted)
        {
            debugArmed = false;
        }

        return ticket;
    }

    private void StartSnapshot(bool includeTypes, string reason)
    {
        if (monitor.TargetPid is not { } pid)
        {
            status = "沒有可快照的目標行程";
            return;
        }

        if (snapshots.IsBusy || backgroundWork is { IsCompleted: false })
        {
            status = "已有背景工作進行中";
            return;
        }

        status = includeTypes ? $"{reason}快照中…大型堆疊約需數秒" : "讀取執行緒堆疊中…";

        bool started = TryBeginWork(includeTypes ? $"{reason}快照（走訪受控堆疊）" : "更新執行緒堆疊", () =>
        {
            try
            {
                DiagSnapshot snapshot = snapshots.Capture(
                    pid,
                    includeTypes,
                    includeThreads: true,
                    cts.Token
                );
                threadsSnapshot = snapshot;

                if (includeTypes)
                {
                    // 複製後換掉整個清單，避免 UI 執行緒讀到改動中的集合（最多保留 6 份快照）
                    var updated = new List<DiagSnapshot>(heapSnapshots) { snapshot };
                    if (updated.Count > 6)
                    {
                        updated.RemoveAt(0);
                        if (baselineIndex > 0)
                        {
                            baselineIndex--;
                        }
                        else if (baselineIndex == 0)
                        {
                            baselineIndex = -1;
                        }
                    }

                    heapSnapshots = updated;
                    heapCursor = 0;
                    rootPaths = null;
                    status =
                        $"快照 {snapshot.Label}：{snapshot.ObjectCount:N0} 物件 / {snapshot.TotalSizeMb:N1} MB（{Format.Duration(snapshot.Duration)}）";
                    if (snapshot.WalkWarning is { } warning)
                    {
                        status += $" ⚠ {warning}";
                        log.Add("snap", LogKind.Warning, warning);
                    }
                    if (view == DiagView.Memory)
                    {
                        view = DiagView.Heap;
                    }
                }
                else
                {
                    status =
                        $"執行緒堆疊已更新（{snapshot.Threads.Count} 條，{Format.Duration(snapshot.Duration)}）";
                }
            }
            catch (OperationCanceledException)
            {
                status = "快照已取消";
            }
            catch (Exception ex)
            {
                status = $"快照失敗: {ex.Message}";
                log.Add("snap", LogKind.Error, ex.ToString());
            }
        });

        if (!started)
        {
            status = "已有背景工作進行中";
        }
    }

    private void SetBaseline(bool clear)
    {
        if (clear)
        {
            baselineIndex = -1;
            status = "已清除比較基準";
            return;
        }

        List<DiagSnapshot> all = heapSnapshots;
        if (all.Count == 0)
        {
            status = "尚無快照可作為基準";
            return;
        }

        baselineIndex = all.Count - 1;
        status = $"已將 {all[baselineIndex].Label} 設為比較基準，下一次快照會顯示成長量";
    }

    /// <summary>對選取的型別搜尋 GC 根參考鏈，找出是誰讓物件無法被回收。</summary>
    private void FindRootPaths()
    {
        if (monitor.TargetPid is not { } pid)
        {
            status = "沒有可分析的目標行程";
            return;
        }

        List<HeapTypeDelta> rows = CurrentHeapRows();
        if (rows.Count == 0)
        {
            status = "請先按 n 取得快照";
            return;
        }

        if (backgroundWork is { IsCompleted: false })
        {
            status = "已有背景工作進行中";
            return;
        }

        string typeName = rows[Math.Clamp(heapCursor, 0, rows.Count - 1)].TypeName;
        rootPathsType = typeName;
        status = $"從 GC 根搜尋 {Format.ShortType(typeName, 40)} 的參考鏈（上限 30 秒）…";

        bool started = TryBeginWork("搜尋根參考鏈", () =>
        {
            try
            {
                List<RootPath> found = snapshots.FindRootPaths(
                    pid,
                    typeName,
                    maxPaths: 5,
                    budget: TimeSpan.FromSeconds(30),
                    cts.Token
                );

                rootPaths = found;
                view = DiagView.Heap;
                status = $"找到 {found.Count} 條參考鏈（顯示於下方）";
            }
            catch (Exception ex)
            {
                status = $"根參考搜尋失敗: {ex.Message}";
                log.Add("roots", LogKind.Error, ex.ToString());
            }
        });

        if (!started)
        {
            status = "已有背景工作進行中";
        }
    }

    /// <summary>在可監看的受控行程間切換目標（依設定的行程名稱，未設定時列出全部受控行程）。</summary>
    private void CycleTargetProcess()
    {
        List<ManagedProcessInfo> candidates = ManagedProcessFinder.ListForPicking(config.ProcessNames, out _);

        if (candidates.Count == 0)
        {
            status = "找不到任何載入 CLR 的行程";
            return;
        }

        int index = monitor.TargetPid is { } current
            ? candidates.FindIndex(c => c.Pid == current)
            : -1;

        ManagedProcessInfo next = candidates[(index + 1) % candidates.Count];
        monitor.Attach(next.Pid);
        status =
            $"已切換監看 {next.Name} PID {next.Pid}（{next.Runtime}，共 {candidates.Count} 個可選）";
    }

    /// <summary>把目前的堆疊表格（含差異）匯出成 CSV，方便貼進報告或追蹤長期成長。</summary>
    private void ExportReport()
    {
        List<DiagSnapshot> all = heapSnapshots;
        if (all.Count == 0)
        {
            status = "尚無快照可匯出";
            return;
        }

        try
        {
            DiagSnapshot current = all[^1];
            DiagSnapshot? baseline =
                baselineIndex >= 0 && baselineIndex < all.Count ? all[baselineIndex] : null;

            string file = HeapReportWriter.Write(
                config.ReportDirectoryFullPath,
                current,
                baseline,
                CurrentHeapRows(),
                monitor.TargetPid
            );
            status = $"已匯出 {Path.GetFileName(file)}";
            log.Add("diag", LogKind.Success, $"匯出報告: {file}");
        }
        catch (Exception ex)
        {
            status = $"匯出失敗: {ex.Message}";
        }
    }

    /// <summary>目前檢視要顯示的堆疊列（套用過濾與排序，必要時計算與基準的差異）。</summary>
    private List<HeapTypeDelta> CurrentHeapRows()
    {
        // 先取得清單參考再操作：背景執行緒可能在中途換掉 heapSnapshots
        List<DiagSnapshot> all = heapSnapshots;
        if (all.Count == 0)
        {
            return new List<HeapTypeDelta>();
        }

        DiagSnapshot current = all[^1];
        DiagSnapshot? baseline =
            baselineIndex >= 0 && baselineIndex < all.Count ? all[baselineIndex] : null;

        if (ReferenceEquals(baseline, current))
        {
            baseline = null;
        }

        IEnumerable<HeapTypeDelta> rows = HeapSnapshotService.Diff(current, baseline);

        if (!string.IsNullOrWhiteSpace(filter))
        {
            rows = rows.Where(r => r.TypeName.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        return heapSort switch
        {
            HeapSort.Count => rows.OrderByDescending(r => r.Count).ToList(),
            HeapSort.SizeDelta => rows.OrderByDescending(r => r.SizeDelta).ToList(),
            HeapSort.CountDelta => rows.OrderByDescending(r => r.CountDelta).ToList(),
            _ => rows.OrderByDescending(r => r.TotalSize).ToList(),
        };
    }

    private List<ManagedThreadInfo> CurrentThreads()
    {
        if (threadsSnapshot is null)
        {
            return new List<ManagedThreadInfo>();
        }

        IEnumerable<ManagedThreadInfo> threads = threadsSnapshot.Threads;

        if (!string.IsNullOrWhiteSpace(filter))
        {
            threads = threads.Where(t =>
                t.TopFrame.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || t.State.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || t.Frames.Any(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase))
            );
        }

        return threads.ToList();
    }
}
