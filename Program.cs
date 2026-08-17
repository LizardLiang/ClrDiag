using System.IO.Pipes;
using System.Reflection;
using System.Text;
using ClrDiag.Core;
using ClrDiag.Core.Dap;
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
bool dapMode = false;
bool pipeNameMode = false;
bool installSkillMode = false;
bool force = false;
string? installSkillScope = null;
string? sendCommand = null;
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
        case "--dap":
            dapMode = true;
            break;
        case "--send" when i + 1 < args.Length:
            sendCommand = args[++i];
            break;
        case "--pipe-name":
            pipeNameMode = true;
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
        case "--install-skill":
            installSkillMode = true;
            // 範圍是必填；這裡先收下值，缺漏或拼錯留到迴圈後統一報錯，
            // 才能印出這個旗標自己的用法而不是整份說明
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                installSkillScope = args[++i];
            }

            break;
        case "--force":
            force = true;
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

// --install-skill 的範圍先驗證：值缺漏或不是 global／local 都算參數錯誤，
// 用跟未知參數同一個結束碼 2
SkillScope? skillScope = SkillInstaller.ParseScope(installSkillScope);
if (installSkillMode && skillScope is null)
{
    AnsiConsole.MarkupLine(
        installSkillScope is null
            ? "[red]--install-skill 需要指定範圍（global 或 local）[/]"
            : $"[red]--install-skill 的範圍只能是 global 或 local:[/] {Markup.Escape(installSkillScope)}"
    );
    SkillInstaller.PrintUsage();
    return 2;
}

// 這幾個非互動批次模式（設計上就是給重新導向到檔案／管線，或代理程式讀取用）必須輸出 UTF-8：
// 不主動設定的話 Console 會沿用作業系統目前的主控台字碼頁（繁體中文 Windows 預設是 Big5 950），
// 寫進檔案後任何用 UTF-8 讀取的消費端（例如本檔案）看到的都是亂碼。互動式儀表板刻意不套用這段，
// 免得動到 Spectre.Console 畫框線／版面時的終端機能力偵測。一定要搶在第一次呼叫 AnsiConsole
// 之前設定——包括下面 DiagConfig.Load 失敗時的錯誤訊息——Spectre 的 Profile 是第一次使用時
// 惰性建立並快取，事後才改字碼頁不會讓已經印出的內容或已快取的判斷跟著變。
bool batchOutputMode = dapMode || snapshotMode || threadMode || rootsType is not null || renderMode || outputMode;
if (batchOutputMode)
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
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

// 安裝 Claude Code 技能：跟 --init 一樣是一次性動作，做完就結束，不進診斷流程。
// local 範圍直接沿用 DiagConfig 解析好的專案根目錄，所以 --root 可以一起用。
if (skillScope is not null)
{
    return SkillInstaller.Install(skillScope.Value, config.Root, force);
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

// 只印出目前專案對應的具名管道名稱：編輯器外掛（例如 Neovim Lua 客戶端）用這個做一次性
// 發現，不必自己重新實作一份雜湊邏輯而跟本體的演算法兜不起來
if (pipeNameMode)
{
    Console.WriteLine(DebugCommandServer.PipeNameFor(config.Root));
    return 0;
}

// 對本機（或 --pid 對應目標）專案的除錯指令管道送一個指令、印出回覆後結束
// ——VS Code 的過渡方案，也是這個通道最方便手動測試的入口
if (sendCommand is not null)
{
    return await RunSend(config, sendCommand);
}

// 非互動除錯：附加到目標、開具名管道（讓 Neovim／--send 可以下指令），每次中斷印出區塊
if (dapMode)
{
    return await RunDap(config, pid);
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

// 攔截 Ctrl+C 走一般的收尾流程（斷開除錯階段、砍掉 netcoredbg），而不是讓執行階段直接強制結束——
// 同 RunOutput 的教訓，直接讓行程被砍掉會跳過 DiagApp.Dispose，留下孤兒的 netcoredbg 子行程。
ConsoleCancelEventHandler onCancel = (_, e) =>
{
    e.Cancel = true;
    app.RequestExit();
};
Console.CancelKeyPress += onCancel;

// 儀表板畫在終端機的「替代畫面緩衝區」（alternate screen buffer，就是 vim／less 用的那一塊）：
// 進入時整個畫面換成一塊空白緩衝區，離開時原本的畫面與捲動歷史原封不動回來，儀表板本身
// 不會留在捲動歷史裡。終端機不支援 ANSI（dumb terminal、部分 CI）時就照舊直接畫在主畫面，
// 只是少了這層還原，不因為這個功能讓工具跑不起來。
// 只有互動儀表板走這段：--render／--snapshot 等批次模式在上面就已經回傳，輸出保持純文字。
// ESC 用字元碼寫，不把原始控制字元留在原始碼裡——它在編輯器與 diff 裡都是隱形的，很容易被改壞
const char esc = (char)0x1b;
bool alternateScreen = AnsiConsole.Profile.Capabilities.Ansi;
if (alternateScreen)
{
    // ?1049h 切到替代緩衝區，[H 再把游標移到左上角
    AnsiConsole.Write(new ControlCode($"{esc}[?1049h{esc}[H"));
}

try
{
    app.Run(pid);
}
finally
{
    Console.CancelKeyPress -= onCancel;

    // 不論是按 q、Ctrl+C 還是往外丟例外，都要在這裡把終端機還原。
    // 放在 finally 是重點：例外的訊息要印在還原後的主畫面上才看得到——
    // 印在即將被丟棄的替代緩衝區裡等於沒印。
    if (alternateScreen)
    {
        // Live 結束時（正常結束與丟例外都算）會自己還原游標，這裡再確保一次：
        // 切回主畫面後游標若還隱藏著，使用者拿到的就是一個看不見游標的 shell，
        // 而多送一個控制碼的成本遠低於那個後果。
        AnsiConsole.Cursor.Show();
        AnsiConsole.Write(new ControlCode($"{esc}[?1049l"));
    }
}

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
/// 對專案的除錯指令管道送一個指令、印出回覆後結束。這是最方便手動測試通道的入口，
/// 也是 VS Code 在專屬擴充套件（規劃中的後續階段）完成前的過渡方案——綁一個鍵跑
/// clrdiag --send 就能設中斷點。
/// </summary>
static async Task<int> RunSend(DiagConfig config, string commandJson)
{
    string pipeName = DebugCommandServer.PipeNameFor(config.Root);
    try
    {
        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous
        );
        await client.ConnectAsync(5000).ConfigureAwait(false);

        using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
        var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        // 一連上就會先收到一次目前狀態（不是這次指令的回覆），讀掉但不印出
        await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        await writer.WriteLineAsync(commandJson).ConfigureAwait(false);
        string? reply = await reader
            .ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);

        if (reply is null)
        {
            AnsiConsole.MarkupLine("[red]管道在等待回覆時關閉[/]");
            return 1;
        }

        Console.WriteLine(reply);
        return 0;
    }
    catch (TimeoutException)
    {
        AnsiConsole.MarkupLine($"[red]連線逾時:[/] \\\\.\\pipe\\{Markup.Escape(pipeName)}");
        AnsiConsole.MarkupLine(
            "確認 ClrDiag 是否正在這個專案目錄下執行、且未以 dapEnabled:false 停用除錯功能"
        );
        return 1;
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]連線失敗:[/] {Markup.Escape(ex.Message)}");
        return 1;
    }
}

/// <summary>
/// 非互動除錯：附加到目標行程、開具名管道通道（讓 Neovim 或 clrdiag --send 可以下指令），
/// 每次真正的中斷都把當下的堆疊／區域變數／監看印成一個區塊，格式比照 --threads／--render
/// 的風格；同一次中斷內因為新增/移除監看而重新整理，只印一行異動提示，不重印整個區塊
///（靠 DebugHaltedState.Generation 分辨，不是物件參考——每次重新整理都會產生新物件，
/// 參考比對永遠不相等）。真正的執行控制仍然來自通道，這裡只負責觀察與印出，可直接用
/// &gt; file 導向保存。Ctrl+C 乾淨結束（斷開除錯階段、砍掉 netcoredbg）。
/// </summary>
static async Task<int> RunDap(DiagConfig config, int? pid)
{
    int? target = ResolveTarget(config, pid);
    if (target is null)
    {
        return 1;
    }

    var log = new LogBuffer();
    using var session = new DapSessionService(log);
    session.SeedFromConfig(config.ParsedDapBreakpoints, config.DapWatches);

    using DebugCommandServer? pipe = config.DapEnabled
        ? new DebugCommandServer(session, log, DebugCommandServer.PipeNameFor(config.Root))
        : null;
    pipe?.Start();

    DebugHaltedState? lastPrinted = null;
    session.StateChanged += () =>
    {
        DebugHaltedState? current = session.Halted;
        if (current is null || session.State != DebugSessionState.Halted)
        {
            return;
        }

        if (lastPrinted is not null && current.Generation == lastPrinted.Generation)
        {
            // 同一次中斷的資料更新（例如中斷中新增了監看），不是新的中斷事件
            PrintWatchDelta(lastPrinted, current);
            lastPrinted = current;
            return;
        }

        lastPrinted = current;
        PrintHaltBlock(target.Value, current);
    };

    AnsiConsole.MarkupLine(
        $"[green]開始非互動除錯[/]（PID {target}），中斷點與監看透過具名管道或 Neovim 設定，Ctrl+C 結束"
    );

    bool ok = await session
        .AttachAsync(target.Value, config.DapAdapterPath, CancellationToken.None)
        .ConfigureAwait(false);
    if (!ok)
    {
        AnsiConsole.MarkupLine($"[red]附加除錯器失敗:[/] {Markup.Escape(session.LastError ?? "未知錯誤")}");
        return 1;
    }

    var stop = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    ConsoleCancelEventHandler onCancel = (_, e) =>
    {
        e.Cancel = true;
        stop.TrySetResult(0);
    };

    Console.CancelKeyPress += onCancel;
    try
    {
        return await stop.Task.ConfigureAwait(false);
    }
    finally
    {
        Console.CancelKeyPress -= onCancel;
        await session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>把一次中斷的堆疊／區域變數／監看印成人類可讀的區塊，風格比照 RunHeadless。</summary>
static void PrintHaltBlock(int pid, DebugHaltedState halted)
{
    AnsiConsole.MarkupLine(
        $"[bold]PID {pid}[/]  中斷: [yellow]{Markup.Escape(halted.Reason)}[/]（執行緒 {halted.ThreadId}）  {DateTime.Now:HH:mm:ss}"
    );

    for (int i = 0; i < halted.Frames.Count; i++)
    {
        DebugFrame frame = halted.Frames[i];
        string marker = i == halted.SelectedFrameIndex ? "→" : " ";
        AnsiConsole.MarkupLine(
            $"  {marker} {Markup.Escape(frame.Name)}  {Markup.Escape(frame.SourcePath ?? "?")}:{frame.Line}"
        );
    }

    AnsiConsole.MarkupLine($"[bold]區域變數[/] ({halted.Locals.Count})");
    foreach (DebugVariable local in halted.Locals)
    {
        AnsiConsole.MarkupLine(
            $"    {Markup.Escape(local.Name)} = {Markup.Escape(local.Value)} ({Markup.Escape(local.Type)})"
        );
    }

    if (halted.Watches.Count > 0)
    {
        AnsiConsole.MarkupLine($"[bold]監看[/] ({halted.Watches.Count})");
        foreach (WatchResult watch in halted.Watches)
        {
            string value = watch.TimedOut ? "逾時" : watch.Error ?? watch.Value ?? "null";
            AnsiConsole.MarkupLine($"    {Markup.Escape(watch.Expression)} = {Markup.Escape(value)}");
        }
    }

    AnsiConsole.WriteLine();
}

/// <summary>
/// 同一次中斷內監看清單異動時的簡短提示（新增/移除監看，不是新的中斷，不重印整個區塊）。
/// 目前 --dap 的具名管道通道沒有切換框架的指令，所以這裡只需要比對監看清單。
/// </summary>
static void PrintWatchDelta(DebugHaltedState previous, DebugHaltedState current)
{
    var previousExpressions = previous.Watches.Select(w => w.Expression).ToHashSet();
    var currentExpressions = current.Watches.Select(w => w.Expression).ToHashSet();

    foreach (WatchResult watch in current.Watches)
    {
        if (previousExpressions.Contains(watch.Expression))
        {
            continue;
        }

        string value = watch.TimedOut ? "逾時" : watch.Error ?? watch.Value ?? "null";
        AnsiConsole.MarkupLine($"    [dim]+ 監看[/] {Markup.Escape(watch.Expression)} = {Markup.Escape(value)}");
    }

    foreach (string expression in previousExpressions.Except(currentExpressions))
    {
        AnsiConsole.MarkupLine($"    [dim]- 監看[/] {Markup.Escape(expression)}");
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
          clrdiag                       啟動互動式儀表板（建置 / 伺服器 / 記憶體 / 堆疊 / 執行緒 / 偵錯）
          clrdiag --list                列出可監看的受控行程
          clrdiag --pid 12345           直接監看指定行程
          clrdiag --port 8080           覆寫設定檔的連接埠
          clrdiag --snapshot [--top N]  取一次堆疊快照後輸出文字並結束
          clrdiag --threads             輸出受控執行緒堆疊後結束
          clrdiag --roots <型別全名>    找出該型別的物件被哪個 GC 根握住（上限 30 秒）
          clrdiag --export              取一次快照並輸出 CSV
          clrdiag --build [Release]     依設定建置一次（不進互動介面）
          clrdiag --output [--pid N]    串流應用程式的 Debug/Trace 輸出（OutputDebugString），Ctrl+C 結束
          clrdiag --dap                 非互動除錯：印出每次中斷的堆疊/區域變數/監看，Ctrl+C 結束
          clrdiag --send '<json>'       對本機（或 --pid 對應）專案的除錯指令管道送一個指令並印出回覆
          clrdiag --render              把九個面板渲染成純文字（可貼進問題回報）
          clrdiag --init                在專案根目錄產生 clrdiag.json 範本
          clrdiag --install-skill       安裝 Claude Code 技能（後接 global 或 local）
          clrdiag --force               搭配 --install-skill：安裝位置已有真實目錄時覆寫它
          clrdiag --config <path>       指定設定檔
          clrdiag --root <path>         指定專案根目錄

        設定檔
          在專案根目錄放 clrdiag.json（會從目前目錄往上尋找）即可設定建置指令、
          啟動伺服器指令、要監看的行程名稱與自己的命名空間。
          沒有設定檔時會自動偵測 .sln / .csproj，仍可監看、快照、分析既有行程。

        互動按鍵
          0/1/2        選 build / serve / process 面板
          3/4/5/6/7/8  選 記憶體 / 堆疊 / 執行緒 / 記錄 / 輸出 / 偵錯
                       同一個數字再按一次＝放大該面板（Esc 或同號鍵還原）；上排數字與數字鍵盤都可用
          b 建置    c 切換設定    s 啟動    Shift+S 準備下次以除錯器啟動    x 停止    r 重建並重啟
          n 取快照  T 只更新執行緒堆疊    d 設比較基準（Shift+D 清除）    a 自動快照
          o 切換排序  / 過濾型別  f 找出根參考鏈  e 匯出 CSV  p 切換行程
          g 輸出檢視的 PID 範圍（只看附加 PID / 全部行程）  w 新增/移除監看運算式
          F5 續行  F10 下一步  F11 進入函式  Shift+F11 跳出函式  F6 暫停
          q 離開

        除錯（.NET 8+，需要 netcoredbg）
          在 Neovim 或 VS Code 設定中斷點，中斷後的呼叫堆疊、區域變數、監看結果都顯示在本工具
          的偵錯分頁（按 8）。ClrDiag 是唯一的 DAP 客戶端，編輯器只透過具名管道送意圖過來，
          細節見 README「除錯」一節。

        """
    );
}
