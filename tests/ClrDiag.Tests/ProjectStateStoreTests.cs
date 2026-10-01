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
    }
}
