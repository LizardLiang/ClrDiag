# Memory leak hunt with clrdiag

Read `SKILL.md` section 0 first. Resolve the binary and run `clrdiag --list`.

Use batch commands. Do not describe the interactive key sequence to a user who
asked you to do the work.

## Step 1: take a baseline histogram

```powershell
clrdiag --snapshot --top 30 --pid 12345
```

The output starts with a header line. It reports the PID, the CLR version, the
timestamp, and the capture duration. The next line reports object count, total
size in MB, and segment count. A walk warning prints when the heap walk was
incomplete. The table lists the largest types by size.

**Do not combine `--snapshot` and `--threads` in one call.** The type table wins
and the thread output is dropped. Run the two commands separately.

## Step 2: operate the feature under test

Exercise the suspected code path. Send the requests, open the page, or run the job.

## Step 3: take a second histogram and compare

```powershell
clrdiag --snapshot --top 30 --pid 12345
```

Compare the two type tables yourself. A batch snapshot holds no baseline, so the
CLI prints no delta column. The `Δ MB` and `Δ count` columns exist in the
interactive heap view only, after a human sets a baseline with the `d` key.

Rank the types by growth between the two runs. The types that grew the most are
the candidates. Save both outputs to files when the run is long.

```powershell
clrdiag --snapshot --top 50 --pid 12345 > .\heap-before.txt
clrdiag --snapshot --top 50 --pid 12345 > .\heap-after.txt
```

## Step 4: find the GC root chain

```powershell
clrdiag --roots "MyApp.Caching.EntryList" --pid 12345
```

**The type name must match exactly.** The comparison is ordinal and
case-sensitive against the full ClrMD type name. Copy the name from the
`--snapshot` table. Do not shorten it and do not guess the generic syntax.

The search walks the reachable graph from every GC root. It prints at most 5
paths. It stops after 30 seconds. Each path prints the root description first,
then the reference chain, one `→` line per step.

### Interpretation rules

These rules stop wrong conclusions. Apply them literally.

- A path that ends in `StrongHandle`, a static field, or a pinned handle means the
  objects are genuinely held. This is a real leak candidate.
- The message "沒有任何 GC 根 → 屬於等待回收的垃圾，不是洩漏" means the whole
  reachable graph was walked and nothing holds the type. That is garbage awaiting
  collection. **It is not a leak.**
- The message "已達 30 秒上限 ... （結果不完整）" means the budget ran out. The
  result is incomplete. Draw no conclusion. Narrow the type or retry on a smaller
  heap.

Never report "no leak" from an incomplete search. The two messages exist to keep
those cases apart.

## Step 5: export the numbers

```powershell
clrdiag --export --pid 12345
```

`--export` takes one snapshot and writes a CSV into `reportDirectory`, which
defaults to `.clrdiag-reports`. It prints the written path and the type count. The
export covers types only, never threads.

## Snapshot cost model

Measured cost is about 2.2 microseconds per object.

| Object count | Approximate duration |
| --- | --- |
| 100,000 | 0.6 s |
| 2,360,000 | 5 s |
| 5,800,000 | 6 to 8 s |

The target process is copied once by Windows PSS. Budget for that pause before you
snapshot a production process.

## Snapshots never stop the target

Snapshots use ClrMD plus `CreateSnapshotAndAttach`. The target never enters a debug
state. A crash of clrdiag never takes the target with it. Snapshots and a debug
session are independent paths. Both can run at the same time.

## Thread callstacks

```powershell
clrdiag --threads --pid 12345
```

Each thread prints the OS thread id, the managed thread id, an inferred state, and
a pending exception when one exists. It prints at most 20 frames per thread.

**Thread state is approximate.** It is classified from the top frame string, for
example `lock-wait`, `db`, or `network`. ClrMD 4 removed `BlockingObjects`, so the
actual blocking object cannot be read. Treat the state as a hint, not as evidence.

Use `--threads` for a hang, a deadlock, or thread pool starvation. Use `--snapshot`
for growth in memory.

## The "own code" marker

The `●` marker means own code. With `appNamespaces` empty, the marker means "not a
framework type", which also covers third-party packages. Fill in the prefixes to
mark only your own assemblies.

## Reading counters

`Gen0 budget` comes from the `.NET CLR Memory\Gen 0 heap size` counter. It reports
the gen0 allocation budget, not the live size. A large number is normal. Use Gen1,
Gen2, and LOH for real size.

Request rate is not a counter. Many machines expose no `ASP.NET Applications`
instance. The serve panel therefore shows the status code and latency of a
`probeUrl` probe every 5 seconds.
