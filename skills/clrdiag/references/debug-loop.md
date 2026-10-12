# Debug loop with clrdiag

Read `SKILL.md` section 0 first. Resolve the binary and run `clrdiag --list`.

## Requirements

- Windows x64, and a 64-bit target on .NET 8 or later.
- netcoredbg installed (Samsung, MIT). Resolution order: `dapAdapterPath` in
  `clrdiag.json`, then `PATH`, then the mason default path
  `%LOCALAPPDATA%\nvim-data\mason\packages\netcoredbg\netcoredbg\netcoredbg.exe`.
- A missing adapter prints a clear error. It never fails in silence.
- `dapEnabled: false` disables the whole feature. No process spawns and no pipe opens.

**ClrDiag is the only DAP client.** Clients that use `--send` are not DAP clients.
They send intent over a named pipe. The Neovim client works the same way. See
https://github.com/LizardLiang/clrdiag.nvim for that editor plugin.

**netcoredbg does not start at launch.** The first breakpoint action spawns it and
attaches it to the process under watch.

## The pipe name

The pipe name is `\\.\pipe\clrdiag-<first 12 hex of the SHA-256 of the lowercased
root path>`. **Never compute this hash.** Ask the tool.

```powershell
clrdiag --pipe-name
clrdiag --pipe-name --root C:\Users\me\App
```

It prints the bare name, for example `clrdiag-327f19d30c93`. Prefix `\\.\pipe\`
for the full path. `--root` must name an existing directory, otherwise config
loading fails with exit code 2.

Run one clrdiag instance per project root. Two instances share one pipe name, and
a `--send` command then reaches an arbitrary one of them.

## Commands on the pipe

The protocol is newline-delimited JSON in both directions. The client sends one
command object. ClrDiag replies with one state message.

| `cmd` | Other fields | Effect |
| --- | --- | --- |
| `setBreakpoint` | `path`, `line` | Add a breakpoint. Idempotent. |
| `clearBreakpoint` | `path`, `line` | Remove a breakpoint. |
| `addWatch` | `expression` | Add a watch expression. Idempotent. |
| `removeWatch` | `expression` | Remove a watch expression. |
| `continue` | none | Resume. |
| `stepOver` | none | Step over. |
| `stepIn` | none | Step into. |
| `stepOut` | none | Step out. |
| `pause` | none | Pause. |
| `status` | none | Read the current state. Changes nothing. |
| `start` | none | Start the dev server. Same code as the `s` key. |
| `stop` | none | Stop the dev server. Same code as the `x` key. |
| `restart` | none | Stop, build, and start. Same code as the `r` key. |

A malformed or unknown command returns the same state object plus an `error` field.

**The `error` text arrives `\u`-escaped, not as literal CJK.** An unknown command
`bogus` returns this, verified against a live session:

```json
{"type":"state","sessionState":"Running","pid":24368,"launchMode":false,"breakpoints":[],"watches":[],"error":"\u672A\u77E5\u6307\u4EE4: bogus"}
```

Test for the presence of the `error` key. Never match the CJK text literally. Parse
the JSON first if you need to read the message.

## Server control: `start`, `stop`, `restart`

These three commands call the code that the `s`, `x`, and `r` keys call. They work
only in the interactive dashboard. A `--dap` host has no server to control and
returns an `error` field. The command name field also accepts `type`, so
`{"type":"restart"}` equals `{"cmd":"restart"}`.

```powershell
clrdiag --send '{"cmd":"restart"}'
```

**ClrDiag replies after the server is listening, or after the failure.** The reply to
`restart` can take a minute because it includes the build. `--send` waits up to 10
minutes for these three commands. Other commands still wait 10 seconds.

The reply is the usual state object plus a `result` object and a `server` object.

```json
{"type":"state","sessionState":"Idle","launchMode":false,"breakpoints":[],"watches":[],"result":{"command":"restart","accepted":true,"ok":true,"message":"...","phase":"running","pid":24368,"errors":[]},"server":{"phase":"running","pid":24368,"url":"http://localhost:5000","busy":null,"last":{"command":"restart","ok":true,"phase":"running","message":"...","pid":24368,"errors":[]}}}
```

| Outcome | `result.accepted` | `result.ok` | `result.phase` | Other |
| --- | --- | --- | --- | --- |
| Build passed and server listens | `true` | `true` | `running` | `pid` set |
| Build failed | `true` | `false` | `buildFailed` | `errors` holds up to 20 compiler errors. The old server is already stopped. |
| Build passed, server did not start | `true` | `false` | `startFailed` | `message` holds the reason |
| Another background job runs (build, start, stop, snapshot) | `false` | `false` | `null` | Nothing started. `message` names the running job. |
| `start` while launch under the debugger is armed (`Shift+S`) and no `serveCommand` exists | `false` | `false` | `null` | No job runs. `message` holds the reason. Do not poll. |
| Stop step failed (`stop`, or the first step of `restart`) | `true` | `false` | `running` | The server still runs. No build happened. |

Every failure also sets the top-level `error` field. Test for the `error` key first.

**A second `restart` while one runs is refused, not joined.** Poll `status` until
`server.phase` leaves `building`, `starting`, and `stopping`, then send it again.

**Concurrency.** A key press and a pipe command share one lock around the check and
the assignment of the background job. At most one job runs. When both arrive
together, one runs and the other is refused. Two builds never run at once.

`status` and every reply carry the `server` object in the dashboard:

| Field | Meaning |
| --- | --- |
| `server.phase` | `stopped`, `building`, `starting`, `running`, `stopping`, `buildFailed`, `startFailed` |
| `server.pid` | Server PID. Set while the process is alive, including during `stopping`. |
| `server.url` | Probe URL. Set while the process is alive, including during `stopping`. |
| `server.busy` | Text of the running background job, or `null`. |
| `server.last` | Result of the last start, stop, or restart. Absent before the first one. |

If the user armed launch under the debugger with `Shift+S`, `start` and `restart`
launch under the debugger, as the keys do.

`status` must exist in the binary that hosts the session. That is the process
running `--dap` or the TUI. The sending binary may be any version, because `--send`
is only a pipe client. It serializes the JSON, writes it to the pipe, and prints
the reply. An older host replies with the state object plus an `error` field
holding the escaped form of `未知指令: status`. The connection still succeeds.

## Sending a command from PowerShell

`--send` connects to the pipe, discards the greeting state, sends one command,
prints the reply, and exits. It waits 5 seconds for the connection and 10 seconds
for the reply (10 minutes for `start`, `stop`, and `restart`). It exits with code 1 on timeout or failure. A connect timeout means
no clrdiag instance runs for this root, or `dapEnabled` is `false`.

**Quoting differs by PowerShell version. Every form below is tested.**

Write the `path` with forward slashes. ClrDiag normalizes `/` to `\` on its own.
Forward slashes avoid every backslash trap in both shells.

PowerShell 7 (`pwsh`) passes a single-quoted argument through unchanged.

```powershell
clrdiag --send '{"cmd":"setBreakpoint","path":"C:/App/Program.cs","line":42}'
clrdiag --send '{"cmd":"clearBreakpoint","path":"C:/App/Program.cs","line":42}'
clrdiag --send '{"cmd":"addWatch","expression":"counter"}'
clrdiag --send '{"cmd":"continue"}'
```

Windows PowerShell 5.1 strips those inner quotes. It needs the stop-parsing token
`--%` and backslash-escaped quotes.

```powershell
clrdiag --% --send "{\"cmd\":\"setBreakpoint\",\"path\":\"C:/App/Program.cs\",\"line\":42}"
```

Do not mix the forms. PowerShell 7 passes `--%` text literally, backslashes and
all, which produces an invalid command. Never write `\\` inside the JSON under
PowerShell 7. It arrives as one backslash and breaks the JSON escape.

Check the version with `$PSVersionTable.PSVersion`.

For a dynamic value under PowerShell 7, build the JSON with `ConvertTo-Json`.

```powershell
$json = @{
    cmd  = 'setBreakpoint'
    path = 'C:/App/Program.cs'
    line = 42
} | ConvertTo-Json -Compress
clrdiag --send $json
```

`--%` passes the rest of the line verbatim, so PowerShell variables never expand
there. A reply that reports a JSON parse error means the quoting failed. Fix the
quoting before you change anything else.

## The state reply

ClrDiag replies to every command with the current session state. It also pushes the
state to every connected client on any change, with no request. A client receives
one state message immediately on connect.

```jsonc
{
  "type": "state",
  "sessionState": "Halted",
  "pid": 12345,
  "launchMode": false,
  "threadId": 1,
  "stopReason": "breakpoint",
  "location": { "path": "C:\\App\\Program.cs", "line": 42 },
  "watchResults": [
    { "expression": "counter", "value": "7", "timedOut": false, "error": null }
  ],
  "breakpoints": [
    { "path": "C:\\App\\Program.cs", "line": 42, "verified": true, "message": null }
  ],
  "watches": [ "counter" ]
}
```

`sessionState` is one of `Idle`, `Connecting`, `Running`, `Halted`, `Terminated`,
or `Failed`. The fields `threadId`, `stopReason`, `location`, and `watchResults`
appear only when the session is halted and the capture finished.

## Breakpoint paths

The stored path keeps the case you sent. Only the slash direction is normalized,
from `/` to `\`. Comparison is then case-insensitive. Nothing else is normalized.
A relative path stays relative and matches nothing.

**A path that does not match the PDB is the most common silent failure.** The
breakpoint stays pending forever and the program never halts. Such a breakpoint
appears in `breakpoints` with `"verified": false` and a `message` that carries the
adapter reason. Always read `verified` after `setBreakpoint`. Always send an
absolute path.

## Waiting for a halt

**There is no blocking "wait for halt" command.** `--send` returns the current
state and exits. State this plainly when a user expects a blocking wait.

Two options remain. Tail a background `--dap` process for a long wait. Poll with
`status` for a short bounded check. Each poll starts a process, so keep the poll
interval above one second.

### Recommended: run `--dap` in the background and read its output

```powershell
clrdiag --dap --pid 12345 > .\halt.log
```

`--dap` attaches to the target, opens the named pipe, and prints a block on every
halt. Each block holds the frames, the locals of the selected frame, and every
watch result. The selected frame carries a `→` marker. A watch added during one
halt prints a one line delta instead of a new block. `--dap` runs until Ctrl+C.

The full loop for an agent:

1. Start `clrdiag --dap --pid <pid> > .\halt.log` as a background process.
2. Set the breakpoints with `--send`. Read `verified` in each reply.
3. Exercise the application.
4. Read `.\halt.log` for the halt blocks.
5. Send `continue`, `stepOver`, `stepIn`, or `stepOut` with `--send`.
6. Stop `--dap` with Ctrl+C.

`--dap` exits with code 1 when the attach fails and prints the adapter error. It
seeds itself from `dapBreakpoints` and `dapWatches` in the config.

Ctrl+C disconnects the session and kills netcoredbg. If you kill the process
another way, check for an orphaned `netcoredbg` process and stop it.

### Fallback: poll with `status`

Send `status` and read `sessionState` from the reply. The command is read-only and
has no side effect.

```powershell
clrdiag --send '{"cmd":"status"}'
```

Each poll starts a new process and opens one pipe connection. Keep the interval
above one second. Prefer the `--dap` approach for a long wait.

## Breakpoints on the startup path

Attach arrives too late for code that runs during startup, such as the first lines
of `Program.cs` or the DI wiring. The launch-under-debugger path solves this.

**That path is interactive only.** A human presses `Shift+S` to arm it, then `s` to
start or `r` to rebuild and restart. ClrDiag then starts `serveCommand` and
`serveArguments` through a DAP launch, and reuses the placeholders unchanged. The
serve panel shows `RUNNING (debug)`, and `x` sends a DAP terminate instead of
killing the process. No CLI flag exists for this.

The debugged process id also takes over the process monitor. The switch is
announced in the status line and the log tab. It never switches back on its own.

## The `dotnet run` wrapper case

`serveCommand: "dotnet"` with `run` as the first argument is a wrapper. A plain DAP
launch would attach to the wrapper, and the real application runs in a child
process that no debugger controls.

ClrDiag detects that exact shape. It starts the wrapper undebugged, finds the
direct child with Win32 Toolhelp32, and sends a DAP attach to the child. Toolhelp32
is faster than a full process scan and it excludes helpers such as `conhost.exe`.
The gap between child creation and attach is single-digit milliseconds.

Two limits remain.

- A very short startup path can still finish inside that gap. This is a race, not a
  forced pause at process creation.
- Only `dotnet` plus `run` is detected. Other wrappers, such as a batch file or a
  PowerShell script, attach to the wrapper itself.

The workaround is to point `serveCommand` at the built assembly or the apphost.
Then no child process exists and netcoredbg launches the target directly.

```jsonc
{ "serveCommand": "dotnet", "serveArguments": [ "bin/Debug/net8.0/MyApp.dll" ] }
```

A failure to find the child, an early wrapper exit, or a failed attach prints a
clear reason in the log pane and kills the wrapper. It never degrades into a silent
"attached to the wrapper, no breakpoint ever hits". A plain `s` or `r` start,
without the debugger, is unaffected.

## Setting breakpoints from the heap pane

This does not exist. The heap pane cannot set a breakpoint on a selected type. Only
the editor path and `--send` set breakpoints.
