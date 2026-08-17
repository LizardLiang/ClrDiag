using System.Diagnostics;
using System.Reflection;
using Spectre.Console;

namespace ClrDiag.Core;

/// <summary>安裝範圍：全域（使用者家目錄）或本機（單一專案根目錄）。</summary>
internal enum SkillScope
{
    /// <summary>安裝到 %USERPROFILE%\.claude\skills\clrdiag。</summary>
    Global,

    /// <summary>安裝到 &lt;專案根目錄&gt;\.claude\skills\clrdiag。</summary>
    Local,
}

/// <summary>
/// 把內嵌在組件裡的 Claude Code 技能檔裝到使用者的技能目錄。
///
/// 流程是「解壓到快取 + 建立連結」而不是直接複製：技能檔先解壓到一個不含版本號的固定快取路徑，
/// 安裝位置只放一個指向該快取的連結。這樣升級 clrdiag 後重跑一次 --install-skill，
/// 全域與每個專案的連結都會同時看到新內容，不必逐一重裝。
/// </summary>
internal static class SkillInstaller
{
    /// <summary>內嵌資源的名稱前綴，對應 ClrDiag.csproj 裡設定的 LogicalName。</summary>
    private const string ResourcePrefix = "skill/";

    private const string SkillName = "clrdiag";

    /// <summary>技能檔解壓後的快取目錄；刻意不含版本號，重裝才能就地更新。</summary>
    public static string CachePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "clrdiag",
            "skills",
            SkillName
        );

    /// <summary>把 --install-skill 的參數值轉成安裝範圍；無法辨識時回傳 null。</summary>
    public static SkillScope? ParseScope(string? value) =>
        value switch
        {
            "global" => SkillScope.Global,
            "local" => SkillScope.Local,
            _ => null,
        };

    /// <summary>--install-skill 參數用法說明（參數缺漏或拼錯時印出）。</summary>
    public static void PrintUsage()
    {
        // 說明文字含方括號，交給 Spectre 會被當成樣式標記，因此直接輸出純文字
        Console.WriteLine(
            """
            用法
              clrdiag --install-skill global   裝到 %USERPROFILE%\.claude\skills\clrdiag（所有專案都能用）
              clrdiag --install-skill local    裝到 <專案根目錄>\.claude\skills\clrdiag
              clrdiag --install-skill local --root <path>   指定專案根目錄
              clrdiag --install-skill <範圍> --force        安裝位置已有真實目錄時覆寫它

            """
        );
    }

    /// <summary>解壓技能檔到快取，再於安裝位置建立指向快取的連結。</summary>
    /// <param name="scope">安裝範圍。</param>
    /// <param name="projectRoot">已解析的專案根目錄，供 <see cref="SkillScope.Local"/> 使用。</param>
    /// <param name="force">安裝位置已有真實目錄時是否遞迴刪除後重建連結。</param>
    /// <returns>結束碼：0 成功，非 0 失敗。</returns>
    public static int Install(SkillScope scope, string projectRoot, bool force)
    {
        string cachePath = CachePath;
        int fileCount;
        try
        {
            fileCount = Extract(cachePath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]技能檔解壓失敗:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        // 一個檔案都沒解出來表示組件裡根本沒有內嵌資源（建置設定壞了）；
        // 這時候要明講，不能留下一個空快取跟一個指向空目錄的連結
        if (fileCount == 0)
        {
            AnsiConsole.MarkupLine(
                $"[red]組件內沒有任何技能檔資源（前綴 {ResourcePrefix}），無法安裝[/]"
            );
            return 1;
        }

        string basePath = scope == SkillScope.Global
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(projectRoot);
        string linkPath = Path.Combine(basePath, ".claude", "skills", SkillName);

        int cleared = ClearExisting(linkPath, force);
        if (cleared != 0)
        {
            return cleared;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]無法建立安裝目錄:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        (string? linkKind, string? error) = CreateLink(linkPath, cachePath);
        if (linkKind is null)
        {
            AnsiConsole.MarkupLine($"[red]無法建立連結:[/] {Markup.Escape(error ?? "原因不明")}");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"已解壓 {fileCount} 個技能檔到 {Markup.Escape(cachePath)}"
        );
        AnsiConsole.MarkupLine($"已建立連結 {Markup.Escape(linkPath)}");
        AnsiConsole.MarkupLine($"連結型式 {Markup.Escape(linkKind)}");
        AnsiConsole.MarkupLine("[green]新開的 Claude Code 工作階段就會載入 clrdiag 技能。[/]");
        return 0;
    }

    /// <summary>
    /// 列舉內嵌資源、還原目錄結構後逐檔寫出，回傳寫出的檔案數。
    /// 每次都無條件覆寫——重新解壓正是更新內容的手段。
    /// </summary>
    private static int Extract(string cachePath)
    {
        Assembly assembly = typeof(SkillInstaller).Assembly;
        int count = 0;

        // 先清空快取再解壓：只覆寫的話，日後版本移掉某個技能檔時，舊檔會留在快取裡繼續被載入。
        // 快取整個目錄都由本工具管理，清掉是安全的
        var cache = new DirectoryInfo(cachePath);
        if (cache.LinkTarget is not null)
        {
            cache.Delete();
        }
        else if (cache.Exists)
        {
            cache.Delete(recursive: true);
        }

        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // MSBuild 的 %(RecursiveDir) 會帶 Windows 分隔符號，資源名稱因此長得像
            // skill/references\debug-loop.md，讀回來時要自己正規化
            string relative = name[ResourcePrefix.Length..]
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            string destination = Path.Combine(cachePath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using Stream source =
                assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"讀不到內嵌資源: {name}");
            // 逐位元組複製，不做任何編碼轉換：技能檔是 UTF-8，內含中文與 \u 轉義的 JSON 範例，
            // 必須原封不動地保留
            using FileStream target = File.Create(destination);
            source.CopyTo(target);
            count++;
        }

        return count;
    }

    /// <summary>
    /// 清掉安裝位置既有的東西。連結一律直接刪除重建（重跑安裝要能冪等）；
    /// 真實目錄預設拒絕，只有帶 --force 才遞迴刪除。
    /// </summary>
    /// <returns>0 表示可以繼續建立連結，非 0 表示應以該結束碼中止。</returns>
    private static int ClearExisting(string linkPath, bool force)
    {
        var entry = new DirectoryInfo(linkPath);

        // LinkTarget 有值就是符號連結或目錄連接點；刪掉連結不會動到被連到的內容
        if (entry.LinkTarget is not null)
        {
            try
            {
                entry.Delete();
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[red]無法移除既有連結 {Markup.Escape(linkPath)}:[/] {Markup.Escape(ex.Message)}"
                );
                return 1;
            }
        }

        bool isDirectory = entry.Exists;
        bool isFile = !isDirectory && File.Exists(linkPath);
        if (!isDirectory && !isFile)
        {
            return 0;
        }

        if (!force)
        {
            AnsiConsole.MarkupLine($"[red]安裝位置已存在且不是連結:[/] {Markup.Escape(linkPath)}");
            AnsiConsole.MarkupLine(
                "裡面是真實檔案，不會自動刪除。請自行移除，或加上 --force 覆寫。"
            );
            return 1;
        }

        try
        {
            if (isDirectory)
            {
                Directory.Delete(linkPath, recursive: true);
            }
            else
            {
                File.Delete(linkPath);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[red]--force 刪除失敗 {Markup.Escape(linkPath)}:[/] {Markup.Escape(ex.Message)}"
            );
            return 1;
        }

        AnsiConsole.MarkupLine($"[yellow]--force 已刪除原有內容:[/] {Markup.Escape(linkPath)}");
        return 0;
    }

    /// <summary>
    /// 建立指向快取的連結：先試符號連結，失敗就退回目錄連接點（junction）。
    /// junction 不需要提權，也不需要開發人員模式，是權限受限機器上唯一行得通的方式。
    /// </summary>
    /// <returns>成功時回傳連結型式與 null；失敗時回傳 null 與失敗原因。</returns>
    private static (string? Kind, string? Error) CreateLink(string linkPath, string cachePath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, cachePath);
            return ("符號連結 (symlink)", null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            string symlinkError = ex.Message;
            try
            {
                var startInfo = new ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/c mklink /J \"{linkPath}\" \"{cachePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // cmd 用主控台字碼頁輸出（繁中 Windows 是 Big5 950），不指定的話
                    // 失敗訊息讀回來會是亂碼
                    StandardOutputEncoding = Console.OutputEncoding,
                    StandardErrorEncoding = Console.OutputEncoding,
                };

                using Process? process = Process.Start(startInfo);
                if (process is null)
                {
                    return (null, $"符號連結失敗（{symlinkError}），且無法啟動 mklink");
                }

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0)
                {
                    return ("目錄連接點 (junction)", null);
                }

                string detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return (
                    null,
                    $"符號連結失敗（{symlinkError}），mklink /J 也失敗: {detail.Trim()}"
                );
            }
            catch (Exception fallbackEx)
            {
                return (
                    null,
                    $"符號連結失敗（{symlinkError}），mklink /J 也失敗: {fallbackEx.Message}"
                );
            }
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
