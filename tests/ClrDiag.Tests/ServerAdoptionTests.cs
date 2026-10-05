using System.Net;
using System.Net.Sockets;
using ClrDiag.Core;

namespace ClrDiag.Tests;

/// <summary>接管既有行程的選擇：沒設定 processNames 時只認監聽設定連接埠的受控行程。</summary>
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

    [Fact]
    public void 查得到監聽連接埠的行程PID()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
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
    public void 沒有人監聽的連接埠查不到行程()
    {
        Assert.Null(PortOwnerFinder.FindListenerPid(FreePort()));
    }

    [Fact]
    public void 沒設定行程名稱且連接埠沒人監聽時不接管任何行程並寫入原因()
    {
        var log = new LogBuffer();
        using var server = new ServerService(new DiagConfig(), log, FreePort());

        Assert.Null(server.FindExistingServer());
        Assert.True(Contains(log, "查不到監聽的行程"));
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
    public void 設定的行程名稱找不到時不退回掃描全部受控行程()
    {
        var config = new DiagConfig { ProcessNames = new[] { "clrdiag-no-such-process" } };
        using var server = new ServerService(config, new LogBuffer(), FreePort());

        Assert.Null(server.FindExistingServer());
    }
}
