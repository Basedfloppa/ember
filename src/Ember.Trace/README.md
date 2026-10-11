# Ember.Trace worker

Build with `dotnet build src/Ember.Trace/Ember.Trace.csproj`. This external .NET 10 worker attaches to the **target** process through its diagnostics IPC endpoint.

```powershell
dotnet src/Ember.Trace/bin/Debug/net10.0/Ember.Trace.dll --pid 1234 --seconds 30 --output C:\captures\work-1234 --max-mb 256 --map C:\captures\assembly-mod-map.json
```

All five arguments are required; optional `--max-samples 1..200000` lowers the retained stack-sample limit. Duration is 5–300 seconds, raw trace budget is 16–1024 MiB, and output must be an absolute directory without an existing `profile.json`. The assembly map is a JSON object, for example `{"Example.Mod":["example","shared-owner"]}`. Matching is case-insensitive for module names; paths and `.dll`/`.exe` suffixes are normalized. All owners are retained.

## Worker protocol

- `READY` on stdout after EventPipe starts and stream copying is active.
- Write `stop` followed by a newline to stdin to finish early, run rundown, and produce a profile.
- Write `cancel` followed by a newline (or send Ctrl+C) to stop without publishing a profile; exit code **2**. Closing stdin alone does not stop capture.
- `DONE` on stdout once `profile.json` is atomically published, followed by exit code **0**.
- Failures go to stderr and exit **1**. Stdout is reserved for the protocol.

## Data and limits

The worker enables the sample profiler and CLR allocation ticks, exceptions, contention, JIT and loader events, with stop-time rundown. It drains the stream concurrently with stop, including when the disk byte cap is hit. Capture stops at 75% of the byte budget to leave room for rundown; the file never exceeds the hard budget. A truncated trace is flagged and parsed best-effort. Raw trace and TraceLog artifacts live in a unique temporary subdirectory and are deleted on completion, cancellation or failure.

TraceLog resolves managed full method signatures after rundown. Sample stacks are root-first, timestamps use trace session start, and recursive frames count only once per sample in inclusive hotspots. Managed/external is the profiler's execution classification, **not exact on-CPU time**. Allocations aggregate `AllocationAmount64` by the tick's type (sampled allocation amounts, not exact per-type totals). Contention sums `DurationNs / 1,000,000`. Only exception type names are exported.

For conversion-limited or truncated traces, `DurationSeconds` remains the nominal capture duration and `UsableDurationSeconds` bounds stack coverage to the last actually processed resolved stack event, observed before reservoir selection. No resolved events means zero usable coverage. Completed segment endpoints use that usable prefix, so an unconverted or truncated tail is not credited as covered time.

Retained profiles are bounded to 200,000 samples, 8,000,000 stack entries, 100,000 unique frames, 512 frames per stack and 20,000 distinct types per aggregate. Limits are recorded in the profile notes/counters. TraceLog conversion retains at most 2,000,000 events and still reads rundown. Its child process has a 90-second deadline, monitored 1.5-GiB working-set budget and temporary-disk budget of `max(256 MiB, 4 × --max-mb)`. Budget monitoring occurs every 100 ms. Start has a 10-second deadline; stop/drain has a 20-second deadline (5 seconds when already cancelled).

## Session recording

```powershell
dotnet src/Ember.Trace/bin/Debug/net10.0/Ember.Trace.dll --session C:\captures\session-id
dotnet src/Ember.Trace/bin/Debug/net10.0/Ember.Trace.dll --recover C:\captures\session-id
```

The game creates `request.json` (`SessionRequest`), `seed.json` (`DiagnosticReport`) and a compact, flushed `batches.ndjson` journal. On normal stop it flushes the journal and writes optional `final.json`, then atomically publishes `journal.closed.json` and `stop.json` (`SessionStop`). The worker publishes `stop.json` itself for autonomous duration/disk/process-exit stops and waits for `journal.closed.json` (or parent exit). It writes atomic `session.json` and `status.json` snapshots, then a `result.json` (`SessionResult`) when export finishes, fails, or is cancelled.

- `READY` is emitted once capture starts, or after attachment fails and monitoring-only recording continues. Session success emits `DONE <absolute-zip-path>` and exits **0**. Cancellation commits a `cancelled` result with no ZIP, cleans recording payloads, emits `CANCELLED`, and exits **0**. Failure exits **1** and retains recovery data.
- Session `stop` exports retained data; `cancel` or Ctrl+C discards the recording without changing previous exports. Startup receives stop/cancel commands too. A closed stdin/stdout pipe does not prevent independent finalization.
- Capture rotates at `SegmentSeconds`; each segment retains at most `SegmentSamples`. A private segment worker emits `CAPTURED` after stop/rundown drains, allowing the next capture to begin while the old segment parses. There are at most two segment workers. Parser backpressure, rundown and attach/retry gaps are not counted as stack coverage.
- Three consecutive capture failures disable stack capture for the rest of the session; journaled monitoring continues. Retry delays and failures are retained in metadata rather than repeatedly printed.
- Stop triggers are `stop.json`, an stdin command, original-process exit (PID **and** start time), `MaxSeconds`, or the disk budget. The total recording-directory budget includes journal, profiles, raw traces and conversion files, is capped at **2 GiB**, and is checked every 200 ms with a metadata reserve. Each raw trace also has its own hard byte cap. Parser workers are limited to 90 seconds and a monitored 1.5 GiB working set. Automatic stops publish `finalizing` and wait up to ten seconds for the game's journal-close acknowledgment.
- Each successfully parsed segment atomically commits and flushes `profile.json` and its completed `segment.json`, then removes its raw trace and parser artifacts before reporting completion. Successful partial-trace recovery uses the same lifecycle. Failed parsing retains raw recovery files. Profiles/checkpoints and journal/seed/final payloads remain until a successful ZIP and committed `result.json`; failed export preserves those recovery payloads. Request, result, status, session and stop metadata remain after success.
- `--recover` never attaches to the game. It rejects a live original parent unless `stop.json` exists, holds an exclusive supervisor lock, discovers retained segment checkpoints, and attempts bounded conversion of partial raw traces when disk allows. An orphaned segment notices supervisor death and stops itself; recovery also respects that segment's exclusive lock. Incomplete traces are best-effort recovery, not crash-perfect recordings.
- `OpenFolderWhenFinished` opens the export directory only after successful export.

## Heap dumps

```powershell
dotnet src/Ember.Trace/bin/Debug/net10.0/Ember.Trace.dll --heapdump-private C:\captures\session-id\heapdumps\1
```

One isolated process per request. It reads `job.json` (`HeapDumpJob`), attaches with the measured GC heap-dump mask (`0x1980009` = `GCHeapSnapshot | Loader`; allocation figures then come from the GC allocation ticks that mask already carries, because .NET 10 emits nothing for the `GCSampledObjectAllocation*` keywords a dump used to add), captures the walk, reduces it to per-owner retained bytes and writes `heapdump.json` (`HeapDump`). **The raw trace is deleted in a `finally`, on success and on failure alike**, because it is a binary object graph that cannot be redacted. Stdout carries `READY`, `CAPTURED`, `DONE`; a `stop` line on stdin or the parent's `cancel` aborts it.

Limits travel in the job, never from session budgets: `TimeoutSeconds` (default 60) bounds the walk itself and starts when the worker starts, so time spent holding new stack capture back is not charged to it, `BufferMegabytes` (default 4096, ceiling 8192) is the EventPipe circular buffer, and `MaxNodes` (default 40,000,000) bounds the walked objects the analysis will read, and a repeated walk counts every copy, so holding new segments back is what keeps a churning dump under it. The timeout is the point at which the worker stops the session, not the end of the capture: it then drains until the stream goes quiet, so a real capture can exceed the budget — the first client dump took **63.8 s** against a 60 s budget, and the interval it held the stack capture for was 70.8 s once a 6.3 s reduction was added — that dump predates the release at the capture, which now ends the hold before the reduction starts. A dump is `complete` only when the node/edge accounting balances, `reachable + unreachable == shallow total`, and the trace did not reach its buffer; otherwise every size is a lower bound on a partial heap and the note says so. A dump that stops before it produces an aggregate keeps both facts that stopped it: the failure names the buffer first when the trace filled it (with the size to raise) and still reports the object budget the same walk exceeded, and that text is written into the failed aggregate's notes before the exception, because the buffer is the one limit a larger object budget cannot substitute for. The three phases are bounded separately in `HeapDumpBudget`, so a dump that overruns records why instead of being killed with nothing: the capture may run for `TimeoutSeconds + 30` (ten seconds of draining plus twenty of session teardown), the reduction then gets `clamp(TimeoutSeconds, 45, 120)` seconds of its own — armed when it starts, so a capture that finished early does not lend it time it never declared — and the supervisor's kill sits `PublishGraceSeconds` behind both and asks the worker to stop first, which makes it a backstop for a worker that cannot stop itself rather than the dump's deadline. The reduction is interruptible: the deadline is checked in the walk's bulk callbacks and once per dominator sweep, and what a stopped dump keeps is the stage it reached, the trace size and how many objects it had read. `AnalysisSeconds` in the job overrides the derived reduction budget. A failed dump keeps no heap figures at all — no object count, no byte totals, no tables — because a reduction interrupted part way through the dominator pass has already counted them; the cause travels as `FailureKind` (`buffer`, `node-limit`, `time-budget`, `no-walk`, `interrupted`, `supervisor-gone`, `not-started`, `refused`) so the summary, the findings and the viewer name the limit that actually stopped it rather than matching message text.

The buffer is the limit a walk can outgrow, and it holds what this worker has not read yet: **when it fills, the oldest events are discarded**, and a walk emits its roots and the objects they point at first, so a saturated buffer costs exactly the part that makes the rest readable. The first real client dump (a 1.43 GiB modded heap) emitted 1,045.6 MiB of trace against the old 1024 MiB default cap, overwrote the beginning of the walk and reported 62% of the heap as unreachable with no strong root. Reproduced against one synthetic target and one heap with only the cap changed: 858.2 MiB of trace at a 1024 MiB cap kept every event and reported 0.00% unreachable, while 824.1 MiB at an 840 MiB cap and 502.1 MiB at a 512 MiB cap each lost the walk's beginning and reported 100%. `HeapDumpBuffer.Saturated` therefore treats a trace at or above **90%** of the cap as truncated. The ceiling is high and the default generous because the ceiling costs nothing until the burst arrives: the runtime allocates ring buffers as they fill, and the same target measured the same peak RSS (1.65 GiB, +857 MiB over its baseline) at 1024, 2048 and 4096 MiB caps. The aggregate carries `bufferMegabytes` so a reader can see what the trace had to fit in.

The walk is emitted by the runtime once per EventPipe session that attaches while it is being captured, and that walk is broadcast to every session that asked for one, which is why the supervisor holds new stack segments back while the walk is captured and leaves the segment that is already attached alone; `spanMilliseconds` is the first-to-last event span and is **not** a measured freeze. Attach cycles therefore make one dump carry the same unchanged heap many times over — measured on a fixed 300k-object heap, fifteen two-second segments produced 4,553,580 walked objects carrying only 303,949 distinct addresses. Those copies are collapsed onto the walk that arrived first before anything is measured, so the aggregate describes the heap once (**303,949** objects, **0.13%** unattributable, `complete` true) and keeps what it carried as `walkedNodeCount`; what the copies still cost is the trace (**245.2** MiB against **16.4** MiB), the capture (**42.7** s against **9.2** s) and the span (**33.2** s against **18** ms). A session that merely stays attached costs nothing measurable (**0.09%** unattributed against **0.04%** with nothing attached), and neither does the target's own churn: **90** forced blocking gen2 collections inside one capture window left one walk, a **19 ms** span and **0.06%**. Measured shares: **0.05-0.06%** for a large or fixed heap with nothing else attached to the process, **5.9%** and **6.3%** for two dumps in one recording that held new capture back, and **0.13%** once the copies from a churning capture are collapsed. The hold costs the recording whatever that interval would have covered: it ends when the walk has been captured, so it is the capture and not the capture plus the reduction — measured **12.9-13.1 s** held for a **12.8 s** capture, against **10.0 s** for a **9.2 s** capture and a **0.3 s** reduction when it covered the reduction too — and because the segment that is already attached keeps running, the interval is not by itself a coverage gap. The report says all of it in the aggregate, in the session notes and in the event timeline. Per-owner sizes are a partition by holder root: summing retained per module instead would count ancestor chains repeatedly.

The game requests a dump by writing `heapdump.request.json` (`HeapDumpRequest`). The supervisor consumes it by moving it into `heapdumps/<index>/`, writes `job.json`, then holds new stack segments back and starts the worker on the next poll: `heapdump.status.json` (`HeapDumpStatus`) reports `pausing` first and `running` once the walk is being captured. No new segment attaches while the walk is in flight, the segment that is already capturing is left running, and the release at `CAPTURED` — before the reduction, which only reads a closed trace — is what lets one attach again. The same file reports every refusal — no recording, a dump already in flight, a monitoring-only recording, or insufficient free space. Refused and failed requests are also written into their work directory so the report lists them. The dump work directory is **excluded from the recording's disk budget**; a committed export removes it with the rest of the recording payloads.

## Integration checks

```powershell
dotnet run --project tests/Ember.Trace.Session.Tests/Ember.Trace.Session.Tests.csproj
pwsh -NoProfile -File tests/Ember.Trace.Integration/run.ps1
```

Session checks exercise real rotation and sample limits, journal/context assembly, cancellation, process death with broken stdout, duplicate-supervisor exclusion, stopped-session and orphan recovery, failed-export preservation/retry, disk limits, maximum duration, and PID-reuse protection.

### Original development harness

The temporary harness in `.research/ember-trace-integration` runs a separate .NET target with recursion, allocations, exceptions and contended locks:

```powershell
dotnet build .research/ember-trace-integration/Target.csproj
pwsh -NoProfile -File .research/ember-trace-integration/test.ps1
```

It checks timed capture, early stop, cancel, byte-limit capture, real symbol names, multiple mod owners, both sample classifications, relative timestamps, recursive inclusive counts, allocation/exception/contention aggregates, absence of exception messages, and temporary artifact cleanup.
