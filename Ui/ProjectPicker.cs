using ClrDiag.Core;
using Spectre.Console;

namespace ClrDiag.Ui;

/// <summary>
/// 啟動前的專案選單：可輸入名稱篩選，上次執行的專案排在第一個，最後一項是「取消」。
/// 選單在切換替代畫面與啟動任何子行程之前執行，子行程不會搶走選單的鍵盤輸入。
/// 標記文字只用 Big5（cp950）主控台也能顯示的字元。
/// </summary>
public static class ProjectPicker
{
    /// <summary>預設專案的標記。</summary>
    public const string DefaultMarker = "★ 預設";

    /// <summary>上次執行專案的標記。</summary>
    public const string LastRunMarker = "◎ 上次";

    /// <summary>選單最後一項的文字；選它代表不啟動並結束。</summary>
    public const string CancelLabel = "取消";

    /// <summary>代表「取消」的選項，以參考比較與真正的專案區分。</summary>
    private static readonly DiscoveredProject CancelChoice = new(string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// 顯示選單並詢問是否設為預設；preselect 是上次執行的專案，會排在第一個。
    /// 使用者選「取消」時回傳 null，不再詢問是否設為預設。
    /// </summary>
    public static ProjectPick? Pick(
        IReadOnlyList<DiscoveredProject> projects,
        DiscoveredProject? preselect,
        ProjectState saved
    )
    {
        var prompt = new SelectionPrompt<DiscoveredProject>()
            .Title("選擇要執行的專案（↑↓ 移動、Enter 確認，選「取消」離開）")
            .PageSize(15)
            .EnableSearch()
            .SearchPlaceholderText("輸入名稱篩選…")
            .MoreChoicesText("[grey]（還有更多專案，往下捲動查看）[/]")
            .UseConverter(project =>
                ReferenceEquals(project, CancelChoice) ? $"[grey]{CancelLabel}[/]" : LabelMarkup(project, saved)
            )
            .AddChoices(OrderChoices(projects, preselect))
            .AddChoices(CancelChoice);

        DiscoveredProject picked = AnsiConsole.Prompt(prompt);
        if (ReferenceEquals(picked, CancelChoice))
        {
            return null;
        }

        bool setDefault = AnsiConsole.Confirm("設為預設？", defaultValue: false);
        return new ProjectPick(picked, setDefault);
    }

    /// <summary>選單的項目順序：上次執行的專案排第一，其餘維持掃描結果的排序。</summary>
    public static IReadOnlyList<DiscoveredProject> OrderChoices(
        IReadOnlyList<DiscoveredProject> projects,
        DiscoveredProject? preselect
    )
    {
        if (preselect is null || !projects.Contains(preselect))
        {
            return projects;
        }

        var ordered = new List<DiscoveredProject>(projects.Count) { preselect };
        ordered.AddRange(projects.Where(p => p != preselect));
        return ordered;
    }

    /// <summary>專案相對於記錄的標記文字（預設、上次），以空白分隔；沒有標記時回傳空字串。</summary>
    public static string Markers(DiscoveredProject project, ProjectState saved)
    {
        var markers = new List<string>(2);
        if (IsSame(project, saved.Default))
        {
            markers.Add(DefaultMarker);
        }

        if (IsSame(project, saved.LastRun))
        {
            markers.Add(LastRunMarker);
        }

        return string.Join(" ", markers);
    }

    /// <summary>選單項目的顯示文字：相對路徑加上標記，已跳脫 Spectre 標記字元。</summary>
    private static string LabelMarkup(DiscoveredProject project, ProjectState saved)
    {
        string markers = Markers(project, saved);
        string path = Markup.Escape(project.RelativePath);
        return markers.Length == 0 ? path : $"{path}  [yellow]{Markup.Escape(markers)}[/]";
    }

    private static bool IsSame(DiscoveredProject project, string? savedPath) =>
        savedPath is not null
        && string.Equals(project.FullPath, savedPath, StringComparison.OrdinalIgnoreCase);
}
