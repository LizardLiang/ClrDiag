namespace ClrDiag.Core;

/// <summary>決定使用某個專案的依據，用於啟動訊息的說明文字。</summary>
public enum ProjectSource
{
    /// <summary>由 --project 指定。</summary>
    Explicit,

    /// <summary>工作目錄設定的預設專案。</summary>
    Default,

    /// <summary>工作目錄底下只找到一個專案。</summary>
    Single,

    /// <summary>非互動模式沿用上次在互動模式執行的專案。</summary>
    LastRun,
}

/// <summary>專案解析的結果；呼叫端依子型別決定直接啟動、顯示選單、改走往上搜尋或回報錯誤。</summary>
public abstract record ResolveOutcome
{
    /// <summary>直接使用這個專案。</summary>
    public sealed record Use(DiscoveredProject Project, ProjectSource Source) : ResolveOutcome;

    /// <summary>顯示選單讓使用者挑選；Preselect 是應放在第一個的上次執行專案。</summary>
    public sealed record Prompt(IReadOnlyList<DiscoveredProject> Projects, DiscoveredProject? Preselect)
        : ResolveOutcome;

    /// <summary>工作目錄底下沒有任何專案，沿用往上搜尋專案根目錄的行為。</summary>
    public sealed record FallBackUpward : ResolveOutcome;

    /// <summary>無法決定專案；Candidates 是要列給使用者參考的專案。</summary>
    public sealed record Error(string Message, IReadOnlyList<DiscoveredProject> Candidates) : ResolveOutcome;
}

/// <summary>
/// 解析結果加上已失效的記錄。StaleDefault / StaleLastRun 為 true 表示記錄的檔案已不存在，
/// 呼叫端應從狀態檔移除。
/// </summary>
public sealed record ProjectResolution(ResolveOutcome Outcome, bool StaleDefault, bool StaleLastRun);

/// <summary>
/// 依「--project → 沒有任何專案時往上搜尋 → --pick → 預設專案 → 唯一專案 → 選單／上次執行」的順序決定要用哪個專案。
/// 純邏輯，不做任何主控台輸出；非互動模式永遠不回傳 Prompt。
/// </summary>
public static class ProjectResolver
{
    /// <summary>
    /// 解析要使用的專案。projectQuery 是 --project 的值，不為 null 就代表有指定：
    /// 空白或沒有唯一符合（包括工作目錄底下沒有任何專案）時回傳 Error。
    /// pick 對應 --pick，只在互動模式生效；interactive 表示可以顯示選單。
    /// </summary>
    public static ProjectResolution Resolve(
        IReadOnlyList<DiscoveredProject> discovered,
        ProjectState saved,
        string? projectQuery,
        bool pick,
        bool interactive
    )
    {
        var defaultProject = FindSaved(discovered, saved.Default, out var staleDefault);
        var lastRunProject = FindSaved(discovered, saved.LastRun, out var staleLastRun);

        ProjectResolution Result(ResolveOutcome outcome) => new(outcome, staleDefault, staleLastRun);

        if (projectQuery is not null)
        {
            var match = ProjectDiscovery.Match(discovered, projectQuery);
            if (match.Hit is not null)
                return Result(new ResolveOutcome.Use(match.Hit, ProjectSource.Explicit));

            var miss = ProjectDiscovery.DescribeMiss(projectQuery, match, discovered);
            return Result(new ResolveOutcome.Error(miss.Message, miss.Candidates));
        }

        if (discovered.Count == 0)
            return Result(new ResolveOutcome.FallBackUpward());

        if (interactive && pick)
            return Result(new ResolveOutcome.Prompt(discovered, lastRunProject));

        if (defaultProject is not null)
            return Result(new ResolveOutcome.Use(defaultProject, ProjectSource.Default));

        if (discovered.Count == 1)
            return Result(new ResolveOutcome.Use(discovered[0], ProjectSource.Single));

        if (interactive)
            return Result(new ResolveOutcome.Prompt(discovered, lastRunProject));

        if (lastRunProject is not null)
            return Result(new ResolveOutcome.Use(lastRunProject, ProjectSource.LastRun));

        return Result(
            new ResolveOutcome.Error("找到多個專案，請用 --project 指定，或用 --set-default 設定預設專案", discovered)
        );
    }

    /// <summary>
    /// 在掃描結果中找出記錄的專案。記錄的檔案已不存在時 stale 為 true；
    /// 檔案還在但不在掃描範圍內時回傳 null 且不視為失效。
    /// </summary>
    private static DiscoveredProject? FindSaved(
        IReadOnlyList<DiscoveredProject> discovered,
        string? savedPath,
        out bool stale
    )
    {
        stale = false;
        if (string.IsNullOrEmpty(savedPath))
            return null;

        var hit = discovered.FirstOrDefault(p =>
            string.Equals(p.FullPath, savedPath, StringComparison.OrdinalIgnoreCase)
        );
        if (hit is null)
            stale = !File.Exists(savedPath);
        return hit;
    }
}
