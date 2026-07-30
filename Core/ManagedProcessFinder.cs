using System.Diagnostics;

namespace ClrDiag.Core;

public sealed record ManagedProcessInfo(int Pid, string Name, long WorkingSet64, string Runtime);

/// <summary>
/// 找出可監看的受控行程。不綁定特定主機（IIS Express、w3wp、自架 dotnet 皆可）：
/// 設定檔給了 processNames 就依名稱找，否則掃描所有載入 CLR 的行程。
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

    /// <summary>挑選最可能是目標的受控行程（工作集最大者）。</summary>
    public static int? FindBest(IReadOnlyList<string> processNames)
    {
        List<ManagedProcessInfo> candidates = List(processNames);

        // 有指定名稱卻找不到時，退回掃描全部受控行程，避免換了主機方式就完全找不到目標
        if (candidates.Count == 0 && processNames.Count > 0)
        {
            candidates = List(Array.Empty<string>());
        }

        return candidates.Count == 0 ? null : candidates[0].Pid;
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
