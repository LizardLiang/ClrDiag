using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClrDiag.Core;

/// <summary>
/// 工具的全部專案相關設定。沒有設定檔時會自動偵測（往上找 .sln / .csproj / .git），
/// 因此在任何 .NET 專案裡都能直接執行；需要建置或啟動伺服器時才需要設定檔。
/// </summary>
public sealed record DiagConfig
{
    /// <summary>設定檔預設檔名，會從目前目錄往上尋找。</summary>
    public const string FileName = "clrdiag.json";

    /// <summary>專案根目錄。設定檔中的相對路徑都以此為基準。</summary>
    [JsonIgnore]
    public string Root { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>設定檔實際路徑；null 表示全靠自動偵測。</summary>
    [JsonIgnore]
    public string? ConfigFile { get; init; }

    // --- 建置 ---

    /// <summary>建置用的執行檔。null = 自動判斷（SDK 專案用 dotnet，舊式專案用 vswhere 找到的 MSBuild）。</summary>
    public string? BuildCommand { get; init; }

    /// <summary>建置參數，支援 {project} {config} {root} 佔位符。null = 依 BuildCommand 給預設值。</summary>
    public string[]? BuildArguments { get; init; }

    /// <summary>建置目標（.sln 或專案檔），相對於 Root。null = 自動挑選。</summary>
    public string? BuildProject { get; init; }

    /// <summary>可切換的建置設定，對應介面上的 c 鍵。</summary>
    public string[] Configurations { get; init; } = { "Debug", "Release" };

    // --- 啟動伺服器 ---

    /// <summary>啟動伺服器的執行檔（例: pwsh、dotnet）。null = 不支援啟動，只能附加到既有行程。</summary>
    public string? ServeCommand { get; init; }

    /// <summary>啟動參數，支援 {port} {root} {project} 佔位符。</summary>
    public string[]? ServeArguments { get; init; }

    /// <summary>預設連接埠，可被 --port 覆寫。</summary>
    public int Port { get; init; } = 5000;

    /// <summary>健康探測網址，支援 {port}。設為空字串可停用探測。</summary>
    public string ProbeUrl { get; init; } = "http://localhost:{port}/";

    // --- 監看目標 ---

    /// <summary>要尋找的行程名稱（不含 .exe），依序比對。空陣列 = 列出所有受控行程讓使用者挑。</summary>
    public string[] ProcessNames { get; init; } = Array.Empty<string>();

    /// <summary>視為「自己的程式碼」的命名空間前綴，用於標記執行緒與堆疊。空 = 以「非框架」判斷。</summary>
    public string[] AppNamespaces { get; init; } = Array.Empty<string>();

    /// <summary>CSV 報告輸出目錄，相對於 Root。</summary>
    public string ReportDirectory { get; init; } = ".clrdiag-reports";

    // --- 衍生值 ---

    [JsonIgnore]
    public string? MsBuildPath { get; private set; }

    [JsonIgnore]
    public string? BuildToolError { get; private set; }

    [JsonIgnore]
    public string ReportDirectoryFullPath => Path.GetFullPath(Path.Combine(Root, ReportDirectory));

    [JsonIgnore]
    public bool CanBuild =>
        ResolvedBuildProject is not null
        && (BuildCommand is not null || MsBuildPath is not null || IsSdkProject);

    [JsonIgnore]
    public bool CanServe => ServeCommand is not null;

    [JsonIgnore]
    public string? ResolvedBuildProject { get; private set; }

    [JsonIgnore]
    public bool IsSdkProject { get; private set; }

    public string ExpandProbeUrl(int port) =>
        ProbeUrl.Replace("{port}", port.ToString(), StringComparison.Ordinal);

    /// <summary>把 {root} {project} {config} {port} 佔位符換成實際值。</summary>
    public string Expand(string argument, string? configuration = null, int? port = null) =>
        argument
            .Replace("{root}", Root, StringComparison.Ordinal)
            .Replace("{project}", ResolvedBuildProject ?? string.Empty, StringComparison.Ordinal)
            .Replace("{config}", configuration ?? string.Empty, StringComparison.Ordinal)
            .Replace("{port}", (port ?? Port).ToString(), StringComparison.Ordinal);

    /// <summary>
    /// 載入設定：先從 explicitConfig 或往上尋找 clrdiag.json，找不到就純自動偵測。
    /// </summary>
    public static DiagConfig Load(string? explicitConfig, string? explicitRoot)
    {
        string? configFile =
            explicitConfig ?? FindConfigFile(explicitRoot ?? Directory.GetCurrentDirectory());
        DiagConfig config;

        if (configFile is not null)
        {
            string json = File.ReadAllText(configFile);
            config =
                JsonSerializer.Deserialize<DiagConfig>(json, JsonOptions)
                ?? throw new InvalidOperationException($"設定檔內容無法解析: {configFile}");
            config = config with
            {
                ConfigFile = configFile,
                Root = explicitRoot ?? Path.GetDirectoryName(Path.GetFullPath(configFile))!,
            };
        }
        else
        {
            config = new DiagConfig
            {
                Root = explicitRoot ?? FindProjectRoot(Directory.GetCurrentDirectory()),
            };
        }

        config.ResolveBuildTarget();
        return config;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string? FindConfigFile(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>沒有設定檔時，往上找第一個含 .sln / 專案檔 / .git 的目錄當作根目錄。</summary>
    private static string FindProjectRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (
                directory.EnumerateFiles("*.sln").Any()
                || directory.EnumerateFiles("*.slnx").Any()
                || directory.EnumerateFiles("*.csproj").Any()
                || Directory.Exists(Path.Combine(directory.FullName, ".git"))
            )
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return startDirectory;
    }

    /// <summary>決定建置目標與建置工具；兩者都只在按下建置鍵時才真的被使用。</summary>
    private void ResolveBuildTarget()
    {
        string? target = BuildProject is not null
            ? Path.GetFullPath(Path.Combine(Root, BuildProject))
            : FindBuildTarget(Root);

        if (target is not null && File.Exists(target))
        {
            ResolvedBuildProject = target;
            IsSdkProject = DetectSdkProject(target);
        }
        else if (target is not null)
        {
            BuildToolError = $"找不到建置目標: {target}";
            return;
        }
        else
        {
            BuildToolError = "找不到 .sln 或專案檔，無法建置（仍可監看既有行程）";
            return;
        }

        // 指定了自訂建置指令就不需要 MSBuild；SDK 專案用 dotnet build 即可
        if (BuildCommand is not null || IsSdkProject)
        {
            return;
        }

        MsBuildPath = FindMsBuild(out string? error);
        BuildToolError = error;
    }

    private static string? FindBuildTarget(string root)
    {
        var directory = new DirectoryInfo(root);
        return directory.EnumerateFiles("*.sln").FirstOrDefault()?.FullName ?? directory
                .EnumerateFiles("*.slnx")
                .FirstOrDefault()
                ?.FullName
            ?? directory.EnumerateFiles("*.csproj").FirstOrDefault()?.FullName ?? directory
                .EnumerateFiles("*.vbproj")
                .FirstOrDefault()
                ?.FullName;
    }

    /// <summary>SDK 樣式專案（Project Sdk="..."）用 dotnet build；舊式 .NET Framework 專案需要 MSBuild。</summary>
    private static bool DetectSdkProject(string projectFile)
    {
        if (
            Path.GetExtension(projectFile).Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(projectFile).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        try
        {
            foreach (string line in File.ReadLines(projectFile).Take(10))
            {
                if (
                    line.Contains("<Project", StringComparison.Ordinal)
                    && line.Contains("Sdk=", StringComparison.Ordinal)
                )
                {
                    return true;
                }
            }
        }
        catch
        {
            // 讀不到就當成舊式專案
        }

        return false;
    }

    /// <summary>用 vswhere 找出 MSBuild.exe，不需要開啟 Visual Studio。</summary>
    private static string? FindMsBuild(out string? error)
    {
        const string vsWhere =
            @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";

        if (!File.Exists(vsWhere))
        {
            error = "找不到 vswhere，無法定位 MSBuild（可在設定檔以 buildCommand 指定）";
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo(vsWhere)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (
                string arg in new[]
                {
                    "-latest",
                    "-prerelease",
                    "-products",
                    "*",
                    "-requires",
                    "Microsoft.Component.MSBuild",
                    "-find",
                    @"MSBuild\**\Bin\amd64\MSBuild.exe",
                }
            )
            {
                psi.ArgumentList.Add(arg);
            }

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                error = "無法啟動 vswhere";
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);

            string? found = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(File.Exists);

            error = found is null ? "vswhere 沒有回報可用的 MSBuild.exe" : null;
            return found;
        }
        catch (Exception ex)
        {
            error = $"解析 MSBuild 失敗: {ex.Message}";
            return null;
        }
    }
}
