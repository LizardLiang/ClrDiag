using System.Net;
using System.Net.Sockets;
using ClrDiag.Core;

namespace ClrDiag.Tests;

/// <summary>假的系統狀態：Listeners 是連接埠的監聽者，其餘為各 PID 的屬性。</summary>
internal sealed class FakeSystem
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

    /// <summary>加入一個行程；managed 為 true 表示已載入 CLR。</summary>
    public void Add(int pid, string name, string? commandLine, bool managed = true, bool wow64 = false)
    {
        Names[pid] = name;
        if (commandLine is not null)
        {
            CommandLines[pid] = commandLine;
        }

        if (managed)
        {
            Managed.Add(pid);
        }

        if (wow64)
        {
            Wow64.Add(pid);
        }
    }
}

/// <summary>測試用的連接埠工具。</summary>
internal static class TestPorts
{
    /// <summary>挑一個目前沒人監聽的連接埠；釋放到使用之間可能被搶走，使用端要能重試。</summary>
    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
