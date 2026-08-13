using System.Diagnostics;

namespace ClrDiag.Core.Dap;

/// <summary>
/// 解析與啟動 netcoredbg（Samsung，MIT 授權）子行程，把它的 stdin/stdout 接成 DapTransport 的來源。
/// 生成方式比照 ServerService 啟動開發伺服器的做法（Core/ServerService.cs:158-172）：
/// 三個串流全部導向、不開主控台，子行程才不會搶走 TUI 的按鍵輸入（見 commit dde156b 的教訓）。
///
/// 與 ServerService.Dispose（Core/ServerService.cs:427-431）不同：這裡的子行程結束時「一定」要
/// 跟著砍掉 —— ClrDiag 是唯一的除錯客戶端，行程不會被別的東西恢復執行，留著孤兒行程等於讓
/// 目標行程永遠停在中斷點。
/// </summary>
public sealed class NetcoredbgProcess : IDisposable
{
    private const string MasonDefaultRelativePath =
        @"nvim-data\mason\packages\netcoredbg\netcoredbg\netcoredbg.exe";

    private Process? process;

    private NetcoredbgProcess(Process process) => this.process = process;

    public int Pid => process?.Id ?? -1;

    public Stream StandardInput => process!.StandardInput.BaseStream;

    public Stream StandardOutput => process!.StandardOutput.BaseStream;

    /// <summary>是否仍在執行（可能已被外部或除錯階段結束而提前退出）。</summary>
    public bool HasExited => process is null || process.HasExited;

    /// <summary>netcoredbg 寫到 stderr 的診斷行；stdout 已被 DAP 訊框佔用，不能拿來記錄。</summary>
    public event Action<string>? ErrorLineReceived;

    /// <summary>子行程自行結束時觸發一次（正常關閉或當機都算），供 DapSessionService 偵測掉線。</summary>
    public event Action<int>? Exited;

    /// <summary>
    /// 解析 netcoredbg 執行檔路徑，依序：設定檔 DapAdapterPath → PATH → mason 預設安裝路徑。
    /// 找不到就回傳 null，呼叫端應給出可操作的錯誤訊息，而不是丟例外。
    /// </summary>
    public static string? ResolveExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        string? fromPath = FindOnPath("netcoredbg.exe") ?? FindOnPath("netcoredbg");
        if (fromPath is not null)
        {
            return fromPath;
        }

        string masonDefault = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            MasonDefaultRelativePath
        );
        return File.Exists(masonDefault) ? masonDefault : null;
    }

    private static string? FindOnPath(string fileName)
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (pathVar is null)
        {
            return null;
        }

        foreach (
            string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        )
        {
            string candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>啟動 netcoredbg，以 --interpreter=vscode 進入 DAP 模式（stdin/stdout 走訊框化 JSON）。</summary>
    public static NetcoredbgProcess Start(string executablePath)
    {
        var psi = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 同 ServerService：沒有主控台就沒有 CONIN$ 可開，子行程搶不走 TUI 的按鍵。
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--interpreter=vscode");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var wrapper = new NetcoredbgProcess(proc);

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                wrapper.ErrorLineReceived?.Invoke(e.Data);
            }
        };
        proc.Exited += (_, _) => wrapper.Exited?.Invoke(SafeExitCode(proc));

        proc.Start();
        proc.BeginErrorReadLine();
        return wrapper;
    }

    private static int SafeExitCode(Process proc)
    {
        try
        {
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>結束並釋放子行程；一定會嘗試終止，不像 ServerService 只 Dispose 不 Kill。</summary>
    public void Dispose()
    {
        Process? current = process;
        if (current is null)
        {
            return;
        }

        process = null;
        try
        {
            if (!current.HasExited)
            {
                current.Kill(entireProcessTree: true);
                current.WaitForExit(3000);
            }
        }
        catch
        {
            // 行程可能已經自行結束或正在結束中，略過
        }
        finally
        {
            current.Dispose();
        }
    }
}
