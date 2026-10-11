# Testing and evidence

Ember's .NET checks are **console executables**, not test-runner projects. Run the commands below with `dotnet run`; `dotnet test` is not the test entry point. Run from the repository root unless noted otherwise.

All Ember project code, including test code, was written with AI assistance using **OpenAI GPT-6 Astra**. Test results establish only the checks actually executed; they do not imply human code review or an independent audit.

## Choose the right tier

| Tier | Entry point | What it exercises |
| --- | --- | --- |
| Game-free Core | `tests/Ember.Tests/Ember.Tests.csproj` | Collection bounds, statistics, journal lifecycle/backpressure, redaction, report/export formats, hostile HTML, hashes, retention, heap-dump configuration limits, heap-dump assembly (including refused and unfinished requests) and heap-dump impact (walk freeze against a fixed frame journal, coverage-gap split and growth between dumps). |
| Game-free session analysis | `tests/Ember.Session.Tests/Ember.Session.Tests.csproj` | Synthetic long sessions, journal salvage, density-aware merge, usable-prefix coverage, path rejection, incidents and omissions. |
| Game-free real EventPipe | `tests/Ember.Trace.Session.Tests/Ember.Trace.Session.Tests.csproj` | Actual target/worker processes, rotation, cancellation races, parent exit, recovery, locks, diagnostics failure, limits, atomic publication, and requested heap dumps next to a live recording: the stack capture is held back for the walk and attaches again as soon as the walk has been captured, each dump carries its own held-back interval and describes each address once, the release is stamped before the aggregate is published, a concurrent request is refused without releasing the running dump's target, and the raw trace is deleted. A separate `--dumppark-only [arm]` run measures what nothing attached, a steadily attached segment, rotating segments, the target's own forced collections and the two production holds (a segment outliving the capture, and one stopping itself inside it) each do to the walk, and asserts which of those arms carry repeated walks, that collapsing them leaves a coherent heap, and that the segment which was already capturing was left to finish its interval. A `--dumpfail-only` run forces a dump that fills its buffer and exceeds its object budget at once and asserts the failure names the buffer first while still reporting the object budget, that the failed aggregate keeps the same reason, and that its raw trace is still deleted. It also runs a dump whose one-second reduction budget cannot cover its walk: the failure has to name the reduction stage, record `time-budget`, report the trace size and the walked count it measured before the deadline, send the reader to `HeapDumpTimeoutSeconds` rather than to a limit that cannot help, and still delete its raw trace. A third case asks a running dump to stop over its standard input — the channel the supervisor uses before it kills a worker over budget — and asserts that it records the interruption and publishes no heap figures. |
| Game-free one-shot trace | `tests/Ember.Trace.Integration/run.ps1` | Real one-shot capture, stop/cancel protocol, byte limit, invalid PID, symbols and independently recomputed hotspot counts. |
| Game-free browser | `viewer/verify.cjs` | Synthetic old/new/empty reports, filters, time ranges, imports/exports, hostile text, keyboard behavior, layout and stress fixtures. |
| Game-required build | `scripts/build.ps1` or `scripts/build.sh` | Game API references and complete mod/worker packaging; no graphical client is launched. |
| Graphical Windows | `scripts/test-session-client.ps1` | Real client lifecycle, loopback multiplayer or singleplayer, session exports, frames and GPU data. |

The synthetic 30-minute journal test is not a 30-minute gameplay run. EventPipe checks require diagnostics enabled and permission to attach to their own targets even though they do not need Vintage Story.

## Game-free .NET checks

Requirements: .NET 10 SDK and initial NuGet restore access.

```text
dotnet run --project tests/Ember.Tests/Ember.Tests.csproj -c Release
dotnet run --project tests/Ember.Session.Tests/Ember.Session.Tests.csproj -c Release
dotnet run --project tests/Ember.Trace.Session.Tests/Ember.Trace.Session.Tests.csproj -c Release
```

The session supervisor project builds its worker through a project reference. Run the full suite for capture/lifecycle/protocol changes; its `--atomic-only` option is a focused publication diagnostic, not equivalent coverage:

```text
dotnet run --project tests/Ember.Trace.Session.Tests/Ember.Trace.Session.Tests.csproj -c Release -- --atomic-only
```

A focused heap-dump parking measurement runs one deterministic target heap against seven attachment patterns (nothing attached, one steady segment, two-second and five-second segment rotation, the target's own forced gen2 collections with nothing attached, and the two production holds — new segments held back while the segment that is already capturing is left alone, once with a segment that outlives the capture and once with one that stops itself inside it), asserts that the two churning arms are the ones carrying repeated walks, that collapsing those copies leaves a coherent heap of one walk's size, that the hold is released before the dump is published and covers the capture rather than the reduction, and that the segment which was already capturing ran out its own interval, and prints a comparison table; each arm copies its aggregate to `test-runs/parking/`:

```text
dotnet run --project tests/Ember.Trace.Session.Tests/Ember.Trace.Session.Tests.csproj -c Release -- --dumppark-only
```

Core writes `test-runs/core/results.json` and export fixtures. Session analysis and supervisor checks print results and use temporary scratch directories that they attempt to remove. Save console output when submitting evidence; do not assume every suite writes a durable results file.

For the standalone capture path, use PowerShell 7.4 or newer:

```powershell
pwsh -NoProfile -File tests/Ember.Trace.Integration/run.ps1
```

`-Smoke` selects normal capture, cancel and invalid PID only. The full harness also tests early stop and byte limits. It builds its own target and worker, saves per-run results/logs under `test-runs/trace/`, and cleans up owned processes. See its [harness guide](../tests/Ember.Trace.Integration/README.md) for assertions and timeouts. Do not run the synthetic workload executable directly as a substitute for the harness.

## Browser checks

The repository also checks local Markdown links, matching package/build versions and tracked-file hygiene with `node scripts/check-repository.cjs`. It operates on Git's tracked file list; stage new files before running it locally. The [CI workflow](../.github/workflows/ci.yml) runs these checks and the synthetic viewer on Ubuntu, plus Core/session/EventPipe checks on Windows. This does not establish Linux game compatibility.

Use Node.js 22 or newer and a local Chromium browser; no npm install is required.

```text
node viewer/verify.cjs
```

`CHROME_PATH` overrides browser discovery. The runner uses headless Chromium and a temporary loopback HTTP server. It includes the 400,000-sample / 200,000-frame stress fixture, old/new/empty report shapes, text-only hostile payload checks, selection exports, baseline comparisons, keyboard focus and mobile-width layout. Run Core checks too after changing the embedded template or export contract.

For an additional **Windows-specific real-report check**:

```text
node viewer/review.cjs path/to/extracted/report.json
```

This script currently hard-codes the standard Windows Chrome installation location and uses `LOCALAPPDATA`; it does **not** honor `CHROME_PATH`. It expects a populated report with incidents/methods suitable for its interaction assertions, not an arbitrary empty report. It writes `test-runs/viewer-workbench/report.html` and screenshots/verification under `.impeccable/review/workbench/` (`EMBER_REVIEW_NAME` changes the final directory name). Those outputs embed real report data and local paths; inspect/redact them before sharing. Browser viewport emulation is not real mobile-device testing.

## Graphical Windows checks

Requirements: an interactive Windows desktop with working graphics, Vintage Story 1.22.7, .NET 10, PowerShell, and existing client settings under the normal game data directory. Build a fresh package first:

```powershell
./scripts/build.ps1 -GamePath 'C:\path\to\Vintagestory'
pwsh -File scripts/test-session-client.ps1 -GamePath 'C:\path\to\Vintagestory' -Package artifacts/emberdiagnostics-1.1.2.zip
```

Use the ZIP version produced by your build. The session harness currently accepts **1.1.x** package metadata. Defaults exercise `manual`, `profile`, `leave`, `shutdown`, `cancel`, `immediate`, `double-start`, and `reconnect` in loopback multiplayer. For focused singleplayer or long-session evidence:

```powershell
pwsh -File scripts/test-session-client.ps1 -GamePath 'C:\path\to\Vintagestory' -Package artifacts/emberdiagnostics-1.1.2.zip -Mode Singleplayer -Scenario leave
pwsh -File scripts/test-session-client.ps1 -GamePath 'C:\path\to\Vintagestory' -Package artifacts/emberdiagnostics-1.1.2.zip -Mode Multiplayer -Scenario manual -DurationSeconds 1800
```

Long runs default to 60-second segments and ordinary 900-second/120,000-frame rolling-history settings. The harness builds `tests/Ember.Session.GameTests` as a **game-loaded driver mod**, not a console test. It creates isolated data directories under `test-runs/session-game/`, copies account-bearing client settings privately for the launch, and removes copied settings in cleanup. Multiplayer uses a loopback dedicated server with no Ember, authentication verification disabled and an ordinary player role; it does not test external public-server authentication. It rejects unexpected extra mods in the installation's server Mods directory.

Treat generated data, settings backups, logs and reports as private until inspected. A force-killed harness cannot guarantee cleanup. Share a sanitized verification summary and package hash, rather than the entire run tree.

`scripts/smoke-client.ps1` and `tests/Ember.Integration` are the older graphical smoke path. The script still contains a 1.0.0 package fallback; pass an explicit package if using it. Prefer the session harness for current recording lifecycle changes. Neither graphical driver is included as an executable test in `Ember.slnx`.

## What to record with a change

- Exact commands, exit/result summaries, OS and SDK/runtime versions; game version and package SHA-256 for game checks.
- The reproduction and the invariant tested, including a regression fixture for a fixed behavior where practical.
- For viewer changes, browser version, relevant screenshots, empty/large/hostile-data behavior and keyboard results.
- For recording/export changes, stop/cancel/recovery behavior, coverage gaps, counts/limits and manifest checks. Use the [package/export checklist](verification.md#package-inspection) for release work.
- Checks not run and the reason. Do not relabel historical results as results for a new commit or ZIP.

For performance claims, compare settled baseline, collection, active recording and export on the same repeatable workload/settings; include target **and worker** CPU/memory and repeated frame distributions. Sequential frame averages with injected stalls are not a controlled overhead benchmark.

## Historical evidence is version-specific

[Verification history](verification.md) records the **1.1.0** graphical lifecycle and actual 30-minute soak: 1,800.216 seconds, 107,384 frames, 30 completed segments and approximately 98.975% usable stack interval coverage, with the remaining gaps disclosed. The **1.1.1/1.1.2** entries concern viewer refinements exercised against that existing capture, browser checks and Core regressions; they are not new gameplay soaks of those versions.

Historical `test-runs/` and `.impeccable/` paths identify local evidence, which need not be distributed in a public checkout. The verification record does not establish Linux/macOS support, public-server authentication coverage, or controlled profiler overhead. New changes need their own relevant test evidence.
