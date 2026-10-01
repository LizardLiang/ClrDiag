using ClrDiag.Core;

namespace ClrDiag.Tests;

/// <summary>ProjectResolver 每一個決策分支，包含已失效的預設與上次執行記錄。</summary>
public sealed class ProjectResolverTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly DiscoveredProject _a;
    private readonly DiscoveredProject _b;
    private readonly DiscoveredProject _web;

    public ProjectResolverTests()
    {
        _tree.File("A/A.csproj");
        _tree.File("B/B.sln");
        _tree.File("B/Web/Web.csproj");
        var all = ProjectDiscovery.Scan(_tree.Root).Projects;
        _a = all[0];
        _b = all[1];
        _web = all[2];
    }

    public void Dispose() => _tree.Dispose();

    private IReadOnlyList<DiscoveredProject> All => new[] { _a, _b, _web };

    private string Missing => _tree.PathOf("Gone/Gone.csproj");

    private static ProjectResolution Resolve(
        IReadOnlyList<DiscoveredProject> discovered,
        ProjectState? saved = null,
        string? project = null,
        bool pick = false,
        bool interactive = true
    ) => ProjectResolver.Resolve(discovered, saved ?? ProjectState.Empty, project, pick, interactive);

    [Fact]
    public void 沒有任何專案時改走往上搜尋()
    {
        var result = Resolve(Array.Empty<DiscoveredProject>(), new ProjectState { Default = Missing });

        Assert.IsType<ResolveOutcome.FallBackUpward>(result.Outcome);
        Assert.True(result.StaleDefault);
    }

    [Fact]
    public void 指定專案但沒有任何專案時回報錯誤而不是往上搜尋()
    {
        var result = Resolve(Array.Empty<DiscoveredProject>(), project: "Api", interactive: false);

        var error = Assert.IsType<ResolveOutcome.Error>(result.Outcome);
        Assert.Equal("找不到符合「Api」的專案", error.Message);
        Assert.Empty(error.Candidates);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void 指定空白專案名稱時回報錯誤(string project)
    {
        var result = Resolve(All, new ProjectState { Default = _a.FullPath }, project: project);

        var error = Assert.IsType<ResolveOutcome.Error>(result.Outcome);
        Assert.Equal("專案名稱不能空白", error.Message);
        Assert.Equal(All, error.Candidates);
    }

    [Fact]
    public void 指定專案時優先於預設專案()
    {
        var result = Resolve(All, new ProjectState { Default = _a.FullPath }, project: "Web");

        var use = Assert.IsType<ResolveOutcome.Use>(result.Outcome);
        Assert.Equal(_web, use.Project);
        Assert.Equal(ProjectSource.Explicit, use.Source);
    }

    [Fact]
    public void 指定專案找不到時回報錯誤並列出所有專案()
    {
        var result = Resolve(All, project: "Nope", interactive: false);

        var error = Assert.IsType<ResolveOutcome.Error>(result.Outcome);
        Assert.Contains("Nope", error.Message);
        Assert.Equal(All, error.Candidates);
    }

    [Fact]
    public void 指定專案符合多筆時回報錯誤並只列出候選()
    {
        var result = Resolve(All, project: "B/", interactive: false);

        var error = Assert.IsType<ResolveOutcome.Error>(result.Outcome);
        Assert.Equal(new[] { _b, _web }, error.Candidates);
    }

    [Fact]
    public void 互動模式加上pick時即使有預設也顯示選單()
    {
        var result = Resolve(
            All,
            new ProjectState { Default = _a.FullPath, LastRun = _web.FullPath },
            pick: true
        );

        var prompt = Assert.IsType<ResolveOutcome.Prompt>(result.Outcome);
        Assert.Equal(_web, prompt.Preselect);
    }

    [Fact]
    public void 非互動模式忽略pick並使用預設專案()
    {
        var result = Resolve(All, new ProjectState { Default = _a.FullPath }, pick: true, interactive: false);

        var use = Assert.IsType<ResolveOutcome.Use>(result.Outcome);
        Assert.Equal(_a, use.Project);
        Assert.Equal(ProjectSource.Default, use.Source);
    }

    [Fact]
    public void 預設專案存在時直接使用()
    {
        var result = Resolve(All, new ProjectState { Default = _b.FullPath.ToUpperInvariant() });

        var use = Assert.IsType<ResolveOutcome.Use>(result.Outcome);
        Assert.Equal(_b, use.Project);
        Assert.Equal(ProjectSource.Default, use.Source);
        Assert.False(result.StaleDefault);
    }

    [Fact]
    public void 預設專案檔案已不存在時視為失效並顯示選單()
    {
        var result = Resolve(All, new ProjectState { Default = Missing });

        Assert.IsType<ResolveOutcome.Prompt>(result.Outcome);
        Assert.True(result.StaleDefault);
        Assert.False(result.StaleLastRun);
    }

    [Fact]
    public void 只找到一個專案時直接使用()
    {
        var result = Resolve(new[] { _a });

        var use = Assert.IsType<ResolveOutcome.Use>(result.Outcome);
        Assert.Equal(_a, use.Project);
        Assert.Equal(ProjectSource.Single, use.Source);
    }

    [Fact]
    public void 互動模式多個專案時顯示選單並預選上次執行()
    {
        var result = Resolve(All, new ProjectState { LastRun = _web.FullPath });

        var prompt = Assert.IsType<ResolveOutcome.Prompt>(result.Outcome);
        Assert.Equal(All, prompt.Projects);
        Assert.Equal(_web, prompt.Preselect);
    }

    [Fact]
    public void 互動模式上次執行已失效時選單不預選()
    {
        var result = Resolve(All, new ProjectState { LastRun = Missing });

        var prompt = Assert.IsType<ResolveOutcome.Prompt>(result.Outcome);
        Assert.Null(prompt.Preselect);
        Assert.True(result.StaleLastRun);
    }

    [Fact]
    public void 非互動模式沿用上次執行的專案()
    {
        var result = Resolve(All, new ProjectState { LastRun = _b.FullPath }, interactive: false);

        var use = Assert.IsType<ResolveOutcome.Use>(result.Outcome);
        Assert.Equal(_b, use.Project);
        Assert.Equal(ProjectSource.LastRun, use.Source);
    }

    [Fact]
    public void 非互動模式沒有任何記錄時回報錯誤()
    {
        var result = Resolve(All, interactive: false);

        var error = Assert.IsType<ResolveOutcome.Error>(result.Outcome);
        Assert.Equal(All, error.Candidates);
    }

    [Fact]
    public void 非互動模式上次執行已失效時回報錯誤並標記失效()
    {
        var result = Resolve(All, new ProjectState { LastRun = Missing }, interactive: false);

        Assert.IsType<ResolveOutcome.Error>(result.Outcome);
        Assert.True(result.StaleLastRun);
    }

    [Fact]
    public void 記錄的檔案仍存在但不在掃描結果內時不視為失效()
    {
        var outside = _tree.File("A/bin/Hidden.csproj");

        var result = Resolve(All, new ProjectState { Default = outside, LastRun = outside });

        Assert.IsType<ResolveOutcome.Prompt>(result.Outcome);
        Assert.False(result.StaleDefault);
        Assert.False(result.StaleLastRun);
    }
}
