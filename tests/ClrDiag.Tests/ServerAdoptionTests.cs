using System.Net;
using System.Net.Sockets;
using ClrDiag.Core;

namespace ClrDiag.Tests;

/// <summary>接管既有行程：只認監聽設定連接埠的受控行程，認不出來就不接管並寫入原因。</summary>
public sealed class ServerAdoptionTests
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static int? SingleListener(int port)
    {
        IReadOnlyList<int>? pids = PortOwnerFinder.FindListenerPids(port);
        return pids is { Count: 1 } ? pids[0] : null;
    }

    private static (ServerService Server, LogBuffer Log) Service(FakeSystem system, string[]? names = null, int port = 5000)
    {
        var log = new LogBuffer();
        var config = new DiagConfig { ProcessNames = names ?? Array.Empty<string>() };
        return (new ServerService(config, log, port, system.Probes), log);
    }

    [Fact]
    public void 查得到監聽連接埠的行程PID()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.Equal(Environment.ProcessId, SingleListener(port));
            Assert.True(PortOwnerFinder.IsListening(port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void IPv6監聽的連接埠也查得到行程PID()
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
        }
        catch (SocketException)
        {
            // 這台機器沒有啟用 IPv6，無從驗證
            return;
        }

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.Equal(Environment.ProcessId, SingleListener(port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void 同一行程同時監聽IPv4與IPv6時只回報一個PID()
    {
        TcpListener v4;
        TcpListener v6;
        try
        {
            v4 = new TcpListener(IPAddress.Loopback, 0);
            v4.Start();
            int port = ((IPEndPoint)v4.LocalEndpoint).Port;
            v6 = new TcpListener(IPAddress.IPv6Loopback, port);
            v6.Start();
        }
        catch (SocketException)
        {
            return;
        }

        try
        {
            int port = ((IPEndPoint)v4.LocalEndpoint).Port;

            Assert.Equal(new[] { Environment.ProcessId }, PortOwnerFinder.FindListenerPids(port));
        }
        finally
        {
            v4.Stop();
            v6.Stop();
        }
    }

    [Fact]
    public void 沒有人監聽的連接埠查不到行程()
    {
        int port = FreePort();

        Assert.Null(SingleListener(port));
        Assert.Empty(PortOwnerFinder.FindListenerPids(port)!);
        Assert.False(PortOwnerFinder.IsListening(port));
    }

    [Fact]
    public void 沒設定行程名稱且連接埠沒人監聽時不接管任何行程並寫入原因()
    {
        var log = new LogBuffer();
        using var server = new ServerService(new DiagConfig(), log, FreePort());

        Assert.Null(server.FindExistingServer());
        Assert.Equal(ServerFailure.NotListening, server.LastFailure);
    }

    [Fact]
    public void 沒設定行程名稱時接管監聽連接埠的受控行程()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var server = new ServerService(new DiagConfig(), new LogBuffer(), port);

            Assert.Equal(Environment.ProcessId, server.FindExistingServer());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void 設定的行程名稱符合監聽者時接管()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            var config = new DiagConfig { ProcessNames = new[] { self } };
            using var server = new ServerService(config, new LogBuffer(), port);

            Assert.Equal(Environment.ProcessId, server.FindExistingServer());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void 設定的行程名稱不符合監聽者時不接管()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var log = new LogBuffer();
            var config = new DiagConfig { ProcessNames = new[] { "clrdiag-no-such-process" } };
            using var server = new ServerService(config, log, port);

            Assert.Null(server.FindExistingServer());
            Assert.Equal(ServerFailure.NameMismatch, server.LastFailure);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void 設定的行程名稱找不到時不退回掃描全部受控行程()
    {
        var config = new DiagConfig { ProcessNames = new[] { "clrdiag-no-such-process" } };
        using var server = new ServerService(config, new LogBuffer(), FreePort());

        Assert.Null(server.FindExistingServer());
    }

    [Fact]
    public void 連接埠由HTTP_sys的PID4監聽且沒設定行程名稱時不接管()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        (ServerService server, LogBuffer log) = Service(system);
        using (server)
        {
            Assert.Null(server.FindExistingServer());
            Assert.Equal(ServerFailure.HttpSysNeedsProcessNames, server.LastFailure);
        }
    }

    [Fact]
    public void 監聽者不是受控行程時不接管()
    {
        var system = new FakeSystem { Listeners = new[] { 900 } };
        system.Names[900] = "nginx";
        (ServerService server, LogBuffer log) = Service(system);
        using (server)
        {
            Assert.Null(server.FindExistingServer());
            Assert.Equal(ServerFailure.NotManaged, server.LastFailure);
        }
    }

    [Fact]
    public void 監聽者是32位元行程時不接管並說明原因()
    {
        var system = new FakeSystem { Listeners = new[] { 900 } };
        system.Names[900] = "iisexpress";
        system.Wow64.Add(900);
        (ServerService server, LogBuffer log) = Service(system);
        using (server)
        {
            Assert.Null(server.FindExistingServer());
            Assert.Equal(ServerFailure.Wow64, server.LastFailure);
        }
    }

    [Fact]
    public void 查不到連接埠擁有者與多個擁有者時都不接管()
    {
        var failing = new FakeSystem { Listeners = null };
        (ServerService failedServer, LogBuffer failedLog) = Service(failing);
        using (failedServer)
        {
            Assert.Null(failedServer.FindExistingServer());
            Assert.Equal(ServerFailure.QueryFailed, failedServer.LastFailure);
        }

        var split = new FakeSystem { Listeners = new[] { 900, 901 } };
        split.Managed.UnionWith(new[] { 900, 901 });
        (ServerService splitServer, LogBuffer splitLog) = Service(split);
        using (splitServer)
        {
            Assert.Null(splitServer.FindExistingServer());
            Assert.Equal(ServerFailure.MultipleListeners, splitServer.LastFailure);
        }
    }

    [Fact]
    public void HTTP_sys站台依命令列的連接埠挑出名稱符合的行程()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Names[10] = "iisexpress";
        system.Managed.Add(10);
        system.Names[11] = "iisexpress";
        system.Managed.Add(11);
        system.CommandLines[10] = "\"C:\\IIS Express\\iisexpress.exe\" /path:C:\\a /port:5001";
        system.CommandLines[11] = "\"C:\\IIS Express\\iisexpress.exe\" /path:C:\\b /port:5002";
        (ServerService server, _) = Service(system, new[] { "iisexpress" }, port: 5002);
        using (server)
        {
            Assert.Equal(11, server.FindExistingServer());
        }
    }

    [Fact]
    public void HTTP_sys站台沒有任何行程服務該連接埠時不接管()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Names[10] = "iisexpress";
        system.Managed.Add(10);
        system.CommandLines[10] = "iisexpress.exe /path:C:\\a /port:5001";
        (ServerService server, LogBuffer log) = Service(system, new[] { "iisexpress" }, port: 5002);
        using (server)
        {
            Assert.Null(server.FindExistingServer());
            Assert.Equal(ServerFailure.NoServingProcess, server.LastFailure);
        }
    }

    [Fact]
    public void HTTP_sys站台有多個名稱相同的行程且無法由命令列區分時不接管()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Names[10] = "w3wp";
        system.Managed.Add(10);
        system.Names[11] = "w3wp";
        system.Managed.Add(11);
        system.CommandLines[10] = "w3wp.exe -ap \"A\"";
        system.CommandLines[11] = "w3wp.exe -ap \"B\"";
        (ServerService server, LogBuffer log) = Service(system, new[] { "w3wp" });
        using (server)
        {
            Assert.Null(server.FindExistingServer());
            Assert.Equal(ServerFailure.MultipleCandidates, server.LastFailure);
        }
    }

    [Fact]
    public void HTTP_sys站台有多個行程都指定同一連接埠時不接管()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Names[10] = "iisexpress";
        system.Managed.Add(10);
        system.Names[11] = "iisexpress";
        system.Managed.Add(11);
        system.CommandLines[10] = "iisexpress.exe /port:5000";
        system.CommandLines[11] = "iisexpress.exe /port:5000";
        (ServerService server, _) = Service(system, new[] { "iisexpress" });
        using (server)
        {
            Assert.Null(server.FindExistingServer());
        }
    }

    [Fact]
    public void HTTP_sys站台名稱符合的行程只有一個且命令列沒有連接埠時接管()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Names[10] = "w3wp";
        system.Managed.Add(10);
        system.CommandLines[10] = "w3wp.exe -ap \"A\"";
        (ServerService server, _) = Service(system, new[] { "w3wp" });
        using (server)
        {
            Assert.Equal(10, server.FindExistingServer());
        }
    }

    [Fact]
    public void HTTP_sys站台只有32位元的IISExpress時說明無法診斷()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Names[10] = "iisexpress";
        system.Managed.Add(10);
        system.Wow64.Add(10);
        system.CommandLines[10] = "iisexpress.exe /port:5000";
        (ServerService server, LogBuffer log) = Service(system, new[] { "iisexpress" });
        using (server)
        {
            Assert.Null(server.FindExistingServer());
            Assert.Equal(ServerFailure.Wow64, server.LastFailure);
        }
    }

    [Fact]
    public void 啟動時監聽者不在啟動的行程樹內就不接管()
    {
        var system = new FakeSystem { Listeners = new[] { 900 } };
        system.Names[900] = "other";
        system.Managed.Add(900);
        system.Descendants[100] = new HashSet<int> { 101 };

        LocateResult result = ServerLocator.Locate(5000, Array.Empty<string>(), startedRootPid: 100, system.Probes);

        Assert.Null(result.Pid);
        Assert.Equal(ServerFailure.NotInStartedTree, result.Failure);
    }

    [Fact]
    public void 啟動時監聽者是啟動行程的子行程就採用()
    {
        var system = new FakeSystem { Listeners = new[] { 101 } };
        system.Names[101] = "dotnet";
        system.Managed.Add(101);
        system.Descendants[100] = new HashSet<int> { 101, 102 };

        LocateResult result = ServerLocator.Locate(5000, Array.Empty<string>(), startedRootPid: 100, system.Probes);

        Assert.Equal(101, result.Pid);
    }

    [Fact]
    public void 啟動時HTTP_sys站台取行程樹內名稱符合的行程本身()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Names[100] = "iisexpress";

        LocateResult result = ServerLocator.Locate(5000, new[] { "iisexpress" }, startedRootPid: 100, system.Probes);

        Assert.Equal(100, result.Pid);
    }

    [Fact]
    public void 啟動時HTTP_sys站台沒設定行程名稱立即失敗而不是接管行程樹內的行程()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Add(100, "powershell", null);
        system.Add(101, "iisexpress", null);
        system.Descendants[100] = new HashSet<int> { 101 };

        LocateResult result = ServerLocator.Locate(5000, Array.Empty<string>(), 100, system.Probes);

        Assert.Null(result.Pid);
        Assert.Equal(ServerFailure.HttpSysNeedsProcessNames, result.Failure);
        Assert.True(ServerLocator.IsFatal(result.Failure));
    }

    [Fact]
    public void 啟動時HTTP_sys站台只看名稱不要求已載入CLR()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Add(100, "powershell", null, managed: true);
        system.Add(101, "iisexpress", null, managed: false);
        system.Descendants[100] = new HashSet<int> { 101 };

        LocateResult result = ServerLocator.Locate(5000, new[] { "iisexpress" }, 100, system.Probes);

        Assert.Equal(101, result.Pid);
    }

    [Fact]
    public void 真正的站台是32位元行程時不會接管另一個沒有連接埠的IISExpress()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Add(10, "iisexpress", "iisexpress.exe /path:C:\\a /port:58649", wow64: true);
        system.Add(11, "iisexpress", "iisexpress.exe /config:C:\\x\\applicationhost.config");
        (ServerService server, _) = Service(system, new[] { "iisexpress" }, port: 58649);
        using (server)
        {
            Assert.Null(server.FindExistingServer());
            Assert.Equal(ServerFailure.Wow64, server.LastFailure);
        }
    }

    [Fact]
    public void 真正的站台還沒載入CLR時不會接管另一個沒有連接埠的IISExpress()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Add(10, "iisexpress", "iisexpress.exe /path:C:\\a /port:58649", managed: false);
        system.Add(11, "iisexpress", "iisexpress.exe /config:C:\\x\\applicationhost.config");
        (ServerService server, _) = Service(system, new[] { "iisexpress" }, port: 58649);
        using (server)
        {
            Assert.Null(server.FindExistingServer());
            Assert.Equal(ServerFailure.NotLoadedYet, server.LastFailure);
        }
    }

    [Fact]
    public void 讀不到命令列的行程不列入備援()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Add(10, "w3wp", null);
        (ServerService server, _) = Service(system, new[] { "w3wp" });
        using (server)
        {
            Assert.Null(server.FindExistingServer());
            Assert.Equal(ServerFailure.NoServingProcess, server.LastFailure);
        }
    }

    [Fact]
    public void 命令列指定別的連接埠的行程被排除而沒有連接埠的備援行程被接管()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Add(10, "iisexpress", "iisexpress.exe /port:5001");
        system.Add(11, "iisexpress", "iisexpress.exe /config:C:\\x\\applicationhost.config");
        (ServerService server, _) = Service(system, new[] { "iisexpress" }, port: 5002);
        using (server)
        {
            Assert.Equal(11, server.FindExistingServer());
        }
    }

    [Fact]
    public void HTTP_sys站台名稱符合但還沒載入CLR時接管既有行程會先略過並說明()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Names[10] = "w3wp";
        system.CommandLines[10] = "w3wp.exe -ap \"A\"";
        (ServerService server, LogBuffer log) = Service(system, new[] { "w3wp" });
        using (server)
        {
            Assert.Null(server.FindExistingServer());
            Assert.Equal(ServerFailure.NotLoadedYet, server.LastFailure);
        }
    }

    [Fact]
    public void 啟動時HTTP_sys站台行程命令列指定別的連接埠就不採用()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Add(100, "iisexpress", "iisexpress.exe /port:5001", managed: false);

        LocateResult result = ServerLocator.Locate(5002, new[] { "iisexpress" }, 100, system.Probes);

        Assert.Null(result.Pid);
        Assert.Equal(ServerFailure.NoServingProcess, result.Failure);
    }
}
