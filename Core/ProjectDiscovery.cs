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
/// 往下掃描工作目錄，找出所有 .sln / .slnx / .csproj / .vbproj。
/// 副檔名清單與 DiagConfig 的建置目標偵測一致；建置輸出與套件資料夾直接略過以維持掃描速度。
/// </summary>
public static class ProjectDiscovery
{
    /// <summary>視為專案的副檔名。</summary>
    private static readonly string[] ProjectExtensions = { ".sln", ".slnx", ".csproj", ".vbproj" };

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

    /// <summary>
    /// 掃描 workingDir 與其子資料夾。workingDir 本身是第 0 層，
    /// 第 maxDepth 層的資料夾仍會檢查，更深的不再進入。無法讀取的資料夾直接略過。
    /// </summary>
    public static IReadOnlyList<DiscoveredProject> Scan(string workingDir, int maxDepth = 5)
    {
        var root = Path.GetFullPath(workingDir);
        var results = new List<DiscoveredProject>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            var (folder, depth) = pending.Pop();
            string[] files;
            string[] subFolders;
            try
            {
                files = Directory.GetFiles(folder);
                subFolders = depth < maxDepth ? Directory.GetDirectories(folder) : Array.Empty<string>();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                if (!ProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    continue;

                results.Add(
                    new DiscoveredProject(
                        file,
                        Path.GetRelativePath(root, file).Replace('\\', '/'),
                        Path.GetFileNameWithoutExtension(file)
                    )
                );
            }

            foreach (var subFolder in subFolders)
            {
                var name = Path.GetFileName(subFolder);
                if (name.StartsWith('.') || SkippedFolders.Contains(name))
                    continue;
                pending.Push((subFolder, depth + 1));
            }
        }

        results.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return results;
    }

    /// <summary>
    /// 依序比對：完整相對路徑 → 檔名（不含副檔名）→ 相對路徑的子字串，皆不分大小寫。
    /// 某一步有唯一結果就採用；檔名或子字串符合多筆時回傳那些候選，交給呼叫端提示使用者。
    /// </summary>
    public static MatchResult Match(IReadOnlyList<DiscoveredProject> projects, string query)
    {
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
}
