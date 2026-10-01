using System.Diagnostics;

namespace ClrDiag.Core;

/// <summary>工作目錄底下找到的一個方案檔或專案檔。</summary>
/// <param name="FullPath">檔案的完整路徑。</param>
/// <param name="RelativePath">相對於工作目錄的路徑，一律以 / 分隔。</param>
/// <param name="Name">不含副檔名的檔名，供 --project 以名稱比對。</param>
public sealed record DiscoveredProject(string FullPath, string RelativePath, string Name);

/// <summary>
/// 以名稱或路徑比對專案的結果。Hit 不為 null 表示唯一命中；
/// 否則 Candidates 是符合的候選清單（零筆代表完全沒有符合）。
/// </summary>
public sealed record MatchResult(DiscoveredProject? Hit, IReadOnlyList<DiscoveredProject> Candidates);

/// <summary>
/// 掃描結果。Truncated 為 true 表示掃描在資料夾數或時間上限內沒有走完，Projects 可能不完整；
/// UnreadableLinks 是因為無法讀取連結目標而略過的資料夾數。
/// </summary>
public sealed record ScanResult(IReadOnlyList<DiscoveredProject> Projects, bool Truncated, int UnreadableLinks);

/// <summary>掃描對一個子資料夾的處理方式。</summary>
internal enum FolderEntry
{
    /// <summary>進入並掃描這個資料夾。</summary>
    Enter,

    /// <summary>略過：名稱在略過清單中、以「.」開頭，或是連結點與符號連結。</summary>
    Skip,

    /// <summary>略過：讀取連結目標失敗，計入 ScanResult.UnreadableLinks。</summary>
    UnreadableLink,
}

/// <summary>比對沒有唯一結果時要告訴使用者的訊息，以及要列出的候選專案（可能是空清單）。</summary>
public sealed record ProjectMiss(string Message, IReadOnlyList<DiscoveredProject> Candidates);

/// <summary>
/// 往下掃描工作目錄，找出所有 .sln / .slnx / .csproj / .vbproj。
/// 副檔名清單與 DiagConfig 的建置目標偵測共用；建置輸出與套件資料夾直接略過以維持掃描速度。
/// </summary>
public static class ProjectDiscovery
{
    /// <summary>預設掃描深度；工作目錄本身是第 0 層。</summary>
    public const int DefaultMaxDepth = 5;

    /// <summary>一次掃描最多檢查的資料夾數。</summary>
    public const int DefaultMaxFolders = 20_000;

    /// <summary>
    /// 一次掃描的時間上限。資料夾數上限是主要的停止條件，讓掃描結果不受機器負載影響；
    /// 這個時間上限縮短慢速網路磁碟或 OneDrive 只存放在雲端的資料夾造成的等待，
    /// 正常的工作目錄在資料夾數上限內就會走完。時間只在處理每個資料夾之前檢查：
    /// 單一次列舉資料夾內容或讀取連結目標的呼叫停住時，掃描等到該呼叫回傳，實際耗時可能超過這個上限。
    /// </summary>
    public static readonly TimeSpan DefaultTimeBudget = TimeSpan.FromSeconds(10);

    /// <summary>掃描達到上限時提示使用者的訊息。</summary>
    public const string TruncatedNotice =
        "專案掃描已達資料夾數或時間上限，清單可能不完整；可用 --project 或 --root 指定專案";

    /// <summary>
    /// 掃描結果要提示使用者的訊息：達到上限時的 TruncatedNotice，以及無法讀取連結目標而略過的資料夾數。
    /// 兩者都沒有時回傳空清單。
    /// </summary>
    public static IReadOnlyList<string> ScanNotices(ScanResult scan)
    {
        var notices = new List<string>();
        if (scan.Truncated)
            notices.Add(TruncatedNotice);
        if (scan.UnreadableLinks > 0)
            notices.Add(
                $"專案掃描略過 {scan.UnreadableLinks} 個無法讀取連結目標的資料夾，清單可能不完整；"
                    + "可用 --project 或 --root 指定專案"
            );
        return notices;
    }

    /// <summary>不往下掃描的資料夾名稱；以「.」開頭的資料夾另外一律略過。</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        "node_modules",
        "packages",
        ".git",
        ".vs",
    };

    /// <summary>給使用者看的副檔名清單，例如「.sln / .slnx / .csproj / .vbproj」。</summary>
    public static string ExtensionList => string.Join(" / ", DiagConfig.ProjectExtensions);

    /// <summary>
    /// 以廣度優先掃描 workingDir 與其子資料夾。workingDir 本身是第 0 層，
    /// 第 maxDepth 層的資料夾仍會檢查，更深的不再進入。無法讀取的資料夾直接略過；
    /// 連結點與符號連結的資料夾不進入，避免重複列出專案或繞成迴圈；其他 reparse point（例如 OneDrive 資料夾）照常進入；
    /// 無法讀取連結目標的資料夾不進入，並計入 UnreadableLinks。
    /// 每處理一個資料夾之前檢查上限：檢查的資料夾數達到 maxFolders，或耗時超過 timeBudget
    /// （預設 DefaultTimeBudget）時停止，回傳已找到的專案並把 Truncated 設為 true；廣度優先讓淺層的專案先被找到。
    /// </summary>
    public static ScanResult Scan(
        string workingDir,
        int maxDepth = DefaultMaxDepth,
        int maxFolders = DefaultMaxFolders,
        TimeSpan? timeBudget = null
    )
    {
        var root = Path.GetFullPath(workingDir);
        var budget = timeBudget ?? DefaultTimeBudget;
        var clock = Stopwatch.StartNew();
        var results = new List<DiscoveredProject>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;
        var truncated = false;
        var unreadableLinks = 0;

        while (pending.Count > 0)
        {
            if (visited >= maxFolders || clock.Elapsed > budget)
            {
                truncated = true;
                break;
            }

            var (folder, depth) = pending.Dequeue();
            visited++;
            try
            {
                var directory = new DirectoryInfo(folder);
                foreach (var file in directory.EnumerateFiles())
                {
                    if (IsProjectFile(file.FullName))
                        results.Add(Describe(root, file.FullName));
                }

                if (depth >= maxDepth)
                    continue;

                foreach (var subFolder in directory.EnumerateDirectories())
                {
                    switch (ShouldEnter(subFolder.Name, subFolder.Attributes, () => subFolder.LinkTarget))
                    {
                        case FolderEntry.Enter:
                            pending.Enqueue((subFolder.FullName, depth + 1));
                            break;
                        case FolderEntry.UnreadableLink:
                            unreadableLinks++;
                            break;
                        case FolderEntry.Skip:
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }

        SortByPath(results);
        return new ScanResult(results, truncated, unreadableLinks);
    }

    /// <summary>
    /// 掃描沒有走完時，把仍存在、位於工作目錄底下且不在清單裡的記錄（預設、上次執行）補進清單，
    /// 讓「預設專案 → 上次執行」的決定順序不受掃描上限影響。
    /// </summary>
    public static IReadOnlyList<DiscoveredProject> IncludeSaved(
        IReadOnlyList<DiscoveredProject> projects,
        string workingDir,
        ProjectState saved
    )
    {
        var root = Path.GetFullPath(workingDir);
        var results = new List<DiscoveredProject>(projects);
        foreach (var path in new[] { saved.Default, saved.LastRun })
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || !IsProjectFile(path))
                continue;

            if (!IsInside(root, path))
                continue;

            if (results.Any(p => string.Equals(p.FullPath, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            results.Add(Describe(root, Path.GetFullPath(path)));
        }

        SortByPath(results);
        return results;
    }

    /// <summary>
    /// 依序比對：完整相對路徑 → 檔名（不含副檔名）→ 相對路徑的子字串，皆不分大小寫。
    /// 某一步有唯一結果就採用；檔名或子字串符合多筆時回傳那些候選，交給呼叫端提示使用者。
    /// 空白的 query 不符合任何專案。
    /// </summary>
    public static MatchResult Match(IReadOnlyList<DiscoveredProject> projects, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new MatchResult(null, Array.Empty<DiscoveredProject>());

        var normalized = query.Trim().Replace('\\', '/');

        var byPath = projects
            .Where(p => string.Equals(p.RelativePath, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byPath.Count == 1)
            return new MatchResult(byPath[0], byPath);

        var byName = projects
            .Where(p => string.Equals(p.Name, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byName.Count == 1)
            return new MatchResult(byName[0], byName);
        if (byName.Count > 1)
            return new MatchResult(null, byName);

        var bySubstring = projects
            .Where(p => p.RelativePath.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return bySubstring.Count == 1
            ? new MatchResult(bySubstring[0], bySubstring)
            : new MatchResult(null, bySubstring);
    }

    /// <summary>
    /// 比對沒有唯一結果時的訊息與候選：空白名稱與完全沒有符合時列出所有偵測到的專案，
    /// 符合多筆時只列出那些候選。--project 與 --set-default 共用這段文字。
    /// </summary>
    public static ProjectMiss DescribeMiss(
        string query,
        MatchResult match,
        IReadOnlyList<DiscoveredProject> discovered
    )
    {
        if (string.IsNullOrWhiteSpace(query))
            return new ProjectMiss("專案名稱不能空白", discovered);

        return match.Candidates.Count > 1
            ? new ProjectMiss($"「{query}」符合多個專案，請指定更完整的路徑", match.Candidates)
            : new ProjectMiss($"找不到符合「{query}」的專案", discovered);
    }

    private static bool IsProjectFile(string path) =>
        DiagConfig.ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>path 相對於 root 的路徑，一律以 / 分隔；專案清單與狀態列共用這個格式。</summary>
    public static string RelativePathOf(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>
    /// path 是否位於 root 底下：相對路徑的第一段是「..」或相對路徑仍是絕對路徑（不同磁碟）時不算。
    /// 以完整路徑段比對，名稱以「..」開頭的資料夾（例如 ..foo）仍算在 root 底下。
    /// </summary>
    public static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative))
            return false;

        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return firstSegment != "..";
    }

    /// <summary>
    /// 掃描是否進入這個子資料夾：略過清單中的名稱、以「.」開頭的資料夾，以及連結點與符號連結。
    /// 帶有 ReparsePoint 屬性但 linkTarget 回傳 null 的資料夾（例如 OneDrive 同步根目錄與其下的子資料夾）照常進入；
    /// 只有帶 ReparsePoint 屬性時才呼叫 linkTarget；讀取連結目標失敗時不進入並回傳 UnreadableLink，由呼叫端計數。
    /// </summary>
    internal static FolderEntry ShouldEnter(string name, FileAttributes attributes, Func<string?> linkTarget)
    {
        if (name.StartsWith('.') || SkippedFolders.Contains(name))
            return FolderEntry.Skip;

        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
            return FolderEntry.Enter;

        try
        {
            return linkTarget() is null ? FolderEntry.Enter : FolderEntry.Skip;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FolderEntry.UnreadableLink;
        }
    }

    private static DiscoveredProject Describe(string root, string file) =>
        new(file, RelativePathOf(root, file), Path.GetFileNameWithoutExtension(file));

    private static void SortByPath(List<DiscoveredProject> projects) =>
        projects.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
}
