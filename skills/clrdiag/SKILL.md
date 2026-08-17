---
name: clrdiag
description: Drive the clrdiag CLI, a terminal .NET memory and thread diagnostics console for Windows x64. Use this skill for .NET memory leak investigation, managed heap snapshots, type histograms, snapshot diffs, GC root chain search, managed thread callstack dumps, building or restarting a .NET dev server from the terminal, setting breakpoints and stepping without Visual Studio, watch expressions over a named pipe, and streaming Debug.WriteLine or Trace.WriteLine (OutputDebugString) output. Trigger phrases include "clrdiag", "ClrDiag", "clrdiag.json", "memory leak", "heap snapshot", "type histogram", "GC root", "root chain", "thread callstack dump", "netcoredbg", "breakpoint without Visual Studio", "OutputDebugString", "Debug.WriteLine output", "why does my app memory keep growing", and "restart the dev server".
---

# clrdiag

`clrdiag` is a terminal console for .NET diagnostics. It replaces the parts of the
Visual Studio "Diagnostic Tools" window that need no code change in the target.
It also drives builds, a dev server, breakpoints, and application debug output.

This skill covers four workflows. Read section 0 first. Then read only the section
you need.

1. Build and serve
2. Memory leak hunt, detailed in `references/memory-leak-hunt.md`
3. Debug loop with breakpoints, detailed in `references/debug-loop.md`
4. Debug output streaming

## 0. Preflight

**Platform limits. Check these before you promise a result.**

- The tool runs on Windows x64 only.
- The target process must be 64-bit. ClrMD loads a DAC of matching bitness.
- A 32-bit process never appears in `--list`. It cannot be diagnosed at all.
- The debug loop (section 3) also requires .NET 8 or later in the target.
- Snapshots and threads (section 2) work on .NET Framework 4.5+, .NET Core, and .NET 5+.

**Resolve the binary in this order.**

1. `clrdiag` on `PATH` (installed with `dotnet tool install --global`).
2. `.\.tools\clrdiag.exe` (installed with `dotnet tool install --tool-path .\.tools`).
3. `dotnet run -c Release --` from the ClrDiag source directory.

Every command below writes `clrdiag`. Substitute the form you resolved. With
`dotnet run`, put the flags after the `--` separator.

```powershell
dotnet run -c Release -- --list
```

**Run the liveness check first.** It is cheap and it proves three things at once.
The binary resolves, the config loads, and a 64-bit managed target exists.

```powershell
clrdiag --list
```

`--list` prints a table of PID, process name, runtime, and working set. It prints
at most 30 rows. It exits with code 1 when no managed process is running. When
`processNames` in the config matches nothing, it warns and lists every managed
process instead.

**Target selection.** Every batch command accepts `--pid N`. Without `--pid`, the
tool picks the best match from `processNames`. Pass `--pid` whenever more than one
candidate exists. Ambiguity produces a correct report about the wrong process.

**Working directory.** The tool searches upward from the current directory for
`clrdiag.json`. Use `--root <path>` or `--config <path>` to override that search.
`--root` also sets the project root that derives the debug pipe name.

## 1. Build and serve

### Build

```powershell
clrdiag --build Release
clrdiag --build
```

`--build` runs the same code path as the interactive `b` key. It prints the last
400 build log lines. It exits with code 0 on success and 1 on failure. Without an
argument it uses the first entry of `configurations`, which defaults to `Debug`.

The plain SDK command stays available and needs no config file.

```powershell
dotnet build -c Release
```

### Generate the config

```powershell
clrdiag --init
```

`--init` writes a commented `clrdiag.json` template into the resolved project root.
It never overwrites an existing file. It exits with code 1 and prints the path when
the file already exists.

**The config is read once at startup.** Runtime changes never write back. A
breakpoint added from an editor, a watch added with the `w` key, and a build
configuration changed with the `c` key all live in memory only.

### clrdiag.json fields

Every field is optional. JSON comments and trailing commas are accepted.

| Field | Purpose |
| --- | --- |
| `buildProject` | Build target (.sln or project file). Default: the root .sln, else the .csproj. |
| `buildCommand` | Build executable. Default: `dotnet` for SDK projects, MSBuild from vswhere for legacy projects. |
| `buildArguments` | Build argument array. Supports placeholders. |
| `configurations` | Configurations the `c` key cycles. Default `["Debug", "Release"]`. |
| `serveCommand` | Dev server executable. **Omit it and the `s` and `r` keys stop working.** |
| `serveArguments` | Server argument array. Supports placeholders. |
| `port` | Default port. Default `5000`. `--port` overrides it. |
| `probeUrl` | Health probe URL. Supports `{port}`. Default `http://localhost:{port}/`. |
| `processNames` | Process names to find. Empty means scan every process that loaded the CLR. |
| `appNamespaces` | Namespace prefixes counted as "own code". Empty means approximate by "not a framework type". |
| `reportDirectory` | CSV output directory. Default `.clrdiag-reports`. |
| `dapEnabled` | Enable the debug features. Default `true`. `false` spawns nothing and opens no pipe. |
| `dapAdapterPath` | Path to the netcoredbg executable. |
| `dapBreakpoints` | Startup breakpoint list. Format `"path:line"`. Bad entries are skipped in silence. |
| `dapWatches` | Startup watch expression list. |

Placeholders `{project}`, `{config}`, `{root}`, and `{port}` expand inside
`buildArguments` and `serveArguments`. `{port}` also expands inside `probeUrl`.

**Example: ASP.NET Core self-host.**

```jsonc
{ "serveCommand": "dotnet",
  "serveArguments": [ "run", "--project", "{project}", "--urls", "http://localhost:{port}" ],
  "port": 5000, "appNamespaces": [ "MyApp." ] }
```

**Example: watch an IIS worker process that the tool does not start.**

```jsonc
{ "processNames": [ "w3wp" ], "probeUrl": "https://localhost/health" }
```

### Starting and stopping the server

**There is no `--serve` flag and no `--start` flag.** Server control is
interactive only. A human presses `s` to start, `x` to stop, and `r` to rebuild
and restart. An agent cannot press those keys.

To start a server from an agent, run the project command directly.

```powershell
dotnet run --project .\src\MyApp\MyApp.csproj --urls http://localhost:5000
```

Then attach with `clrdiag --pid <the new pid>` or let `--list` find it.

## 2. Memory leak hunt

Full recipe, cost model, and counter rules: `references/memory-leak-hunt.md`.

Use batch commands. The four steps are a histogram, the feature under test, a
second histogram, and a root chain search.

```powershell
clrdiag --snapshot --top 30 --pid 12345 > .\heap-before.txt
# operate the feature under test
clrdiag --snapshot --top 30 --pid 12345 > .\heap-after.txt
clrdiag --roots "MyApp.Caching.EntryList" --pid 12345
clrdiag --export --pid 12345
```

Compare the two type tables yourself. A batch snapshot holds no baseline, so the
CLI prints no delta column. Rank the types by growth between the two runs.

**Four rules that decide the conclusion.**

- Copy the type name for `--roots` from the `--snapshot` table. The match is
  ordinal and case-sensitive against the full ClrMD type name.
- A root of `StrongHandle`, a static field, or a pinned handle means the objects are
  genuinely held. This is a real leak candidate.
- "沒有任何 GC 根 → 屬於等待回收的垃圾，不是洩漏" means the graph was walked in
  full and nothing holds the type. That is garbage awaiting collection, not a leak.
- "已達 30 秒上限 ...（結果不完整）" means the 30 second budget ran out. The result
  is incomplete. Draw no conclusion.

**Cost.** About 2.2 microseconds per object. 100k objects take 0.6 s, 2.36M take
5 s, and 5.8M take 6 to 8 s. The target is copied once by Windows PSS.

**Snapshots never stop the target.** They use ClrMD plus Windows PSS. The target
never enters a debug state. A snapshot and a debug session are independent and can
run at the same time.

`clrdiag --threads --pid 12345` dumps the managed thread callstacks. Thread state
is inferred from the top frame string and is approximate. **Do not combine
`--snapshot` and `--threads` in one call.** The type table wins and the thread
output is dropped.

With `appNamespaces` empty, the `●` own-code marker also covers third-party
packages. `Gen0 budget` is an allocation budget, not live size, so a large number
is normal. Use Gen1, Gen2, and LOH for real size.

## 3. Debug loop

Full protocol, quoting, and wrapper details: `references/debug-loop.md`.

Requirements are .NET 8 or later, a 64-bit target, and netcoredbg installed. The
adapter resolution order is `dapAdapterPath`, then `PATH`, then the mason path
`%LOCALAPPDATA%\nvim-data\mason\packages\netcoredbg\netcoredbg\netcoredbg.exe`.

**ClrDiag is the only DAP client.** Clients that use `--send` send intent over a
named pipe. netcoredbg does not start at launch. The first breakpoint action spawns
it. The Neovim client is at https://github.com/LizardLiang/clrdiag.nvim.

**Ask the tool for the pipe name. Never compute the hash.**

```powershell
clrdiag --pipe-name
clrdiag --pipe-name --root C:\Users\me\App
```

It prints the bare name, for example `clrdiag-327f19d30c93`. The full path is
`\\.\pipe\` plus that name. The name is the first 12 hex characters of the SHA-256
of the lowercased root path. `--root` must name an existing directory, otherwise
config loading fails with exit code 2.

**Commands.** `setBreakpoint(path, line)`, `clearBreakpoint(path, line)`,
`addWatch(expression)`, `removeWatch(expression)`, `continue`, `stepOver`,
`stepIn`, `stepOut`, `pause`, and `status`. `setBreakpoint` and `addWatch` are
idempotent. `status` is read-only. It returns the current state and changes
nothing.

`status` must exist in the binary that hosts the session. That is the process
running `--dap` or the TUI. The sending binary may be any version, because `--send`
is only a pipe client. An older host replies with the state object plus an `error`
field of `未知指令: status`. The connection still succeeds.

**Quoting differs by PowerShell version. Both forms below are tested.** Write the
`path` with forward slashes. ClrDiag normalizes `/` to `\` on its own, and forward
slashes avoid every backslash trap.

```powershell
# PowerShell 7 (pwsh). Plain single quotes.
clrdiag --send '{"cmd":"setBreakpoint","path":"C:/App/Program.cs","line":42}'

# Windows PowerShell 5.1. Stop-parsing token plus escaped quotes.
clrdiag --% --send "{\"cmd\":\"setBreakpoint\",\"path\":\"C:/App/Program.cs\",\"line\":42}"
```

Do not mix the two forms. PowerShell 7 passes `--%` text literally, backslashes
and all. PowerShell 5.1 strips the inner quotes from a single-quoted argument.
Never write `\\` inside the JSON under PowerShell 7. It arrives as one backslash
and breaks the JSON escape. Check the version with `$PSVersionTable.PSVersion`.

Every command returns one state message. `sessionState` is one of `Idle`,
`Connecting`, `Running`, `Halted`, `Terminated`, or `Failed`.

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

`threadId`, `stopReason`, `location`, and `watchResults` appear only when the
session is halted and the capture finished.

**Paths are compared case-insensitively after slash normalization only.** Nothing
else is normalized, so a relative path matches nothing. A path that does not match
the PDB is the most common silent breakpoint failure. Such a breakpoint reports
`"verified": false` with a reason. Read `verified` after every `setBreakpoint`.

**There is no blocking "wait for halt" command.** `--send` returns the current
state and exits. Say this plainly when a user expects a blocking wait. Two options
remain.

- **Recommended for a long wait.** Run `clrdiag --dap --pid <pid> > .\halt.log` as
  a background process. It streams a block per halt with frames, locals, and watch
  results until Ctrl+C. Set breakpoints with `--send`, exercise the application,
  and read the log.
- **Poll.** Send `status` and read `sessionState` from the reply. Each poll starts
  a new process, so keep the interval above one second.

```powershell
clrdiag --send '{"cmd":"status"}'
```

**Launch under the debugger is interactive only** (`Shift+S`, then `s` or `r`).
Startup path breakpoints need it, because attach arrives too late.

**The `dotnet run` wrapper case.** ClrDiag detects `serveCommand` of `dotnet` with
`run` as the first argument. It starts the wrapper undebugged, finds the direct
child with Toolhelp32, and sends a DAP attach. The gap is single-digit
milliseconds, so a very short startup path can still be missed. Other wrappers,
such as a batch file or a PowerShell script, attach to the wrapper itself. The
workaround is to point `serveCommand` at the built assembly or the apphost, for
example `"dotnet"` with `[ "bin/Debug/net8.0/MyApp.dll" ]`.

## 4. Debug output streaming

```powershell
clrdiag --output
clrdiag --output --pid 12345
clrdiag --output --pid 12345 > .\app-output.log
```

`--output` prints the application `Debug.WriteLine` and `Trace.WriteLine` messages
line by line. Both APIs use the Win32 `OutputDebugString`. Each line carries a
timestamp, the source PID, and the text. `--pid` filters to one process. Ctrl+C
ends the stream.

Redirect to a file to keep the history. The interactive buffer holds 5000 lines and
is lost on exit.

**DBWIN allows one listener at a time.** This conflicts with SysInternals DebugView
and similar tools. The first listener wins. `--output` then exits with code 3 and
prints the reason. The other clrdiag features keep working.

**Release and Testing builds compile `Debug.WriteLine` away.** Only `Trace.*`
messages survive. An empty stream is usually this, not a defect. Check the build
configuration before you investigate further.

**A debugger attached to the target silences this view.** Windows then routes
`OutputDebugString` to the debugger instead of the DBWIN buffer. This is Windows
debug API behavior. There is no fix. Expect a quiet output stream while section 3
holds a debug session on the same process.

## 5. Batch flag reference

| Flag | Argument | Output | Use it when |
| --- | --- | --- | --- |
| `--list` | none | Table of PID, name, runtime, working set. Max 30 rows. | Always first. Liveness check and PID discovery. |
| `--pid` | `N` | none | Any command, to pin the target process. |
| `--port` | `N` | none | Override the config port for the probe and placeholders. |
| `--root` | `path` | none | Set the project root for config search and pipe name. |
| `--config` | `path` | none | Point at an explicit clrdiag.json. |
| `--snapshot` | none | Header, totals, and a type histogram. | Memory leak hunt, step 1 and step 3. |
| `--top` | `N` | none | With `--snapshot`, set the histogram row count. Default 25. |
| `--threads` | none | Managed thread callstacks. Max 20 frames each. | Hangs, deadlocks, thread pool starvation. Run alone. |
| `--roots` | `type name` | Up to 5 GC root chains, or a no-root message. 30 s budget. | Memory leak hunt, step 4. Exact type name required. |
| `--export` | none | Writes CSV to `reportDirectory`, prints the path. | You need the numbers in a file or a spreadsheet. |
| `--build` | `[config]` | Last 400 build log lines. Exit 1 on failure. | Build through the configured command. |
| `--init` | none | Writes clrdiag.json. Exit 1 if it exists. | First-time setup of build or serve integration. |
| `--output` | none | Debug and Trace lines, streaming. Exit 3 if DBWIN is taken. | Watch application logging. Ctrl+C ends it. |
| `--dap` | none | A block per halt: frames, locals, watches. | Non-interactive debugging. Run in the background. |
| `--send` | `json` | One state reply as JSON. | Set breakpoints, watches, and execution control. |
| `--pipe-name` | none | The pipe name for this project root. | Before any pipe work. Never compute the hash. |
| `--render` | none | Renders the nine panels as plain text. | Bug reports and pipelines with no console. |
| `--width` | `N` | none | Render width for `--render`. Default 120. |
| `--height` | `N` | none | Render height for `--render`. Default 40. |
| `--help` | none | Usage text and version. `-h` is an alias. | Confirm the installed version supports a flag. |

`--render` sleeps 3 seconds to collect samples and takes a full snapshot with types
and threads. Expect it to be slow on a large heap.

The interactive dashboard needs a real console. It refuses to start when the output
is redirected. It exits with code 2 and points at the batch modes.

## 6. TUI keys appendix

**Human-operated. An agent cannot use these.** They exist so you can tell a user
which key to press. Use the batch flags in sections 1 to 4 for your own work.

| Key | Action |
| --- | --- |
| `0` `1` `2` | Select the build, serve, or process panel. |
| `3` to `8` | Select the memory, heap, threads, log, output, or debug tab. |
| same digit again | Zoom the panel. `Esc` or the same digit restores it. |
| `b` / `c` | Build / cycle the build configuration. |
| `s` / `x` | Start / stop the server. Needs `serveCommand`. |
| `Shift+S` | Arm or disarm "start under the debugger on the next `s` or `r`". |
| `r` | Stop, build, and start, keeping the monitor history. |
| `n` | Take a managed heap snapshot. |
| `Shift+T` | Refresh the thread stacks only. Much faster than a full snapshot. |
| `d` / `Shift+D` | Set the comparison baseline / clear it. |
| `o` / `/` | Cycle the sort / filter types. `Esc` clears the filter. |
| `f` | Search the GC root chain for the selected type. |
| `e` | Export CSV. |
| `a` | Toggle the auto snapshot, every 5 minutes. |
| `p` | Cycle the process under watch. |
| `g` | Toggle the output scope between the attached PID and all processes. |
| `w` | Add or remove a watch expression. An existing expression is removed. |
| `F5` `F10` `F11` `Shift+F11` `F6` | Continue, step over, step in, step out, pause. |
| `q` | Quit. |
| `?` | Print the full key list into the log tab. |
