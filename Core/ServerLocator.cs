using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClrDiag.Core;

/// <summary>辨識結果：Pid 為 null 表示沒有辨識出伺服器行程，Reason 是要寫進 6 記錄的原因。</summary>
public readonly record struct LocateResult(int? Pid, LogKind Kind, string? Reason);

/// <summary>
/// 查詢系統狀態的函式集合；正式使用 Default，測試可替換成假資料。
/// </summary>
public sealed record LocatorProbes(
    Func<int, IReadOnlyList<int>?> ListenerPids,
    Func<int, string?> RuntimeOf,
    Func<int, string?> ProcessName,
    Func<int, bool> IsWow64,
    Func<int, IReadOnlyCollection<int>> DescendantsOf,
    Func<string, IReadOnlyList<int>> PidsByName,
    Func<int, string?> CommandLine
)
{
    public static LocatorProbes Default { get; } =
        new(
            PortOwnerFinder.FindListenerPids,
            ManagedProcessFinder.RuntimeOf,
            ProcessNameOf,
            NativeProcess.IsWow64,
            ChildProcessFinder.DescendantsOf,
            PidsByNameOf,
            NativeProcess.CommandLine
        );

    private static string? ProcessNameOf(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<int> PidsByNameOf(string name)
    {
        Process[] processes = Process.GetProcessesByName(name);
        try
        {
            return processes.Select(process => process.Id).Where(pid => pid != Environment.ProcessId).ToList();
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }
}

/// <summary>
/// 辨識「哪個行程是開發伺服器」的唯一入口，啟動（StartAsync）、接管既有行程（儀表板啟動、s 鍵）
/// 與批次指令（沒有 --pid）都走這裡，不會各自用不同規則去猜。
///
/// 依據是監聽設定連接埠的行程（PortOwnerFinder），而且只接受：
///   - 受控行程（RuntimeOf 看得到 CLR）、64 位元；
///   - 啟動時（startedRootPid 不為 null）還必須是啟動的行程本身或它的後代，
///     不會因為連接埠被別的行程佔用而接管到無關的行程；
///   - 設定了 processNames 時，行程映像名稱必須在清單內（不再用「工作集最大」挑選）。
/// 連接埠沒人監聽、查詢失敗、有多個不同的監聽行程、擁有者不是受控行程，一律不接管並回傳原因。
///
/// HTTP.sys（IIS Express、w3wp）的監聽記在系統行程 PID 4 名下，看不出站台行程是誰：
///   - 啟動時：HTTP.sys 的站台行程必然在啟動的行程樹內，從樹裡挑符合 processNames 的行程
///     （沒設定 processNames 就要求是受控行程），剛好一個才接管；
///   - 接管既有行程時：必須設定 processNames，依名稱找行程，再用命令列的 /port: 比對連接埠
///     （寫了別的連接埠的排除；沒寫 /port: 的只在剛好一個時才接管）。
/// </summary>
public static partial class ServerLocator
{
    /// <summary>HTTP.sys 的核心模式監聽記在這個系統行程名下。</summary>
    public const int HttpSysPid = 4;

    [GeneratedRegex(@"[/-]port:(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PortArgument();

    /// <summary>
    /// 辨識伺服器行程。startedRootPid 為本工具剛啟動的行程 PID（啟動流程）；null 表示接管既有行程。
    /// </summary>
    public static LocateResult Locate(
        int port,
        IReadOnlyList<string> processNames,
        int? startedRootPid,
        LocatorProbes? probes = null
    )
    {
        probes ??= LocatorProbes.Default;

        IReadOnlyList<int>? owners = probes.ListenerPids(port);
        if (owners is null)
        {
            return Fail(LogKind.Warning, $"查詢連接埠 {port} 的監聽行程失敗，未接管任何行程");
        }

        if (owners.Count == 0)
        {
            return Fail(LogKind.Info, $"連接埠 {port} 沒有人監聽，未接管任何行程");
        }

        if (owners.Count > 1)
        {
            return Fail(
                LogKind.Warning,
                $"連接埠 {port} 同時由多個行程監聽（PID {string.Join("、", owners)}），無法判斷哪一個是伺服器，未接管；請用 p 鍵 / --pid 指定"
            );
        }

        int owner = owners[0];
        return owner == HttpSysPid
            ? LocateHttpSys(port, processNames, startedRootPid, probes)
            : LocateDirect(port, owner, processNames, startedRootPid, probes);
    }

    private static LocateResult LocateDirect(
        int port,
        int owner,
        IReadOnlyList<string> processNames,
        int? startedRootPid,
        LocatorProbes probes
    )
    {
        string label = Describe(owner, probes);

        if (
            startedRootPid is { } root
            && owner != root
            && !probes.DescendantsOf(root).Contains(owner)
        )
        {
            return Fail(
                LogKind.Warning,
                $"連接埠 {port} 由 {label} 監聽，它不是本工具啟動的行程或其子行程，未接管"
            );
        }

        if (processNames.Count > 0 && !NameMatches(owner, processNames, probes))
        {
            return Fail(
                LogKind.Warning,
                $"連接埠 {port} 由 {label} 監聽，不在 processNames（{string.Join("、", processNames)}）內，未接管"
            );
        }

        if (probes.IsWow64(owner))
        {
            return Fail(LogKind.Warning, Wow64Reason(label));
        }

        if (probes.RuntimeOf(owner) is null)
        {
            return Fail(
                LogKind.Warning,
                $"連接埠 {port} 由 {label} 監聽，但無法確認它是可監看的 64 位元受控行程（可能不是受控行程，或沒有權限檢查），未接管"
            );
        }

        return new LocateResult(owner, LogKind.Info, null);
    }

    private static LocateResult LocateHttpSys(
        int port,
        IReadOnlyList<string> processNames,
        int? startedRootPid,
        LocatorProbes probes
    )
    {
        return startedRootPid is { } root
            ? LocateHttpSysStarted(port, root, processNames, probes)
            : LocateHttpSysExisting(port, processNames, probes);
    }

    private static LocateResult LocateHttpSysStarted(
        int port,
        int root,
        IReadOnlyList<string> processNames,
        LocatorProbes probes
    )
    {
        var tree = new List<int> { root };
        tree.AddRange(probes.DescendantsOf(root));

        var candidates = new List<int>();
        var skipped32 = new List<string>();
        foreach (int pid in tree)
        {
            if (probes.ProcessName(pid) is null)
            {
                continue;
            }

            // 有 processNames 就依名稱（站台行程可能還沒載入 CLR）；沒設定就只認已載入 CLR 的受控行程
            bool matches = processNames.Count > 0
                ? NameMatches(pid, processNames, probes)
                : probes.RuntimeOf(pid) is not null || probes.IsWow64(pid);
            if (!matches)
            {
                continue;
            }

            if (probes.IsWow64(pid))
            {
                skipped32.Add(Describe(pid, probes));
                continue;
            }

            candidates.Add(pid);
        }

        if (candidates.Count == 1)
        {
            return new LocateResult(candidates[0], LogKind.Info, null);
        }

        if (candidates.Count > 1)
        {
            return Fail(
                LogKind.Warning,
                $"連接埠 {port} 由 HTTP.sys 監聽，啟動的行程樹內有多個符合的行程（PID {string.Join("、", candidates)}），無法判斷哪一個是站台，未接管；請用 p 鍵 / --pid 指定"
            );
        }

        if (skipped32.Count > 0)
        {
            return Fail(LogKind.Warning, Wow64Reason(string.Join("、", skipped32)));
        }

        string condition = processNames.Count > 0
            ? $"名稱在 processNames（{string.Join("、", processNames)}）內"
            : "已載入 CLR 的受控行程（沒設定 processNames）";
        return Fail(
            LogKind.Warning,
            $"連接埠 {port} 由 HTTP.sys 監聽，但啟動的行程樹內（PID {string.Join("、", tree)}）還沒有{condition}"
        );
    }

    private static LocateResult LocateHttpSysExisting(
        int port,
        IReadOnlyList<string> processNames,
        LocatorProbes probes
    )
    {
        if (processNames.Count == 0)
        {
            return Fail(
                LogKind.Warning,
                $"連接埠 {port} 由系統 HTTP.sys（PID {HttpSysPid}）監聽，無法得知是哪個行程；請在 {DiagConfig.FileName} 設定 processNames（例如 iisexpress），或用 p 鍵 / --pid 指定"
            );
        }

        var matching = new List<int>();
        var unknown = new List<int>();
        var skipped32 = new List<string>();
        var notManaged = new List<string>();
        foreach (string name in processNames)
        {
            foreach (int pid in probes.PidsByName(TrimExe(name)))
            {
                if (probes.IsWow64(pid))
                {
                    skipped32.Add(Describe(pid, probes));
                    continue;
                }

                // 接管既有行程要能立刻監看，CLR 還沒載入的站台行程（閒置的 w3wp、靜態站台）先不接管
                if (probes.RuntimeOf(pid) is null)
                {
                    notManaged.Add(Describe(pid, probes));
                    continue;
                }

                Match match = PortArgument().Match(probes.CommandLine(pid) ?? string.Empty);
                if (!match.Success)
                {
                    unknown.Add(pid);
                }
                else if (int.TryParse(match.Groups[1].Value, out int cmdPort) && cmdPort == port)
                {
                    matching.Add(pid);
                }
            }
        }

        if (matching.Count == 1)
        {
            return new LocateResult(matching[0], LogKind.Info, null);
        }

        if (matching.Count > 1)
        {
            return Fail(
                LogKind.Warning,
                $"連接埠 {port} 由 HTTP.sys 監聽，有多個行程的命令列都指定 /port:{port}（PID {string.Join("、", matching)}），無法判斷，未接管；請用 p 鍵 / --pid 指定"
            );
        }

        if (unknown.Count == 1)
        {
            return new LocateResult(unknown[0], LogKind.Info, null);
        }

        if (unknown.Count > 1)
        {
            return Fail(
                LogKind.Warning,
                $"連接埠 {port} 由 HTTP.sys 監聽，名稱符合的行程有多個（PID {string.Join("、", unknown)}）且命令列沒有 /port: 可比對，無法判斷，未接管；請用 p 鍵 / --pid 指定"
            );
        }

        if (skipped32.Count > 0)
        {
            return Fail(LogKind.Warning, Wow64Reason(string.Join("、", skipped32)));
        }

        if (notManaged.Count > 0)
        {
            return Fail(
                LogKind.Info,
                $"連接埠 {port} 由 HTTP.sys 監聽，名稱符合的 {string.Join("、", notManaged)} 還沒載入 CLR（站台尚未被請求），未接管；請求一次站台後再試，或用 p 鍵 / --pid 指定"
            );
        }

        return Fail(
            LogKind.Info,
            $"連接埠 {port} 由 HTTP.sys 監聽，但找不到名稱為 {string.Join("、", processNames)}、且服務此連接埠的行程，未接管任何行程"
        );
    }

    private static bool NameMatches(int pid, IReadOnlyList<string> processNames, LocatorProbes probes)
    {
        string? name = probes.ProcessName(pid);
        return name is not null
            && processNames.Any(candidate => TrimExe(candidate).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string TrimExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static string Describe(int pid, LocatorProbes probes) =>
        probes.ProcessName(pid) is { } name ? $"PID {pid}（{name}）" : $"PID {pid}";

    private static string Wow64Reason(string label) =>
        $"{label} 是 32 位元行程，本工具（64 位元）無法診斷，未接管；請改用 64 位元的 IIS Express / 主機（例如 Program Files 底下的 iisexpress.exe）";

    private static LocateResult Fail(LogKind kind, string reason) => new(null, kind, reason);
}
