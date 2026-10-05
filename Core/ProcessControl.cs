using System.Diagnostics;

namespace ClrDiag.Core;

/// <summary>
/// ServerService 對行程的操作：查建立時間、查是否仍在執行、結束行程。
/// 正式使用 Default，測試可替換成假資料，驗證停止失敗與狀態還原等不易用真行程重現的路徑。
/// </summary>
/// <param name="StartTime">行程建立時間；行程不存在或讀不到時為 null。</param>
/// <param name="IsRunning">行程是否仍在執行；查不到狀態（例如沒有權限）時回傳 true，寧可回報停不掉也不誤報已停止。</param>
/// <param name="Kill">以 PID 結束行程與它的後代；force 為 false 時只送正常關閉訊號。回傳是否成功送出。</param>
public sealed record ProcessControl(
    Func<int, DateTime?> StartTime,
    Func<int, bool> IsRunning,
    Func<int, bool, bool> Kill
)
{
    public static ProcessControl Default { get; } =
        new(ChildProcessFinder.StartTimeOf, IsRunningOf, TaskKill);

    private static bool IsRunningOf(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // 沒有這個 PID 的行程
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>以 taskkill 結束行程與後代（/T）；結束碼 0 才算成功。</summary>
    private static bool TaskKill(int pid, bool force)
    {
        var psi = new ProcessStartInfo("taskkill", $"/PID {pid} /T{(force ? " /F" : string.Empty)}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 標準輸入導向管道，讓 taskkill 沒有主控台可開（沒有 CONIN$），不會搶走 TUI 的按鍵。
            RedirectStandardInput = true,
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        using Process? process = Process.Start(psi);
        return (process?.WaitForExit(5000) ?? false) && process.ExitCode == 0;
    }
}
