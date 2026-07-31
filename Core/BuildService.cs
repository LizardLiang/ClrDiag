using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClrDiag.Core;

public sealed record BuildDiagnostic(string File, int Line, int Column, string Code, string Message)
{
    public string ShortFile => Path.GetFileName(File);

    public override string ToString() => $"{ShortFile}({Line},{Column}) {Code}: {Message}";
}

public sealed record BuildResult(
    bool Success,
    TimeSpan Duration,
    string Configuration,
    IReadOnlyList<BuildDiagnostic> Errors,
    int WarningCount
)
{
    public static BuildResult NotRun { get; } =
        new(false, TimeSpan.Zero, "-", Array.Empty<BuildDiagnostic>(), 0);
}

/// <summary>依設定執行建置指令（MSBuild 或 dotnet build），並把輸出解析成結構化的錯誤清單。</summary>
public sealed class BuildService
{
    // 例: C:\path\Foo.cs(12,34): error CS1061: 訊息內容 [C:\path\Proj.csproj]
    private static readonly Regex DiagnosticPattern = new(
        @"^(?<file>[^(]+)\((?<line>\d+)(?:,(?<col>\d+))?\)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+[0-9]+)\s*:\s*(?<msg>.*?)(?:\s\[[^\]]*\])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private readonly DiagConfig config;
    private readonly LogBuffer log;

    public BuildService(DiagConfig config, LogBuffer log)
    {
        this.config = config;
        this.log = log;
    }

    public bool IsRunning { get; private set; }

    public BuildResult Last { get; private set; } = BuildResult.NotRun;

    public async Task<BuildResult> BuildAsync(string configuration, CancellationToken token)
    {
        if (IsRunning)
        {
            return Last;
        }

        if (!config.CanBuild)
        {
            string reason = config.BuildToolError ?? "沒有可用的建置目標";
            log.Add("build", LogKind.Error, reason);
            Last = new BuildResult(
                false,
                TimeSpan.Zero,
                configuration,
                new[] { new BuildDiagnostic("-", 0, 0, "CFG0000", reason) },
                0
            );
            return Last;
        }

        IsRunning = true;
        var sw = Stopwatch.StartNew();
        var collector = new DiagnosticCollector();

        try
        {
            (string executable, string[] arguments) = ResolveCommand(configuration);

            var psi = new ProcessStartInfo(executable)
            {
                WorkingDirectory = config.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 同 ServerService：不導向標準輸入，MSBuild 會跟 TUI 搶主控台按鍵。
                // 建置行程活得短，症狀只在建置期間出現，但成因與 serve 完全相同。
                RedirectStandardInput = true,
                // 同 ServerService：子行程必須完全沒有主控台，才能擋住直接開 CONIN$
                // 讀鍵盤的工具（stdin 導向只能擋走 stdin handle 的讀取）。
                CreateNoWindow = true,
                UseShellExecute = false,
            };

            foreach (string arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            log.Add("build", LogKind.Info, $"{Path.GetFileName(executable)} {configuration} 開始");

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => HandleLine(e.Data, collector);
            process.ErrorDataReceived += (_, e) => HandleLine(e.Data, collector);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(token).ConfigureAwait(false);

            bool success = process.ExitCode == 0;
            Last = new BuildResult(
                success,
                sw.Elapsed,
                configuration,
                collector.SnapshotErrors(),
                collector.Warnings
            );

            log.Add(
                "build",
                success ? LogKind.Success : LogKind.Error,
                success
                    ? $"建置成功 {sw.Elapsed.TotalSeconds:N1}s（警告 {collector.Warnings}）"
                    : $"建置失敗 {sw.Elapsed.TotalSeconds:N1}s（錯誤 {collector.ErrorCount}、警告 {collector.Warnings}）"
            );

            return Last;
        }
        catch (OperationCanceledException)
        {
            log.Add("build", LogKind.Warning, "建置已取消");
            Last = new BuildResult(
                false,
                sw.Elapsed,
                configuration,
                collector.SnapshotErrors(),
                collector.Warnings
            );
            return Last;
        }
        catch (Exception ex)
        {
            log.Add("build", LogKind.Error, $"建置例外: {ex.Message}");
            Last = new BuildResult(
                false,
                sw.Elapsed,
                configuration,
                collector.SnapshotErrors(),
                collector.Warnings
            );
            return Last;
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// 決定實際要執行的建置指令：
    /// 設定檔明確指定 → 照用；SDK 樣式專案 → dotnet build；舊式專案 → vswhere 找到的 MSBuild。
    /// </summary>
    private (string Executable, string[] Arguments) ResolveCommand(string configuration)
    {
        string project = config.ResolvedBuildProject!;

        // 執行檔與參數可以分別覆寫：只給 buildArguments 時仍沿用自動偵測到的建置工具
        string executable =
            config.BuildCommand is not null ? config.Expand(config.BuildCommand, configuration)
            : config.IsSdkProject ? "dotnet"
            : config.MsBuildPath!;

        if (config.BuildArguments is not null)
        {
            return (
                executable,
                config.BuildArguments.Select(arg => config.Expand(arg, configuration)).ToArray()
            );
        }

        if (config.BuildCommand is not null)
        {
            return (executable, new[] { project, "-c", configuration });
        }

        return config.IsSdkProject
            ? (
                executable,
                new[] { "build", project, "-c", configuration, "--nologo", "-v", "minimal" }
            )
            : (
                executable,
                new[]
                {
                    project,
                    $"/p:Configuration={configuration}",
                    "/m",
                    "/nr:false",
                    "/verbosity:minimal",
                    "/clp:NoSummary;NoItemAndPropertyList",
                }
            );
    }

    private void HandleLine(string? line, DiagnosticCollector collector)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Match match = DiagnosticPattern.Match(line.Trim());
        if (!match.Success)
        {
            log.Add("build", LogKind.Output, line);
            return;
        }

        bool isError = match
            .Groups["sev"]
            .Value.Equals("error", StringComparison.OrdinalIgnoreCase);
        if (isError)
        {
            collector.AddError(
                new BuildDiagnostic(
                    match.Groups["file"].Value.Trim(),
                    int.Parse(match.Groups["line"].Value),
                    match.Groups["col"].Success ? int.Parse(match.Groups["col"].Value) : 0,
                    match.Groups["code"].Value,
                    match.Groups["msg"].Value.Trim()
                )
            );

            log.Add("build", LogKind.Error, line);
        }
        else
        {
            collector.AddWarning();
            log.Add("build", LogKind.Warning, line);
        }
    }

    /// <summary>MSBuild 的輸出來自背景執行緒，集中在此類別做同步。</summary>
    private sealed class DiagnosticCollector
    {
        private readonly object gate = new();
        private readonly List<BuildDiagnostic> errors = new();
        private int warnings;

        public int Warnings => Volatile.Read(ref warnings);

        public int ErrorCount
        {
            get
            {
                lock (gate)
                {
                    return errors.Count;
                }
            }
        }

        public void AddError(BuildDiagnostic diagnostic)
        {
            lock (gate)
            {
                errors.Add(diagnostic);
            }
        }

        public void AddWarning() => Interlocked.Increment(ref warnings);

        public IReadOnlyList<BuildDiagnostic> SnapshotErrors()
        {
            lock (gate)
            {
                return errors.ToArray();
            }
        }
    }
}
