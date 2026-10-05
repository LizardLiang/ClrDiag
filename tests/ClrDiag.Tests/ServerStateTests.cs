using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ClrDiag.Core;
using static ClrDiag.Tests.TestPorts;
using static ClrDiag.Tests.TestProcesses;

namespace ClrDiag.Tests;

/// <summary>
/// ServerService 的狀態轉換：停止失敗與還原、啟動中／除錯階段的互斥、連接埠還原。
/// 停止路徑用假的 ProcessControl（不動真行程）；啟動互斥用真的 TestServer 行程。
/// </summary>
public sealed class ServerStateTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>假的行程操作：記錄每次 Kill，行程是否仍在執行與建立時間由測試控制。</summary>
    private sealed class FakeControl
    {
        public bool Running { get; set; } = true;
        public DateTime? Start { get; set; } = T0;
        public bool DieOnKill { get; set; }
        public List<(int Pid, bool Force)> Kills { get; } = new();

        public ProcessControl Control =>
            new(
                _ => Start,
                _ => Running,
                (pid, force) =>
                {
                    Kills.Add((pid, force));
                    if (DieOnKill)
                    {
                        Running = false;
                    }

                    return true;
                }
            );
    }

    private static ServerService FastStopService(FakeControl fake, int port = 5000) =>
        new(new DiagConfig(), new LogBuffer(), port, control: fake.Control)
        {
            StopPollInterval = TimeSpan.FromMilliseconds(1),
            StopSignalAttempts = 2,
            StopForceAttempts = 2,
        };

    [Fact]
    public async Task 停止後行程仍活著時回報停止失敗並還原接管狀態()
    {
        var fake = new FakeControl();
        using ServerService server = FastStopService(fake);
        server.AdoptExisting(4242);

        bool stopped = await server.StopAsync(CancellationToken.None);

        Assert.False(stopped);
        Assert.Equal(ServerFailure.StopFailed, server.LastFailure);
        Assert.Equal(ServerState.External, server.State);
        Assert.Equal(4242, server.ServerPid);
        Assert.Contains((4242, false), fake.Kills);
        Assert.Contains((4242, true), fake.Kills);
    }

    [Fact]
    public async Task 行程在結束指令後消失時停止成功並清掉PID()
    {
        var fake = new FakeControl { DieOnKill = true };
        using ServerService server = FastStopService(fake);
        server.AdoptExisting(4242);

        bool stopped = await server.StopAsync(CancellationToken.None);

        Assert.True(stopped);
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.Null(server.ServerPid);
        Assert.Equal(ServerFailure.None, server.LastFailure);
        Assert.Equal(new[] { (4242, false) }, fake.Kills);
    }

    [Fact]
    public async Task 讀不到建立時間時不送結束指令並回報停止失敗()
    {
        var fake = new FakeControl { Start = null };
        using ServerService server = FastStopService(fake);
        server.AdoptExisting(4242);

        bool stopped = await server.StopAsync(CancellationToken.None);

        Assert.False(stopped);
        Assert.Empty(fake.Kills);
        Assert.Equal(ServerState.External, server.State);
        Assert.Equal(ServerFailure.StopFailed, server.LastFailure);
    }

    [Fact]
    public async Task PID被建立時間不同的行程重複使用時視為已結束且不送結束指令()
    {
        var fake = new FakeControl();
        using ServerService server = FastStopService(fake);
        server.AdoptExisting(4242);
        fake.Start = T0.AddMinutes(5);

        bool stopped = await server.StopAsync(CancellationToken.None);

        Assert.True(stopped);
        Assert.Empty(fake.Kills);
        Assert.Equal(ServerState.Stopped, server.State);
    }

    [Fact]
    public async Task 啟動中停止回報忙碌並且不動狀態與行程()
    {
        using ServerService server = new(Config("idle"), new LogBuffer(), FreePort());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<int?> starting = server.StartAsync(null, cts.Token);
        for (int i = 0; i < 100 && server.LastStartedPid is null; i++)
        {
            await Task.Delay(50);
        }

        int started = server.LastStartedPid!.Value;

        bool stopped = await server.StopAsync(CancellationToken.None);

        Assert.False(stopped);
        Assert.Equal(ServerState.Starting, server.State);
        Assert.True(Alive(started));

        cts.Cancel();
        Assert.Null(await starting);
    }

    [Fact]
    public async Task 啟動中不接受接管且啟動失敗後不留下PID()
    {
        using ServerService server = new(Config("idle"), new LogBuffer(), FreePort());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<int?> starting = server.StartAsync(null, cts.Token);
        for (int i = 0; i < 100 && server.LastStartedPid is null; i++)
        {
            await Task.Delay(50);
        }

        // 啟動期間收到除錯器的 process 事件，不能改寫啟動中的狀態與 PID
        bool adopted = server.AdoptDebuggee(Environment.ProcessId);
        cts.Cancel();
        await starting;

        Assert.False(adopted);
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.Null(server.ServerPid);
    }

    [Fact]
    public async Task 啟動失敗後連接埠還原成啟動前的值()
    {
        int original = FreePort();
        using ServerService server = new(Config("exit", "3"), new LogBuffer(), original);

        int? pid = await server.StartAsync(FreePort(), CancellationToken.None);

        Assert.Null(pid);
        Assert.Equal(original, server.Port);
    }

    [Fact]
    public async Task 除錯階段進行中再次除錯啟動被拒絕且不動既有行程()
    {
        using Process target = StartListening(out _);
        try
        {
            using ServerService server = new(Config("idle"), new LogBuffer(), FreePort());
            server.AdoptDebuggee(target.Id);

            int? pid = await server.StartUnderDebuggerAsync(null!, null, CancellationToken.None);

            Assert.Null(pid);
            Assert.Equal(ServerFailure.DebugSessionActive, server.LastFailure);
            Assert.Equal(ServerState.Debug, server.State);
            Assert.Equal(target.Id, server.ServerPid);
            Assert.Null(server.LastStartedPid);
            Assert.False(target.HasExited);
        }
        finally
        {
            Kill(target);
        }
    }

    [Fact]
    public async Task 前一次啟動的行程仍在執行時不再啟動也不覆蓋它()
    {
        // 伺服器行程對 Refresh 而言已消失（狀態回到 Stopped），但本工具啟動的行程其實還活著
        bool hideServer = false;
        int hidden = 0;
        ProcessControl control = ProcessControl.Default with
        {
            IsRunning = pid => !(hideServer && pid == hidden) && ProcessControl.Default.IsRunning(pid),
        };
        int port = FreePort();
        using ServerService server = new(Config("listen", "{port}"), new LogBuffer(), port, control: control);
        int? pid = await server.StartAsync(null, CancellationToken.None);
        try
        {
            Assert.NotNull(pid);
            hidden = pid!.Value;
            hideServer = true;
            server.Refresh();
            Assert.Equal(ServerState.Stopped, server.State);
            int firstStarted = server.LastStartedPid!.Value;

            int? second = await server.StartAsync(null, CancellationToken.None);

            Assert.Null(second);
            Assert.Equal(ServerFailure.StartedProcessStillRunning, server.LastFailure);
            Assert.Equal(firstStarted, server.LastStartedPid);
            Assert.True(Alive(firstStarted));
        }
        finally
        {
            hideServer = false;
            await server.StopAsync(CancellationToken.None);
            Assert.False(Alive(pid!.Value));
        }
    }

    [Fact]
    public async Task 啟動中被CleanupStartedProcess結束行程時啟動失敗收尾不重複處理()
    {
        LogBuffer log = new();
        using ServerService server = new(Config("idle"), log, FreePort());
        Task<int?> starting = server.StartAsync(null, CancellationToken.None);
        for (int i = 0; i < 100 && server.LastStartedPid is null; i++)
        {
            await Task.Delay(50);
        }

        int started = server.LastStartedPid!.Value;

        // 啟動進行到一半，別的流程（例如重建重啟）已經把 serveProcess 換掉並結束了行程
        server.CleanupStartedProcess();
        int? pid = await starting;

        Assert.Null(pid);
        Assert.NotEqual(ServerFailure.None, server.LastFailure);
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.False(server.HasStartedProcess);
        Assert.False(Alive(started));
    }

    [Fact]
    public async Task 伺服器PID已不在但仍持有啟動的行程時StopAsync仍會結束它()
    {
        // r 重建重啟的條件是 ServerPid 不為 null 或 HasStartedProcess；後者為 true 時要走 StopAsync 結束並釋放行程
        bool hideServer = false;
        int hidden = 0;
        ProcessControl control = ProcessControl.Default with
        {
            IsRunning = pid => !(hideServer && pid == hidden) && ProcessControl.Default.IsRunning(pid),
        };
        using ServerService server = new(Config("listen", "{port}"), new LogBuffer(), FreePort(), control: control);
        int? pid = await server.StartAsync(null, CancellationToken.None);
        try
        {
            Assert.NotNull(pid);
            hidden = pid!.Value;
            hideServer = true;
            server.Refresh();
            hideServer = false;
            Assert.Null(server.ServerPid);
            Assert.True(server.HasStartedProcess);

            bool stopped = await server.StopAsync(CancellationToken.None);

            Assert.True(stopped);
            Assert.False(server.HasStartedProcess);
            Assert.False(Alive(pid.Value));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task 啟動中收到除錯目標事件不記警告_停止中才記警告()
    {
        LogBuffer startingLog = new();
        using ServerService starting = new(Config("idle"), startingLog, FreePort());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<int?> startTask = starting.StartAsync(null, cts.Token);
        for (int i = 0; i < 100 && starting.LastStartedPid is null; i++)
        {
            await Task.Delay(50);
        }

        bool adoptedWhileStarting = starting.AdoptDebuggee(4242);
        cts.Cancel();
        await startTask;

        Assert.False(adoptedWhileStarting);
        Assert.DoesNotContain(startingLog.TakeLast(100), line => line.Kind == LogKind.Warning && line.Text.Contains("未接管"));

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool running = true;
        ProcessControl control = new(
            _ => T0,
            _ => running,
            (_, _) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                running = false;
                return true;
            }
        );
        LogBuffer stoppingLog = new();
        using ServerService stopping = new(new DiagConfig(), stoppingLog, 5000, control: control);
        stopping.AdoptExisting(4242);
        Task<bool> stopTask = Task.Run(() => stopping.StopAsync(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        bool adoptedWhileStopping = stopping.AdoptDebuggee(4243);
        release.Set();
        await stopTask;

        Assert.False(adoptedWhileStopping);
        Assert.Contains(stoppingLog.TakeLast(100), line => line.Kind == LogKind.Warning && line.Text.Contains("正在停止中，未接管 PID 4243"));
    }

    [Fact]
    public async Task 讀不到建立時間的警告每個PID只記一次()
    {
        var fake = new FakeControl { Start = null };
        LogBuffer log = new();
        using ServerService server = new(new DiagConfig(), log, 5000, control: fake.Control)
        {
            StopPollInterval = TimeSpan.FromMilliseconds(1),
            StopSignalAttempts = 2,
            StopForceAttempts = 2,
        };
        server.AdoptExisting(4242);

        await server.StopAsync(CancellationToken.None);

        Assert.Single(log.TakeLast(100), line => line.Kind == LogKind.Warning && line.Text.Contains("PID 4242 讀不到建立時間"));
    }

    [Fact]
    public async Task HTTP_sys已佔用連接埠而照常啟動成功時記錄探測可能連到別的站台()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        LogBuffer log = new();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            DiagConfig config = Config("idle") with { ProcessNames = new[] { "iisexpress" } };
            // 啟動的行程自己就被認成站台行程（名稱符合、64 位元）
            LocatorProbes probes = system.Probes with { ProcessName = _ => "iisexpress" };
            using ServerService server = new(config, log, port, probes);

            int? pid = await server.StartAsync(null, CancellationToken.None);
            try
            {
                Assert.NotNull(pid);
                Assert.Contains(
                    log.TakeLast(100),
                    line => line.Kind == LogKind.Warning && line.Text.Contains("HTTP.sys 佔用")
                );
            }
            finally
            {
                await server.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 啟動的站台行程是32位元時立即失敗而不等到逾時()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        LocatorProbes probes = system.Probes with { ProcessName = _ => "iisexpress", IsWow64 = _ => true };
        DiagConfig config = Config("idle") with { ProcessNames = new[] { "iisexpress" } };
        using ServerService server = new(config, new LogBuffer(), FreePort(), probes);

        var elapsed = Stopwatch.StartNew();
        int? pid = await server.StartAsync(null, CancellationToken.None);

        Assert.Null(pid);
        Assert.Equal(ServerFailure.Wow64, server.LastFailure);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(ServerState.Stopped, server.State);
        int started = server.LastStartedPid!.Value;
        for (int i = 0; i < 50 && Alive(started); i++)
        {
            await Task.Delay(100);
        }

        Assert.False(Alive(started));
    }

    private static Process StartListening(out int port)
    {
        port = FreePort();
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(TestServerDll);
        psi.ArgumentList.Add("listen");
        psi.ArgumentList.Add(port.ToString());
        return Process.Start(psi)!;
    }
}
