using System.Diagnostics;
using System.Linq;
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

    /// <summary>
    /// 視為方案檔或專案檔的副檔名，依自動挑選建置目標的優先順序排列；
    /// 工作目錄的專案掃描也使用同一份清單。
    /// </summary>
    public static readonly IReadOnlyList<string> ProjectExtensions = new[] { ".sln", ".slnx", ".csproj", ".vbproj" };

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

    /// <summary>
    /// 伺服器行程的映像名稱（不含 .exe）。空陣列 = 只靠連接埠辨識：自動接管（儀表板、s 鍵、不加 --pid 的批次指令）
    /// 只認監聽 Port 的受控行程，名稱不限。寫了名稱就多一道條件：行程名稱必須在清單內。
    /// HTTP.sys 站台（IIS Express、w3wp）的連接埠監聽記在系統行程名下，一律必須寫名稱
    /// （沒寫時啟動與接管都立即失敗並說明）。接管既有行程時比對命令列的 /port:：
    /// 寫了別的連接埠的行程排除；等於 Port 的行程是證據（32 位元或還沒載入 CLR 就回報原因，不接管別的行程）；
    /// 沒有證據時，命令列讀得到、沒有 /port:、64 位元、已載入 CLR 的行程剛好一個才接管（備援），多個則都不接管。
    /// HTTP.sys 站台啟動時只看名稱，從啟動的行程與後代行程挑，不要求已載入 CLR；命令列 /port: 寫了別的連接埠的行程服務別的站台，同樣排除。
    /// 舊式 ASP.NET 專案省略時推斷為 iisexpress。p 鍵與 --list 只是列出清單讓使用者自己挑。null 視為空陣列。
    /// </summary>
    public string[] ProcessNames
    {
        get => processNames;
        init => processNames = value ?? Array.Empty<string>();
    }

    private readonly string[] processNames = Array.Empty<string>();

    /// <summary>視為「自己的程式碼」的命名空間前綴，用於標記執行緒與堆疊。空 = 以「非框架」判斷。</summary>
    public string[] AppNamespaces { get; init; } = Array.Empty<string>();

    /// <summary>CSV 報告輸出目錄，相對於 Root。</summary>
    public string ReportDirectory { get; init; } = ".clrdiag-reports";

    // --- 除錯（DAP） ---

    /// <summary>是否啟用除錯功能（spawn netcoredbg、開具名管道指令通道）。預設開啟，可關閉。</summary>
    public bool DapEnabled { get; init; } = true;

    /// <summary>netcoredbg 執行檔路徑。null = 自動解析（PATH → mason 預設安裝路徑）。</summary>
    public string? DapAdapterPath { get; init; }

    /// <summary>
    /// 啟動時要載入的中斷點初始清單，格式 "路徑:行號"（例: "C:/App/Program.cs:42"）。
    /// 純粹是「下次啟動的初始值」，執行期用 Neovim 或 TUI 新增／移除的變更不會寫回這裡——
    /// 與 buildConfiguration 等其他欄位一致，設定檔只在啟動時讀一次。
    /// </summary>
    public string[] DapBreakpoints { get; init; } = Array.Empty<string>();

    /// <summary>啟動時要載入的監看運算式初始清單。</summary>
    public string[] DapWatches { get; init; } = Array.Empty<string>();

    /// <summary>把 DapBreakpoints 的 "路徑:行號" 字串解析成結構化資料；格式錯誤的項目直接略過。</summary>
    [JsonIgnore]
    public IEnumerable<(string Path, int Line)> ParsedDapBreakpoints =>
        DapBreakpoints
            .Select(spec =>
            {
                int colon = spec.LastIndexOf(':');
                if (colon <= 0 || !int.TryParse(spec[(colon + 1)..], out int line))
                {
                    return ((string Path, int Line)?)null;
                }

                return (spec[..colon], line);
            })
            .Where(parsed => parsed is not null)
            .Select(parsed => parsed!.Value);

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

    /// <summary>
    /// serveCommand 是否為「wrapper」型指令——執行檔本身不是目標 app，而是會再開一個子行程
    /// 才跑真正的程式。目前只認 `dotnet run`（本文件範例的預設寫法，也是最常見的情形）；
    /// 這種指令直接交給 netcoredbg 的 `launch` 只會附加到 wrapper 本身，wrapper 另外開的
    /// 子行程完全不受除錯器控制，中斷點永遠不會命中。真正需要在除錯器下啟動時
    /// （ServerService.StartUnderDebuggerAsync）要改走「啟動 wrapper → 找子行程 → attach」，
    /// 見 Ui/DiagApp.Actions.cs 的 LaunchServerUnderDebuggerAsync。
    /// </summary>
    [JsonIgnore]
    public bool IsWrapperServeCommand =>
        ServeCommand is not null
        && Path.GetFileNameWithoutExtension(ServeCommand)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        && (ServeArguments?.FirstOrDefault()?.Equals("run", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>serveCommand 的推斷結果說明（或無法推斷的原因）；設定檔已有 serveCommand 時為 null。啟動時寫進 6 記錄。</summary>
    [JsonIgnore]
    public string? ServeInferenceNote { get; init; }

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
    /// explicitBuildProject 是從工作目錄掃描選定的方案檔或專案檔：往上尋找 clrdiag.json 改從它的資料夾開始，
    /// 沒有設定檔時 Root 就是它的資料夾；設定檔沒有指定 buildProject 時，建置目標就是這個檔案。
    /// 找到設定檔時 Root 仍是設定檔所在資料夾，設定檔內的相對路徑才會對齊。
    /// </summary>
    public static DiagConfig Load(
        string? explicitConfig,
        string? explicitRoot,
        string? explicitBuildProject = null,
        Func<string?>? iisExpressLocator = null
    )
    {
        string? projectFile = explicitBuildProject is null
            ? null
            : Path.GetFullPath(explicitBuildProject);
        string? projectFolder = projectFile is null ? null : Path.GetDirectoryName(projectFile);
        string searchStart = explicitRoot ?? projectFolder ?? Directory.GetCurrentDirectory();

        string? configFile = explicitConfig ?? FindConfigFile(searchStart);
        DiagConfig config;
        bool portConfigured = false;
        bool processNamesConfigured = false;

        if (configFile is not null)
        {
            string json = File.ReadAllText(configFile);
            portConfigured = HasProperty(json, nameof(Port));
            processNamesConfigured = HasProperty(json, nameof(ProcessNames));
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
                Root = explicitRoot ?? projectFolder ?? FindProjectRoot(Directory.GetCurrentDirectory()),
            };
        }

        if (projectFile is not null && config.BuildProject is null)
        {
            config = config with { BuildProject = projectFile };
        }

        config.ResolveBuildTarget();

        // 唯一的推斷點：之後儀表板與批次模式讀到的 ServeCommand、Port、ProcessNames 都是這份結果。
        ServeInference.Result inference = ServeInference.Apply(
            config,
            portConfigured,
            processNamesConfigured,
            iisExpressLocator ?? ServeInference.LocateIisExpress
        );
        return inference.Config with { ServeInferenceNote = inference.Note };
    }

    /// <summary>設定檔的 JSON 是否明確寫了某個屬性（不分大小寫），用來分辨「沒寫」與「寫了預設值」。</summary>
    private static bool HasProperty(string json, string name)
    {
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }
        );
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.EnumerateObject().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// 沒有設定檔時，往上找第一個含 ProjectExtensions 任一副檔名的檔案或 .git 的目錄當作根目錄。
    /// </summary>
    private static string FindProjectRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            DirectoryInfo current = directory;
            if (
                ProjectExtensions.Any(extension => current.EnumerateFiles("*" + extension).Any())
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
            IsSdkProject = ProjectFile.IsSdkStyle(target);
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
        foreach (string extension in ProjectExtensions)
        {
            string? found = directory.EnumerateFiles("*" + extension).FirstOrDefault()?.FullName;
            if (found is not null)
            {
                return found;
            }
        }

        return null;
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
                // 同 ServerService：沒有主控台就沒有 CONIN$ 可開，避免搶走 TUI 的按鍵。
                RedirectStandardInput = true,
                CreateNoWindow = true,
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
