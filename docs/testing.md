# Testing and evidence

Ember's .NET checks are **console executables**, not test-runner projects. Run the commands below with `dotnet run`; `dotnet test` is not the test entry point. Run from the repository root unless noted otherwise.

All Ember project code, including test code, was written with AI assistance using **OpenAI GPT-6 Astra**. Test results establish only the checks actually executed; they do not imply human code review or an independent audit.

## Choose the right tier

| Tier | Entry point | What it exercises |
| --- | --- | --- |
| Game-free Core | `tests/Ember.Tests/Ember.Tests.csproj` | Collection bounds, statistics, journal lifecycle/backpressure, redaction, report/export formats, hostile HTML, hashes and retention. |
| Game-free session analysis | `tests/Ember.Session.Tests/Ember.Session.Tests.csproj` | Synthetic long sessions, journal salvage, density-aware merge, usable-prefix coverage, path rejection, incidents and omissions. |
| Game-free real EventPipe | `tests/Ember.Trace.Session.Tests/Ember.Trace.Session.Tests.csproj` | Actual target/worker processes, rotation, cancellation races, parent exit, recovery, locks, diagnostics failure, limits and atomic publication. |
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
