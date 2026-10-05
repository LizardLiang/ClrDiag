using System.Diagnostics;

namespace ClrDiag.Core;

public sealed record ManagedProcessInfo(int Pid, string Name, long WorkingSet64, string Runtime);

/// <summary>
/// 找出可監看的受控行程。不綁定特定主機（IIS Express、w3wp、自架 dotnet 皆可）：
/// 提供受控行程的清單與判斷；自動決定要監看哪個行程的規則在 ServerLocator，不在這裡。
/// </summary>
public static class ManagedProcessFinder
{
    /// <summary>列出所有載入 .NET 執行階段的行程，依工作集由大到小排序。</summary>
    public static List<ManagedProcessInfo> List(IReadOnlyList<string> processNames)
    {
        var result = new List<ManagedProcessInfo>();

        Process[] processes =
            processNames.Count > 0
                ? processNames.SelectMany(Process.GetProcessesByName).ToArray()
                : Process.GetProcesses();

        int self = Environment.ProcessId;

        foreach (Process process in processes)
        {
            try
            {
                if (process.Id == self)
                {
                    continue;
                }

                string? runtime = DetectRuntime(process);
                if (runtime is null)
                {
                    continue;
                }

                result.Add(
                    new ManagedProcessInfo(
                        process.Id,
                        process.ProcessName,
                        process.WorkingSet64,
                        runtime
                    )
                );
            }
            catch
            {
                // 沒有權限或行程已結束，略過
            }
            finally
            {
                process.Dispose();
            }
        }

        return result.OrderByDescending(p => p.WorkingSet64).ToList();
    }

    /// <summary>
    /// 列出供使用者挑選的受控行程（--list、p 鍵）：依 processNames 列出，名稱都沒有執行中的實例時
    /// 退回列出全部受控行程，並以 fellBack 告知。只用於「列出讓使用者自己選」；
    /// 自動決定目標一律走 ServerLocator，不從這份清單挑。
    /// </summary>
    public static List<ManagedProcessInfo> ListForPicking(IReadOnlyList<string> processNames, out bool fellBack)
    {
        List<ManagedProcessInfo> candidates = List(processNames);
        fellBack = candidates.Count == 0 && processNames.Count > 0;
        return fellBack ? List(Array.Empty<string>()) : candidates;
    }

    /// <summary>
    /// 回傳指定 PID 載入的 .NET 執行階段名稱；不是受控行程、行程不存在、沒有權限檢查或是 32 位元行程
    /// 一律回傳 null（這幾種情形無法區分）。
    /// </summary>
    public static string? RuntimeOf(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return DetectRuntime(process);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 以載入的模組判斷行程使用哪個執行階段：
    /// clr.dll = .NET Framework、coreclr.dll = .NET Core / .NET 5+。
    /// </summary>
    private static string? DetectRuntime(Process process)
    {
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                string name = module.ModuleName;
                if (
                    name.Equals("clr.dll", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("mscorwks.dll", StringComparison.OrdinalIgnoreCase)
                )
                {
                    return ".NET Framework";
                }

                if (name.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase))
                {
                    return ".NET Core";
                }
            }
        }
        catch
        {
            // 32 位元行程或受保護行程無法列舉模組（本工具是 x64，也無法對其取快照）
        }

        return null;
    }
}
