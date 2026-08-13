using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;

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
/// </summary>
public sealed class ServerService : IDisposable
{
    private readonly DiagConfig config;
    private readonly LogBuffer log;
    private readonly HttpClient probeClient;
    private readonly RingBuffer<ProbeResult> probes = new(120);
    private readonly object gate = new();

    private Process? serveProcess;

    public ServerService(DiagConfig config, LogBuffer log, int port)
    {
        this.config = config;
        this.log = log;
        Port = port;

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

    public int Port { get; private set; }

    public ServerState State { get; private set; } = ServerState.Stopped;

    /// <summary>目前監看的行程 PID（可能是本工具啟動的，也可能是外部既有的）。</summary>
    public int? ServerPid { get; private set; }

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
    public bool IsPortListening()
    {
        try
        {
            return IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == Port);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>找出可監看的受控行程（依設定的行程名稱，否則掃描所有載入 CLR 的行程）。</summary>
    public int? FindExistingServer() => ManagedProcessFinder.FindBest(config.ProcessNames);

    /// <summary>掛上外部既有的行程（attach-only 模式）。</summary>
    public void AdoptExisting(int pid)
    {
        ServerPid = pid;
        State = ServerState.External;
        log.Add("serve", LogKind.Info, $"接管既有行程 PID {pid}（非本工具啟動）");
    }

    /// <summary>掛上由 DapSessionService 啟動或附加的除錯目標；serve 面板會標示「(debug)」。</summary>
    public void AdoptDebuggee(int pid)
    {
        ServerPid = pid;
        State = ServerState.Debug;
        log.Add("serve", LogKind.Info, $"接管除錯目標 PID {pid}（掛著除錯器）");
    }

    /// <summary>啟動伺服器並回傳新行程的 PID。</summary>
    public async Task<int?> StartAsync(int? port, CancellationToken token)
    {
        if (State is ServerState.Starting or ServerState.Running)
        {
            log.Add("serve", LogKind.Warning, "伺服器已在執行中");
            return ServerPid;
        }

        if (port is not null)
        {
            Port = port.Value;
        }

        if (!config.CanServe)
        {
            log.Add(
                "serve",
                LogKind.Error,
                $"設定檔未指定 serveCommand，無法啟動伺服器（可在 {DiagConfig.FileName} 設定，或自行啟動後由本工具附加）"
            );
            return null;
        }

        if (IsPortListening())
        {
            log.Add("serve", LogKind.Warning, $"連接埠 {Port} 已被監聽，改為接管既有行程");
            int? existing = FindExistingServer();
            if (existing is not null)
            {
                AdoptExisting(existing.Value);
            }

            return existing;
        }

        State = ServerState.Starting;
        HashSet<int> before = SnapshotCandidatePids();

        Process process;
        try
        {
            process = StartWrapperProcess(BuildServeStartInfo());
            serveProcess = process;
            log.Add(
                "serve",
                LogKind.Info,
                $"{Path.GetFileName(process.StartInfo.FileName)} 啟動中，連接埠 {Port}"
            );
        }
        catch (Exception ex)
        {
            State = ServerState.Stopped;
            log.Add("serve", LogKind.Error, $"啟動失敗: {ex.Message}");
            return null;
        }

        // 啟動指令通常會再開子行程（腳本 → 伺服器），等新行程出現後才取 PID
        int? found = await WaitForNewPidAsync(
                before,
                process,
                pollInterval: TimeSpan.FromMilliseconds(500),
                maxAttempts: 60,
                token
            )
            .ConfigureAwait(false);

        if (found is not null)
        {
            ServerPid = found;
            State = ServerState.Running;
            log.Add("serve", LogKind.Success, $"PID {found} 已啟動 → {Url}");
            return found;
        }

        State = ServerState.Stopped;
        log.Add(
            "serve",
            LogKind.Error,
            process.HasExited
                ? $"啟動指令已結束（結束碼 {process.ExitCode}），未偵測到新的受控行程"
                : "等待伺服器行程出現逾時（30 秒）"
        );
        return null;
    }

    /// <summary>
    /// 在除錯器下啟動伺服器：專治 serveCommand 是 `dotnet run` 這類 wrapper 的情形——netcoredbg
    /// 的 `launch` 只會附加到 wrapper 本身，wrapper 另外開的子行程（真正的 app）完全不受除錯器
    /// 控制，中斷點永遠不會命中（DiagConfig.IsWrapperServeCommand 的說明）。
    ///
    /// 做法：wrapper 行程照 StartAsync 原樣啟動（不受除錯器控制），但找子行程改用
    /// ChildProcessFinder.DirectChildrenOf（Toolhelp32Snapshot 直接查 wrapper 的子行程），
    /// 不是 StartAsync 用的 SnapshotCandidatePids／WaitForNewPidAsync 那套「掃全部行程找
    /// coreclr.dll、跟啟動前的快照取差集」——實測沒設定 processNames 時那套機制單次呼叫要
    /// 5～8 秒（機器上行程數量多，逐一開 Modules 檢查很慢），子行程早就跑過啟動路徑上的
    /// 中斷點才會被偵測到，等於讓這個方法的存在意義落空。ChildProcessFinder 只讀行程清單的
    /// PID／PPID，不開行程控制代碼、不列舉模組，可以用遠低於 50ms 的間隔輪詢，把「行程出現」
    /// 到「除錯器接手」的間隔壓到最短。
    ///
    /// 這個間隔仍然不是零——不是行程建立時的強制暫停（Win32 CREATE_SUSPENDED 那一類機制），
    /// 只是盡快追上去。極早、只有一兩行就執行完的啟動路徑仍可能撲空；但 DI 期間、
    /// ConfigureServices 這類實際會設中斷點的位置，執行到那裡所需的時間遠大於輪詢間隔，
    /// 可穩定命中（見 README「除錯」一節）。
    ///
    /// 找不到子行程、wrapper 提前結束、或 attach 本身失敗，一律視為失敗、清掉 wrapper 行程並在
    /// 「6 記錄」印出明確原因——絕不會悄悄退化成「附加到 wrapper、中斷點全部不會命中」的狀態。
    /// </summary>
    public async Task<int?> StartUnderDebuggerAsync(
        DapSessionService dap,
        string? adapterPathOverride,
        CancellationToken token
    )
    {
        if (!config.CanServe)
        {
            log.Add("serve", LogKind.Error, $"設定檔未指定 serveCommand，無法在除錯器下啟動");
            return null;
        }

        if (IsPortListening())
        {
            log.Add(
                "serve",
                LogKind.Error,
                $"連接埠 {Port} 已被監聽，無法在除錯器下啟動新行程（先按 x 停止既有行程，除錯啟動需要一個全新的行程才能命中啟動路徑上的中斷點）"
            );
            return null;
        }

        State = ServerState.Starting;

        Process wrapper;
        try
        {
            wrapper = StartWrapperProcess(BuildServeStartInfo());
            serveProcess = wrapper;
            log.Add(
                "serve",
                LogKind.Info,
                $"{Path.GetFileName(wrapper.StartInfo.FileName)}（wrapper）啟動中，等待實際 app 子行程…"
            );
        }
        catch (Exception ex)
        {
            State = ServerState.Stopped;
            log.Add("serve", LogKind.Error, $"啟動 wrapper 失敗: {ex.Message}");
            return null;
        }

        int? childPid = await WaitForDirectChildAsync(
                wrapper,
                pollInterval: TimeSpan.FromMilliseconds(20),
                maxAttempts: 1500,
                token
            )
            .ConfigureAwait(false);

        if (childPid is null)
        {
            State = ServerState.Stopped;
            string reason = wrapper.HasExited
                ? $"wrapper 已結束（結束碼 {wrapper.ExitCode}），未偵測到子行程"
                : "等待 wrapper 產生子行程逾時（30 秒）";
            log.Add(
                "serve",
                LogKind.Error,
                $"除錯啟動失敗：{reason}。serveCommand/serveArguments 可能不是會再開子行程的 wrapper，"
                    + "或子行程啟動太慢；也可考慮改指向建置好的組件本身（例如 \"dotnet\", [\"bin/Debug/net8.0/MyApp.dll\"]）"
            );
            CleanupDebugWrapper();
            return null;
        }

        log.Add("serve", LogKind.Info, $"偵測到子行程 PID {childPid}，附加除錯器中…");
        bool attached = await dap.AttachAsync(childPid.Value, adapterPathOverride, ownedByThisTool: true, token)
            .ConfigureAwait(false);

        if (!attached)
        {
            State = ServerState.Stopped;
            log.Add(
                "serve",
                LogKind.Error,
                $"除錯啟動失敗：附加到 PID {childPid} 失敗（{dap.LastError}）——子行程與 wrapper 行程已一併清除，"
                    + "不會留下沒有除錯器附加、卻繼續佔用連接埠的殭屍行程"
            );
            // 附加失敗時子行程可能已經在跑（甚至佔著連接埠），跟 wrapper 一起清乾淨，
            // 不留下「沒有除錯器、也沒人知道」的殭屍行程
            RunTaskKill($"/PID {childPid}");
            CleanupDebugWrapper();
            return null;
        }

        AdoptDebuggee(childPid.Value);
        log.Add(
            "serve",
            LogKind.Success,
            $"已在除錯器下啟動：wrapper → 子行程 PID {childPid}（等待中斷點，按 8 看偵錯分頁）"
        );
        return childPid;
    }

    /// <summary>
    /// 清掉 StartUnderDebuggerAsync 啟動的 wrapper 行程。直接 launch（非 wrapper）沒有 wrapper
    /// 行程可清，這裡呼叫也安全（serveProcess 為 null 時直接略過）。除錯階段結束時
    /// （DisconnectAsync 已經連帶終止子行程）與除錯啟動本身失敗時都會呼叫這個方法收尾。
    /// </summary>
    public void CleanupDebugWrapper()
    {
        Process? wrapper = serveProcess;
        if (wrapper is null)
        {
            return;
        }

        serveProcess = null;
        try
        {
            if (!wrapper.HasExited)
            {
                wrapper.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // wrapper 可能已經自行結束（子行程退出後很多 wrapper 會跟著結束），略過
        }
        finally
        {
            wrapper.Dispose();
        }
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
            log.Add("serve", LogKind.Output, e.Data ?? string.Empty);
        process.ErrorDataReceived += (_, e) =>
            log.Add("serve", LogKind.Error, e.Data ?? string.Empty);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>
    /// 輪詢 wrapper 的直接子行程（見 StartUnderDebuggerAsync／ChildProcessFinder 為什麼不能用
    /// SnapshotCandidatePids 那套機制的說明）。實測 `dotnet run` 會先開一個 conhost.exe
    /// （終端機主控台的輔助行程，即使 CreateNoWindow/重新導向三個串流也還是會出現），
    /// 幾秒後才輪到真正的 app 子行程——如果不排除 conhost，會在 app 還沒起來前就誤判
    /// conhost 是目標並嘗試附加，白白浪費一次啟動機會（attach 到非受控行程，
    /// configurationDone 會直接失敗）。真的同時出現多個非 conhost 候選時挑 PID 最小的
    /// （最先建立的）——寧可有一個明確、可預期的行為，也不要隨機挑到不確定是哪個的行程。
    /// </summary>
    private async Task<int?> WaitForDirectChildAsync(
        Process wrapperProcess,
        TimeSpan pollInterval,
        int maxAttempts,
        CancellationToken token
    )
    {
        for (int attempt = 0; attempt < maxAttempts; attempt++)
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
    private static bool IsConhost(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName.Equals("conhost", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 等待新的候選行程出現（辨識 wrapper 產生的子行程）；StartAsync 用的差集輪詢邏輯——
    /// 一般啟動時間不敏感，掃全部受控行程（SnapshotCandidatePids）的開銷可以接受。
    /// 除錯啟動（StartUnderDebuggerAsync）對時間敏感得多，改用更快的 WaitForDirectChildAsync，
    /// 不共用這個方法（見該方法的說明）。
    ///
    /// 刻意排除 wrapperProcess.Id 本身：若設定的 processNames 剛好也符合 wrapper 執行檔
    /// （例如 processNames 含 "dotnet"，而 serveCommand 就是 `dotnet run`），wrapper 一啟動
    /// 就會符合候選條件，若不排除會把 wrapper 自己誤判成「新出現的目標行程」，導致監看
    /// 到 wrapper 而不是它底下真正的子行程。
    /// </summary>
    private async Task<int?> WaitForNewPidAsync(
        HashSet<int> before,
        Process wrapperProcess,
        TimeSpan pollInterval,
        int maxAttempts,
        CancellationToken token
    )
    {
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            await Task.Delay(pollInterval, token).ConfigureAwait(false);

            int? found = SnapshotCandidatePids()
                .Except(before)
                .Where(candidatePid => candidatePid != wrapperProcess.Id)
                .OrderBy(candidatePid => candidatePid)
                .Select(candidatePid => (int?)candidatePid)
                .FirstOrDefault();
            if (found is not null)
            {
                return found;
            }

            if (wrapperProcess.HasExited)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 停止伺服器。刻意不使用強制終止：以 IIS Express 為例，強制砍掉會讓 http.sys 留下
    /// URL 註冊，下次啟動同一個埠就會出現 0x800700b7。先送正常關閉訊號，逾時才退而求其次。
    /// </summary>
    public async Task StopAsync(CancellationToken token)
    {
        if (ServerPid is null && serveProcess is null)
        {
            log.Add("serve", LogKind.Warning, "沒有可停止的伺服器");
            return;
        }

        State = ServerState.Stopping;

        if (ServerPid is { } pid)
        {
            RunTaskKill($"/PID {pid}");
        }
        else if (config.ProcessNames.Length > 0)
        {
            RunTaskKill($"/IM {config.ProcessNames[0]}.exe");
        }

        for (int attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(500, token).ConfigureAwait(false);
            if (ServerPid is null || !IsProcessAlive(ServerPid.Value))
            {
                break;
            }
        }

        if (ServerPid is { } stubborn && IsProcessAlive(stubborn) && config.ProcessNames.Length > 0)
        {
            log.Add("serve", LogKind.Warning, "行程未回應關閉訊號，改以映像名稱再送一次");
            RunTaskKill($"/IM {config.ProcessNames[0]}.exe");
            await Task.Delay(1500, token).ConfigureAwait(false);
        }

        try
        {
            serveProcess?.Dispose();
        }
        catch
        {
            // 略過
        }

        serveProcess = null;
        ServerPid = null;
        State = ServerState.Stopped;
        log.Add("serve", LogKind.Info, "伺服器已停止");
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

    /// <summary>回報目標行程是否還活著，供 UI 偵測伺服器意外結束。</summary>
    public void Refresh()
    {
        if (ServerPid is { } pid && !IsProcessAlive(pid))
        {
            log.Add("serve", LogKind.Warning, $"監看的行程 PID {pid} 已結束");
            ServerPid = null;
            State = ServerState.Stopped;
        }
    }

    private void AddProbe(ProbeResult result)
    {
        lock (gate)
        {
            probes.Add(result);
        }
    }

    /// <summary>
    /// 取得候選行程 PID 集合，用來比對啟動前後的差異。
    /// 有設定 processNames 就依名稱找（此時行程可能還沒載入 CLR，不能只看受控行程）；
    /// 沒設定就掃描所有受控行程 —— 新啟動的網站載入 CLR 後就會出現在這份清單裡。
    /// </summary>
    private HashSet<int> SnapshotCandidatePids()
    {
        if (config.ProcessNames.Length == 0)
        {
            return ManagedProcessFinder.List(Array.Empty<string>()).Select(p => p.Pid).ToHashSet();
        }

        var pids = new HashSet<int>();
        foreach (string name in config.ProcessNames)
        {
            Process[] processes = Process.GetProcessesByName(name);
            try
            {
                foreach (Process process in processes)
                {
                    pids.Add(process.Id);
                }
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return pids;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void RunTaskKill(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("taskkill", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 同上：沒有主控台就沒有 CONIN$ 可開，taskkill 不會搶走 TUI 的按鍵。
                RedirectStandardInput = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            };

            using Process? process = Process.Start(psi);
            process?.WaitForExit(5000);
            log.Add("serve", LogKind.Info, $"taskkill {arguments}");
        }
        catch (Exception ex)
        {
            log.Add("serve", LogKind.Error, $"taskkill 失敗: {ex.Message}");
        }
    }

    public void Dispose()
    {
        probeClient.Dispose();
        serveProcess?.Dispose();
    }
}
