using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using ClrDiag.Core;
using ClrDiag.Core.Dap;
using ClrDiag.Ui;

namespace ClrDiag.Tests;

/// <summary>
/// 管道的 start／stop／restart：用假的 IServerControl 驗證指令分派、回覆時間點與回覆形狀；
/// 並行保護用真的 DiagApp 驗證，因為保護的鎖在 DiagApp 裡。
/// </summary>
public sealed class ServerControlPipeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>假的伺服器控制：記錄收到的指令，結果由測試用 TaskCompletionSource 決定。</summary>
    private sealed class FakeServerControl : IServerControl
    {
        public List<string> Calls { get; } = new();
        public TaskCompletionSource<ServerCommandOutcome> Pending { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Accept { get; set; } = true;
        public ServerControlSnapshot Current { get; set; } =
            new(ServerPhases.Stopped, null, null, null, null);

        public ServerControlSnapshot Snapshot() => Current;

        public ServerCommandTicket Start() => Ticket(ServerCommands.Start);

        public ServerCommandTicket Stop() => Ticket(ServerCommands.Stop);

        public ServerCommandTicket Restart() => Ticket(ServerCommands.Restart);

        private ServerCommandTicket Ticket(string command)
        {
            Calls.Add(command);
            return Accept
                ? new ServerCommandTicket(true, command, Pending.Task)
                : new ServerCommandTicket(false, "已有背景工作進行中：重建並重啟", null);
        }
    }

    /// <summary>開一個真的具名管道伺服器與客戶端；客戶端先讀掉連線時送的問候狀態。</summary>
    private sealed class PipeHarness : IDisposable
    {
        private readonly DapSessionService session;
        private readonly DebugCommandServer server;
        private readonly NamedPipeClientStream client;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;

        private PipeHarness(IServerControl? control)
        {
            var log = new LogBuffer();
            session = new DapSessionService(log);
            server = new DebugCommandServer(
                session,
                log,
                "clrdiag-test-" + Guid.NewGuid().ToString("N"),
                control
            );
            server.Start();
            client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.ConnectAsync(5000).GetAwaiter().GetResult();
            reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
            writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            ReadReply().GetAwaiter().GetResult(); // 連線時先送的問候狀態，不是任何指令的回覆
        }

        public static PipeHarness Open(IServerControl? control) => new(control);

        public Task Send(string json) => writer.WriteLineAsync(json);

        public async Task<JsonObject> ReadReply()
        {
            string? line = await reader.ReadLineAsync().WaitAsync(Wait);
            return (JsonObject)JsonNode.Parse(line!)!;
        }

        public async Task<JsonObject> Command(string json)
        {
            await Send(json);
            return await ReadReply();
        }

        public void Dispose()
        {
            client.Dispose();
            server.Dispose();
            session.Dispose();
        }
    }

    private static ServerCommandOutcome Outcome(
        string command,
        bool ok,
        string phase,
        string message,
        int? pid = null,
        params string[] errors
    ) => new(command, ok, phase, message, pid, errors);

    [Fact]
    public async Task restart在伺服器開始監聽後才回覆並帶出PID()
    {
        var control = new FakeServerControl();
        using var pipe = PipeHarness.Open(control);

        await pipe.Send("{\"cmd\":\"restart\"}");
        Task<JsonObject> reply = pipe.ReadReply();
        await Task.Delay(300);
        Assert.False(reply.IsCompleted); // 建置與啟動尚未完成，不能先回覆

        control.Pending.SetResult(
            Outcome(ServerCommands.Restart, true, ServerPhases.Running, "重建並重啟完成 PID 4242", 4242)
        );
        JsonObject state = await reply;

        Assert.Equal(new[] { "restart" }, control.Calls);
        Assert.Equal("restart", (string?)state["result"]!["command"]);
        Assert.True((bool)state["result"]!["accepted"]!);
        Assert.True((bool)state["result"]!["ok"]!);
        Assert.Equal("running", (string?)state["result"]!["phase"]);
        Assert.Equal(4242, (int)state["result"]!["pid"]!);
        Assert.False(state.ContainsKey("error"));
    }

    [Fact]
    public async Task restart建置失敗時回覆錯誤摘要並設定error欄位()
    {
        var control = new FakeServerControl();
        control.Pending.SetResult(
            Outcome(
                ServerCommands.Restart,
                false,
                ServerPhases.BuildFailed,
                "建置失敗，未重啟伺服器（1 個錯誤）",
                null,
                "Foo.cs(12,5) CS1002: ; expected"
            )
        );
        using var pipe = PipeHarness.Open(control);

        JsonObject state = await pipe.Command("{\"cmd\":\"restart\"}");

        Assert.False((bool)state["result"]!["ok"]!);
        Assert.True((bool)state["result"]!["accepted"]!);
        Assert.Equal("buildFailed", (string?)state["result"]!["phase"]);
        Assert.Equal("Foo.cs(12,5) CS1002: ; expected", (string?)state["result"]!["errors"]![0]);
        Assert.True(state.ContainsKey("error"));
    }

    [Fact]
    public async Task 已有背景工作時restart被拒絕而不是併入()
    {
        var control = new FakeServerControl { Accept = false };
        using var pipe = PipeHarness.Open(control);

        JsonObject state = await pipe.Command("{\"cmd\":\"restart\"}");

        Assert.False((bool)state["result"]!["accepted"]!);
        Assert.False((bool)state["result"]!["ok"]!);
        Assert.Contains("已有背景工作進行中", (string?)state["error"]);
    }

    [Fact]
    public async Task start與stop各自呼叫對應的方法且type欄位等同cmd()
    {
        var control = new FakeServerControl();
        control.Pending.SetResult(Outcome("x", true, ServerPhases.Running, "ok", 1));
        using var pipe = PipeHarness.Open(control);

        await pipe.Command("{\"cmd\":\"start\"}");
        await pipe.Command("{\"cmd\":\"stop\"}");
        await pipe.Command("{\"type\":\"restart\"}");

        Assert.Equal(new[] { "start", "stop", "restart" }, control.Calls);
    }

    [Fact]
    public async Task status回報伺服器階段與PID()
    {
        var control = new FakeServerControl
        {
            Current = new ServerControlSnapshot(
                ServerPhases.Running,
                4242,
                "http://localhost:5000",
                null,
                null
            ),
        };
        using var pipe = PipeHarness.Open(control);

        JsonObject state = await pipe.Command("{\"cmd\":\"status\"}");

        Assert.Equal("running", (string?)state["server"]!["phase"]);
        Assert.Equal(4242, (int)state["server"]!["pid"]!);
        Assert.False(state.ContainsKey("result")); // status 沒有副作用，也不帶指令結果
    }

    [Fact]
    public async Task 沒有伺服器控制的模式回覆錯誤且既有回覆形狀不變()
    {
        using var pipe = PipeHarness.Open(null);

        JsonObject restart = await pipe.Command("{\"cmd\":\"restart\"}");
        JsonObject status = await pipe.Command("{\"cmd\":\"status\"}");

        Assert.True(restart.ContainsKey("error"));
        Assert.False(status.ContainsKey("server"));
        Assert.False(status.ContainsKey("result"));
        Assert.False(status.ContainsKey("error"));
        Assert.Equal("state", (string?)status["type"]);
    }

    [Fact]
    public async Task 未知指令仍回覆error欄位()
    {
        using var pipe = PipeHarness.Open(new FakeServerControl());

        JsonObject state = await pipe.Command("{\"cmd\":\"bogus\"}");

        Assert.True(state.ContainsKey("error"));
    }

    [Fact]
    public async Task 按鍵與管道同時要求重啟時只有一個被接受()
    {
        using var app = new DiagApp(new DiagConfig(), port: 0);
        var control = (IServerControl)app;
        int callers = 16;
        using var gate = new Barrier(callers);

        ServerCommandTicket[] tickets = await Task.WhenAll(
            Enumerable
                .Range(0, callers)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        gate.SignalAndWait();
                        return control.Restart();
                    })
                )
        );

        Assert.Equal(1, tickets.Count(t => t.Accepted));
        Assert.All(tickets.Where(t => !t.Accepted), t => Assert.Contains("已有背景工作進行中", t.Message));
        await tickets.Single(t => t.Accepted).Completion!.WaitAsync(Wait); // 讓背景流程收尾再釋放 app
    }

    [Fact]
    public async Task 背景工作跑完後不必等介面重繪就能接受下一個指令()
    {
        using var app = new DiagApp(new DiagConfig(), port: 0);
        var control = (IServerControl)app;

        ServerCommandTicket first = control.Restart();
        await first.Completion!.WaitAsync(Wait);
        ServerCommandTicket second = control.Restart();

        Assert.True(second.Accepted);
        await second.Completion!.WaitAsync(Wait);
    }
}
