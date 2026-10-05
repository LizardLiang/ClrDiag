using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClrDiag.Core;

/// <summary>辨識、啟動或停止失敗的原因類別，測試與呼叫端依它判斷，不比對記錄文字。</summary>
public enum ServerFailure
{
    None,
    QueryFailed,
    NotListening,
    MultipleListeners,
    NotInStartedTree,
    NameMismatch,
    Wow64,
    NotManaged,
    HttpSysNeedsProcessNames,
    MultipleCandidates,
    NotLoadedYet,
    NoServingProcess,
    NoServeCommand,
    StartError,
    StartCommandExited,
    StartTimedOut,
    StartCancelled,
    PortAlreadyListening,
    StopFailed,
    DebugSessionActive,
    StartedProcessStillRunning,
}

/// <summary>辨識結果：Pid 為 null 表示沒有辨識出伺服器行程，Reason 是要寫進 6 記錄的原因，Failure 是原因類別。</summary>
public readonly record struct LocateResult(
    int? Pid,
    LogKind Kind,
    string? Reason,
    ServerFailure Failure = ServerFailure.None
);

/// <summary>
/// 查詢系統狀態的函式集合；正式使用 Default，測試可替換成假資料。
/// </summary>
public sealed record LocatorProbes(
    Func<int, IReadOnlyList<int>?> ListenerPids,
    Func<int, string?> RuntimeOf,
    Func<int, string?> ProcessName,
    Func<int, bool> IsWow64,
    Func<int, DateTime?, IReadOnlyCollection<int>> DescendantsOf,
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

    /// <summary>
    /// 回傳同一輪辨識內快取結果的版本：同一個 PID 的名稱、位元數、CLR 與命令列只查一次，
    /// 避免每輪輪詢對同一行程重複開控制代碼、列舉模組。
    /// </summary>
    public LocatorProbes Memoized() =>
        this with
        {
            RuntimeOf = Cache(RuntimeOf),
            ProcessName = Cache(ProcessName),
            IsWow64 = Cache(IsWow64),
            CommandLine = Cache(CommandLine),
        };

    private static Func<int, T> Cache<T>(Func<int, T> source)
    {
        var values = new Dictionary<int, T>();
        return pid =>
        {
            if (!values.TryGetValue(pid, out T? value))
            {
                value = source(pid);
                values[pid] = value;
            }

            return value;
        };
    }

    /// <summary>行程映像名稱（不含 .exe）；行程不存在或沒有權限時回傳 null。</summary>
    internal static string? ProcessNameOf(int pid)
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
///   - 啟動時（startedRootPid 不為 null）還必須是啟動的行程本身或它的後代行程，
///     不會因為連接埠被別的行程佔用而接管到無關的行程；
///   - 設定了 processNames 時，行程映像名稱必須在清單內。
/// 連接埠沒人監聽、查詢失敗、有多個不同的監聽行程、擁有者不是受控行程，一律不接管並回傳原因。
///
/// HTTP.sys（IIS Express、w3wp）的監聽記在系統行程 PID 4 名下，看不出站台行程是誰，
/// 一律要設定 processNames（沒設定就立即失敗並說明）：
///   - 啟動時：從啟動的行程樹（啟動的行程與它的後代行程）裡挑名稱符合、64 位元的行程，剛好一個才接管
///     （站台行程可能還沒載入 CLR，所以只看名稱）；命令列的 /port: 寫了別的連接埠的行程服務的是別的站台，排除；
///   - 接管既有行程時：依名稱找行程，用命令列的 /port: 比對連接埠。
///     /port: 等於設定連接埠的行程是證據：它是 32 位元或還沒載入 CLR 就回報該原因，不接管別的行程；
///     /port: 是別的連接埠的排除；沒有證據時，命令列讀得到、沒有 /port:、64 位元且已載入 CLR 的行程
///     剛好一個才接管（例如 w3wp；同一個 w3wp 也可能服務別的站台，這個備援無法分辨）；
///     讀不到命令列的行程不算在內。
/// </summary>
public static partial class ServerLocator
{
    /// <summary>HTTP.sys 的核心模式監聽記在這個系統行程名下。</summary>
    public const int HttpSysPid = 4;

    [GeneratedRegex(@"[/-]port:(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PortArgument();

    /// <summary>
    /// 失敗後繼續輪詢也不會成功的原因，啟動流程據此立即放棄而不等到逾時：設定不足，
    /// 或伺服器行程是 32 位元（本工具無法診斷，行程不會自己變成 64 位元）。
    /// </summary>
    public static bool IsFatal(ServerFailure failure) =>
        failure is ServerFailure.HttpSysNeedsProcessNames or ServerFailure.Wow64;

    /// <summary>
    /// 辨識伺服器行程。startedRootPid 為本工具剛啟動的行程 PID（啟動流程）；null 表示接管既有行程。
    /// startedRootStart 是該行程的建立時間（啟動時行程還活著就先讀好），用來核對它底下的父子連結。
    /// </summary>
    public static LocateResult Locate(
        int port,
        IReadOnlyList<string> processNames,
        int? startedRootPid,
        LocatorProbes? probes = null,
        DateTime? startedRootStart = null
    )
    {
        probes = (probes ?? LocatorProbes.Default).Memoized();

        IReadOnlyList<int>? owners = probes.ListenerPids(port);
        if (owners is null)
        {
            return Fail(LogKind.Warning, ServerFailure.QueryFailed, $"查詢連接埠 {port} 的監聽行程失敗，未接管任何行程");
        }

        if (owners.Count == 0)
        {
            return Fail(LogKind.Info, ServerFailure.NotListening, $"連接埠 {port} 沒有人監聽，未接管任何行程");
        }

        if (owners.Count > 1)
        {
            return Fail(
                LogKind.Warning,
                ServerFailure.MultipleListeners,
                $"連接埠 {port} 同時由多個行程監聽（PID {string.Join("、", owners)}），無法判斷哪一個是伺服器，未接管；請用 p 鍵 / --pid 指定"
            );
        }

        int owner = owners[0];
        return owner == HttpSysPid
            ? LocateHttpSys(port, processNames, startedRootPid, startedRootStart, probes)
            : LocateDirect(port, owner, processNames, startedRootPid, startedRootStart, probes);
    }

    private static LocateResult LocateDirect(
        int port,
        int owner,
        IReadOnlyList<string> processNames,
        int? startedRootPid,
        DateTime? startedRootStart,
        LocatorProbes probes
    )
    {
        string label = Describe(owner, probes);

        if (
            startedRootPid is { } root
            && owner != root
            && !probes.DescendantsOf(root, startedRootStart).Contains(owner)
        )
        {
            return Fail(
                LogKind.Warning,
                ServerFailure.NotInStartedTree,
                $"連接埠 {port} 由 {label} 監聽，它不是本工具啟動的行程或其後代行程，未接管"
            );
        }

        if (processNames.Count > 0 && !NameMatches(owner, processNames, probes))
        {
            return Fail(
                LogKind.Warning,
                ServerFailure.NameMismatch,
                $"連接埠 {port} 由 {label} 監聽，不在 processNames（{string.Join("、", processNames)}）內，未接管"
            );
        }

        if (probes.IsWow64(owner))
        {
            return Fail(LogKind.Warning, ServerFailure.Wow64, Wow64Reason(label));
        }

        if (probes.RuntimeOf(owner) is null)
        {
            return Fail(
                LogKind.Warning,
                ServerFailure.NotManaged,
                $"連接埠 {port} 由 {label} 監聽，但無法確認它是可監看的 64 位元受控行程（可能不是受控行程，或沒有權限檢查），未接管"
            );
        }

        return new LocateResult(owner, LogKind.Info, null);
    }

    private static LocateResult LocateHttpSys(
        int port,
        IReadOnlyList<string> processNames,
        int? startedRootPid,
        DateTime? startedRootStart,
        LocatorProbes probes
    )
    {
        if (processNames.Count == 0)
        {
            return Fail(
                LogKind.Warning,
                ServerFailure.HttpSysNeedsProcessNames,
                $"連接埠 {port} 由系統 HTTP.sys（PID {HttpSysPid}）監聽，無法得知站台是哪個行程；請在 {DiagConfig.FileName} 設定 processNames（例如 iisexpress），或用 p 鍵 / --pid 指定"
            );
        }

        return startedRootPid is { } root
            ? LocateHttpSysStarted(port, root, startedRootStart, processNames, probes)
            : LocateHttpSysExisting(port, processNames, probes);
    }

    private static LocateResult LocateHttpSysStarted(
        int port,
        int root,
        DateTime? rootStart,
        IReadOnlyList<string> processNames,
        LocatorProbes probes
    )
    {
        var tree = new List<int> { root };
        tree.AddRange(probes.DescendantsOf(root, rootStart));

        var candidates = new List<int>();
        var skipped32 = new List<string>();
        foreach (int pid in tree)
        {
            if (!NameMatches(pid, processNames, probes))
            {
                continue;
            }

            // 命令列寫了別的連接埠，這個行程服務的是別的站台
            if (ParsePort(probes.CommandLine(pid)) is { } commandPort && commandPort != port)
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
                ServerFailure.MultipleCandidates,
                $"連接埠 {port} 由 HTTP.sys 監聽，啟動的行程與後代行程內有多個符合的行程（PID {string.Join("、", candidates)}），無法判斷哪一個是站台，未接管；請用 p 鍵 / --pid 指定"
            );
        }

        if (skipped32.Count > 0)
        {
            return Fail(LogKind.Warning, ServerFailure.Wow64, Wow64Reason(string.Join("、", skipped32)));
        }

        return Fail(
            LogKind.Warning,
            ServerFailure.NoServingProcess,
            $"連接埠 {port} 由 HTTP.sys 監聽，但啟動的行程與後代行程內（PID {string.Join("、", tree)}）還沒有名稱在 processNames（{string.Join("、", processNames)}）內的行程"
        );
    }

    private static LocateResult LocateHttpSysExisting(
        int port,
        IReadOnlyList<string> processNames,
        LocatorProbes probes
    )
    {
        var matching = new List<int>();
        var servingWow64 = new List<string>();
        var servingNotLoaded = new List<string>();
        var fallback = new List<int>();
        var otherWow64 = new List<string>();
        var otherNotLoaded = new List<string>();
        int unreadable = 0;
        var seen = new HashSet<int>();

        foreach (string name in processNames)
        {
            foreach (int pid in probes.PidsByName(TrimExe(name)))
            {
                if (!seen.Add(pid))
                {
                    continue;
                }

                string? commandLine = probes.CommandLine(pid);
                int? commandPort = ParsePort(commandLine);
                if (commandPort is not null && commandPort != port)
                {
                    // 命令列指定了別的連接埠：服務的是別的站台
                    continue;
                }

                if (commandLine is null)
                {
                    // 讀不到命令列（沒有權限或行程已結束），沒有任何證據，不列入備援
                    unreadable++;
                    continue;
                }

                bool serving = commandPort == port;
                bool wow64 = probes.IsWow64(pid);

                // 接管既有行程要能立刻監看，CLR 還沒載入的站台行程（閒置的 w3wp、靜態站台）先不接管
                if (wow64)
                {
                    (serving ? servingWow64 : otherWow64).Add(Describe(pid, probes));
                }
                else if (probes.RuntimeOf(pid) is null)
                {
                    (serving ? servingNotLoaded : otherNotLoaded).Add(Describe(pid, probes));
                }
                else
                {
                    (serving ? matching : fallback).Add(pid);
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
                ServerFailure.MultipleCandidates,
                $"連接埠 {port} 由 HTTP.sys 監聽，有多個行程的命令列都指定 /port:{port}（PID {string.Join("、", matching)}），無法判斷，未接管；請用 p 鍵 / --pid 指定"
            );
        }

        // 命令列指定了這個連接埠的行程才是證據；它不能監看就說明原因，不退而求其次接管別的行程
        if (servingWow64.Count > 0)
        {
            return Fail(LogKind.Warning, ServerFailure.Wow64, Wow64Reason(string.Join("、", servingWow64)));
        }

        if (servingNotLoaded.Count > 0)
        {
            return Fail(LogKind.Info, ServerFailure.NotLoadedYet, NotLoadedReason(port, servingNotLoaded));
        }

        if (fallback.Count == 1)
        {
            return new LocateResult(fallback[0], LogKind.Info, null);
        }

        if (fallback.Count > 1)
        {
            return Fail(
                LogKind.Warning,
                ServerFailure.MultipleCandidates,
                $"連接埠 {port} 由 HTTP.sys 監聽，名稱符合的行程有多個（PID {string.Join("、", fallback)}）且命令列沒有 /port: 可比對，無法判斷，未接管；請用 p 鍵 / --pid 指定"
            );
        }

        if (otherWow64.Count > 0)
        {
            return Fail(LogKind.Warning, ServerFailure.Wow64, Wow64Reason(string.Join("、", otherWow64)));
        }

        if (otherNotLoaded.Count > 0)
        {
            return Fail(LogKind.Info, ServerFailure.NotLoadedYet, NotLoadedReason(port, otherNotLoaded));
        }

        string unreadableNote = unreadable > 0 ? $"（另有 {unreadable} 個行程讀不到命令列，無法比對）" : string.Empty;
        return Fail(
            LogKind.Info,
            ServerFailure.NoServingProcess,
            $"連接埠 {port} 由 HTTP.sys 監聽，但找不到名稱為 {string.Join("、", processNames)}、且服務此連接埠的行程{unreadableNote}，未接管任何行程"
        );
    }

    private static int? ParsePort(string? commandLine)
    {
        if (commandLine is null)
        {
            return null;
        }

        Match match = PortArgument().Match(commandLine);
        return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? value : null;
    }

    private static bool NameMatches(int pid, IReadOnlyList<string> processNames, LocatorProbes probes)
    {
        string? name = probes.ProcessName(pid);
        return name is not null
            && processNames.Any(candidate => TrimExe(candidate).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>去掉映像名稱結尾的 .exe（Process.ProcessName 不含副檔名）。</summary>
    internal static string TrimExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static string Describe(int pid, LocatorProbes probes) =>
        probes.ProcessName(pid) is { } name ? $"PID {pid}（{name}）" : $"PID {pid}";

    private static string Wow64Reason(string label) =>
        $"{label} 是 32 位元行程，本工具（64 位元）無法診斷，未接管；請改用 64 位元的 IIS Express / 主機（例如 Program Files 底下的 iisexpress.exe）";

    private static string NotLoadedReason(int port, List<string> labels) =>
        $"連接埠 {port} 由 HTTP.sys 監聽，名稱符合的 {string.Join("、", labels)} 還沒載入 CLR（站台尚未被請求），未接管；請求一次站台後再試，或用 p 鍵 / --pid 指定";

    private static LocateResult Fail(LogKind kind, ServerFailure failure, string reason) =>
        new(null, kind, reason, failure);
}
