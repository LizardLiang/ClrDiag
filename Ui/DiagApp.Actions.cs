using System.Globalization;
using System.Text;
using ClrDiag.Core;

namespace ClrDiag.Ui;

public sealed partial class DiagApp
{
    private void StartBuild()
    {
        if (backgroundWork is not null)
        {
            status = "已有背景工作進行中";
            return;
        }

        busy = $"建置 {buildConfiguration}";
        status = $"建置 {buildConfiguration} 執行中…";
        backgroundWork = Task.Run(async () =>
        {
            BuildResult result = await build
                .BuildAsync(buildConfiguration, cts.Token)
                .ConfigureAwait(false);
            status = result.Success
                ? $"建置成功（{Format.Duration(result.Duration)}，警告 {result.WarningCount}）"
                : $"建置失敗：{result.Errors.Count} 個錯誤（按 4 看完整輸出）";
        });
    }

    private void StartServer()
    {
        if (backgroundWork is not null)
        {
            status = "已有背景工作進行中";
            return;
        }

        busy = "啟動伺服器";
        status = $"啟動開發伺服器（連接埠 {server.Port}）…";
        backgroundWork = Task.Run(async () =>
        {
            int? pid = await server.StartAsync(null, cts.Token).ConfigureAwait(false);
            if (pid is not null)
            {
                monitor.Attach(pid.Value);
                status = $"伺服器已啟動 PID {pid} → {server.Url}";
            }
            else
            {
                status = "伺服器啟動失敗（按 4 看輸出）";
            }
        });
    }

    private void StopServer()
    {
        if (backgroundWork is not null)
        {
            status = "已有背景工作進行中";
            return;
        }

        busy = "停止伺服器";
        backgroundWork = Task.Run(async () =>
        {
            await server.StopAsync(cts.Token).ConfigureAwait(false);
            monitor.Attach(null);
            status = "伺服器已停止";
        });
    }

    /// <summary>完整的日常開發迴圈：停止 → 建置 → 啟動，並保留監看歷史。</summary>
    private void RestartWithBuild()
    {
        if (backgroundWork is not null)
        {
            status = "已有背景工作進行中";
            return;
        }

        busy = "重建並重啟";
        backgroundWork = Task.Run(async () =>
        {
            if (server.ServerPid is not null)
            {
                status = "停止伺服器…";
                await server.StopAsync(cts.Token).ConfigureAwait(false);
                monitor.Attach(null);
            }

            status = $"建置 {buildConfiguration}…";
            BuildResult result = await build
                .BuildAsync(buildConfiguration, cts.Token)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                status = $"建置失敗，未重啟伺服器（{result.Errors.Count} 個錯誤）";
                return;
            }

            status = "啟動伺服器…";
            int? pid = await server.StartAsync(null, cts.Token).ConfigureAwait(false);
            if (pid is not null)
            {
                monitor.Attach(pid.Value);
                status = $"重建並重啟完成 PID {pid}（建置 {Format.Duration(result.Duration)}）";
            }
            else
            {
                status = "建置成功但伺服器啟動失敗（按 4 看輸出）";
            }
        });
    }

    private void StartSnapshot(bool includeTypes, string reason)
    {
        if (monitor.TargetPid is not { } pid)
        {
            status = "沒有可快照的目標行程";
            return;
        }

        if (snapshots.IsBusy || backgroundWork is not null)
        {
            status = "已有背景工作進行中";
            return;
        }

        busy = includeTypes ? $"{reason}快照（走訪受控堆疊）" : "更新執行緒堆疊";
        status = includeTypes ? $"{reason}快照中…大型堆疊約需數秒" : "讀取執行緒堆疊中…";

        backgroundWork = Task.Run(() =>
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

        if (backgroundWork is not null)
        {
            status = "已有背景工作進行中";
            return;
        }

        string typeName = rows[Math.Clamp(heapCursor, 0, rows.Count - 1)].TypeName;
        rootPathsType = typeName;
        busy = "搜尋根參考鏈";
        status = $"從 GC 根搜尋 {Format.ShortType(typeName, 40)} 的參考鏈（上限 30 秒）…";

        backgroundWork = Task.Run(() =>
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
    }

    /// <summary>在可監看的受控行程間切換目標（依設定的行程名稱，未設定時列出全部受控行程）。</summary>
    private void CycleTargetProcess()
    {
        List<ManagedProcessInfo> candidates = ManagedProcessFinder.List(config.ProcessNames);

        if (candidates.Count == 0 && config.ProcessNames.Length > 0)
        {
            candidates = ManagedProcessFinder.List(Array.Empty<string>());
        }

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
