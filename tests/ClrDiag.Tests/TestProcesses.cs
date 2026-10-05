using System.Diagnostics;
using ClrDiag.Core;

namespace ClrDiag.Tests;

/// <summary>測試用的行程工具：ClrDiag.TestServer 的設定、行程是否存活、結束行程。</summary>
internal static class TestProcesses
{
    public static readonly string TestServerDll = Path.Combine(AppContext.BaseDirectory, "ClrDiag.TestServer.dll");

    /// <summary>用 dotnet 執行 ClrDiag.TestServer 的設定；mode 是 TestServer 的模式，extra 是額外引數。</summary>
    public static DiagConfig Config(string mode, params string[] extra) =>
        new()
        {
            ServeCommand = "dotnet",
            ServeArguments = new[] { TestServerDll, mode }.Concat(extra).ToArray(),
        };

    public static bool Alive(int pid)
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

    /// <summary>結束行程與它的後代；已經結束就略過。</summary>
    public static void Kill(Process process)
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
}
