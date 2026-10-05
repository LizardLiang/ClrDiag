using ClrDiag.Core;

namespace ClrDiag.Tests;

/// <summary>沒有 serveCommand 時依專案類型推斷啟動方式的每個分支，以及設定檔明確值優先。</summary>
public sealed class ServeInferenceTests : IDisposable
{
    private const string FakeIis = @"C:\Fake\IIS Express\iisexpress.exe";
    private const string AspNetGuid = "{349C5851-65DF-11DA-9384-00065B846F21};{fae04ec0-301f-11d3-bf4b-00c04f79efbc}";

    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string Write(string relativePath, string content)
    {
        string path = _tree.File(relativePath);
        File.WriteAllText(path, content);
        return path;
    }

    private static string OldWebProject(string extra = "") =>
        $"<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">"
        + $"<PropertyGroup><ProjectTypeGuids>{AspNetGuid}</ProjectTypeGuids></PropertyGroup>{extra}</Project>";

    private static string UserFile(string inner) =>
        "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><ProjectExtensions><VisualStudio><FlavorProperties>"
        + $"<WebProjectProperties>{inner}</WebProjectProperties></FlavorProperties></VisualStudio></ProjectExtensions></Project>";

    private DiagConfig Load(string project, Func<string?>? iis = null) =>
        DiagConfig.Load(null, null, project, iis ?? (() => FakeIis));

    [Fact]
    public void SdkWeb專案推斷dotnet_run且連接埠取自launchSettings()
    {
        string project = Write("Web/Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        Write(
            "Web/Properties/launchSettings.json",
            "{ \"profiles\": { \"Web\": { \"applicationUrl\": \"https://localhost:7001;http://localhost:5123\" } } }"
        );

        var config = Load(project);

        Assert.Equal("dotnet", config.ServeCommand);
        Assert.Equal(new[] { "run", "--project", "{project}", "--urls", "http://localhost:{port}" }, config.ServeArguments);
        Assert.Equal(5123, config.Port);
        Assert.True(config.IsWrapperServeCommand);
        Assert.Contains("launchSettings.json", config.ServeInferenceNote);
    }

    [Fact]
    public void SdkWeb專案沒有launchSettings時沿用預設連接埠()
    {
        string project = Write("Web/Web.csproj", "<Project><Sdk Name=\"Microsoft.NET.Sdk.Web\" /></Project>");

        var config = Load(project);

        Assert.Equal("dotnet", config.ServeCommand);
        Assert.Equal(5000, config.Port);
    }

    [Fact]
    public void 舊式ASP_NET專案推斷iisexpress且連接埠取自user檔()
    {
        string project = Write("Web/Web.csproj", OldWebProject(UserFile("<DevelopmentServerPort>58649</DevelopmentServerPort>")));
        File.WriteAllText(project + ".user", UserFile("<DevelopmentServerPort>58649</DevelopmentServerPort>"));

        var config = Load(project);

        Assert.Equal(FakeIis, config.ServeCommand);
        Assert.Equal(new[] { $"/path:{Path.GetDirectoryName(project)}", "/port:{port}" }, config.ServeArguments);
        Assert.Equal(58649, config.Port);
        Assert.Equal(new[] { "iisexpress" }, config.ProcessNames);
        Assert.False(config.IsWrapperServeCommand);
        Assert.Contains("Web.csproj.user", config.ServeInferenceNote);
    }

    [Fact]
    public void 舊式ASP_NET專案只有IISUrl時取其http連接埠()
    {
        string project = Write(
            "Web/Web.csproj",
            OldWebProject(UserFile("<IISUrl>https://localhost:44300/</IISUrl><IISUrl>http://localhost:8080/</IISUrl>"))
        );

        var config = Load(project);

        Assert.Equal(8080, config.Port);
        Assert.Contains("Web.csproj", config.ServeInferenceNote);
    }

    [Fact]
    public void 舊式ASP_NET專案只有https的IISUrl時沿用預設連接埠()
    {
        string project = Write("Web/Web.csproj", OldWebProject(UserFile("<IISUrl>https://localhost:44300/</IISUrl>")));

        var config = Load(project);

        Assert.Equal(FakeIis, config.ServeCommand);
        Assert.Equal(5000, config.Port);
    }

    [Fact]
    public void 找不到IISExpress時不推斷並說明原因()
    {
        string project = Write("Web/Web.csproj", OldWebProject());

        var config = Load(project, () => null);

        Assert.Null(config.ServeCommand);
        Assert.False(config.CanServe);
        Assert.Empty(config.ProcessNames);
        Assert.Contains("iisexpress.exe", config.ServeInferenceNote);
    }

    [Fact]
    public void 類別庫與主控台專案不推斷並說明原因()
    {
        string project = Write("Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var config = Load(project);

        Assert.False(config.CanServe);
        Assert.Contains("不是網站專案", config.ServeInferenceNote);
    }

    [Fact]
    public void 方案檔不推斷並說明原因()
    {
        string solution = Write("App.sln", "");

        var config = Load(solution);

        Assert.False(config.CanServe);
        Assert.Contains("方案檔", config.ServeInferenceNote);
    }

    [Fact]
    public void 設定檔明確寫的serveCommand與其他欄位不被推斷覆蓋()
    {
        string project = Write("Web/Web.csproj", OldWebProject());
        File.WriteAllText(project + ".user", UserFile("<DevelopmentServerPort>58649</DevelopmentServerPort>"));
        Write("Web/clrdiag.json", "{ \"serveCommand\": \"pwsh\", \"serveArguments\": [\"x\"], \"port\": 9000, \"processNames\": [\"w3wp\"] }");

        var config = Load(project);

        Assert.Equal("pwsh", config.ServeCommand);
        Assert.Equal(new[] { "x" }, config.ServeArguments);
        Assert.Equal(9000, config.Port);
        Assert.Equal(new[] { "w3wp" }, config.ProcessNames);
        Assert.Null(config.ServeInferenceNote);
    }

    [Fact]
    public void 設定檔明確寫空的processNames時不補iisexpress()
    {
        string project = Write("Web/Web.csproj", OldWebProject());
        Write("Web/clrdiag.json", "{ \"processNames\": [] }");

        var config = Load(project);

        Assert.Equal(FakeIis, config.ServeCommand);
        Assert.Empty(config.ProcessNames);
    }

    [Fact]
    public void 預設連接埠80的http網址也取得連接埠()
    {
        string project = Write("Web/Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        Write("Web/Properties/launchSettings.json", "{ \"profiles\": { \"Web\": { \"applicationUrl\": \"http://localhost\" } } }");

        Assert.Equal(80, Load(project).Port);
    }

    [Fact]
    public void 設定檔只寫port時推斷出啟動指令但保留設定的連接埠與行程名稱()
    {
        string project = Write("Web/Web.csproj", OldWebProject());
        File.WriteAllText(project + ".user", UserFile("<DevelopmentServerPort>58649</DevelopmentServerPort>"));
        Write("Web/clrdiag.json", "{ \"port\": 5000, \"processNames\": [\"w3wp\"] }");

        var config = Load(project);

        Assert.Equal(FakeIis, config.ServeCommand);
        Assert.Equal(5000, config.Port);
        Assert.Equal(new[] { "w3wp" }, config.ProcessNames);
    }
}
