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

    private static bool Contains(LogBuffer log, string text) =>
        log.TakeLast(log.Count).Any(l => l.Text.Contains(text, StringComparison.Ordinal));

    /// <summary>假的系統狀態：listeners 是連接埠的監聽者，其餘為各 PID 的屬性。</summary>
    private sealed class FakeSystem
    {
        public IReadOnlyList<int>? Listeners { get; set; } = Array.Empty<int>();
        public Dictionary<int, string> Names { get; } = new();
        public HashSet<int> Managed { get; } = new();
        public HashSet<int> Wow64 { get; } = new();
        public Dictionary<int, string> CommandLines { get; } = new();
        public Dictionary<int, HashSet<int>> Descendants { get; } = new();

        public LocatorProbes Probes =>
            new(
                _ => Listeners,
                pid => Managed.Contains(pid) ? ".NET Framework" : null,
                pid => Names.GetValueOrDefault(pid),
                pid => Wow64.Contains(pid),
                pid => Descendants.GetValueOrDefault(pid) ?? new HashSet<int>(),
                name => Names.Where(p => p.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList(),
                pid => CommandLines.GetValueOrDefault(pid)
            );
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

            Assert.Equal(Environment.ProcessId, PortOwnerFinder.FindListenerPid(port));
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

            Assert.Equal(Environment.ProcessId, PortOwnerFinder.FindListenerPid(port));
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

        Assert.Null(PortOwnerFinder.FindListenerPid(port));
        Assert.Empty(PortOwnerFinder.FindListenerPids(port)!);
        Assert.False(PortOwnerFinder.IsListening(port));
    }

    [Fact]
    public void 沒設定行程名稱且連接埠沒人監聽時不接管任何行程並寫入原因()
    {
        var log = new LogBuffer();
        using var server = new ServerService(new DiagConfig(), log, FreePort());

        Assert.Null(server.FindExistingServer());
        Assert.True(Contains(log, "沒有人監聽"));
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
            Assert.True(Contains(log, "不在 processNames"));
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
            Assert.True(Contains(log, "HTTP.sys"));
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
            Assert.True(Contains(log, "無法確認它是可監看的 64 位元受控行程"));
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
            Assert.True(Contains(log, "32 位元"));
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
            Assert.True(Contains(failedLog, "查詢連接埠"));
        }

        var split = new FakeSystem { Listeners = new[] { 900, 901 } };
        split.Managed.UnionWith(new[] { 900, 901 });
        (ServerService splitServer, LogBuffer splitLog) = Service(split);
        using (splitServer)
        {
            Assert.Null(splitServer.FindExistingServer());
            Assert.True(Contains(splitLog, "多個行程監聽"));
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
            Assert.True(Contains(log, "找不到名稱為 iisexpress"));
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
            Assert.True(Contains(log, "無法判斷"));
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
            Assert.True(Contains(log, "32 位元"));
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
        Assert.Contains("不是本工具啟動的行程", result.Reason);
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
    public void 啟動時HTTP_sys站台沒設定行程名稱則要求樹內有受控行程()
    {
        var system = new FakeSystem { Listeners = new[] { 4 } };
        system.Names[100] = "pwsh";
        system.Names[101] = "iisexpress";
        system.Descendants[100] = new HashSet<int> { 101 };

        Assert.Null(ServerLocator.Locate(5000, Array.Empty<string>(), 100, system.Probes).Pid);

        system.Managed.Add(101);

        Assert.Equal(101, ServerLocator.Locate(5000, Array.Empty<string>(), 100, system.Probes).Pid);
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
            Assert.True(Contains(log, "還沒載入 CLR"));
        }
    }
}
