using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ClrDiag.Core;

namespace ClrDiag.Tests;

/// <summary>
/// 以真的行程驗證 ServerService 的啟動與停止：用 ClrDiag.TestServer（受控行程）當伺服器，
/// 確認辨識的是監聽連接埠的行程、停止只動自己啟動的那一棵樹，不碰名稱相同的其他行程。
/// 各測試用自己的連接埠與行程，互不干擾。
/// </summary>
public sealed class ServerLifecycleTests
{
    private static readonly string TestServerDll = Path.Combine(AppContext.BaseDirectory, "ClrDiag.TestServer.dll");

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static DiagConfig Config(string mode, params string[] extra) =>
        new()
        {
            ServeCommand = "dotnet",
            ServeArguments = new[] { TestServerDll, mode }.Concat(extra).ToArray(),
        };

    private static ServerService Service(DiagConfig config, LogBuffer log, int port, int timeoutSeconds = 30) =>
        new(config, log, port) { StartTimeout = TimeSpan.FromSeconds(timeoutSeconds) };

    private static bool Contains(LogBuffer log, string text) =>
        log.TakeLast(log.Count).Any(l => l.Text.Contains(text, StringComparison.Ordinal));

    private static int StartedPid(LogBuffer log)
    {
        string line = log.TakeLast(log.Count).First(l => l.Text.Contains("啟動中", StringComparison.Ordinal)).Text;
        return int.Parse(Regex.Match(line, @"PID (\d+)").Groups[1].Value);
    }

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

    /// <summary>啟動一個與被測行程同映像名稱（dotnet）、監聽別的連接埠的旁觀行程。</summary>
    private static Process StartBystander(out int port)
    {
        port = FreePort();
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(TestServerDll);
        psi.ArgumentList.Add("listen");
        psi.ArgumentList.Add(port.ToString());
        Process process = Process.Start(psi)!;
        for (int i = 0; i < 100 && !PortOwnerFinder.IsListening(port); i++)
        {
            Thread.Sleep(100);
        }

        return process;
    }

    [Fact]
    public async Task 啟動指令本身就是伺服器時辨識出它並能以PID停止且不碰同名的其他行程()
    {
        using Process bystander = StartBystander(out int bystanderPort);
        try
        {
            int port = FreePort();
            var log = new LogBuffer();
            using ServerService server = Service(Config("listen", "{port}"), log, port);

            int? pid = await server.StartAsync(null, CancellationToken.None);

            Assert.NotNull(pid);
            Assert.Equal(StartedPid(log), pid);
            Assert.NotEqual(bystander.Id, pid);
            Assert.Equal(ServerState.Running, server.State);
            Assert.Equal(pid, PortOwnerFinder.FindListenerPid(port));

            await server.StopAsync(CancellationToken.None);

            Assert.Equal(ServerState.Stopped, server.State);
            Assert.Null(server.ServerPid);
            Assert.False(Alive(pid!.Value));
            Assert.False(bystander.HasExited);
            Assert.True(PortOwnerFinder.IsListening(bystanderPort));
            Assert.False(Contains(log, "/IM"));
        }
        finally
        {
            bystander.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task wrapper的子行程監聽時採用子行程且停止時整棵樹都結束()
    {
        int port = FreePort();
        var log = new LogBuffer();
        using ServerService server = Service(Config("wrap", "{port}"), log, port);

        int? pid = await server.StartAsync(null, CancellationToken.None);

        Assert.NotNull(pid);
        int wrapperPid = StartedPid(log);
        Assert.NotEqual(wrapperPid, pid);
        Assert.Contains(pid!.Value, ChildProcessFinder.DescendantsOf(wrapperPid));
        Assert.Equal(pid, PortOwnerFinder.FindListenerPid(port));

        await server.StopAsync(CancellationToken.None);

        Assert.False(Alive(pid.Value));
        Assert.False(Alive(wrapperPid));
        Assert.Equal(ServerState.Stopped, server.State);
    }

    [Fact]
    public async Task 啟動指令提早結束時回報結束碼並維持已停止()
    {
        var log = new LogBuffer();
        using ServerService server = Service(Config("exit", "3"), log, FreePort());

        int? pid = await server.StartAsync(null, CancellationToken.None);

        Assert.Null(pid);
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.Null(server.ServerPid);
        Assert.True(Contains(log, "結束碼 3"));
    }

    [Fact]
    public async Task 等不到監聽者時逾時並清除啟動的行程樹()
    {
        var log = new LogBuffer();
        using ServerService server = Service(Config("idle"), log, FreePort(), timeoutSeconds: 2);

        int? pid = await server.StartAsync(null, CancellationToken.None);

        Assert.Null(pid);
        Assert.Equal(ServerState.Stopped, server.State);
        Assert.True(Contains(log, "逾時（"));
        Assert.True(Contains(log, "沒有人監聽"));
        int started = StartedPid(log);
        for (int i = 0; i < 30 && Alive(started); i++)
        {
            await Task.Delay(100);
        }

        Assert.False(Alive(started));
    }

    [Fact]
    public async Task 啟動被取消時清除啟動的行程並還原狀態()
    {
        var log = new LogBuffer();
        using ServerService server = Service(Config("idle"), log, FreePort());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));

        int? pid = await server.StartAsync(null, cts.Token);

        Assert.Null(pid);
        Assert.Equal(ServerState.Stopped, server.State);
        int started = StartedPid(log);
        for (int i = 0; i < 30 && Alive(started); i++)
        {
            await Task.Delay(100);
        }

        Assert.False(Alive(started));
    }

    [Fact]
    public async Task 停止接管的行程只結束該PID而不是同名的其他行程()
    {
        using Process target = StartBystander(out int targetPort);
        using Process bystander = StartBystander(out int bystanderPort);
        try
        {
            var log = new LogBuffer();
            using var server = new ServerService(new DiagConfig(), log, targetPort);
            Assert.Equal(target.Id, server.FindExistingServer());
            server.AdoptExisting(target.Id);

            await server.StopAsync(CancellationToken.None);

            Assert.True(target.WaitForExit(5000));
            Assert.False(bystander.HasExited);
            Assert.True(PortOwnerFinder.IsListening(bystanderPort));
            Assert.Equal(ServerState.Stopped, server.State);
        }
        finally
        {
            bystander.Kill(entireProcessTree: true);
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
            }
        }
    }
}
