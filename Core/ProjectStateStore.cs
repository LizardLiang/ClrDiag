using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// 這筆記錄最後一次變更的時間，清理記錄時保留最近變更的工作目錄。
    /// 沒有 updated 欄位的記錄視為最舊。
    /// </summary>
    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }
}

/// <summary>
/// 讀寫 %LOCALAPPDATA%\clrdiag\projects.json，以工作目錄（不分大小寫）為鍵保存 ProjectState。
/// 讀取時檔案不存在、無法讀取或 JSON 損毀都視為空狀態。
/// 寫入是「讀取 → 修改 → 寫回」，以具名 Mutex 串接同一個檔案的所有 clrdiag 行程：
/// 檔案存在但讀不到時不寫入，避免蓋掉其他工作目錄的記錄；JSON 損毀時先改名為
/// projects.json.&lt;時間&gt;.bak 再寫入新檔，最多保留 MaxBackups 份備份。
/// 寫入先寫暫存檔再取代，中途中斷也不會留下半份檔案；每次寫入順便清理超過上限的舊記錄
/// 與先前中斷留下的暫存檔。清理只依記錄數與變更時間決定，不檢查工作目錄是否存在，
/// 因此離線的隨身碟、網路磁碟或 VPN 路徑的記錄會保留下來。
/// </summary>
public sealed class ProjectStateStore
{
    /// <summary>保存的工作目錄數上限，超過時保留最近變更的記錄。</summary>
    public const int DefaultMaxEntries = 100;

    /// <summary>損毀狀態檔備份的保留份數，超過時刪除最舊的備份。</summary>
    public const int MaxBackups = 3;

    /// <summary>等待其他 clrdiag 行程釋放檔案鎖的時間上限。</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(2);

    /// <summary>暫存檔超過這個時間沒有更新，就視為先前中斷留下的檔案。</summary>
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(5);

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
    private readonly int _maxEntries;
    private readonly TimeSpan _lockTimeout;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// filePath 指定狀態檔位置，測試可指向暫存資料夾；maxEntries 是保存的工作目錄數上限；
    /// lockTimeout 是等待檔案鎖的時間上限；clock 提供記錄的變更時間與備份檔名中的時間。
    /// </summary>
    public ProjectStateStore(
        string filePath,
        int maxEntries = DefaultMaxEntries,
        TimeSpan? lockTimeout = null,
        Func<DateTimeOffset>? clock = null
    )
    {
        _filePath = Path.GetFullPath(filePath);
        _maxEntries = maxEntries;
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>狀態檔的完整路徑。</summary>
    public string FilePath => _filePath;

    /// <summary>寫入失敗時給使用者看的訊息。</summary>
    public string WriteFailureMessage => $"無法寫入專案記錄: {_filePath}";

    /// <summary>串接同一個狀態檔讀寫的具名 Mutex 名稱，由檔案完整路徑（不分大小寫）衍生。</summary>
    public static string MutexNameFor(string filePath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(filePath).ToLowerInvariant()));
        // Local\ 的範圍是目前的登入工作階段，狀態檔位於該使用者自己的 %LOCALAPPDATA%。
        return $@"Local\clrdiag-projects-{Convert.ToHexString(hash)[..16]}";
    }

    /// <summary>取得工作目錄的記錄；沒有記錄或檔案無法讀取時回傳 ProjectState.Empty。</summary>
    public ProjectState Get(string workingDir) =>
        ReadAll().Entries.TryGetValue(NormalizeKey(workingDir), out var state) && state is not null
            ? state
            : ProjectState.Empty;

    /// <summary>設定預設專案。回傳是否成功寫入。</summary>
    public bool SetDefault(string workingDir, string projectPath) =>
        Update(workingDir, s => s with { Default = Path.GetFullPath(projectPath) });

    /// <summary>清除預設專案。回傳是否成功寫入。</summary>
    public bool ClearDefault(string workingDir) => Update(workingDir, s => s with { Default = null });

    /// <summary>記錄上次執行的專案；與現有記錄相同時不寫檔。回傳是否成功寫入。</summary>
    public bool SetLastRun(string workingDir, string projectPath) =>
        Update(workingDir, s => s with { LastRun = Path.GetFullPath(projectPath) });

    /// <summary>清除上次執行的專案，用於移除已不存在的記錄。回傳是否成功寫入。</summary>
    public bool ClearLastRun(string workingDir) => Update(workingDir, s => s with { LastRun = null });

    /// <summary>
    /// 在同一次「讀取 → 修改 → 寫回」中清除指定的欄位，用於一併移除已不存在的預設與上次執行記錄。
    /// 兩個欄位都不清除時不讀寫檔案。回傳是否成功寫入。
    /// </summary>
    public bool Clear(string workingDir, bool clearDefault, bool clearLastRun)
    {
        if (!clearDefault && !clearLastRun)
            return true;

        return Update(
            workingDir,
            s => s with { Default = clearDefault ? null : s.Default, LastRun = clearLastRun ? null : s.LastRun }
        );
    }

    /// <summary>
    /// 在同一次「讀取 → 修改 → 寫回」中設定預設專案，clearLastRun 為 true 時一併清除上次執行記錄；
    /// 用於選單設為預設時同時移除已不存在的記錄。回傳是否成功寫入。
    /// </summary>
    public bool SetDefault(string workingDir, string projectPath, bool clearLastRun) =>
        Update(
            workingDir,
            s => s with { Default = Path.GetFullPath(projectPath), LastRun = clearLastRun ? null : s.LastRun }
        );

    /// <summary>把工作目錄轉成狀態檔的鍵：完整路徑、去掉結尾分隔符、轉小寫。</summary>
    public static string NormalizeKey(string workingDir) =>
        Path.GetFullPath(workingDir).TrimEnd('\\', '/').ToLowerInvariant();

    /// <summary>讀取狀態檔的結果分類；Update 依此決定能不能寫回。</summary>
    private enum ReadStatus
    {
        Missing,
        Loaded,
        Unreadable,
        Corrupt,
    }

    private bool Update(string workingDir, Func<ProjectState, ProjectState> change)
    {
        using FileLock? fileLock = FileLock.Acquire(MutexNameFor(_filePath), _lockTimeout);
        if (fileLock is null)
            return false;

        var (status, all) = ReadAll();
        if (status == ReadStatus.Unreadable)
            return false;

        var key = NormalizeKey(workingDir);
        var current = all.TryGetValue(key, out var existing) && existing is not null ? existing : ProjectState.Empty;
        var updated = change(current);

        if (status != ReadStatus.Corrupt && SameProjects(current, updated))
            return true;

        if (status == ReadStatus.Corrupt && !BackUpCorruptFile())
            return false;

        if (updated.Default is null && updated.LastRun is null)
            all.Remove(key);
        else
            all[key] = updated with { Updated = _clock() };

        Prune(all, key);
        return WriteAll(all);
    }

    private static bool SameProjects(ProjectState a, ProjectState b) =>
        string.Equals(a.Default, b.Default, StringComparison.Ordinal)
        && string.Equals(a.LastRun, b.LastRun, StringComparison.Ordinal);

    private (ReadStatus Status, Dictionary<string, ProjectState?> Entries) ReadAll()
    {
        string json;
        try
        {
            json = File.ReadAllText(_filePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (ReadStatus.Missing, new Dictionary<string, ProjectState?>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ReadStatus.Unreadable, new Dictionary<string, ProjectState?>());
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, ProjectState?>>(json, JsonOptions);
            return parsed is null
                ? (ReadStatus.Loaded, new Dictionary<string, ProjectState?>())
                : (ReadStatus.Loaded, new Dictionary<string, ProjectState?>(parsed, StringComparer.Ordinal));
        }
        catch (JsonException)
        {
            return (ReadStatus.Corrupt, new Dictionary<string, ProjectState?>());
        }
    }

    /// <summary>
    /// 把損毀的狀態檔改名為 projects.json.&lt;yyyyMMddHHmmssfff&gt;.bak 保留原始內容，
    /// 再刪除超過 MaxBackups 份的舊備份。改名失敗時回傳 false。
    /// </summary>
    private bool BackUpCorruptFile()
    {
        try
        {
            File.Move(_filePath, $"{_filePath}.{_clock():yyyyMMddHHmmssfff}.bak", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        RemoveOldBackups();
        return true;
    }

    /// <summary>依檔名中的時間保留最新的 MaxBackups 份備份；刪不掉的留到下次。</summary>
    private void RemoveOldBackups()
    {
        try
        {
            var folder = Path.GetDirectoryName(_filePath) ?? ".";
            var oldBackups = Directory
                .EnumerateFiles(folder, Path.GetFileName(_filePath) + ".*.bak")
                .OrderByDescending(file => file, StringComparer.OrdinalIgnoreCase)
                .Skip(MaxBackups)
                .ToList();
            foreach (var file in oldBackups)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 移除空的記錄；數量仍超過上限時保留目前的工作目錄與最近變更的記錄。
    /// 只依記錄數與變更時間決定，不存取檔案系統。
    /// </summary>
    private void Prune(Dictionary<string, ProjectState?> all, string currentKey)
    {
        foreach (var key in all.Keys.ToList())
        {
            if (key != currentKey && all[key] is null)
                all.Remove(key);
        }

        if (all.Count <= _maxEntries)
            return;

        var keep = all.OrderByDescending(entry => entry.Key == currentKey)
            .ThenByDescending(entry => entry.Value?.Updated ?? DateTimeOffset.MinValue)
            .Take(_maxEntries)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in all.Keys.Where(key => !keep.Contains(key)).ToList())
            all.Remove(key);
    }

    private bool WriteAll(Dictionary<string, ProjectState?> all)
    {
        var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var folder = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
                RemoveStaleTempFiles(folder);
            }

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

    /// <summary>刪除先前寫入中斷留下、超過 StaleTempAge 沒有更新的暫存檔；刪不掉的留到下次。</summary>
    private void RemoveStaleTempFiles(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, Path.GetFileName(_filePath) + ".*.tmp"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > StaleTempAge)
                        File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>持有狀態檔的具名 Mutex；Dispose 時釋放。</summary>
    private sealed class FileLock : IDisposable
    {
        private readonly Mutex _mutex;

        private FileLock(Mutex mutex)
        {
            _mutex = mutex;
        }

        /// <summary>
        /// 在 timeout 內取得鎖；逾時或無法開啟具名 Mutex 時回傳 null。前一個持有者異常結束時視為取得。
        /// </summary>
        public static FileLock? Acquire(string name, TimeSpan timeout)
        {
            Mutex mutex;
            try
            {
                mutex = new Mutex(initiallyOwned: false, name);
            }
            catch (Exception ex)
                when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                return null;
            }

            bool acquired;
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (acquired)
                return new FileLock(mutex);

            mutex.Dispose();
            return null;
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
