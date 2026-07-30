using ClrDiag.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ClrDiag.Ui;

public sealed partial class DiagApp
{
    private IRenderable RenderBuildPanel()
    {
        BuildResult last = build.Last;
        var rows = new List<string>
        {
            $"設定 [bold]{Format.Esc(buildConfiguration)}[/]   MSBuild {(config.MsBuildPath is null ? "[red]未找到[/]" : "[green]就緒[/]")}",
        };

        if (build.IsRunning)
        {
            rows.Add("[yellow]建置中…[/]");
        }
        else if (ReferenceEquals(last, BuildResult.NotRun))
        {
            rows.Add($"[{Format.Muted}]尚未建置（按 b）[/]");
        }
        else if (last.Success)
        {
            rows.Add($"[green]成功[/] {Format.Duration(last.Duration)}  警告 {last.WarningCount}");
        }
        else
        {
            rows.Add(
                $"[red]失敗[/] {Format.Duration(last.Duration)}  錯誤 {last.Errors.Count}  警告 {last.WarningCount}"
            );
            foreach (BuildDiagnostic error in last.Errors.Take(3))
            {
                rows.Add(
                    $"[red]{Format.Esc(error.Code)}[/] {Format.Esc(Format.ShortType(error.ShortFile, 18))}:{error.Line} {Format.Esc(Truncate(error.Message, 40))}"
                );
            }
        }

        return new Panel(new Markup(string.Join('\n', rows)))
            .Header(" build ")
            .Border(BoxBorder.Rounded)
            .BorderColor(
                build.IsRunning ? Color.Yellow
                : last.Success ? Color.Green
                : Color.Silver
            )
            .Expand();
    }

    private IRenderable RenderServePanel()
    {
        ProbeResult? probe = server.LastProbe;
        ProbeResult[] history = server.ProbeHistory;

        string stateText = server.State switch
        {
            ServerState.Running => "[green]RUNNING[/]",
            ServerState.External => "[aqua]EXTERNAL[/]",
            ServerState.Starting => "[yellow]STARTING[/]",
            ServerState.Stopping => "[yellow]STOPPING[/]",
            _ => $"[{Format.Muted}]STOPPED[/]",
        };

        var rows = new List<string>
        {
            $"{stateText}  :{server.Port}  PID {(server.ServerPid?.ToString() ?? "-")}",
            $"[{Format.Muted}]{Format.Esc(server.Url)}[/]",
        };

        if (probe is { } p)
        {
            string statusText =
                p.Ok ? $"[green]{p.StatusCode}[/]"
                : p.StatusCode > 0 ? $"[red]{p.StatusCode}[/]"
                : "[red]無回應[/]";
            rows.Add($"探測 {statusText} {p.ElapsedMs:N0} ms  {p.TimeStamp:HH:mm:ss}");

            double[] latencies = history.Where(h => h.Ok).Select(h => h.ElapsedMs).ToArray();
            if (latencies.Length >= 3)
            {
                Array.Sort(latencies);
                double p50 = latencies[latencies.Length / 2];
                double p95 = latencies[
                    (int)Math.Min(latencies.Length - 1, latencies.Length * 0.95)
                ];
                rows.Add($"延遲 p50 {p50:N0} ms  p95 {p95:N0} ms  n={latencies.Length}");
            }

            if (p.Error is not null)
            {
                rows.Add($"[red]{Format.Esc(Truncate(p.Error, 40))}[/]");
            }
        }
        else
        {
            rows.Add(
                server.IsPortListening()
                    ? $"[{Format.Muted}]連接埠有人監聽，尚未探測[/]"
                    : $"[{Format.Muted}]未啟動（按 s 啟動 / r 重建並重啟）[/]"
            );
        }

        return new Panel(new Markup(string.Join('\n', rows)))
            .Header(" serve ")
            .Border(BoxBorder.Rounded)
            .BorderColor(server.ServerPid is not null ? Color.Green : Color.Silver)
            .Expand();
    }

    private IRenderable RenderProcessPanel()
    {
        MetricSample? latest = monitor.Latest;
        var rows = new List<string>();

        if (monitor.TargetPid is null || latest is null)
        {
            rows.Add($"[{Format.Muted}]未附加任何行程[/]");
            rows.Add($"[{Format.Muted}]按 s 啟動伺服器，或 p 選擇受控行程[/]");
        }
        else
        {
            MetricSample sample = latest.Value;
            TimeSpan uptime = monitor.TargetStartTime is { } start
                ? DateTime.Now - start
                : TimeSpan.Zero;

            rows.Add(
                $"PID [bold]{monitor.TargetPid}[/] {Format.Esc(monitor.TargetName ?? "")}  已執行 {Format.Duration(uptime)}"
            );
            rows.Add(
                $"私有 [bold]{sample.PrivateMb:N0} MB[/]  工作集 {sample.WorkingSetMb:N0} MB  CPU {sample.CpuPercent:N1}%"
            );
            rows.Add(
                $"受控堆疊 {Format.Mb(sample.AllHeapsMb)}  已提交 {Format.Mb(sample.CommittedMb)}"
            );
            rows.Add(
                $"執行緒 {sample.ThreadCount}  控制代碼 {sample.HandleCount:N0}  %GC {Format.Percent(sample.TimeInGcPercent)}"
            );

            if (monitor.CounterStatus is { } counterError)
            {
                rows.Add($"[yellow]{Format.Esc(Truncate(counterError, 44))}[/]");
            }
        }

        return new Panel(new Markup(string.Join('\n', rows)))
            .Header(" process ")
            .Border(BoxBorder.Rounded)
            .BorderColor(monitor.TargetPid is not null ? Color.Aqua : Color.Silver)
            .Expand();
    }

    private IRenderable RenderMemoryView()
    {
        MetricSample[] history = monitor.History;
        if (history.Length == 0)
        {
            return Placeholder("等待取樣資料…（先按 s 啟動伺服器或 p 選擇受控行程）");
        }

        MetricSample latest = history[^1];
        int sparkWidth = Math.Max(20, ViewWidth - 46);

        var lines = new List<string>
        {
            SparkRow(
                "私有記憶體",
                history.Select(h => h.PrivateMb).ToArray(),
                sparkWidth,
                latest.PrivateMb,
                "MB"
            ),
            SparkRow(
                "工作集",
                history.Select(h => h.WorkingSetMb).ToArray(),
                sparkWidth,
                latest.WorkingSetMb,
                "MB"
            ),
        };

        if (latest.AllHeapsMb is not null)
        {
            lines.Add(
                SparkRow(
                    "受控堆疊",
                    history.Select(h => h.AllHeapsMb ?? 0).ToArray(),
                    sparkWidth,
                    latest.AllHeapsMb.Value,
                    "MB"
                )
            );
            lines.Add(
                SparkRow(
                    "Gen2",
                    history.Select(h => h.Gen2Mb ?? 0).ToArray(),
                    sparkWidth,
                    latest.Gen2Mb ?? 0,
                    "MB"
                )
            );
            lines.Add(
                SparkRow(
                    "LOH",
                    history.Select(h => h.LohMb ?? 0).ToArray(),
                    sparkWidth,
                    latest.LohMb ?? 0,
                    "MB"
                )
            );
        }

        lines.Add(
            SparkRow(
                "CPU",
                history.Select(h => h.CpuPercent).ToArray(),
                sparkWidth,
                latest.CpuPercent,
                "%"
            )
        );

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(3))
            .AddColumn(new GridColumn().NoWrap().PadRight(3))
            .AddColumn(new GridColumn().NoWrap());

        // "Gen 0 heap size" 這個計數器回報的是 gen0 配置預算而非實際存活大小，
        // 標示清楚以免被當成洩漏（Gen1 / Gen2 / LOH 才是實際大小）
        grid.AddRow(
            $"Gen0 預算 {Format.Mb(latest.Gen0Mb)}",
            $"Gen1 {Format.Mb(latest.Gen1Mb)}",
            $"Gen2 {Format.Mb(latest.Gen2Mb)}"
        );
        grid.AddRow(
            $"LOH {Format.Mb(latest.LohMb)}",
            $"已提交 {Format.Mb(latest.CommittedMb)}",
            $"釘選物件 {Format.Number(latest.PinnedObjects)}"
        );
        grid.AddRow(
            $"GC 次數 g0 {Format.Number(latest.Gen0Collections)}",
            $"g1 {Format.Number(latest.Gen1Collections)}",
            $"g2 {Format.Number(latest.Gen2Collections)}"
        );
        grid.AddRow(
            $"例外 {Format.Rate(latest.ExceptionsPerSec)}",
            $"鎖競爭 {Format.Rate(latest.ContentionPerSec)}",
            $"%GC 時間 {Format.Percent(latest.TimeInGcPercent)}"
        );

        var content = new List<IRenderable>
        {
            new Markup(string.Join('\n', lines)),
            new Rule($"[{Format.Muted}]目前值[/]") { Justification = Justify.Left },
            grid,
            new Rule($"[{Format.Muted}]成長量[/]") { Justification = Justify.Left },
            new Markup(BuildGrowthSummary(history)),
        };

        return new Panel(new Rows(content))
            .Header($" 記憶體走勢（取樣 {history.Length} 筆 / 每秒一次） ")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    /// <summary>比較最近 1 / 5 / 15 分鐘的成長量，這是判斷「有沒有漏」最直接的指標。</summary>
    private static string BuildGrowthSummary(MetricSample[] history)
    {
        MetricSample latest = history[^1];
        var parts = new List<string>();

        foreach (
            (string label, int seconds) in new[] { ("1 分", 60), ("5 分", 300), ("15 分", 900) }
        )
        {
            // 取指定秒數內最早的一筆；不足兩筆代表這個區間還沒累積夠資料
            MetricSample[] window = history
                .Where(h => (latest.TimeStamp - h.TimeStamp).TotalSeconds <= seconds)
                .ToArray();

            if (window.Length < 2)
            {
                parts.Add($"{label} [{Format.Muted}]資料不足[/]");
                continue;
            }

            MetricSample past = window[0];
            double privateDelta = latest.PrivateMb - past.PrivateMb;
            double? heapDelta =
                latest.AllHeapsMb is not null && past.AllHeapsMb is not null
                    ? latest.AllHeapsMb - past.AllHeapsMb
                    : null;

            string color =
                privateDelta > 50 ? "red"
                : privateDelta > 10 ? "yellow"
                : "green";
            string heapText = heapDelta is null
                ? string.Empty
                : $" / 堆疊 {heapDelta.Value:+0.0;-0.0;0} MB";
            parts.Add($"{label} [{color}]{privateDelta:+0.0;-0.0;0} MB[/]{heapText}");
        }

        return string.Join("    ", parts);
    }

    private static string SparkRow(
        string label,
        double[] values,
        int width,
        double current,
        string unit
    )
    {
        Format.SparkBand band = Format.Sparkline(values, width);
        return $"{Format.PadRightDisplay(label, 12)}[bold]{current, 9:N1}[/] {unit, -2} [aqua]{band.Chart}[/] [{Format.Muted}]{band.Min:N1}–{band.Max:N1}[/]";
    }

    private IRenderable RenderHeapView()
    {
        List<DiagSnapshot> all = heapSnapshots;
        if (all.Count == 0)
        {
            return Placeholder("尚無快照。按 [bold]n[/] 取得受控堆疊快照（大型站台約需 5–10 秒）");
        }

        DiagSnapshot current = all[^1];
        DiagSnapshot? baseline =
            baselineIndex >= 0 && baselineIndex < all.Count && baselineIndex != all.Count - 1
                ? all[baselineIndex]
                : null;

        List<HeapTypeDelta> rows = CurrentHeapRows();
        heapCursor = Math.Clamp(heapCursor, 0, Math.Max(0, rows.Count - 1));

        int available = BodyHeight() - 6;
        if (rootPaths is not null)
        {
            available -= Math.Min(10, rootPaths.Sum(r => r.Chain.Count + 1) + 1);
        }

        int visibleRows = Math.Max(3, available);
        int firstRow = Math.Max(
            0,
            Math.Min(heapCursor - visibleRows / 2, Math.Max(0, rows.Count - visibleRows))
        );

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Silver).Expand();

        table.AddColumn(new TableColumn($"[{Format.Muted}]大小 MB[/]").RightAligned().Width(10));
        if (baseline is not null)
        {
            table.AddColumn(new TableColumn($"[{Format.Muted}]Δ MB[/]").RightAligned().Width(9));
        }

        table.AddColumn(new TableColumn($"[{Format.Muted}]數量[/]").RightAligned().Width(12));
        if (baseline is not null)
        {
            table.AddColumn(new TableColumn($"[{Format.Muted}]Δ 數量[/]").RightAligned().Width(11));
        }

        table.AddColumn(new TableColumn($"[{Format.Muted}]型別[/]"));

        int nameWidth = Math.Max(20, ViewWidth - (baseline is not null ? 56 : 34));

        for (int i = firstRow; i < Math.Min(rows.Count, firstRow + visibleRows); i++)
        {
            HeapTypeDelta row = rows[i];
            bool selected = i == heapCursor;
            string marker = selected ? "[invert]" : string.Empty;
            string markerEnd = selected ? "[/]" : string.Empty;

            var cells = new List<string> { $"{marker}{Format.MbBytes(row.TotalSize)}{markerEnd}" };

            if (baseline is not null)
            {
                string deltaColor =
                    row.SizeDelta > 1024 * 1024 ? "red"
                    : row.SizeDelta < -1024 * 1024 ? "green"
                    : Format.Muted;
                cells.Add($"[{deltaColor}]{Format.Signed(row.SizeDelta)}[/]");
            }

            cells.Add($"{row.Count:N0}");

            if (baseline is not null)
            {
                string deltaColor =
                    row.CountDelta > 0 ? "red"
                    : row.CountDelta < 0 ? "green"
                    : Format.Muted;
                cells.Add($"[{deltaColor}]{Format.SignedCount(row.CountDelta)}[/]");
            }

            cells.Add(
                $"{marker}{Format.Esc(Format.ShortType(row.TypeName, nameWidth))}{markerEnd}"
            );

            table.AddRow(cells.ToArray());
        }

        var header = new Markup(
            string.Join(
                '\n',
                new[]
                {
                    $"快照 [bold]{current.Label}[/]  {current.ObjectCount:N0} 物件  {current.TotalSizeMb:N1} MB  {current.SegmentCount} 個區段  耗時 {Format.Duration(current.Duration)}  CLR {Format.Esc(current.ClrVersion)}",
                    $"基準 {(baseline is null ? $"[{Format.Muted}]未設定（按 d 設定，再取一次快照即可看成長量）[/]" : $"[aqua]{baseline.Label}[/]")}   排序 [bold]{heapSort}[/]   過濾 {(string.IsNullOrEmpty(filter) ? $"[{Format.Muted}]無[/]" : $"[yellow]{Format.Esc(filter)}[/]")}   共 {rows.Count:N0} 個型別",
                }
            )
        );

        var content = new List<IRenderable> { header, table };

        if (rootPaths is not null)
        {
            content.Add(RenderRootPaths());
        }
        else if (rows.Count > 0)
        {
            content.Add(
                new Markup(
                    $"[{Format.Muted}]選取:[/] {Format.Esc(Format.ShortType(rows[heapCursor].TypeName, ViewWidth - 20))}  [{Format.Muted}](按 f 找出誰握著它)[/]"
                )
            );
        }

        return new Panel(new Rows(content)).Header(" 受控堆疊 ").Border(BoxBorder.Rounded).Expand();
    }

    private IRenderable RenderRootPaths()
    {
        var lines = new List<string>
        {
            $"[bold]根參考鏈[/] → {Format.Esc(Format.ShortType(rootPathsType ?? "", 60))}",
        };

        foreach (RootPath path in rootPaths!)
        {
            lines.Add($"[aqua]{Format.Esc(path.RootDescription)}[/]");
            foreach (string step in path.Chain.Take(8))
            {
                lines.Add(
                    $"  [{Format.Muted}]→[/] {Format.Esc(Format.ShortType(step, ViewWidth - 12))}"
                );
            }
        }

        return new Panel(new Markup(string.Join('\n', lines)))
            .Border(BoxBorder.Heavy)
            .BorderColor(Color.Yellow)
            .Expand();
    }

    private IRenderable RenderThreadsView()
    {
        if (threadsSnapshot is null)
        {
            return Placeholder(
                "尚無執行緒資料。按 [bold]T[/] 只讀執行緒堆疊（快），或 [bold]n[/] 取完整快照"
            );
        }

        List<ManagedThreadInfo> threads = CurrentThreads();
        if (threads.Count == 0)
        {
            return Placeholder("過濾條件沒有符合的執行緒（按 / 修改，Esc 清除）");
        }

        threadCursor = Math.Clamp(threadCursor, 0, threads.Count - 1);
        ManagedThreadInfo selected = threads[threadCursor];

        int listWidth = Math.Max(34, ViewWidth * 2 / 5);
        int visibleRows = Math.Max(3, BodyHeight() - 6);
        int firstRow = Math.Max(
            0,
            Math.Min(threadCursor - visibleRows / 2, Math.Max(0, threads.Count - visibleRows))
        );

        var list = new Table().Border(TableBorder.None).Expand();
        list.AddColumn(new TableColumn($"[{Format.Muted}]OS[/]").Width(7).NoWrap());
        list.AddColumn(new TableColumn($"[{Format.Muted}]狀態[/]").Width(11).NoWrap());
        list.AddColumn(new TableColumn($"[{Format.Muted}]最上層框架[/]").NoWrap());

        // 框架欄可用寬度＝面板寬度 − OS 欄(7) − 狀態欄(11) − 三欄各自的左右內距(6)
        // − 面板框線與內距(4) − 應用程式標記(1)。算寬了會換行成兩列，一頁能看的執行緒就少一半。
        int frameWidth = Math.Max(12, listWidth - 29);

        for (int i = firstRow; i < Math.Min(threads.Count, firstRow + visibleRows); i++)
        {
            ManagedThreadInfo thread = threads[i];
            bool isSelected = i == threadCursor;
            string open = isSelected ? "[invert]" : string.Empty;
            string close = isSelected ? "[/]" : string.Empty;

            string stateColor = thread.PendingException is not null
                ? "red"
                : thread.State switch
                {
                    "lock-wait" => "yellow",
                    "db" => "aqua",
                    "network" => "aqua",
                    "running" => "green",
                    // silver（#c0c0c0）而非 grey（#808080）：仍是低調的中性色，但深色主題下讀得到
                    _ => "silver",
                };

            string marker = thread.IsApplicationThread ? "[bold]●[/]" : " ";

            list.AddRow(
                $"{open}{thread.OsThreadId}{close}",
                $"[{stateColor}]{Format.Esc(thread.State)}[/]",
                $"{marker}{open}{Format.Esc(Format.TailFrame(thread.TopFrame, frameWidth))}{close}"
            );
        }

        var stackLines = new List<string>
        {
            $"OS {selected.OsThreadId}  受控 ID {selected.ManagedThreadId}  {(selected.IsFinalizer ? "finalizer " : string.Empty)}{(selected.IsGcThread ? "gc " : string.Empty)}{(selected.IsAlive ? string.Empty : $"[{Format.Muted}](已結束)[/]")}",
        };

        if (selected.PendingException is { } exception)
        {
            stackLines.Add($"[red]例外: {Format.Esc(Truncate(exception, 120))}[/]");
        }

        stackLines.Add(string.Empty);
        if (selected.Frames.Count == 0)
        {
            stackLines.Add($"[{Format.Muted}](沒有受控框架)[/]");
        }
        else
        {
            int stackHeight = Math.Max(4, BodyHeight() - 8);
            foreach (string frame in selected.Frames.Take(stackHeight))
            {
                bool own = appCode.IsAppFrame(frame);
                string text = Format.Esc(
                    Format.TailFrame(frame, Math.Max(20, ViewWidth - listWidth - 10))
                );

                // 框架用終端機預設前景色而非 grey：grey 在深色主題下幾乎看不見，
                // 而堆疊內容本身就是要讀的正文。自己的程式碼仍以 bold yellow 突顯，對比不靠壓暗其他行。
                stackLines.Add(own ? $"[bold yellow]{text}[/]" : text);
            }
        }

        var grid = new Grid();
        grid.AddColumn(new GridColumn().Width(listWidth));
        grid.AddColumn(new GridColumn());
        grid.AddRow(
            new Panel(list)
                .Header(
                    $" 執行緒 {threads.Count}（● {(snapshots.HasExplicitAppNamespaces ? "為含自己程式碼者" : "為非框架程式碼")}） "
                )
                .Border(BoxBorder.Rounded)
                .Expand(),
            new Panel(new Markup(string.Join('\n', stackLines)))
                .Header(" 呼叫堆疊 ")
                .Border(BoxBorder.Rounded)
                .Expand()
        );

        return new Panel(grid)
            .Header($" 執行緒快照 {threadsSnapshot.TakenAt:HH:mm:ss}（按 T 更新） ")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private IRenderable RenderLogView()
    {
        int visible = Math.Max(5, BodyHeight() - 2);
        LogLine[] all = log.TakeLast(Math.Min(log.Count, 1000));

        int end = Math.Max(0, all.Length - logScroll);
        int start = Math.Max(0, end - visible);

        var lines = new List<string>();
        for (int i = start; i < end; i++)
        {
            LogLine line = all[i];
            string color = line.Kind switch
            {
                LogKind.Error => "red",
                LogKind.Warning => "yellow",
                LogKind.Success => "green",
                LogKind.Info => "aqua",
                _ => Format.Muted,
            };

            lines.Add(
                $"[{Format.Muted}]{line.TimeStamp:HH:mm:ss}[/] [{color}]{line.Source, -5}[/] {Format.Esc(line.Text)}"
            );
        }

        if (lines.Count == 0)
        {
            lines.Add($"[{Format.Muted}](尚無訊息)[/]");
        }

        return new Panel(new Markup(string.Join('\n', lines)))
            .Header(
                $" 訊息記錄（{all.Length} 筆{(logScroll > 0 ? $"，往上捲 {logScroll}" : string.Empty)}） "
            )
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private IRenderable RenderOutputView()
    {
        int visible = Math.Max(5, BodyHeight() - 2);

        // 只看附加 PID 時才真的限定 PID：沒有附加行程時篩選條件不會生效，等同顯示全部行程
        int? attachedPid = !outputAllPids ? monitor.TargetPid : null;
        string activeFilter = filter;

        bool Matches(DebugOutputLine l) =>
            (attachedPid is null || l.Pid == attachedPid.Value)
            && (
                string.IsNullOrEmpty(activeFilter)
                || l.Text.Contains(activeFilter, StringComparison.OrdinalIgnoreCase)
            );

        // 由最新往舊走訪，最多收集「可見行數 + 捲動位移」筆就停下：一般情況（沒有捲動）
        // 只碰到畫面上會顯示的那些訊息，不會把整個緩衝區都拿去格式化。
        DebugOutputLine[] tail = outputBuffer.TakeLastMatching(visible + outputScroll, Matches);

        // 標頭要顯示「共幾筆符合」，這裡另外做一次不含格式化的計數，
        // 熱路徑（上面的走訪＋格式化）仍然只受可見行數限制，不受此計數影響。
        int totalMatches = outputBuffer.CountMatching(Matches);

        int end = Math.Max(0, tail.Length - outputScroll);
        int start = Math.Max(0, end - visible);

        var lines = new List<string>();
        for (int i = start; i < end; i++)
        {
            DebugOutputLine line = tail[i];
            string pidCell = outputAllPids ? $"[{Format.Muted}]{line.Pid, 6}[/] " : string.Empty;

            // 內容用終端機預設前景色而非 grey：同執行緒堆疊的既有決定，正文不該被壓暗
            lines.Add(
                $"[{Format.Muted}]{line.TimeStamp:HH:mm:ss}[/] {pidCell}{Format.Esc(line.Text)}"
            );
        }

        if (lines.Count == 0)
        {
            return Placeholder(BuildOutputPlaceholder(totalMatches));
        }

        string scope =
            outputAllPids ? "全部行程"
            : monitor.TargetPid is { } pid ? $"只看 PID {pid}"
            : "全部行程（尚未附加，無法限定 PID）";
        string filterText = string.IsNullOrEmpty(filter)
            ? $"[{Format.Muted}]無[/]"
            : $"[yellow]{Format.Esc(filter)}[/]";
        string dropped =
            outputBuffer.DroppedCount > 0 ? $"，已丟棄 {outputBuffer.DroppedCount}" : string.Empty;
        string scrolled = outputScroll > 0 ? $"，往上捲 {outputScroll}" : string.Empty;

        return new Panel(new Markup(string.Join('\n', lines)))
            .Header(
                $" 輸出（{totalMatches} 筆{dropped}{scrolled}）  範圍 {scope}   過濾 {filterText} "
            )
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    /// <summary>輸出檢視的空狀態要講清楚三種可能原因，而不是只說「沒有資料」。</summary>
    private string BuildOutputPlaceholder(int filteredCount)
    {
        if (debugOutput.Unavailable is { } reason)
        {
            return $"無法攔截應用程式輸出: {Format.Esc(reason)}";
        }

        if (outputBuffer.Count == 0)
        {
            return "尚未收到任何訊息。若目前是 Release 或 Testing 建置，[bold]Debug.WriteLine[/] 會被編譯移除，只留下 [bold]Trace.*[/] 的訊息";
        }

        return filteredCount == 0
            ? "目前的範圍或過濾條件沒有符合的訊息（按 [bold]g[/] 切換範圍、[bold]/[/] 修改過濾，Esc 清除）"
            : "（尚無可顯示的內容）";
    }

    private IRenderable RenderFooter()
    {
        string keys = view switch
        {
            DiagView.Heap =>
                "[bold]n[/] 快照  [bold]d[/] 設基準  [bold]o[/] 排序  [bold]/[/] 過濾  [bold]f[/] 根參考  [bold]e[/] 匯出",
            DiagView.Threads => "[bold]T[/] 更新堆疊  [bold]↑↓[/] 選擇  [bold]/[/] 過濾",
            DiagView.Log => "[bold]↑↓[/] 捲動  [bold]PgUp/PgDn[/] 翻頁",
            DiagView.Output => "[bold]↑↓[/] 捲動  [bold]g[/] PID 範圍  [bold]/[/] 過濾",
            _ => "[bold]n[/] 快照  [bold]a[/] 自動快照  [bold]p[/] 換行程",
        };

        // 狀態訊息用終端機預設前景色：這是最新一則結果（快照筆數、建置進度、錯誤），
        // 是要讀的正文，不該被當成裝飾壓暗。
        string mode = filterMode
            ? $"[yellow]過濾輸入:[/] {Format.Esc(filter)}[blink]_[/]"
            : Format.Esc(status);

        string busyText = busy is null ? string.Empty : $"[yellow]⏳ {Format.Esc(busy)}[/]  ";
        string autoText = autoSnapshot ? "[green]AUTO[/] " : string.Empty;

        var lines = new[]
        {
            $"[bold]1[/] 記憶體  [bold]2[/] 堆疊  [bold]3[/] 執行緒  [bold]4[/] 記錄  [bold]5[/] 輸出  │  [bold]b[/] 建置({Format.Esc(buildConfiguration)})  [bold]c[/] 設定  [bold]s[/] 啟動  [bold]x[/] 停止  [bold]r[/] 重建重啟  [bold]q[/] 離開",
            $"{busyText}{autoText}{keys}  │  {mode}",
        };

        return new Panel(new Markup(string.Join('\n', lines)))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Silver)
            .Expand();
    }

    private static IRenderable Placeholder(string markup) =>
        new Panel(new Markup($"\n  {markup}\n"))
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Silver)
            .Expand();

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "…";
}
