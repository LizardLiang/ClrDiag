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

        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
            log.Add("serve", LogKind.Output, e.Data ?? string.Empty);
        process.ErrorDataReceived += (_, e) =>
            log.Add("serve", LogKind.Error, e.Data ?? string.Empty);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            serveProcess = process;
            log.Add(
                "serve",
                LogKind.Info,
                $"{Path.GetFileName(psi.FileName)} 啟動中，連接埠 {Port}"
            );
        }
        catch (Exception ex)
        {
            State = ServerState.Stopped;
            log.Add("serve", LogKind.Error, $"啟動失敗: {ex.Message}");
            return null;
        }

        // 啟動指令通常會再開子行程（腳本 → 伺服器），等新行程出現後才取 PID
        for (int attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(500, token).ConfigureAwait(false);

            int? found = SnapshotCandidatePids()
                .Except(before)
                .OrderBy(pid => pid)
                .Select(pid => (int?)pid)
                .FirstOrDefault();
            if (found is not null)
            {
                ServerPid = found;
                State = ServerState.Running;
                log.Add("serve", LogKind.Success, $"PID {found} 已啟動 → {Url}");
                return found;
            }

            if (process.HasExited)
            {
                State = ServerState.Stopped;
                log.Add(
                    "serve",
                    LogKind.Error,
                    $"啟動指令已結束（結束碼 {process.ExitCode}），未偵測到新的受控行程"
                );
                return null;
            }
        }

        State = ServerState.Stopped;
        log.Add("serve", LogKind.Error, "等待伺服器行程出現逾時（30 秒）");
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
