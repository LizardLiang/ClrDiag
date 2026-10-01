using ClrDiag.Core;
using ClrDiag.Ui;

namespace ClrDiag.Tests;

/// <summary>選定專案後的銜接：選單項目順序與標記，以及 DiagConfig.Load 依選定專案決定 Root 與建置目標。</summary>
public sealed class ProjectWiringTests : IDisposable
{
    private const string SdkProject = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";

    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string SdkFile(string relativePath)
    {
        var path = _tree.File(relativePath);
        File.WriteAllText(path, SdkProject);
        return path;
    }

    [Fact]
    public void 選單把上次執行的專案排第一其餘維持原順序()
    {
        _tree.File("A/A.csproj");
        _tree.File("B/B.sln");
        _tree.File("B/Web/Web.csproj");
        var all = ProjectDiscovery.Scan(_tree.Root);

        var ordered = ProjectPicker.OrderChoices(all, all[2]);

        Assert.Equal(new[] { "B/Web/Web.csproj", "A/A.csproj", "B/B.sln" }, ordered.Select(p => p.RelativePath));
    }

    [Fact]
    public void 沒有上次執行的專案時選單維持掃描順序()
    {
        _tree.File("A/A.csproj");
        _tree.File("B/B.sln");
        var all = ProjectDiscovery.Scan(_tree.Root);

        Assert.Equal(all, ProjectPicker.OrderChoices(all, null));
    }

    [Fact]
    public void 標記同時標出預設與上次執行並且不分大小寫()
    {
        var path = _tree.File("A/A.csproj");
        var project = ProjectDiscovery.Scan(_tree.Root)[0];
        var saved = new ProjectState { Default = path.ToUpperInvariant(), LastRun = path };

        Assert.Equal($"{ProjectPicker.DefaultMarker} {ProjectPicker.LastRunMarker}", ProjectPicker.Markers(project, saved));
        Assert.Equal(string.Empty, ProjectPicker.Markers(project, ProjectState.Empty));
    }

    [Fact]
    public void 選定專案且沒有設定檔時Root是專案資料夾建置目標是該檔案()
    {
        var project = SdkFile("B/Web/Web.csproj");
        SdkFile("B/Web/Other.csproj");

        var config = DiagConfig.Load(null, null, project);

        Assert.Equal(Path.GetDirectoryName(project), config.Root);
        Assert.Null(config.ConfigFile);
        Assert.Equal(project, config.ResolvedBuildProject);
    }

    [Fact]
    public void 上層設定檔指定buildProject時以設定檔為準()
    {
        SdkFile("B/B.csproj");
        var picked = SdkFile("B/Web/Web.csproj");
        File.WriteAllText(_tree.PathOf("B/clrdiag.json"), """{ "buildProject": "B.csproj" }""");

        var config = DiagConfig.Load(null, null, picked);

        Assert.Equal(_tree.PathOf("B/clrdiag.json"), config.ConfigFile);
        Assert.Equal(_tree.PathOf("B"), config.Root);
        Assert.Equal(_tree.PathOf("B/B.csproj"), config.ResolvedBuildProject);
    }

    [Fact]
    public void 上層設定檔沒有buildProject時建置目標是選定的專案()
    {
        var picked = SdkFile("B/Web/Web.csproj");
        File.WriteAllText(_tree.PathOf("B/clrdiag.json"), """{ "port": 5100 }""");

        var config = DiagConfig.Load(null, null, picked);

        Assert.Equal(_tree.PathOf("B/clrdiag.json"), config.ConfigFile);
        Assert.Equal(picked, config.ResolvedBuildProject);
        Assert.Equal(5100, config.Port);
    }
}
