using ClrDiag.Core;

namespace ClrDiag.Tests;

/// <summary>ProjectStateStore 的讀寫、容錯與鍵正規化。</summary>
public sealed class ProjectStateStoreTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string StorePath => _tree.PathOf("state/projects.json");

    [Fact]
    public void 沒有狀態檔時回傳空狀態()
    {
        var store = new ProjectStateStore(StorePath);

        Assert.Equal(ProjectState.Empty, store.Get(_tree.Root));
    }

    [Fact]
    public void 寫入後以新的實例讀回相同內容()
    {
        var project = _tree.File("A/A.csproj");
        var other = _tree.File("B/B.sln");

        var writer = new ProjectStateStore(StorePath);
        Assert.True(writer.SetDefault(_tree.Root, project));
        Assert.True(writer.SetLastRun(_tree.Root, other));

        var state = new ProjectStateStore(StorePath).Get(_tree.Root);
        Assert.Equal(project, state.Default);
        Assert.Equal(other, state.LastRun);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StorePath)!, "*.tmp"));
    }

    [Fact]
    public void 清除預設專案只影響預設欄位()
    {
        var project = _tree.File("A/A.csproj");
        var store = new ProjectStateStore(StorePath);
        store.SetDefault(_tree.Root, project);
        store.SetLastRun(_tree.Root, project);

        store.ClearDefault(_tree.Root);

        var state = store.Get(_tree.Root);
        Assert.Null(state.Default);
        Assert.Equal(project, state.LastRun);
    }

    [Fact]
    public void 兩個欄位都清除後回到空狀態()
    {
        var project = _tree.File("A/A.csproj");
        var store = new ProjectStateStore(StorePath);
        store.SetDefault(_tree.Root, project);
        store.SetLastRun(_tree.Root, project);

        store.ClearDefault(_tree.Root);
        store.ClearLastRun(_tree.Root);

        Assert.Equal(ProjectState.Empty, store.Get(_tree.Root));
    }

    [Fact]
    public void 不同工作目錄各自保存()
    {
        var a = _tree.File("A/A.csproj");
        var b = _tree.File("B/B.csproj");
        var store = new ProjectStateStore(StorePath);

        store.SetDefault(_tree.PathOf("A"), a);
        store.SetDefault(_tree.PathOf("B"), b);

        Assert.Equal(a, store.Get(_tree.PathOf("A")).Default);
        Assert.Equal(b, store.Get(_tree.PathOf("B")).Default);
    }

    [Fact]
    public void 工作目錄鍵不分大小寫且忽略結尾分隔符()
    {
        var project = _tree.File("A/A.csproj");
        var store = new ProjectStateStore(StorePath);

        store.SetDefault(_tree.Root.ToUpperInvariant() + Path.DirectorySeparatorChar, project);

        Assert.Equal(project, store.Get(_tree.Root.ToLowerInvariant()).Default);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"c:\\\\w\": 42 }")]
    [InlineData("")]
    public void 狀態檔損毀時視為空狀態且可重新寫入(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, content);
        var project = _tree.File("A/A.csproj");
        var store = new ProjectStateStore(StorePath);

        Assert.Equal(ProjectState.Empty, store.Get(_tree.Root));
        Assert.True(store.SetDefault(_tree.Root, project));
        Assert.Equal(project, store.Get(_tree.Root).Default);
        Assert.Equal(content, File.ReadAllText(StorePath + ".bak"));
    }

    [Fact]
    public void 狀態檔無法讀取時不寫入也不覆蓋其他工作目錄的記錄()
    {
        var a = _tree.File("A/A.csproj");
        var store = new ProjectStateStore(StorePath);
        Assert.True(store.SetDefault(_tree.PathOf("A"), a));
        var before = File.ReadAllText(StorePath);

        using (new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(store.SetDefault(_tree.Root, a));
        }

        Assert.Equal(before, File.ReadAllText(StorePath));
        Assert.Equal(a, store.Get(_tree.PathOf("A")).Default);
        Assert.False(File.Exists(StorePath + ".bak"));
    }

    [Fact]
    public void 寫入失敗時不留下暫存檔()
    {
        Directory.CreateDirectory(StorePath);
        var store = new ProjectStateStore(StorePath);

        Assert.False(store.SetDefault(_tree.Root, _tree.File("A/A.csproj")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StorePath)!, "*.tmp"));
    }

    [Fact]
    public void 寫入時清除過期的暫存檔並保留新的暫存檔()
    {
        var folder = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(folder);
        var stale = Path.Combine(folder, "projects.json.old.tmp");
        var fresh = Path.Combine(folder, "projects.json.new.tmp");
        File.WriteAllText(stale, "");
        File.WriteAllText(fresh, "");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));

        Assert.True(new ProjectStateStore(StorePath).SetDefault(_tree.Root, _tree.File("A/A.csproj")));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void 寫入時移除工作目錄已不存在的記錄()
    {
        var project = _tree.File("A/A.csproj");
        var gone = _tree.PathOf("Gone");
        Directory.CreateDirectory(gone);
        var store = new ProjectStateStore(StorePath);
        store.SetDefault(gone, project);
        Directory.Delete(gone);

        Assert.True(store.SetDefault(_tree.Root, project));

        Assert.Equal(ProjectState.Empty, store.Get(gone));
        Assert.Equal(project, store.Get(_tree.Root).Default);
    }

    [Fact]
    public void 超過上限時保留最近變更的記錄()
    {
        var project = _tree.File("A/A.csproj");
        var dirs = new[] { "W1", "W2", "W3" }.Select(name => Directory.CreateDirectory(_tree.PathOf(name)).FullName).ToArray();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new ProjectStateStore(StorePath, maxEntries: 2, clock: () => now = now.AddMinutes(1));

        foreach (var dir in dirs)
            Assert.True(store.SetDefault(dir, project));

        Assert.Equal(ProjectState.Empty, store.Get(dirs[0]));
        Assert.Equal(project, store.Get(dirs[1]).Default);
        Assert.Equal(project, store.Get(dirs[2]).Default);
    }

    [Fact]
    public void 上次執行沒有變更時不寫檔()
    {
        var project = _tree.File("A/A.csproj");
        var store = new ProjectStateStore(StorePath);
        Assert.True(store.SetLastRun(_tree.Root, project));
        var earlier = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(StorePath, earlier);

        Assert.True(store.SetLastRun(_tree.Root, project));

        Assert.Equal(earlier, File.GetLastWriteTimeUtc(StorePath));
    }

    [Fact]
    public void 沒有updated欄位的舊格式檔案可以讀取與更新()
    {
        var project = _tree.File("A/A.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var key = ProjectStateStore.NormalizeKey(_tree.Root);
        File.WriteAllText(
            StorePath,
            $$"""{ "{{key.Replace("\\", "\\\\")}}": { "default": "{{project.Replace("\\", "\\\\")}}", "lastRun": null } }"""
        );
        var store = new ProjectStateStore(StorePath);

        Assert.Equal(project, store.Get(_tree.Root).Default);
        Assert.Null(store.Get(_tree.Root).Updated);
        Assert.True(store.SetLastRun(_tree.Root, project));
        Assert.NotNull(store.Get(_tree.Root).Updated);
        Assert.Equal(project, store.Get(_tree.Root).Default);
    }

    [Fact]
    public void 檔案鎖被其他行程占用時逾時回傳false()
    {
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(false, ProjectStateStore.MutexNameFor(StorePath));
            mutex.WaitOne();
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        holder.Start();
        held.Wait();
        try
        {
            var store = new ProjectStateStore(StorePath, lockTimeout: TimeSpan.FromMilliseconds(100));

            Assert.False(store.SetDefault(_tree.Root, _tree.File("A/A.csproj")));
            Assert.False(File.Exists(StorePath));
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }
}
