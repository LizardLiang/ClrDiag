using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

// ServerService 測試用的假伺服器（受控行程）：
//   listen <port> 監聽連接埠直到被結束（模擬直接就是伺服器的行程，例如 IIS Express 或編譯好的 app）
//   wrap <port>   不自己監聽，啟動 listen 子行程後等它結束（模擬 dotnet run 這類 wrapper）
//   exit <code>   立刻結束（模擬啟動指令失敗）
//   idle          不監聽、也不結束（模擬永遠等不到伺服器）
// 任何模式下，父行程結束或存活超過 ParentWatch.MaxLifetime 就自行結束，測試失敗也不會留下行程占用連接埠。
ParentWatch.Start();

string mode = args.Length > 0 ? args[0] : "idle";
switch (mode)
{
    case "listen":
    {
        var listener = new TcpListener(IPAddress.Loopback, int.Parse(args[1]));
        listener.Start();
        Thread.Sleep(Timeout.Infinite);
        break;
    }

    case "wrap":
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        psi.ArgumentList.Add("listen");
        psi.ArgumentList.Add(args[1]);
        using Process child = Process.Start(psi)!;
        child.WaitForExit();
        break;
    }

    case "exit":
        return int.Parse(args[1]);

    default:
        Thread.Sleep(Timeout.Infinite);
        break;
}

return 0;

/// <summary>監看父行程：父行程結束或超過最長存活時間就結束自己。</summary>
internal static class ParentWatch
{
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(2);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process,
        int informationClass,
        ref ProcessBasicInformation information,
        int length,
        out int returnLength
    );

    public static void Start()
    {
        int parentPid = ParentPid();
        DateTime? parentStart = parentPid > 0 ? StartTimeOf(parentPid) : null;
        var thread = new Thread(() =>
        {
            var lifetime = Stopwatch.StartNew();
            while (true)
            {
                Thread.Sleep(500);
                bool parentGone = parentStart is { } start && StartTimeOf(parentPid) != start;
                if (parentGone || lifetime.Elapsed > MaxLifetime)
                {
                    Environment.Exit(1);
                }
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
    }

    private static int ParentPid()
    {
        var info = new ProcessBasicInformation();
        int status = NtQueryInformationProcess(
            Process.GetCurrentProcess().Handle,
            0,
            ref info,
            Marshal.SizeOf<ProcessBasicInformation>(),
            out _
        );
        return status == 0 ? (int)info.InheritedFromUniqueProcessId : 0;
    }

    private static DateTime? StartTimeOf(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.HasExited ? null : process.StartTime;
        }
        catch
        {
            return null;
        }
    }
}
