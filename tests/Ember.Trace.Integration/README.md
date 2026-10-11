# Ember.Trace integration harness

This harness builds and profiles a separate .NET 10 process that produces recursive managed stacks, allocations, exceptions and lock contention. It checks the worker's real EventPipe output and stdin/stdout protocol.

## Run

Requires the .NET 10 SDK and PowerShell **7.4 or later**. Run the worker with the same permissions as its target, with .NET diagnostics enabled.

```powershell
# Full suite: normal, stop, cancel, byte limit and invalid PID.
pwsh -NoProfile -File tests/Ember.Trace.Integration/run.ps1

# Relocation/quick smoke: normal, cancel and invalid PID.
pwsh -NoProfile -File tests/Ember.Trace.Integration/run.ps1 -Smoke

# Select scenarios from a PowerShell session.
& ./tests/Ember.Trace.Integration/run.ps1 -Modes stop,limit
```

The script resolves all paths relative to its own location. An absolute path to `run.ps1` works from any current directory, including paths containing spaces.

Every invocation builds **Target first, then Ember.Trace**, sequentially in **Release**. Build-server reuse/shared compilation are disabled for these builds. Build artifacts, profiles, maps, process IDs, stdout/stderr logs and `results.json` are placed under a new directory:

```text
test-runs/trace/<UTC timestamp>-<unique suffix>/
  build/                  # All build outputs/intermediates, including Core's project-reference build
  normal/profile.json
  stop/profile.json       # Full suite only
  limit/profile.json      # Full suite only
  *.stdout.log
  *.stderr.log
  processes.log
  results.json
```

Generated test output is never written into this source directory. Each invocation retains its own artifacts for diagnosis and leaves previous runs alone. An assertion/build failure makes the script fail with a nonzero exit code.

## Checks

- **normal:** five-second capture, `READY`, successful exit and `DONE`.
- **stop:** send `stop` after 700 ms; verify capture duration is under four seconds and a valid profile is published.
- **cancel:** send `cancel` after 700 ms; require exit **2**, no `DONE`, no profile and no retained raw trace.
- **limit:** request 30 seconds with a 16-MiB raw trace budget; require `sizeLimitReached` and verify the hard byte bound.
- **invalid-pid:** pass PID **0**, which is deterministically invalid; require exit **1**, a PID diagnostic on stderr, no protocol success line, no profile and no trace artifacts. This tests argument failure without attaching to an unrelated process.

Heap dumps are a separate worker mode (`--heapdump-private`) and are covered by the session tier, which requests dumps against a real target process next to a live recording, including the stack-capture pause that keeps the walk coherent; this one-shot harness does not exercise them.

Successful profiles must contain full method signatures, normalized module ownership with **both** mapped mod IDs, root-first stacks, capture-relative timestamps, managed and external sample classifications, sampled allocation bytes, exception types without exception messages, and contention durations. The runner independently recomputes every hotspot's inclusive, self and managed-self counts, including repeated recursive frames. It also checks frame references and the 200,000-sample limit.

## Process lifetime and time bounds

Only process objects launched by this invocation are tracked and cleaned up in `finally` blocks. The target's printed PID must equal its launched process ID. Cleanup targets those owned process trees (including the worker's private parser); there is no process-name-wide or arbitrary-PID termination. Every owned `Process` object is disposed.

- Build timeout: **180 seconds per project**.
- Target PID / worker `READY` read: **15 seconds**.
- Worker exit: **150 seconds** for capture/stop/limit, **30 seconds** for cancel, **15 seconds** for invalid PID.
- Redirected stream completion and forced-cleanup exit: **5 seconds** each.

Normal completion, assertion failure and timeout all run cleanup. As with any script, force-killing the PowerShell host prevents its `finally` blocks from running. The target's CPU/exception/allocation workload continues until runner cleanup; it is intentionally not a standalone interactive app.
