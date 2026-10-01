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

        var result = ProjectDiscovery.Scan(_tree.Root).Projects;

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

        var result = ProjectDiscovery.Scan(_tree.Root).Projects;

        Assert.Equal(new[] { "App/App.csproj" }, Paths(result));
    }

    [Fact]
    public void Scan_不進入連結點資料夾()
    {
        _tree.File("App/App.csproj");
        var link = _tree.PathOf("Link");
        var loop = _tree.PathOf("App/Loop");
        CreateJunction(link, _tree.PathOf("App"));
        CreateJunction(loop, _tree.Root);
        try
        {
            var result = ProjectDiscovery.Scan(_tree.Root).Projects;

            Assert.True(File.Exists(Path.Combine(link, "App.csproj")));
            Assert.Equal(new[] { "App/App.csproj" }, Paths(result));
        }
        finally
        {
            // 只移除連結點本身，不動連結指向的資料夾
            Directory.Delete(loop);
            Directory.Delete(link);
        }
    }

    [Theory]
    [InlineData("..foo/A.csproj", true)]
    [InlineData("sub/..bar/B.csproj", true)]
    [InlineData("../Outside.csproj", false)]
    public void IsInside_以完整路徑段判斷是否位於工作目錄外(string relative, bool expected)
    {
        var work = _tree.PathOf("work");

        Assert.Equal(expected, ProjectDiscovery.IsInside(work, Path.GetFullPath(Path.Combine(work, relative))));
    }

    [Fact]
    public void IsInside_不同磁碟的路徑不在工作目錄內()
    {
        var other = _tree.Root.StartsWith("Z:", StringComparison.OrdinalIgnoreCase) ? @"Y:\X.csproj" : @"Z:\X.csproj";

        Assert.False(ProjectDiscovery.IsInside(_tree.Root, other));
    }

    [Fact]
    public void IncludeSaved_補上名稱以兩個點開頭的資料夾中的記錄()
    {
        _tree.File("Root.sln");
        var dotted = _tree.File("..foo/Dotted.csproj");
        var limited = ProjectDiscovery.Scan(_tree.Root, maxFolders: 1);

        var merged = ProjectDiscovery.IncludeSaved(limited.Projects, _tree.Root, new ProjectState { Default = dotted });

        Assert.Equal(new[] { "..foo/Dotted.csproj", "Root.sln" }, Paths(merged));
    }

    [Fact]
    public void RelativePathOf_以斜線分隔()
    {
        Assert.Equal("A/B/C.csproj", ProjectDiscovery.RelativePathOf(_tree.Root, _tree.PathOf("A/B/C.csproj")));
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            }
        )!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void Scan_超過深度上限的資料夾不再進入()
    {
        _tree.File("Root.sln");
        _tree.File("1/2/Two.csproj");
        _tree.File("1/2/3/Three.csproj");

        var result = ProjectDiscovery.Scan(_tree.Root, maxDepth: 2).Projects;

        Assert.Equal(new[] { "1/2/Two.csproj", "Root.sln" }, Paths(result));
    }

    [Fact]
    public void Scan_依相對路徑排序且不分大小寫並填入名稱與完整路徑()
    {
        var web = _tree.File("b/Web/Web.csproj");
        _tree.File("A/A.csproj");
        _tree.File("B.sln");

        var result = ProjectDiscovery.Scan(_tree.Root).Projects;

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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Match_空白名稱不符合任何專案(string query)
    {
        var result = ProjectDiscovery.Match(Sample, query);

        Assert.Null(result.Hit);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void DescribeMiss_符合多筆時只列出候選()
    {
        var miss = ProjectDiscovery.DescribeMiss("Web", ProjectDiscovery.Match(Sample, "Web"), Sample);

        Assert.Equal("「Web」符合多個專案，請指定更完整的路徑", miss.Message);
        Assert.Equal(new[] { "B/Web/Web.csproj", "C/Web/Web.csproj" }, Paths(miss.Candidates));
    }

    [Fact]
    public void DescribeMiss_沒有符合時列出所有專案()
    {
        var miss = ProjectDiscovery.DescribeMiss("nothing", ProjectDiscovery.Match(Sample, "nothing"), Sample);

        Assert.Equal("找不到符合「nothing」的專案", miss.Message);
        Assert.Equal(Sample, miss.Candidates);
    }

    [Fact]
    public void DescribeMiss_沒有任何專案時候選清單為空()
    {
        var none = Array.Empty<DiscoveredProject>();

        var miss = ProjectDiscovery.DescribeMiss("Api", ProjectDiscovery.Match(none, "Api"), none);

        Assert.Equal("找不到符合「Api」的專案", miss.Message);
        Assert.Empty(miss.Candidates);
    }

    [Fact]
    public void DescribeMiss_空白名稱說明名稱不能空白並列出所有專案()
    {
        var miss = ProjectDiscovery.DescribeMiss(" ", ProjectDiscovery.Match(Sample, " "), Sample);

        Assert.Equal("專案名稱不能空白", miss.Message);
        Assert.Equal(Sample, miss.Candidates);
    }

    [Fact]
    public void Scan_達到資料夾上限時回傳已找到的專案並標記截斷()
    {
        _tree.File("Root.sln");
        _tree.File("1/One.csproj");
        _tree.File("2/Two.csproj");

        var limited = ProjectDiscovery.Scan(_tree.Root, maxFolders: 1);
        var full = ProjectDiscovery.Scan(_tree.Root);

        Assert.True(limited.Truncated);
        Assert.Equal(new[] { "Root.sln" }, Paths(limited.Projects));
        Assert.False(full.Truncated);
        Assert.Equal(3, full.Projects.Count);
    }

    [Fact]
    public void IncludeSaved_補上掃描範圍外仍存在且位於工作目錄內的記錄()
    {
        _tree.File("Root.sln");
        var deep = _tree.File("1/2/Deep.csproj");
        var outside = Path.Combine(Path.GetTempPath(), "clrdiag-tests", $"{Guid.NewGuid():N}.csproj");
        File.WriteAllText(outside, "");
        try
        {
            var limited = ProjectDiscovery.Scan(_tree.Root, maxFolders: 1);
            var saved = new ProjectState { Default = deep, LastRun = outside };

            var merged = ProjectDiscovery.IncludeSaved(limited.Projects, _tree.Root, saved);

            Assert.Equal(new[] { "1/2/Deep.csproj", "Root.sln" }, Paths(merged));
            Assert.Equal("Deep", merged[0].Name);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void ExtensionList_與DiagConfig共用同一份副檔名()
    {
        Assert.Equal(".sln / .slnx / .csproj / .vbproj", ProjectDiscovery.ExtensionList);
        Assert.Equal(new[] { ".sln", ".slnx", ".csproj", ".vbproj" }, DiagConfig.ProjectExtensions);
    }
}
