using ClrDiag.Core;
using Spectre.Console;

namespace ClrDiag.Ui;

public sealed partial class DiagApp
{
    private int? overrideWidth;
    private int? overrideHeight;

    private int ViewWidth => overrideWidth ?? AnsiConsole.Profile.Width;

    /// <summary>
    /// 在沒有互動主控台的情況下把畫面渲染成純文字。
    /// 用途：驗證版面、把儀表板內容貼進問題回報或交接文件。
    /// </summary>
    public string RenderFramesToText(int? attachPid, int width, int height, bool withSnapshot)
    {
        overrideWidth = width;
        overrideHeight = height;

        monitor.Start();
        StartDebugOutputListener();
        LogStartupInfo();

        int? pid = attachPid ?? server.FindExistingServer();
        if (pid is not null)
        {
            server.AdoptExisting(pid.Value);
            monitor.Attach(pid.Value);
        }

        // 等取樣累積幾筆才有走勢圖可看
        Thread.Sleep(3000);

        if (withSnapshot && pid is not null)
        {
            DiagSnapshot snapshot = snapshots.Capture(
                pid.Value,
                includeTypes: true,
                includeThreads: true,
                CancellationToken.None
            );
            heapSnapshots = new List<DiagSnapshot>(heapSnapshots) { snapshot };
            threadsSnapshot = snapshot;
            status =
                $"快照 {snapshot.Label}：{snapshot.ObjectCount:N0} 物件 / {snapshot.TotalSizeMb:N1} MB";
        }

        var output = new System.Text.StringBuilder();

        // 八個面板各一張：0–2 是上排面板放大後的畫面（分割版面下看不到完整內容），
        // 3–7 是一般分割版面下的中間檢視。
        for (int target = 0; target < PaneNames.Length; target++)
        {
            selectedPane = target;
            zoomed = target < FirstViewPane;
            if (!zoomed)
            {
                view = (DiagView)(target - FirstViewPane);
            }

            output.AppendLine(
                $"===== [{target}] {PaneName(target)}{(zoomed ? " (zoom)" : string.Empty)} ====="
            );
            output.AppendLine(RenderFrame(width, height));
            output.AppendLine();
        }

        return output.ToString();
    }

    private string RenderFrame(int width, int height)
    {
        var writer = new StringWriter();
        IAnsiConsole console = AnsiConsole.Create(
            new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No,
                Out = new AnsiConsoleOutput(writer),
            }
        );

        console.Profile.Width = width;
        console.Profile.Height = height;

        Layout layout = BuildLayout();
        Render(layout);
        console.Write(layout);

        return writer.ToString();
    }
}
