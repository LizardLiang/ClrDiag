namespace ClrDiag.Core;

/// <summary>使用者在選單中挑選專案的結果；SetAsDefault 表示使用者同意把它設為預設專案。</summary>
public sealed record ProjectPick(DiscoveredProject Project, bool SetAsDefault);

/// <summary>
/// 啟動前專案選擇的結果。ExitCode 不為 null 時呼叫端以這個結束碼結束（Error 不為 null 時先印出它）；
/// ExitCode 為 null 且 Project 為 null 表示改走往上搜尋專案根目錄：工作目錄底下沒有專案，
/// 或 --list 遇到多個專案且沒有記錄。
/// </summary>
public sealed record ProjectSelectionResult(
    DiscoveredProject? Project,
    string? Label,
    int? ExitCode,
    ProjectMiss? Error
);

/// <summary>
/// 啟動流程中與專案選擇有關的決策：是否互動、哪些模式做專案偵測、旗標衝突警告、
/// 掃描 → 解析 → 選單 → 記錄的串接，以及狀態列文字。主控台輸出由呼叫端負責，
/// 選單以委派傳入，因此整段流程可以在測試中執行。
/// </summary>
public static class ProjectSelection
{
    /// <summary>使用者在選單選「取消」時的結束碼，與儀表板正常結束相同。</summary>
    public const int CancelExitCode = 0;

    /// <summary>無法決定專案（名稱沒有唯一符合、批次模式有多個專案）時的結束碼。</summary>
    public const int ErrorExitCode = 2;

    /// <summary>
    /// 是否為互動模式：沒有任何批次旗標、輸入輸出都接在主控台上，且終端機支援互動。
    /// consoleInteractive 只在前面條件都成立時才呼叫，批次模式不會觸發 Spectre Profile 的建立。
    /// </summary>
    public static bool IsInteractive(
        bool anyBatchFlag,
        bool inputRedirected,
        bool outputRedirected,
        Func<bool> consoleInteractive
    ) => !anyBatchFlag && !inputRedirected && !outputRedirected && consoleInteractive();

    /// <summary>
    /// 這次執行是否做專案偵測。--output 不讀專案設定；--init 與 --install-skill（不論範圍）
    /// 寫入往上搜尋得到的專案根目錄，因此三者都不掃描工作目錄。
    /// </summary>
    public static bool UsesDiscovery(bool outputMode, bool initMode, bool installSkillMode) =>
        !outputMode && !initMode && !installSkillMode;

    /// <summary>
    /// 彼此不生效的旗標組合對應的警告文字，沒有衝突時回傳空清單。
    /// explicitRoot 表示有 --root 或 --config；projectFlags 表示有 --project 或 --pick；
    /// projectCommand 表示有 --projects、--set-default 或 --clear-default。
    /// </summary>
    public static IReadOnlyList<string> ConflictWarnings(
        bool explicitRoot,
        bool projectFlags,
        bool projectCommand,
        bool usesDiscovery
    )
    {
        var warnings = new List<string>();
        if (projectCommand)
        {
            if (explicitRoot)
                warnings.Add("--projects / --set-default / --clear-default 依目前工作目錄運作，--root / --config 不生效");
            if (projectFlags)
                warnings.Add("--projects / --set-default / --clear-default 不啟動診斷，--project / --pick 不生效");
            return warnings;
        }

        if (!projectFlags)
            return warnings;

        if (explicitRoot)
            warnings.Add("已用 --root / --config 指定專案根目錄，--project / --pick 不生效");
        else if (!usesDiscovery)
            warnings.Add("--init / --install-skill / --output 不做專案偵測，--project / --pick 不生效");

        return warnings;
    }

    /// <summary>--list 遇到多個專案且沒有記錄時，改用往上搜尋的專案根目錄並以這段文字提示使用者。</summary>
    public const string AmbiguousListNotice =
        "找到多個專案，--list 改用往上搜尋得到的專案根目錄設定；可用 --project 指定專案";

    /// <summary>
    /// --list 改走往上搜尋時的提示。記錄的預設或上次執行專案已不存在時，在 AmbiguousListNotice 後面說明哪些記錄失效；
    /// 批次模式不寫入狀態檔，因此一併提示可用 --set-default 重新設定預設專案。
    /// </summary>
    public static string AmbiguousListNoticeFor(bool staleDefault, bool staleLastRun)
    {
        string? stale = (staleDefault, staleLastRun) switch
        {
            (true, true) => "預設與上次執行專案",
            (true, false) => "預設專案",
            (false, true) => "上次執行專案",
            _ => null,
        };
        return stale is null
            ? AmbiguousListNotice
            : $"{AmbiguousListNotice}；記錄的{stale}已不存在，可用 --set-default 重新設定預設專案";
    }

    /// <summary>
    /// 掃描工作目錄並決定要用的專案。必要時呼叫 prompt 顯示選單，prompt 回傳 null 代表使用者取消。
    /// interactive 且選定了專案時，才移除已失效的記錄並在使用者同意時寫入預設專案；
    /// 取消、錯誤與批次模式不寫入狀態檔。
    /// ambiguity 為 AmbiguityPolicy.FallBackUpward（--list）時，批次模式遇到多個專案且沒有記錄不回報錯誤，
    /// 改走往上搜尋專案根目錄並提示 AmbiguousListNoticeFor 的文字；工作目錄底下沒有專案時不提示。
    /// 掃描達到上限、記錄寫入失敗、使用者取消等非致命訊息交給 notice 輸出。
    /// </summary>
    public static ProjectSelectionResult Resolve(
        ProjectStateStore store,
        string workingDir,
        string? projectQuery,
        bool pick,
        bool interactive,
        Func<IReadOnlyList<DiscoveredProject>, DiscoveredProject?, ProjectState, ProjectPick?> prompt,
        Action<string> notice,
        AmbiguityPolicy ambiguity = AmbiguityPolicy.Error
    )
    {
        ScanResult scan = ProjectDiscovery.Scan(workingDir);
        ProjectState saved = store.Get(workingDir);
        IReadOnlyList<DiscoveredProject> discovered = scan.Projects;
        foreach (string message in ProjectDiscovery.ScanNotices(scan))
            notice(message);
        if (scan.Truncated)
            discovered = ProjectDiscovery.IncludeSaved(discovered, workingDir, saved);

        ProjectResolution resolution = ProjectResolver.Resolve(
            discovered,
            saved,
            projectQuery,
            pick,
            interactive,
            ambiguity
        );

        switch (resolution.Outcome)
        {
            case ResolveOutcome.Use use:
                ClearStaleRecords(store, workingDir, resolution, interactive, notice);
                return new ProjectSelectionResult(use.Project, SourceLabel(use.Source), null, null);

            case ResolveOutcome.Prompt choose:
                return RunPrompt(store, workingDir, choose, saved, prompt, resolution, notice);

            case ResolveOutcome.Error error:
                return new ProjectSelectionResult(
                    null,
                    null,
                    ErrorExitCode,
                    new ProjectMiss(error.Message, error.Candidates)
                );

            case ResolveOutcome.FallBackUpward fallBack:
                if (fallBack.Reason == FallBackReason.AmbiguousList)
                    notice(AmbiguousListNoticeFor(resolution.StaleDefault, resolution.StaleLastRun));
                return new ProjectSelectionResult(null, null, null, null);

            default:
                throw new InvalidOperationException($"未處理的解析結果: {resolution.Outcome}");
        }
    }

    /// <summary>
    /// 顯示選單並處理結果：取消時不寫入任何記錄。使用者同意設為預設時，以一次寫入設定預設專案
    /// 並移除已失效的上次執行記錄（已失效的預設由新的預設取代）；否則移除 resolution 標記的已失效記錄。
    /// 選單只在互動模式出現，因此這裡的寫入都以互動模式處理。
    /// </summary>
    private static ProjectSelectionResult RunPrompt(
        ProjectStateStore store,
        string workingDir,
        ResolveOutcome.Prompt choose,
        ProjectState saved,
        Func<IReadOnlyList<DiscoveredProject>, DiscoveredProject?, ProjectState, ProjectPick?> prompt,
        ProjectResolution resolution,
        Action<string> notice
    )
    {
        ProjectPick? picked = prompt(choose.Projects, choose.Preselect, saved);
        if (picked is null)
        {
            notice("已取消，未啟動儀表板");
            return new ProjectSelectionResult(null, null, CancelExitCode, null);
        }

        if (!picked.SetAsDefault)
        {
            ClearStaleRecords(store, workingDir, resolution, interactive: true, notice);
            return new ProjectSelectionResult(picked.Project, "選單", null, null);
        }

        if (store.SetDefault(workingDir, picked.Project.FullPath, clearLastRun: resolution.StaleLastRun))
            return new ProjectSelectionResult(picked.Project, "選單，已設為預設", null, null);

        notice(store.WriteFailureMessage);
        return new ProjectSelectionResult(picked.Project, "選單", null, null);
    }

    /// <summary>
    /// 互動模式選定專案後，以一次寫入移除已不存在的預設與上次執行記錄；批次模式只讀不寫。
    /// </summary>
    private static void ClearStaleRecords(
        ProjectStateStore store,
        string workingDir,
        ProjectResolution resolution,
        bool interactive,
        Action<string> notice
    )
    {
        if (interactive && !store.Clear(workingDir, resolution.StaleDefault, resolution.StaleLastRun))
            notice(store.WriteFailureMessage);
    }

    /// <summary>啟動訊息中說明專案來源的文字。</summary>
    public static string SourceLabel(ProjectSource source) =>
        source switch
        {
            ProjectSource.Explicit => "--project",
            ProjectSource.Default => "預設",
            ProjectSource.Single => "唯一專案",
            ProjectSource.LastRun => "上次執行",
            _ => source.ToString(),
        };

    /// <summary>
    /// 儀表板啟動時狀態列的文字：選定的專案與來源。clrdiag.json 的 buildProject 讓實際建置目標
    /// 不是選定的專案時，一併列出實際建置目標（相對於工作目錄）並註明來自設定檔。
    /// </summary>
    public static string StartupStatus(DiscoveredProject project, string? label, DiagConfig config, string workingDir)
    {
        string status = $"專案: {project.RelativePath}（{label}）";
        if (
            config.ResolvedBuildProject is { } target
            && !string.Equals(
                Path.GetFullPath(target),
                Path.GetFullPath(project.FullPath),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            string relative = ProjectDiscovery.RelativePathOf(workingDir, target);
            status += $"  建置目標: {relative}（{DiagConfig.FileName} 的 buildProject 優先）";
        }

        return status;
    }
}
