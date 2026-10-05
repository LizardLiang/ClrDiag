using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

// ServerService 測試用的假伺服器（受控行程）：
//   listen <port> 監聽連接埠直到被結束（模擬直接就是伺服器的行程，例如 IIS Express 或編譯好的 app）
//   wrap <port>   不自己監聽，啟動 listen 子行程後等它結束（模擬 dotnet run 這類 wrapper）
//   exit <code>   立刻結束（模擬啟動指令失敗）
//   idle          不監聽、也不結束（模擬永遠等不到伺服器）
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
