using ClrDiag.Core;

namespace ClrDiag.Tests;

/// <summary>ProjectDiscovery 的掃描規則與名稱比對。</summary>
public sealed class ProjectDiscoveryTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private static string[] Paths(IReadOnlyList<DiscoveredProject> projects) =>
        projects.Select(p => p.RelativePath).ToArray();

    [Fact]
    public void Scan_找到所有支援的副檔名並忽略其他檔案()
    {
        _tree.File("A/A.csproj");
        _tree.File("B/B.sln");
        _tree.File("C/C.slnx");
        _tree.File("D/D.vbproj");
        _tree.File("E/E.fsproj");
        _tree.File("E/readme.md");

        var result = ProjectDiscovery.Scan(_tree.Root);

        Assert.Equal(new[] { "A/A.csproj", "B/B.sln", "C/C.slnx", "D/D.vbproj" }, Paths(result));
    }

    [Fact]
    public void Scan_略過建置輸出_套件與點開頭的資料夾()
    {
        _tree.File("App/App.csproj");
        _tree.File("App/bin/Debug/X.csproj");
        _tree.File("App/OBJ/Y.csproj");
        _tree.File("node_modules/pkg/Z.csproj");
        _tree.File("packages/Lib/L.csproj");
        _tree.File(".git/G.csproj");
        _tree.File(".vs/V.csproj");
        _tree.File(".hidden/H.csproj");

        var result = ProjectDiscovery.Scan(_tree.Root);

        Assert.Equal(new[] { "App/App.csproj" }, Paths(result));
    }

    [Fact]
    public void Scan_超過深度上限的資料夾不再進入()
    {
        _tree.File("Root.sln");
        _tree.File("1/2/Two.csproj");
        _tree.File("1/2/3/Three.csproj");

        var result = ProjectDiscovery.Scan(_tree.Root, maxDepth: 2);

        Assert.Equal(new[] { "1/2/Two.csproj", "Root.sln" }, Paths(result));
    }

    [Fact]
    public void Scan_依相對路徑排序且不分大小寫並填入名稱與完整路徑()
    {
        var web = _tree.File("b/Web/Web.csproj");
        _tree.File("A/A.csproj");
        _tree.File("B.sln");

        var result = ProjectDiscovery.Scan(_tree.Root);

        Assert.Equal(new[] { "A/A.csproj", "B.sln", "b/Web/Web.csproj" }, Paths(result));
        var webEntry = result[2];
        Assert.Equal("Web", webEntry.Name);
        Assert.Equal(web, webEntry.FullPath);
    }

    private static readonly IReadOnlyList<DiscoveredProject> Sample = new[]
    {
        new DiscoveredProject(@"C:\w\A\A.csproj", "A/A.csproj", "A"),
        new DiscoveredProject(@"C:\w\B\B.sln", "B/B.sln", "B"),
        new DiscoveredProject(@"C:\w\B\Web\Web.csproj", "B/Web/Web.csproj", "Web"),
        new DiscoveredProject(@"C:\w\C\Web\Web.csproj", "C/Web/Web.csproj", "Web"),
        new DiscoveredProject(@"C:\w\C\Api\Api.csproj", "C/Api/Api.csproj", "Api"),
    };

    [Fact]
    public void Match_完整相對路徑優先且接受反斜線()
    {
        var result = ProjectDiscovery.Match(Sample, @"c\web\web.csproj");

        Assert.Equal("C/Web/Web.csproj", result.Hit?.RelativePath);
    }

    [Fact]
    public void Match_以檔名唯一命中()
    {
        var result = ProjectDiscovery.Match(Sample, "api");

        Assert.Equal("C/Api/Api.csproj", result.Hit?.RelativePath);
    }

    [Fact]
    public void Match_檔名重複時回傳候選清單()
    {
        var result = ProjectDiscovery.Match(Sample, "Web");

        Assert.Null(result.Hit);
        Assert.Equal(new[] { "B/Web/Web.csproj", "C/Web/Web.csproj" }, Paths(result.Candidates));
    }

    [Fact]
    public void Match_子字串唯一命中()
    {
        var result = ProjectDiscovery.Match(Sample, "b/web/");

        Assert.Equal("B/Web/Web.csproj", result.Hit?.RelativePath);
    }

    [Fact]
    public void Match_子字串符合多筆時回傳候選清單()
    {
        var result = ProjectDiscovery.Match(Sample, "C/");

        Assert.Null(result.Hit);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Match_完全沒有符合時候選清單為空()
    {
        var result = ProjectDiscovery.Match(Sample, "nothing");

        Assert.Null(result.Hit);
        Assert.Empty(result.Candidates);
    }
}
