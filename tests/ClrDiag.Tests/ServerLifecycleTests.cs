using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ClrDiag.Core;
using static ClrDiag.Tests.TestPorts;

namespace ClrDiag.Tests;

/// <summary>
/// 以真的行程驗證 ServerService 的啟動與停止：用 ClrDiag.TestServer（受控行程）當伺服器，
/// 確認辨識的是監聽連接埠的行程、停止只動自己啟動的那一棵樹，不碰名稱相同的其他行程。
/// 各測試用自己的連接埠與行程，互不干擾；成功啟動伺服器的測試在 finally 停止它，啟動失敗的測試由 StartAsync 自己清除啟動的行程。
/// </summary>
public sealed class ServerLifecycleTests
{
    private static readonly string TestServerDll = Path.Combine(AppContext.BaseDirectory, "ClrDiag.TestServer.dll");

    private static DiagConfig Config(string mode, params string[] extra) =>
        new()
        {
            ServeCommand = "dotnet",
            ServeArguments = new[] { TestServerDll, mode }.Concat(extra).ToArray(),
        };

    private static ServerService Service(
        DiagConfig config,
        LogBuffer log,
        int port,
        int timeoutSeconds = 30,
        LocatorProbes? probes = null
    ) => new(config, log, port, probes) { StartTimeout = TimeSpan.FromSeconds(timeoutSeconds) };

    private static bool Alive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task WaitGoneAsync(int pid)
    {
        for (int i = 0; i < 50 && Alive(pid); i++)
        {
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// 在空閒連接埠啟動測試伺服器；連接埠剛好被別人搶走（TestServer 綁定失敗而結束）就換一個重試。
    /// </summary>
    private static async Task<(ServerService Server, LogBuffer Log, int Port, int? Pid)> StartOnFreePortAsync(string mode)
    {
        ServerService? server = null;
        LogBuffer log = new();
        int port = 0;
        int? pid = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            server?.Dispose();
            log = new LogBuffer();
            port = FreePort();
            server = Service(Config(mode, "{port}"), log, port);
            pid = await server.StartAsync(null, CancellationToken.None);
            if (pid is not null || server.LastFailure != ServerFailure.StartCommandExited)
            {
                break;
            }
        }

        return (server!, log, port, pid);
    }

    /// <summary>啟動一個與被測行程同映像名稱（dotnet）、監聽別的連接埠的旁觀行程；確認它真的在監聽才回傳。</summary>
    private static Process StartBystander(out int port)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            port = FreePort();
            var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(TestServerDll);
            psi.ArgumentList.Add("listen");
            psi.ArgumentList.Add(port.ToString());
            Process process = Process.Start(psi)!;
            for (int i = 0; i < 100 && !process.HasExited && !PortOwnerFinder.IsListening(port); i++)
            {
                Thread.Sleep(100);
            }

            if (!process.HasExited && PortOwnerFinder.IsListening(port))
            {
                return process;
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
        }

        throw new InvalidOperationException("無法啟動監聽中的旁觀行程");
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 已經結束
        }
    }

    [Fact]
    public async Task 啟動指令本身就是伺服器時辨識出它並能以PID停止且不碰同名的其他行程()
    {
        using Process bystander = StartBystander(out int bystanderPort);
        ServerService? server = null;
        try
        {
            int port;
            int? pid;
            (server, _, port, pid) = await StartOnFreePortAsync("listen");

            Assert.NotNull(pid);
            Assert.Equal(server.LastStartedPid, pid);
            Assert.NotEqual(bystander.Id, pid);
            Assert.Equal(ServerState.Running, server.State);
            Assert.Equal(new[] { pid!.Value }, PortOwnerFinder.FindListenerPids(port));

            bool stopped = await server.StopAsync(CancellationToken.None);

            Assert.True(stopped);
            Assert.Equal(ServerState.Stopped, server.State);
            Assert.Null(server.ServerPid);
            Assert.False(Alive(pid.Value));
            Assert.False(bystander.HasExited);
            Assert.True(PortOwnerFinder.IsListening(bystanderPort));
        }
        finally
        {
            if (server is not null)
            {
                await server.StopAsync(CancellationToken.None);
                server.Dispose();
            }

            KillQuietly(bystander);
        }
    }

    [Fact]
    public async Task wrapper的子行程監聽時採用子行程且停止時整棵樹都結束()
    {
        ServerService? server = null;
        try
        {
            int port;
            int? pid;
            (server, _, port, pid) = await StartOnFreePortAsync("wrap");

            Assert.NotNull(pid);
            int wrapperPid = server.LastStartedPid!.Value;
            Assert.NotEqual(wrapperPid, pid);
            Assert.Contains(pid!.Value, ChildProcessFinder.DescendantsOf(wrapperPid));
            Assert.Equal(new[] { pid.Value }, PortOwnerFinder.FindListenerPids(port));

            bool stopped = await server.StopAsync(CancellationToken.None);

            Assert.True(stopped);
            Assert.False(Alive(pid.Value));
            Assert.False(Alive(wrapperPid));
            Assert.Equal(ServerState.Stopped, server.State);
        }
        finally
        {
            if (server is not null)
            {
                await server.StopAsync(CancellationToken.None);
                server.Dispose();
            }
        }
    }

    [Fact]
    public async Task 啟動指令提早結束時回報結束碼並維持已停止()
    {
        using ServerService server = Service(Config("exit", "3"), new LogBuffer(), FreePort());

        int? pid = await server.StartAsync(null, CancellationToken.None);

        Assert.Null(pid);
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.Null(server.ServerPid);
        Assert.Equal(ServerFailure.StartCommandExited, server.LastFailure);
    }

    [Fact]
    public async Task 等不到監聽者時逾時並清除啟動的行程樹()
    {
        using ServerService server = Service(Config("idle"), new LogBuffer(), FreePort(), timeoutSeconds: 2);

        int? pid = await server.StartAsync(null, CancellationToken.None);

        Assert.Null(pid);
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.Equal(ServerFailure.StartTimedOut, server.LastFailure);
        int started = server.LastStartedPid!.Value;
        await WaitGoneAsync(started);
        Assert.False(Alive(started));
    }

    [Fact]
    public async Task 啟動被取消時清除啟動的行程並還原狀態()
    {
        using ServerService server = Service(Config("idle"), new LogBuffer(), FreePort());
        // 斷言失敗時也由計時取消，不會讓啟動中的行程等滿 StartTimeout
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Task<int?> starting = server.StartAsync(null, cts.Token);
        for (int i = 0; i < 100 && server.LastStartedPid is null; i++)
        {
            await Task.Delay(50);
        }

        Assert.NotNull(server.LastStartedPid);
        int started = server.LastStartedPid!.Value;
        cts.Cancel();

        Assert.Null(await starting);
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.Equal(ServerFailure.StartCancelled, server.LastFailure);
        await WaitGoneAsync(started);
        Assert.False(Alive(started));
    }

    [Fact]
    public async Task 啟動進行中再次啟動只回報執行中而不再開行程()
    {
        using ServerService server = Service(Config("idle"), new LogBuffer(), FreePort());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Task<int?> starting = server.StartAsync(null, cts.Token);
        for (int i = 0; i < 100 && server.LastStartedPid is null; i++)
        {
            await Task.Delay(50);
        }

        int first = server.LastStartedPid!.Value;
        int? second = await server.StartAsync(null, CancellationToken.None);

        Assert.Null(second);
        Assert.Equal(first, server.LastStartedPid);

        cts.Cancel();
        Assert.Null(await starting);
        await WaitGoneAsync(first);
        Assert.False(Alive(first));
    }

    [Fact]
    public async Task 停止接管的行程只結束該PID而不是同名的其他行程()
    {
        Process? target = null;
        Process? bystander = null;
        ServerService? server = null;
        try
        {
            target = StartBystander(out int targetPort);
            bystander = StartBystander(out int bystanderPort);
            server = new ServerService(new DiagConfig(), new LogBuffer(), targetPort);
            Assert.Equal(target.Id, server.FindExistingServer());
            server.AdoptExisting(target.Id);

            bool stopped = await server.StopAsync(CancellationToken.None);

            Assert.True(stopped);
            Assert.True(target.WaitForExit(5000));
            Assert.False(bystander.HasExited);
            Assert.True(PortOwnerFinder.IsListening(bystanderPort));
            Assert.Equal(ServerState.Stopped, server.State);
        }
        finally
        {
            server?.Dispose();
            foreach (Process? process in new[] { bystander, target })
            {
                if (process is not null)
                {
                    KillQuietly(process);
                    process.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task 沒有可停止的伺服器時停止回報成功且狀態不變()
    {
        using ServerService server = Service(Config("idle"), new LogBuffer(), FreePort());

        bool stopped = await server.StopAsync(CancellationToken.None);

        Assert.True(stopped);
        Assert.Equal(ServerState.Stopped, server.State);
    }

    [Fact]
    public async Task HTTP_sys擁有連接埠且沒設定行程名稱時啟動立即失敗並清除行程()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        using ServerService server = Service(Config("idle"), new LogBuffer(), FreePort(), probes: system.Probes);

        var elapsed = Stopwatch.StartNew();
        int? pid = await server.StartAsync(null, CancellationToken.None);

        Assert.Null(pid);
        Assert.Equal(ServerFailure.HttpSysNeedsProcessNames, server.LastFailure);
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20));
        int started = server.LastStartedPid!.Value;
        await WaitGoneAsync(started);
        Assert.False(Alive(started));
    }

    [Fact]
    public async Task 連接埠被HTTP_sys占用但沒有行程服務它時照常啟動()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var config = Config("idle") with { ProcessNames = new[] { "iisexpress" } };
            using ServerService server = Service(config, new LogBuffer(), port, timeoutSeconds: 2, probes: system.Probes);

            int? pid = await server.StartAsync(null, CancellationToken.None);

            Assert.Null(pid);
            Assert.NotNull(server.LastStartedPid);
            Assert.Equal(ServerFailure.StartTimedOut, server.LastFailure);
            int started = server.LastStartedPid!.Value;
            await WaitGoneAsync(started);
            Assert.False(Alive(started));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 連接埠被監聽且既有行程不能接管時不啟動新行程()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Add(10, "iisexpress", "iisexpress.exe /port:5002", managed: false);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var config = Config("idle") with { ProcessNames = new[] { "iisexpress" } };
            using ServerService server = Service(config, new LogBuffer(), port, probes: system.Probes);
            system.CommandLines[10] = $"iisexpress.exe /port:{port}";

            int? pid = await server.StartAsync(null, CancellationToken.None);

            Assert.Null(pid);
            Assert.Null(server.LastStartedPid);
            Assert.Equal(ServerFailure.NotLoadedYet, server.LastFailure);
            Assert.Equal(ServerState.Stopped, server.State);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 命令列超過16383個字元時仍能讀到完整命令列()
    {
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(TestServerDll);
        psi.ArgumentList.Add("idle");
        psi.ArgumentList.Add(new string('x', 20000));
        using Process process = Process.Start(psi)!;
        try
        {
            string? commandLine = null;
            for (int i = 0; i < 50 && commandLine is null; i++)
            {
                await Task.Delay(100);
                commandLine = NativeProcess.CommandLine(process.Id);
            }

            Assert.NotNull(commandLine);
            Assert.True(commandLine!.Length > 20000);
        }
        finally
        {
            KillQuietly(process);
        }
    }

    [Fact]
    public async Task 已接管行程時啟動失敗不改變接管狀態()
    {
        using Process target = StartBystander(out int targetPort);
        try
        {
            var system = new FakeSystem { Listeners = new[] { 900, 901 } };
            using ServerService server = Service(Config("idle"), new LogBuffer(), targetPort, probes: system.Probes);
            server.AdoptExisting(target.Id);

            int? pid = await server.StartAsync(null, CancellationToken.None);

            Assert.Null(pid);
            Assert.Equal(ServerFailure.MultipleListeners, server.LastFailure);
            Assert.Equal(ServerState.External, server.State);
            Assert.Equal(target.Id, server.ServerPid);
        }
        finally
        {
            KillQuietly(target);
        }
    }

    [Fact]
    public async Task 除錯階段進行中再次啟動不結束既有的伺服器行程()
    {
        bool failLocate = false;
        LocatorProbes probes = LocatorProbes.Default with
        {
            // 啟動後讓「連接埠已被監聽但辨識不出擁有者」成立，走接管失敗的分支
            ListenerPids = port => failLocate ? new[] { 900, 901 } : PortOwnerFinder.FindListenerPids(port),
        };
        int port = FreePort();
        using ServerService server = Service(Config("listen", "{port}"), new LogBuffer(), port, probes: probes);
        int? pid = await server.StartAsync(null, CancellationToken.None);
        try
        {
            Assert.NotNull(pid);
            // 除錯器附加到目前的伺服器行程（例如對它按 attach），狀態變成 Debug
            server.AdoptDebuggee(pid!.Value);
            failLocate = true;

            int? second = await server.StartAsync(null, CancellationToken.None);

            Assert.True(Alive(pid.Value));
            Assert.Null(second);
            Assert.Equal(ServerFailure.DebugSessionActive, server.LastFailure);
            Assert.Equal(ServerState.Debug, server.State);
            Assert.Equal(pid, server.ServerPid);
        }
        finally
        {
            failLocate = false;
            if (pid is not null)
            {
                server.AdoptExisting(pid.Value);
            }

            await server.StopAsync(CancellationToken.None);
        }
    }
}
