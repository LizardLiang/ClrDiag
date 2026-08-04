using System.Reflection;
using ClrDiag.Core;
using ClrDiag.Ui;
using Spectre.Console;

// clrdiag：終端機版的 .NET 記憶體 / 執行緒診斷主控台（不需要 Visual Studio）
// 任何 .NET 專案都能直接執行；要用建置與啟動伺服器功能時，在專案根目錄放一份 clrdiag.json。
//   互動模式:  clrdiag [--port 5000] [--pid 12345]
//   批次模式:  clrdiag --snapshot / --threads / --roots <型別> / --export / --render / --output
//   產生設定:  clrdiag --init

int? port = null;
int? pid = null;
int top = 25;
string? root = null;
string? configPath = null;
bool snapshotMode = false;
bool threadMode = false;
bool outputMode = false;
bool renderMode = false;
bool exportMode = false;
bool initMode = false;
bool listMode = false;
bool buildMode = false;
string? buildConfiguration = null;
int renderWidth = 120;
int renderHeight = 40;
string? rootsType = null;

for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    switch (arg)
    {
        case "--port" when i + 1 < args.Length:
            port = int.Parse(args[++i]);
            break;
        case "--pid" when i + 1 < args.Length:
            pid = int.Parse(args[++i]);
            break;
        case "--top" when i + 1 < args.Length:
            top = int.Parse(args[++i]);
            break;
        case "--root" when i + 1 < args.Length:
            root = args[++i];
            break;
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        case "--snapshot":
            snapshotMode = true;
            break;
        case "--threads":
            threadMode = true;
            break;
        case "--output":
            outputMode = true;
            break;
        case "--render":
            renderMode = true;
            break;
        case "--roots" when i + 1 < args.Length:
            rootsType = args[++i];
            break;
        case "--export":
            exportMode = true;
            break;
        case "--list":
            listMode = true;
            break;
        case "--init":
            initMode = true;
            break;
        case "--build":
            buildMode = true;
            // 後面若不是另一個參數，就當成建置設定名稱（--build Release）
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                buildConfiguration = args[++i];
            }

            break;
        case "--width" when i + 1 < args.Length:
            renderWidth = int.Parse(args[++i]);
            break;
        case "--height" when i + 1 < args.Length:
            renderHeight = int.Parse(args[++i]);
            break;
        case "--help":
        case "-h":
            PrintHelp();
            return 0;
        default:
            AnsiConsole.MarkupLine($"[red]未知參數:[/] {Markup.Escape(arg)}");
            PrintHelp();
            return 2;
    }
}

DiagConfig config;
try
{
    config = DiagConfig.Load(configPath, root);
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]設定載入失敗:[/] {Markup.Escape(ex.Message)}");
    return 2;
}

int effectivePort = port ?? config.Port;

if (initMode)
{
    return RunInit(config);
}

if (listMode)
{
    return RunList(config);
}

// 批次建置：與互動介面的 b 鍵走同一段程式碼，可用於腳本或先確認設定是否正確
if (buildMode)
{
    var buildLog = new LogBuffer();
    string configuration = buildConfiguration ?? config.Configurations.FirstOrDefault() ?? "Debug";

    AnsiConsole.MarkupLine(
        $"建置目標 {Markup.Escape(config.ResolvedBuildProject ?? "（未解析）")}  設定 {Markup.Escape(configuration)}"
    );

    BuildResult result = await new BuildService(config, buildLog).BuildAsync(
        configuration,
        CancellationToken.None
    );

    foreach (LogLine line in buildLog.TakeLast(400))
    {
        AnsiConsole.MarkupLine(
            line.Kind switch
            {
                LogKind.Error => $"[red]{Markup.Escape(line.Text)}[/]",
                LogKind.Warning => $"[yellow]{Markup.Escape(line.Text)}[/]",
                LogKind.Success => $"[green]{Markup.Escape(line.Text)}[/]",
                _ => Markup.Escape(line.Text),
            }
        );
    }

    return result.Success ? 0 : 1;
}

// 非互動的輸出串流模式：不建 DiagApp，直接接上 DBWIN 監聽器把訊息印到主控台
if (outputMode)
{
    return await RunOutput(pid);
}

if (rootsType is not null)
{
    return RunRootPaths(config, pid, rootsType);
}

// 取一次快照後直接寫成 CSV（與互動介面的 e 鍵走同一段程式碼）
if (exportMode)
{
    int? target = ResolveTarget(config, pid);
    if (target is null)
    {
        return 1;
    }

    DiagSnapshot snapshot = new HeapSnapshotService(config.AppNamespaces).Capture(
        target.Value,
        includeTypes: true,
        includeThreads: false,
        CancellationToken.None
    );
    string reportFile = HeapReportWriter.Write(
        config.ReportDirectoryFullPath,
        snapshot,
        baseline: null,
        HeapSnapshotService.Diff(snapshot, null),
        target
    );

    AnsiConsole.MarkupLine(
        $"已匯出 {Markup.Escape(reportFile)}（{snapshot.Types.Count:N0} 個型別）"
    );
    return 0;
}

if (snapshotMode || threadMode)
{
    return RunHeadless(config, pid, snapshotMode, threadMode, top);
}

// 無主控台環境（管線、CI、貼進文件）用：把四個檢視各渲染一張純文字畫面
if (renderMode)
{
    using var offscreen = new DiagApp(config, effectivePort);
    Console.Out.Write(
        offscreen.RenderFramesToText(pid, renderWidth, renderHeight, withSnapshot: true)
    );
    return 0;
}

// 互動儀表板需要真正的主控台（要能隱藏游標、重畫畫面）；被重新導向時給明確指引而不是丟例外
if (Console.IsOutputRedirected)
{
    AnsiConsole.MarkupLine("[red]輸出被重新導向，無法啟動互動式儀表板[/]");
    AnsiConsole.MarkupLine(
        "請直接在終端機執行，或改用批次模式: [bold]clrdiag --snapshot[/] / [bold]clrdiag --threads[/]"
    );
    return 2;
}

using var app = new DiagApp(config, effectivePort);
app.Run(pid);
return 0;

/// <summary>解析要監看的行程；找不到時輸出可用的候選清單，而不是只說「找不到」。</summary>
static int? ResolveTarget(DiagConfig config, int? pid)
{
    if (pid is not null)
    {
        return pid;
    }

    int? found = ManagedProcessFinder.FindBest(config.ProcessNames);
    if (found is not null)
    {
        return found;
    }

    AnsiConsole.MarkupLine("[red]找不到載入 CLR 的行程[/]");
    AnsiConsole.MarkupLine(
        "請先啟動要診斷的應用程式，或以 [bold]--pid[/] 指定；[bold]--list[/] 可列出候選行程"
    );
    return null;
}

/// <summary>列出可監看的受控行程，方便挑 PID。</summary>
static int RunList(DiagConfig config)
{
    List<ManagedProcessInfo> all = ManagedProcessFinder.List(config.ProcessNames);
    if (all.Count == 0 && config.ProcessNames.Length > 0)
    {
        AnsiConsole.MarkupLine(
            $"[yellow]設定的行程名稱（{string.Join(", ", config.ProcessNames)}）沒有執行中的實例，以下列出所有受控行程[/]"
        );
        all = ManagedProcessFinder.List(Array.Empty<string>());
    }

    if (all.Count == 0)
    {
        AnsiConsole.MarkupLine("[red]找不到任何載入 CLR 的行程[/]");
        return 1;
    }

    var table = new Table().Border(TableBorder.Simple);
    table.AddColumn(new TableColumn("PID").RightAligned());
    table.AddColumn("行程");
    table.AddColumn("執行階段");
    table.AddColumn(new TableColumn("工作集 MB").RightAligned());

    foreach (ManagedProcessInfo process in all.Take(30))
    {
        table.AddRow(
            process.Pid.ToString(),
            Markup.Escape(process.Name),
            Markup.Escape(process.Runtime),
            $"{process.WorkingSet64 / 1024.0 / 1024.0:N0}"
        );
    }

    AnsiConsole.Write(table);
    return 0;
}

/// <summary>在目前目錄產生一份帶註解的 clrdiag.json 範本。</summary>
static int RunInit(DiagConfig config)
{
    string file = Path.Combine(config.Root, DiagConfig.FileName);
    if (File.Exists(file))
    {
        AnsiConsole.MarkupLine($"[yellow]已存在，未覆寫:[/] {Markup.Escape(file)}");
        return 1;
    }

    string template = """
        {
          // 建置：省略 buildCommand 時，SDK 專案用 dotnet build，舊式專案用 vswhere 找到的 MSBuild
          // "buildProject": "src/MyApp/MyApp.csproj",
          // "buildCommand": "msbuild",
          // "buildArguments": [ "{project}", "/p:Configuration={config}", "/verbosity:minimal" ],
          "configurations": [ "Debug", "Release" ],

          // 啟動開發伺服器；省略 serveCommand 就只能附加到既有行程
          // "serveCommand": "dotnet",
          // "serveArguments": [ "run", "--project", "{project}", "--urls", "http://localhost:{port}" ],
          "port": 5000,
          "probeUrl": "http://localhost:{port}/",

          // 監看目標：留空表示掃描所有載入 CLR 的行程
          "processNames": [],

          // 視為「自己的程式碼」的命名空間前綴（執行緒與堆疊會標記出來）；留空則以「非框架」判斷
          "appNamespaces": [],

          "reportDirectory": ".clrdiag-reports"
        }

        """;

    File.WriteAllText(file, template);
    AnsiConsole.MarkupLine($"已建立 {Markup.Escape(file)}");
    return 0;
}

static int RunHeadless(DiagConfig config, int? pid, bool includeTypes, bool includeThreads, int top)
{
    int? target = ResolveTarget(config, pid);
    if (target is null)
    {
        return 1;
    }

    var service = new HeapSnapshotService(config.AppNamespaces);
    DiagSnapshot snapshot;
    try
    {
        snapshot = service.Capture(
            target.Value,
            includeTypes,
            includeThreads,
            CancellationToken.None
        );
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]快照失敗:[/] {Markup.Escape(ex.Message)}");
        return 1;
    }

    AnsiConsole.MarkupLine(
        $"[bold]PID {target}[/]  CLR {Markup.Escape(snapshot.ClrVersion)}  {snapshot.TakenAt:yyyy-MM-dd HH:mm:ss}  耗時 {snapshot.Duration.TotalSeconds:N1}s"
    );

    if (includeTypes)
    {
        AnsiConsole.MarkupLine(
            $"物件 {snapshot.ObjectCount:N0}  總大小 {snapshot.TotalSizeMb:N1} MB  區段 {snapshot.SegmentCount}"
        );

        if (snapshot.WalkWarning is { } warning)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(warning)}[/]");
        }

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn(new TableColumn("大小 MB").RightAligned());
        table.AddColumn(new TableColumn("數量").RightAligned());
        table.AddColumn("型別");

        foreach (HeapTypeStat type in snapshot.Types.Take(top))
        {
            table.AddRow(
                $"{type.TotalSize / 1024.0 / 1024.0:N1}",
                $"{type.Count:N0}",
                Markup.Escape(type.TypeName)
            );
        }

        AnsiConsole.Write(table);
    }

    if (includeThreads && !includeTypes)
    {
        foreach (ManagedThreadInfo thread in snapshot.Threads)
        {
            AnsiConsole.MarkupLine(
                $"[bold]OS {thread.OsThreadId}[/] 受控 {thread.ManagedThreadId} [aqua]{thread.State}[/]{(thread.PendingException is null ? string.Empty : $" [red]{Markup.Escape(thread.PendingException)}[/]")}"
            );
            foreach (string frame in thread.Frames.Take(20))
            {
                AnsiConsole.MarkupLine($"    {Markup.Escape(frame)}");
            }
        }
    }

    return 0;
}

/// <summary>批次模式的根參考鏈搜尋：找出指定型別的物件被誰握住而無法回收。</summary>
static int RunRootPaths(DiagConfig config, int? pid, string typeName)
{
    int? target = ResolveTarget(config, pid);
    if (target is null)
    {
        return 1;
    }

    var service = new HeapSnapshotService(config.AppNamespaces);
    try
    {
        List<RootPath> found = service.FindRootPaths(
            target.Value,
            typeName,
            maxPaths: 5,
            budget: TimeSpan.FromSeconds(30),
            CancellationToken.None
        );

        foreach (RootPath path in found)
        {
            AnsiConsole.MarkupLine($"[aqua]{Markup.Escape(path.RootDescription)}[/]");
            foreach (string step in path.Chain)
            {
                AnsiConsole.MarkupLine($"  → {Markup.Escape(step)}");
            }
        }

        return 0;
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]根參考搜尋失敗:[/] {Markup.Escape(ex.Message)}");
        return 1;
    }
}

/// <summary>
/// 非互動的輸出串流：接上 DBWIN 監聽器，把應用程式 OutputDebugString 訊息逐行印到主控台，
/// 可直接用 &gt; file 導向保存。Ctrl+C 乾淨結束；監聽建立失敗（多半是被 DebugView 等其他監聽者占用）
/// 印出原因並回傳非零結束碼。
/// </summary>
static async Task<int> RunOutput(int? pid)
{
    using var listener = new DebugOutputListener();
    listener.Start();

    if (listener.Unavailable is { } reason)
    {
        AnsiConsole.MarkupLine(
            $"[red]無法攔截應用程式輸出（OutputDebugString）:[/] {Markup.Escape(reason)}"
        );
        return 3;
    }

    var stop = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

    listener.LineReceived += line =>
    {
        if (pid is not null && line.Pid != pid.Value)
        {
            return;
        }

        Console.WriteLine($"{line.TimeStamp:HH:mm:ss} {line.Pid, 6} {line.Text}");
    };

    ConsoleCancelEventHandler onCancel = (_, e) =>
    {
        // 攔截 Ctrl+C 自行收尾（釋放 DBWIN 資源），而不是讓執行階段直接強制結束行程
        e.Cancel = true;
        stop.TrySetResult(0);
    };

    Console.CancelKeyPress += onCancel;
    AnsiConsole.MarkupLine(
        $"[green]開始串流應用程式 Debug/Trace 輸出[/]{(pid is null ? string.Empty : $"（只顯示 PID {pid}）")}，按 Ctrl+C 結束"
    );

    try
    {
        return await stop.Task.ConfigureAwait(false);
    }
    finally
    {
        Console.CancelKeyPress -= onCancel;
    }
}

static void PrintHelp()
{
    string version =
        Assembly
            .GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
    // 說明文字含 [--top N] 這類方括號，交給 Spectre 會被當成樣式標記，因此直接輸出純文字
    Console.WriteLine(
        $"""
        clrdiag {version} — 終端機版 .NET 記憶體 / 執行緒診斷主控台（不需要 Visual Studio）

        用法
          clrdiag                       啟動互動式儀表板（建置 / 伺服器 / 記憶體 / 堆疊 / 執行緒）
          clrdiag --list                列出可監看的受控行程
          clrdiag --pid 12345           直接監看指定行程
          clrdiag --port 8080           覆寫設定檔的連接埠
          clrdiag --snapshot [--top N]  取一次堆疊快照後輸出文字並結束
          clrdiag --threads             輸出受控執行緒堆疊後結束
          clrdiag --roots <型別全名>    找出該型別的物件被哪個 GC 根握住（上限 30 秒）
          clrdiag --export              取一次快照並輸出 CSV
          clrdiag --build [Release]     依設定建置一次（不進互動介面）
          clrdiag --output [--pid N]    串流應用程式的 Debug/Trace 輸出（OutputDebugString），Ctrl+C 結束
          clrdiag --render              把八個面板渲染成純文字（可貼進問題回報）
          clrdiag --init                在專案根目錄產生 clrdiag.json 範本
          clrdiag --config <path>       指定設定檔
          clrdiag --root <path>         指定專案根目錄

        設定檔
          在專案根目錄放 clrdiag.json（會從目前目錄往上尋找）即可設定建置指令、
          啟動伺服器指令、要監看的行程名稱與自己的命名空間。
          沒有設定檔時會自動偵測 .sln / .csproj，仍可監看、快照、分析既有行程。

        互動按鍵
          0/1/2      選 build / serve / process 面板
          3/4/5/6/7  選 記憶體 / 堆疊 / 執行緒 / 記錄 / 輸出
                     同一個數字再按一次＝放大該面板（Esc 或同號鍵還原）；上排數字與數字鍵盤都可用
          b 建置    c 切換設定    s 啟動    x 停止    r 重建並重啟
          n 取快照  T 只更新執行緒堆疊    d 設比較基準（Shift+D 清除）    a 自動快照
          o 切換排序  / 過濾型別  f 找出根參考鏈  e 匯出 CSV  p 切換行程
          g 輸出檢視的 PID 範圍（只看附加 PID / 全部行程）  q 離開

        """
    );
}
