using ClrDiag.Core;

namespace ClrDiag.Tests;

/// <summary>ProjectSelection 的互動判斷、偵測範圍、旗標衝突警告，以及掃描 → 解析 → 選單 → 記錄的串接。</summary>
public sealed class ProjectSelectionTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly List<string> _notices = new();
    private int _promptCalls;

    public void Dispose() => _tree.Dispose();

    private string StorePath => _tree.PathOf("state/projects.json");

    private string WorkDir => _tree.PathOf("work");

    private ProjectSelectionResult Resolve(
        ProjectStateStore store,
        string? project = null,
        bool pick = false,
        bool interactive = true,
        Func<IReadOnlyList<DiscoveredProject>, ProjectPick?>? prompt = null,
        bool ambiguousFallsBackUpward = false
    )
    {
        Directory.CreateDirectory(WorkDir);
        return ProjectSelection.Resolve(
            store,
            WorkDir,
            project,
            pick,
            interactive,
            (projects, _, _) =>
            {
                _promptCalls++;
                return prompt is null ? new ProjectPick(projects[0], false) : prompt(projects);
            },
            _notices.Add,
            ambiguousFallsBackUpward
        );
    }

    private void TwoProjects()
    {
        _tree.File("work/A/A.csproj");
        _tree.File("work/B/B.csproj");
    }

    [Fact]
    public void IsInteractive_有批次旗標時不詢問終端機能力()
    {
        var asked = false;

        var result = ProjectSelection.IsInteractive(true, false, false, () => asked = true);

        Assert.False(result);
        Assert.False(asked);
    }

    [Theory]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    public void IsInteractive_重新導向或終端機不可互動時視為批次(
        bool inputRedirected,
        bool outputRedirected,
        bool consoleInteractive,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            ProjectSelection.IsInteractive(false, inputRedirected, outputRedirected, () => consoleInteractive)
        );
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void UsesDiscovery_output_init_installSkill不做專案偵測(
        bool output,
        bool init,
        bool installSkill,
        bool expected
    )
    {
        Assert.Equal(expected, ProjectSelection.UsesDiscovery(output, init, installSkill));
    }

    [Fact]
    public void ConflictWarnings_沒有衝突時沒有警告()
    {
        Assert.Empty(ProjectSelection.ConflictWarnings(true, false, false, true));
        Assert.Empty(ProjectSelection.ConflictWarnings(false, true, false, true));
    }

    [Fact]
    public void ConflictWarnings_root與project同時指定時警告project不生效()
    {
        var warning = Assert.Single(ProjectSelection.ConflictWarnings(true, true, false, true));
        Assert.Contains("--project / --pick 不生效", warning);
    }

    [Fact]
    public void ConflictWarnings_root搭配專案管理指令時警告root不生效()
    {
        var warning = Assert.Single(ProjectSelection.ConflictWarnings(true, false, true, true));
        Assert.Contains("--root / --config 不生效", warning);
    }

    [Fact]
    public void ConflictWarnings_不做偵測的模式搭配project時警告()
    {
        var warning = Assert.Single(ProjectSelection.ConflictWarnings(false, true, false, false));
        Assert.Contains("--init / --install-skill / --output", warning);
    }

    [Fact]
    public void Resolve_沒有任何專案也沒有指定時改走往上搜尋()
    {
        var result = Resolve(new ProjectStateStore(StorePath));

        Assert.Equal(new ProjectSelectionResult(null, null, null, null), result);
    }

    [Fact]
    public void Resolve_指定專案但沒有任何專案時以結束碼2結束()
    {
        var result = Resolve(new ProjectStateStore(StorePath), project: "Api", interactive: false);

        Assert.Equal(ProjectSelection.ErrorExitCode, result.ExitCode);
        Assert.Equal("找不到符合「Api」的專案", result.Error?.Message);
        Assert.Empty(result.Error!.Candidates);
    }

    [Fact]
    public void Resolve_空白的指定專案以結束碼2結束()
    {
        TwoProjects();

        var result = Resolve(new ProjectStateStore(StorePath), project: " ");

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("專案名稱不能空白", result.Error?.Message);
        Assert.Equal(0, _promptCalls);
    }

    [Fact]
    public void Resolve_只有一個專案時直接使用()
    {
        _tree.File("work/A/A.csproj");

        var result = Resolve(new ProjectStateStore(StorePath));

        Assert.Equal("A/A.csproj", result.Project?.RelativePath);
        Assert.Equal("唯一專案", result.Label);
        Assert.Null(result.ExitCode);
    }

    [Fact]
    public void Resolve_批次模式多個專案沒有記錄時不顯示選單並以結束碼2結束()
    {
        TwoProjects();

        var result = Resolve(new ProjectStateStore(StorePath), interactive: false);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(2, result.Error?.Candidates.Count);
        Assert.Equal(0, _promptCalls);
    }

    [Fact]
    public void Resolve_list多個專案沒有記錄時改走往上搜尋並提示且不寫入記錄()
    {
        TwoProjects();

        var result = Resolve(new ProjectStateStore(StorePath), interactive: false, ambiguousFallsBackUpward: true);

        Assert.Equal(new ProjectSelectionResult(null, null, null, null), result);
        Assert.Contains(ProjectSelection.AmbiguousListNotice, _notices);
        Assert.Equal(0, _promptCalls);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void Resolve_list指定專案沒有符合時仍以結束碼2結束()
    {
        TwoProjects();

        var result = Resolve(
            new ProjectStateStore(StorePath),
            project: "Nope",
            interactive: false,
            ambiguousFallsBackUpward: true
        );

        Assert.Equal(2, result.ExitCode);
        Assert.DoesNotContain(ProjectSelection.AmbiguousListNotice, _notices);
    }

    [Fact]
    public void Resolve_沒有任何專案改走往上搜尋時不提示多個專案()
    {
        var result = Resolve(new ProjectStateStore(StorePath), interactive: false, ambiguousFallsBackUpward: true);

        Assert.Equal(new ProjectSelectionResult(null, null, null, null), result);
        Assert.Empty(_notices);
    }

    [Fact]
    public void Resolve_選單同意設為預設時寫入預設專案()
    {
        TwoProjects();
        var store = new ProjectStateStore(StorePath);

        var result = Resolve(store, prompt: projects => new ProjectPick(projects[1], true));

        Assert.Equal("B/B.csproj", result.Project?.RelativePath);
        Assert.Equal("選單，已設為預設", result.Label);
        Assert.Equal(result.Project!.FullPath, store.Get(WorkDir).Default);
        Assert.Empty(_notices);
    }

    [Fact]
    public void Resolve_選單取消時以取消結束碼結束且不寫入記錄()
    {
        TwoProjects();

        var result = Resolve(new ProjectStateStore(StorePath), prompt: _ => null);

        Assert.Equal(ProjectSelection.CancelExitCode, result.ExitCode);
        Assert.Null(result.Project);
        Assert.Null(result.Error);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void Resolve_預設專案寫入失敗時提示但仍使用選定的專案()
    {
        TwoProjects();
        Directory.CreateDirectory(StorePath);
        var store = new ProjectStateStore(StorePath);

        var result = Resolve(store, prompt: projects => new ProjectPick(projects[0], true));

        Assert.Equal("A/A.csproj", result.Project?.RelativePath);
        Assert.Equal("選單", result.Label);
        Assert.Contains(store.WriteFailureMessage, _notices);
    }

    [Fact]
    public void Resolve_互動模式移除已失效的預設專案()
    {
        TwoProjects();
        var store = new ProjectStateStore(StorePath);
        var gone = _tree.File("work/Gone/Gone.csproj");
        Directory.CreateDirectory(WorkDir);
        store.SetDefault(WorkDir, gone);
        File.Delete(gone);

        Resolve(store);

        Assert.Null(store.Get(WorkDir).Default);
        Assert.Equal(1, _promptCalls);
    }

    [Fact]
    public void Resolve_選單取消時不移除已失效的記錄也不寫檔()
    {
        TwoProjects();
        var store = new ProjectStateStore(StorePath);
        var gone = _tree.File("work/Gone/Gone.csproj");
        Directory.CreateDirectory(WorkDir);
        store.SetDefault(WorkDir, gone);
        store.SetLastRun(WorkDir, gone);
        File.Delete(gone);
        var before = File.ReadAllText(StorePath);
        var stamp = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(StorePath, stamp);

        var result = Resolve(store, prompt: _ => null);

        Assert.Equal(ProjectSelection.CancelExitCode, result.ExitCode);
        Assert.Equal(before, File.ReadAllText(StorePath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(StorePath));
    }

    [Fact]
    public void Resolve_選定專案後同時移除已失效的預設與上次執行()
    {
        TwoProjects();
        var store = new ProjectStateStore(StorePath);
        var gone = _tree.File("work/Gone/Gone.csproj");
        Directory.CreateDirectory(WorkDir);
        store.SetDefault(WorkDir, gone);
        store.SetLastRun(WorkDir, gone);
        File.Delete(gone);

        var result = Resolve(store, prompt: projects => new ProjectPick(projects[0], false));

        Assert.Equal("A/A.csproj", result.Project?.RelativePath);
        Assert.Equal(ProjectState.Empty, store.Get(WorkDir));
        Assert.Empty(_notices);
    }

    [Fact]
    public void Resolve_預設專案失效時選單設為預設保留新的預設()
    {
        TwoProjects();
        var store = new ProjectStateStore(StorePath);
        var gone = _tree.File("work/Gone/Gone.csproj");
        Directory.CreateDirectory(WorkDir);
        store.SetDefault(WorkDir, gone);
        File.Delete(gone);

        var result = Resolve(store, prompt: projects => new ProjectPick(projects[1], true));

        Assert.Equal(result.Project!.FullPath, store.Get(WorkDir).Default);
        Assert.Equal("選單，已設為預設", result.Label);
    }

    [Fact]
    public void Resolve_記錄失效時選單設為預設以一次寫入清除並設定()
    {
        TwoProjects();
        var store = new ProjectStateStore(StorePath);
        var gone = _tree.File("work/Gone/Gone.csproj");
        Directory.CreateDirectory(WorkDir);
        store.SetDefault(WorkDir, gone);
        store.SetLastRun(WorkDir, gone);
        File.Delete(gone);

        var result = Resolve(store, prompt: projects => new ProjectPick(projects[1], true));

        Assert.Equal(new ProjectState { Default = result.Project!.FullPath }, store.Get(WorkDir) with { Updated = null });
        Assert.Equal("選單，已設為預設", result.Label);
        Assert.Empty(_notices);
    }

    [Fact]
    public void Resolve_記錄失效且檔案鎖逾時時只提示一次寫入失敗()
    {
        TwoProjects();
        var gone = _tree.File("work/Gone/Gone.csproj");
        Directory.CreateDirectory(WorkDir);
        new ProjectStateStore(StorePath).SetDefault(WorkDir, gone);
        File.Delete(gone);
        var store = new ProjectStateStore(StorePath, lockTimeout: TimeSpan.FromMilliseconds(50));

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
            var result = Resolve(store, prompt: projects => new ProjectPick(projects[1], true));

            Assert.Equal("選單", result.Label);
            Assert.Equal(new[] { store.WriteFailureMessage }, _notices);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    [Fact]
    public void Resolve_批次模式不移除已失效的記錄()
    {
        _tree.File("work/A/A.csproj");
        var store = new ProjectStateStore(StorePath);
        var gone = _tree.File("work/Gone/Gone.csproj");
        Directory.CreateDirectory(WorkDir);
        store.SetDefault(WorkDir, gone);
        File.Delete(gone);

        var result = Resolve(store, interactive: false);

        Assert.Equal("A/A.csproj", result.Project?.RelativePath);
        Assert.Equal(gone, store.Get(WorkDir).Default);
    }
}
