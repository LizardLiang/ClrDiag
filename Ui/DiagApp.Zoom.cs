using ClrDiag.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ClrDiag.Ui;

/// <summary>
/// 上排三個面板放大後的畫面。分割版面下它們只有 8 列高，訊息被截斷、清單被截短；
/// 放大時改成把完整內容攤開：所有建置錯誤與警告、完整探測記錄、全部計數器與取樣歷史。
/// 中間的五個檢視不需要對應函式，它們本來就依 BodyHeight() 決定高度。
/// </summary>
public sealed partial class DiagApp
{
    private IRenderable RenderZoom(int pane) =>
        pane switch
        {
            0 => RenderBuildZoom(),
            1 => RenderServeZoom(),
            _ => RenderProcessZoom(),
        };

    private IRenderable RenderBuildZoom()
    {
        BuildResult last = build.Last;
        bool notRun = ReferenceEquals(last, BuildResult.NotRun);

        var header = new List<string>
        {
            $"設定 [bold]{Format.Esc(buildConfiguration)}[/]   目標 {Format.Esc(config.ResolvedBuildProject ?? "（未解析）")}",
            config.MsBuildPath is null
                ? $"MSBuild [red]未找到[/] {Format.Esc(config.BuildToolError ?? string.Empty)}"
                : $"MSBuild {Format.Esc(config.MsBuildPath)}",
            build.IsRunning ? "[yellow]建置中…[/]"
            : notRun ? $"[{Format.Muted}]尚未建置（按 b）[/]"
            : last.Success
                ? $"[green]成功[/] {Format.Duration(last.Duration)}   設定 {Format.Esc(last.Configuration)}   警告 {last.WarningCount}"
            : $"[red]失敗[/] {Format.Duration(last.Duration)}   設定 {Format.Esc(last.Configuration)}   錯誤 {last.Errors.Count}   警告 {last.WarningCount}",
        };

        // 每個錯誤兩列：位置一列、完整訊息一列（不截斷，太長就讓終端機自己折行）
        var body = new List<string>();
        foreach (BuildDiagnostic error in last.Errors)
        {
            body.Add(
                $"[red]{Format.Esc(error.Code)}[/] {Format.Esc(error.File)}:{error.Line}:{error.Column}"
            );
            body.Add($"  {Format.Esc(error.Message)}");
        }

        // BuildService 只保留警告「數量」，明細只存在於訊息記錄，因此直接取原始輸出行
        LogLine[] warnings = log.TakeLast(2000)
            .Where(line => line.Source == "build" && line.Kind == LogKind.Warning)
            .ToArray();

        if (warnings.Length > 0)
        {
            body.Add(string.Empty);
            body.Add($"[yellow]警告 {warnings.Length}[/]");
            foreach (LogLine warning in warnings)
            {
                body.Add($"  [yellow]{Format.Esc(warning.Text)}[/]");
            }
        }

        if (body.Count == 0)
        {
            body.Add($"[{Format.Muted}]（沒有錯誤或警告）[/]");
        }

        int visible = Math.Max(3, BodyHeight() - header.Count - 1);
        buildScroll = Math.Clamp(buildScroll, 0, Math.Max(0, body.Count - visible));

        string scrolled = buildScroll > 0 ? $"，往下捲 {buildScroll}" : string.Empty;
        var content = new List<IRenderable>
        {
            new Markup(string.Join('\n', header)),
            new Rule($"[{Format.Muted}]錯誤與警告[/]") { Justification = Justify.Left },
            new Markup(string.Join('\n', body.Skip(buildScroll).Take(visible))),
        };

        return new Panel(new Rows(content))
            .Header(
                PaneHeader(0, $"（錯誤 {last.Errors.Count}，警告 {last.WarningCount}{scrolled}）")
            )
            .Border(PaneBorder(0))
            .BorderColor(
                build.IsRunning ? Color.Yellow
                : last.Success ? Color.Green
                : Color.Silver
            )
            .Expand();
    }

    private IRenderable RenderServeZoom()
    {
        ProbeResult[] history = server.ProbeHistory;

        var header = new List<string>
        {
            $"{ServerStateText()}   連接埠 [bold]{server.Port}[/]   PID {(server.ServerPid?.ToString() ?? "-")}",
            $"探測網址 {Format.Esc(server.Url)}",
            config.CanServe
                ? $"啟動指令 {Format.Esc(config.ServeCommand!)} {Format.Esc(string.Join(' ', config.ServeArguments ?? Array.Empty<string>()))}"
                : $"[{Format.Muted}]未設定 serveCommand，只能附加到既有行程[/]",
        };

        double[] latencies = history.Where(h => h.Ok).Select(h => h.ElapsedMs).ToArray();
        if (latencies.Length > 0)
        {
            Array.Sort(latencies);
            double p50 = latencies[latencies.Length / 2];
            double p95 = latencies[(int)Math.Min(latencies.Length - 1, latencies.Length * 0.95)];
            header.Add(
                $"延遲 p50 {p50:N0} ms   p95 {p95:N0} ms   最大 {latencies[^1]:N0} ms   成功 {latencies.Length}/{history.Length}"
            );
        }
        else
        {
            header.Add(
                server.IsPortListening()
                    ? $"[{Format.Muted}]連接埠有人監聽，但尚無成功的探測[/]"
                    : $"[{Format.Muted}]尚無探測記錄[/]"
            );
        }

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Silver).Expand();
        table.AddColumn(new TableColumn($"[{Format.Muted}]時間[/]").Width(10));
        table.AddColumn(new TableColumn($"[{Format.Muted}]狀態[/]").Width(8));
        table.AddColumn(new TableColumn($"[{Format.Muted}]毫秒[/]").RightAligned().Width(9));
        table.AddColumn(new TableColumn($"[{Format.Muted}]錯誤[/]"));

        // 與訊息記錄一致：最新的在最下面，往上捲才看更早的探測
        int visible = Math.Max(3, BodyHeight() - header.Count - 4);
        probeScroll = Math.Clamp(probeScroll, 0, Math.Max(0, history.Length - visible));

        int end = Math.Max(0, history.Length - probeScroll);
        int start = Math.Max(0, end - visible);

        for (int i = start; i < end; i++)
        {
            ProbeResult probe = history[i];
            string statusText =
                probe.Ok ? $"[green]{probe.StatusCode}[/]"
                : probe.StatusCode > 0 ? $"[red]{probe.StatusCode}[/]"
                : "[red]無回應[/]";

            table.AddRow(
                $"{probe.TimeStamp:HH:mm:ss}",
                statusText,
                $"{probe.ElapsedMs:N0}",
                probe.Error is null ? string.Empty : $"[red]{Format.Esc(probe.Error)}[/]"
            );
        }

        string scrolled = probeScroll > 0 ? $"，往上捲 {probeScroll}" : string.Empty;
        var content = new List<IRenderable>
        {
            new Markup(string.Join('\n', header)),
            new Rule($"[{Format.Muted}]探測記錄[/]") { Justification = Justify.Left },
            table,
        };

        return new Panel(new Rows(content))
            .Header(PaneHeader(1, $"（探測 {history.Length} 筆{scrolled}）"))
            .Border(PaneBorder(1))
            .BorderColor(server.ServerPid is not null ? Color.Green : Color.Silver)
            .Expand();
    }

    private IRenderable RenderProcessZoom()
    {
        MetricSample[] history = monitor.History;
        if (monitor.TargetPid is null || history.Length == 0)
        {
            return Placeholder(
                2,
                "未附加任何行程。按 [bold]s[/] 啟動伺服器，或 [bold]p[/] 選擇受控行程"
            );
        }

        MetricSample latest = history[^1];
        TimeSpan uptime = monitor.TargetStartTime is { } start
            ? DateTime.Now - start
            : TimeSpan.Zero;

        var header = new List<string>
        {
            $"PID [bold]{monitor.TargetPid}[/] {Format.Esc(monitor.TargetName ?? "")}   啟動於 {(monitor.TargetStartTime is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss") : "未知")}   已執行 {Format.Duration(uptime)}",
            $"私有 [bold]{latest.PrivateMb:N0} MB[/]   工作集 {latest.WorkingSetMb:N0} MB   CPU {latest.CpuPercent:N1}%   執行緒 {latest.ThreadCount}   控制代碼 {latest.HandleCount:N0}",
        };

        if (monitor.CounterStatus is { } counterError)
        {
            // 分割版面下這行被截到 44 字，放大就是要看到完整原因
            header.Add($"[yellow]{Format.Esc(counterError)}[/]");
        }

        var counters = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(3))
            .AddColumn(new GridColumn().NoWrap().PadRight(3))
            .AddColumn(new GridColumn().NoWrap().PadRight(3))
            .AddColumn(new GridColumn().NoWrap());

        counters.AddRow(
            $"Gen0 預算 {Format.Mb(latest.Gen0Mb)}",
            $"Gen1 {Format.Mb(latest.Gen1Mb)}",
            $"Gen2 {Format.Mb(latest.Gen2Mb)}",
            $"LOH {Format.Mb(latest.LohMb)}"
        );
        counters.AddRow(
            $"受控堆疊 {Format.Mb(latest.AllHeapsMb)}",
            $"已提交 {Format.Mb(latest.CommittedMb)}",
            $"釘選物件 {Format.Number(latest.PinnedObjects)}",
            $"%GC 時間 {Format.Percent(latest.TimeInGcPercent)}"
        );
        counters.AddRow(
            $"GC 次數 g0 {Format.Number(latest.Gen0Collections)}",
            $"g1 {Format.Number(latest.Gen1Collections)}",
            $"g2 {Format.Number(latest.Gen2Collections)}",
            $"例外 {Format.Rate(latest.ExceptionsPerSec)}"
        );
        counters.AddRow(
            $"鎖競爭 {Format.Rate(latest.ContentionPerSec)}",
            string.Empty,
            string.Empty,
            string.Empty
        );

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Silver).Expand();
        table.AddColumn(new TableColumn($"[{Format.Muted}]時間[/]").Width(10));
        table.AddColumn(new TableColumn($"[{Format.Muted}]私有 MB[/]").RightAligned().Width(10));
        table.AddColumn(new TableColumn($"[{Format.Muted}]工作集 MB[/]").RightAligned().Width(11));
        table.AddColumn(new TableColumn($"[{Format.Muted}]堆疊 MB[/]").RightAligned().Width(10));
        table.AddColumn(new TableColumn($"[{Format.Muted}]CPU %[/]").RightAligned().Width(8));
        table.AddColumn(new TableColumn($"[{Format.Muted}]執行緒[/]").RightAligned().Width(8));
        table.AddColumn(new TableColumn($"[{Format.Muted}]控制代碼[/]").RightAligned());

        // 取樣歷史直接填滿剩餘高度：最新的在最下面
        int visible = Math.Max(3, BodyHeight() - header.Count - 10);
        foreach (MetricSample sample in history.TakeLast(visible))
        {
            table.AddRow(
                $"{sample.TimeStamp:HH:mm:ss}",
                $"{sample.PrivateMb:N0}",
                $"{sample.WorkingSetMb:N0}",
                sample.AllHeapsMb is null ? "n/a" : $"{sample.AllHeapsMb.Value:N1}",
                $"{sample.CpuPercent:N1}",
                $"{sample.ThreadCount}",
                $"{sample.HandleCount:N0}"
            );
        }

        var content = new List<IRenderable>
        {
            new Markup(string.Join('\n', header)),
            new Rule($"[{Format.Muted}]目前值[/]") { Justification = Justify.Left },
            counters,
            new Rule($"[{Format.Muted}]取樣歷史（每秒一次）[/]") { Justification = Justify.Left },
            table,
        };

        return new Panel(new Rows(content))
            .Header(PaneHeader(2, $"（取樣 {history.Length} 筆）"))
            .Border(PaneBorder(2))
            .BorderColor(Color.Aqua)
            .Expand();
    }
}
