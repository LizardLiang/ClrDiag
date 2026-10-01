using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClrDiag.Core;

/// <summary>某個工作目錄記住的專案：預設專案與上次執行的專案，皆為完整路徑。</summary>
public sealed record ProjectState
{
    /// <summary>空狀態，代表這個工作目錄沒有任何記錄。</summary>
    public static readonly ProjectState Empty = new();

    /// <summary>預設專案的完整路徑；有值時啟動直接使用，不顯示選單。</summary>
    [JsonPropertyName("default")]
    public string? Default { get; init; }

    /// <summary>上次在互動模式執行的專案完整路徑。</summary>
    [JsonPropertyName("lastRun")]
    public string? LastRun { get; init; }
}

/// <summary>
/// 讀寫 %LOCALAPPDATA%\clrdiag\projects.json，以工作目錄（不分大小寫）為鍵保存 ProjectState。
/// 檔案不存在、無法讀取或 JSON 損毀時一律視為空狀態；寫入先寫暫存檔再取代，
/// 中途中斷也不會留下半份檔案。
/// </summary>
public sealed class ProjectStateStore
{
    /// <summary>正式使用的狀態檔位置。</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "clrdiag",
            "projects.json"
        );

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _filePath;

    /// <summary>filePath 指定狀態檔位置，測試可指向暫存資料夾。</summary>
    public ProjectStateStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>取得工作目錄的記錄；沒有記錄或檔案無法讀取時回傳 ProjectState.Empty。</summary>
    public ProjectState Get(string workingDir) =>
        ReadAll().TryGetValue(NormalizeKey(workingDir), out var state) && state is not null
            ? state
            : ProjectState.Empty;

    /// <summary>設定預設專案。回傳是否成功寫入。</summary>
    public bool SetDefault(string workingDir, string projectPath) =>
        Update(workingDir, s => s with { Default = Path.GetFullPath(projectPath) });

    /// <summary>清除預設專案。回傳是否成功寫入。</summary>
    public bool ClearDefault(string workingDir) => Update(workingDir, s => s with { Default = null });

    /// <summary>記錄上次執行的專案。回傳是否成功寫入。</summary>
    public bool SetLastRun(string workingDir, string projectPath) =>
        Update(workingDir, s => s with { LastRun = Path.GetFullPath(projectPath) });

    /// <summary>清除上次執行的專案，用於移除已不存在的記錄。回傳是否成功寫入。</summary>
    public bool ClearLastRun(string workingDir) => Update(workingDir, s => s with { LastRun = null });

    /// <summary>把工作目錄轉成狀態檔的鍵：完整路徑、去掉結尾分隔符、轉小寫。</summary>
    public static string NormalizeKey(string workingDir) =>
        Path.GetFullPath(workingDir).TrimEnd('\\', '/').ToLowerInvariant();

    private bool Update(string workingDir, Func<ProjectState, ProjectState> change)
    {
        var all = ReadAll();
        var key = NormalizeKey(workingDir);
        var current = all.TryGetValue(key, out var existing) && existing is not null ? existing : ProjectState.Empty;
        var updated = change(current);

        if (updated.Default is null && updated.LastRun is null)
            all.Remove(key);
        else
            all[key] = updated;

        return WriteAll(all);
    }

    private Dictionary<string, ProjectState?> ReadAll()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new Dictionary<string, ProjectState?>();

            var json = File.ReadAllText(_filePath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, ProjectState?>>(json, JsonOptions);
            return parsed is null
                ? new Dictionary<string, ProjectState?>()
                : new Dictionary<string, ProjectState?>(parsed, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, ProjectState?>();
        }
    }

    private bool WriteAll(Dictionary<string, ProjectState?> all)
    {
        var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var folder = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllText(tempPath, JsonSerializer.Serialize(all, JsonOptions));
            File.Move(tempPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}
