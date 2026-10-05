using System.Text.Json;
using System.Xml.Linq;

namespace ClrDiag.Core;

/// <summary>
/// 設定沒有 serveCommand 時，依選定專案檔的類型推斷預設的啟動方式。
/// 只讀專案檔、.csproj.user 與 Properties/launchSettings.json；不讀 Web.config 或連線字串。
/// 推斷只在 DiagConfig.Load 做一次，之後所有讀取端（儀表板、批次模式）看到同一份設定。
/// </summary>
public static class ServeInference
{
    /// <summary>ASP.NET Web 應用程式（舊式專案）的 ProjectTypeGuids 成員。</summary>
    private const string AspNetWebAppGuid = "349c5851-65df-11da-9384-00065b846f21";

    private const string SdkWebName = "Microsoft.NET.Sdk.Web";

    private const string IisExpressProcessName = "iisexpress";

    /// <summary>推斷結果；Config 是填入欄位後的設定，Note 是要寫進 6 記錄的一行說明。</summary>
    public sealed record Result(DiagConfig Config, string? Note);

    /// <summary>
    /// 設定已有 serveCommand 或沒有專案檔時原樣回傳；否則依專案類型填入缺少的欄位。
    /// 設定檔明確寫的 serveArguments、port、processNames 一律保留。
    /// iisExpressLocator 回傳 iisexpress.exe 的完整路徑，找不到回傳 null（測試用於替換）。
    /// </summary>
    public static Result Apply(DiagConfig config, bool portConfigured, Func<string?> iisExpressLocator)
    {
        string? project = config.ResolvedBuildProject;
        if (config.ServeCommand is not null || project is null)
        {
            return new Result(config, null);
        }

        string name = Path.GetFileName(project);
        string extension = Path.GetExtension(project);
        if (
            extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
        )
        {
            return new Result(config, $"未設定 serveCommand，{name} 是方案檔，無法判斷要啟動哪個專案，只能附加到既有行程");
        }

        XElement? root = LoadXml(project);
        if (root is null)
        {
            return new Result(config, $"未設定 serveCommand，無法讀取 {name}，只能附加到既有行程");
        }

        if (IsSdkWeb(root))
        {
            return InferDotnetRun(config, project, portConfigured);
        }

        if (HasAspNetWebAppGuid(root))
        {
            return InferIisExpress(config, project, root, portConfigured, iisExpressLocator);
        }

        return new Result(config, $"未設定 serveCommand，{name} 不是網站專案（非 Microsoft.NET.Sdk.Web，也不是 ASP.NET Web 應用程式），只能附加到既有行程");
    }

    private static Result InferDotnetRun(DiagConfig config, string project, bool portConfigured)
    {
        string name = Path.GetFileName(project);
        int port = config.Port;
        string portSource = portConfigured ? "設定檔" : "預設";
        if (!portConfigured)
        {
            string launchSettings = Path.Combine(Path.GetDirectoryName(project)!, "Properties", "launchSettings.json");
            if (ReadLaunchSettingsPort(launchSettings) is { } fromLaunch)
            {
                port = fromLaunch;
                portSource = "Properties/launchSettings.json";
            }
        }

        string[] arguments = config.ServeArguments
            ?? new[] { "run", "--project", "{project}", "--urls", "http://localhost:{port}" };
        DiagConfig inferred = config with
        {
            ServeCommand = "dotnet",
            ServeArguments = arguments,
            Port = port,
        };
        return new Result(
            inferred,
            $"未設定 serveCommand，依專案類型推斷：dotnet {string.Join(' ', arguments)}（Sdk.Web 專案 {name}，連接埠 {port} 來源 {portSource}）"
        );
    }

    private static Result InferIisExpress(
        DiagConfig config,
        string project,
        XElement root,
        bool portConfigured,
        Func<string?> iisExpressLocator
    )
    {
        string name = Path.GetFileName(project);
        string? iisExpress = iisExpressLocator();
        if (iisExpress is null)
        {
            return new Result(config, $"未設定 serveCommand，{name} 是 ASP.NET Web 應用程式但找不到 iisexpress.exe（Program Files 與 Program Files (x86) 都沒有），只能附加到既有行程");
        }

        int port = config.Port;
        string portSource = portConfigured ? "設定檔" : "預設";
        if (!portConfigured)
        {
            string userFile = project + ".user";
            XElement? user = File.Exists(userFile) ? LoadXml(userFile) : null;
            (int Port, string Source)? found =
                FromElement(user, "DevelopmentServerPort", Path.GetFileName(userFile))
                ?? FromElement(root, "DevelopmentServerPort", name)
                ?? FromIisUrl(user, Path.GetFileName(userFile))
                ?? FromIisUrl(root, name);
            if (found is { } hit)
            {
                port = hit.Port;
                portSource = hit.Source;
            }
        }

        string[] arguments = config.ServeArguments
            ?? new[] { $"/path:{Path.GetDirectoryName(project)}", "/port:{port}" };
        DiagConfig inferred = config with
        {
            ServeCommand = iisExpress,
            ServeArguments = arguments,
            Port = port,
            ProcessNames = config.ProcessNames.Length > 0
                ? config.ProcessNames
                : new[] { IisExpressProcessName },
        };
        return new Result(
            inferred,
            $"未設定 serveCommand，依專案類型推斷：{IisExpressProcessName} /port:{port}（ASP.NET Web 應用程式 {name}，連接埠來源 {portSource}）"
        );
    }

    /// <summary>預設的 iisexpress.exe 位置：先找 Program Files，再找 Program Files (x86)。</summary>
    public static string? LocateIisExpress()
    {
        foreach (
            Environment.SpecialFolder folder in new[]
            {
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
            }
        )
        {
            string baseDir = Environment.GetFolderPath(folder);
            if (baseDir.Length == 0)
            {
                continue;
            }

            string candidate = Path.Combine(baseDir, "IIS Express", "iisexpress.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static XElement? LoadXml(string path)
    {
        try
        {
            return XDocument.Load(path).Root;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSdkWeb(XElement root) =>
        string.Equals((string?)root.Attribute("Sdk"), SdkWebName, StringComparison.OrdinalIgnoreCase)
        || root.Elements().Any(e =>
            e.Name.LocalName == "Sdk"
            && string.Equals((string?)e.Attribute("Name"), SdkWebName, StringComparison.OrdinalIgnoreCase)
        );

    private static bool HasAspNetWebAppGuid(XElement root) =>
        root.Descendants()
            .Where(e => e.Name.LocalName == "ProjectTypeGuids")
            .Any(e => e.Value.Contains(AspNetWebAppGuid, StringComparison.OrdinalIgnoreCase));

    private static (int Port, string Source)? FromElement(XElement? root, string elementName, string source)
    {
        string? text = root?.Descendants().FirstOrDefault(e => e.Name.LocalName == elementName)?.Value;
        return TryPort(text, out int port) ? (port, source) : null;
    }

    /// <summary>IISUrl 只認 http（https 的連接埠不是 --urls／探測用的 http 連接埠）。</summary>
    private static (int Port, string Source)? FromIisUrl(XElement? root, string source)
    {
        string? text = root is null
            ? null
            : string.Join(';', root.Descendants().Where(e => e.Name.LocalName == "IISUrl").Select(e => e.Value));
        return HttpUrlPort(text) is { } port ? (port, source) : null;
    }

    private static int? ReadLaunchSettingsPort(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }
            );
            if (
                !document.RootElement.TryGetProperty("profiles", out JsonElement profiles)
                || profiles.ValueKind != JsonValueKind.Object
            )
            {
                return null;
            }

            foreach (JsonProperty profile in profiles.EnumerateObject())
            {
                if (
                    profile.Value.ValueKind == JsonValueKind.Object
                    && profile.Value.TryGetProperty("applicationUrl", out JsonElement url)
                    && url.ValueKind == JsonValueKind.String
                )
                {
                    return HttpUrlPort(url.GetString());
                }
            }
        }
        catch
        {
            // launchSettings.json 壞掉就當作沒有，沿用預設連接埠
        }

        return null;
    }

    /// <summary>從以分號分隔的網址清單取第一個 http（非 https）網址的連接埠。</summary>
    private static int? HttpUrlPort(string? urls)
    {
        if (urls is null)
        {
            return null;
        }

        foreach (string part in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (
                Uri.TryCreate(part.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttp
                && !uri.IsDefaultPort
            )
            {
                return uri.Port;
            }
        }

        return null;
    }

    private static bool TryPort(string? text, out int port) =>
        int.TryParse(text?.Trim(), out port) && port is > 0 and <= 65535;
}
