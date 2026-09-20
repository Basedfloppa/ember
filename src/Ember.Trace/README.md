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
