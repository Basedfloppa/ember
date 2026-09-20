# Build and verification

These are development-run records, not certification or an independent human audit. Code, tests, documentation and agent reviews were produced with AI assistance using OpenAI GPT-6 Astra; see [AI_USAGE.md](../AI_USAGE.md). Historical local output paths identify retained development evidence but are excluded from the public repository. Curated screenshots are available under [docs/images](images/README.md); reproducible commands and prerequisites are in the [testing guide](testing.md).

## Initial public repository preparation

The public-source preparation adds documentation, curated images, community templates, CI, maintainer/repository metadata and packaged documentation. It does not change the recording or viewer algorithms. Before the initial push, local Windows validation passed:

- Core **24/24**, session analysis **9 checks**, and external supervisor **58 assertions**, rerun with .NET 10.
- The complete synthetic viewer suite, including hostile text, scoped exports, keyboard/mobile behavior and the 400,000-sample / 200,000-frame fixture.
- Tracked-document link/version/file-hygiene checks; YAML parsing, PowerShell parsing and Bash build-script syntax.
- Package build with zero warnings/errors. The existing, unpublished 1.1.2 ZIP is rebuilt to include the public documentation, AI disclosure and bbauti/repository metadata; previous local build evidence is archived privately. Its checksum is generated alongside the ZIP.
- A separate AI-agent review of the staged repository found no material publication blockers. It was a source/documentation review, not another test execution or a human audit.

There is no new gameplay soak for this preparation. Local results do not imply that a hosted GitHub Actions run has passed; consult the workflow's actual status for the relevant commit.

## Version 1.1.2 recording-first refinement — 2026-09-19

This viewer pass implements the user's request for a less template-like diagnostic interface. Capture date, duration, frame count and coverage replace the introductory slogan; flat incident rows and underlined phase controls reduce repeated outlines; sampling explanations move to a labelled disclosure; controls and the flame graph move higher in the desktop viewport.

- `node viewer/verify.cjs`: **passed**, including existing report sections, range/source alignment, keyboard controls, baseline comparison, scoped JSON export, hostile-text handling, empty reports and the 400,000-sample / 200,000-frame stress case; zero browser errors.
- `EMBER_REVIEW_NAME=recording-first node viewer/review.cjs <report.json>`: **passed** using the actual 30-minute capture in a direct offline HTML file. Desktop 1440×1050, wide 1920×1080 and mobile 390×844 screenshots are under `.impeccable/review/recording-first/`. Checks cover document overflow, visible active incidents, in-place marker selection/focus, mobile flame–inspector–results order, method-detail navigation, repeated pagination focus through both boundaries, reduced motion, and sampling-detail access to measurement/export semantics. The flame graph begins above 750px at both desktop widths.
- `dotnet run --project tests/Ember.Tests/Ember.Tests.csproj -c Release`: **24/24 passed**, including embedding/export and manifest round trips.
- Independent visual/source review of all six recording-first captures: **ship**, no material fixes requested for this refinement.
- No new gameplay capture was needed to exercise this viewer refinement; the original 30-minute session is the same historical recording documented below. Version 1.1.1 release artifacts are retained separately.

## Version 1.1.1 investigation viewer — 2026-09-19

The user-selected Expediente/Laboratorio combination is promoted into the embedded production viewer. The default Investigate workspace combines an incident queue, session timeline, context/phase controls, filtered stacks, flame zoom, method inspection, evidence disclosure, and scoped JSON export.

- `node viewer/verify.cjs`: **passed** legacy/new/empty reports, all existing diagnostic sections, shared time-origin and end-exclusive ranges, dedicated incident sample sources, baseline differences, hostile text rendering, 400,000-sample / 200,000-frame responsiveness, keyboard focus after flame/method/phase activation, scoped selection exports and zero browser errors.
- `node viewer/review.cjs <report.json>`: **passed** with the actual 30-minute capture `07444bd0fb854fe682df35032966599b`, using the production template and a direct local-file URL. Desktop 1440×1050, wide 1920×1080 and mobile 390×844 had no document overflow; selected incidents remained visible in the queue, timeline-marker keyboard activation retained focus, reduced motion was respected, and no console/runtime errors were recorded.
- `dotnet run --project tests/Ember.Tests/Ember.Tests.csproj -c Release`: **24/24 passed**, including the newly embedded HTML's export, hostile-data handling and manifest round trip.
- Viewer evidence: `.impeccable/review/workbench/verification.json` and screenshots; standalone real-data preview: `test-runs/viewer-workbench/report.html`.
- Independent finish review: mobile inspector ordering, pagination focus through both boundaries, and hotspot-column readability were corrected; the reviewer scored all three fixes resolved. Real-report checks also verify the selected method's explicit detail link and focus behavior.
- This patch's changes are the viewer, version metadata and documentation. The lifecycle and 30-minute recording measurements below belong to the historical 1.1.0 package, not a new gameplay run of 1.1.1.

## Version 1.1 session verification — 2026-09-19

- Core collection/export regression checks: **24/24 passed**, including bounded asynchronous journaling, partial-window flush, session isolation, disk limits and manifest hashes.
- Session assembly/analysis checks: **9/9 passed**, including a synthetic complete 30-minute journal, segment-density-aware sampling, pre/during/post incident evidence, truncated journal recovery, usable-prefix coverage and path validation. A nominal 60-second segment with only 40 usable seconds credits 40 seconds, even when its last reservoir-retained observation is at 20 seconds; an incident at 50 seconds receives zero coverage.
- Offline viewer: passed old/new/empty reports, time alignment, incident filters, coverage gaps, baseline differences, 400,000-sample / 200,000-frame stress fixture, mobile layout and zero browser errors.
- External session supervisor: **58 assertions passed**, covering real EventPipe rotation, startup cancellation, cancellation racing an automatic stop, parent exit/broken output pipe, duplicate supervisor locking, interrupted supervisor/raw recovery, failed export retry, unavailable diagnostics, disk/duration limits and process identity. Completed segments discard raw/TraceLog artifacts during recording after durable parsed-profile/checkpoint commit; failed parsing retains recovery material. Concurrent snapshot testing also exercises three simultaneous readers and 100 replacements on Windows, using both client and worker publishers; no sharing errors or torn JSON. Forced conversion-limit/truncated-trace checks verify usable endpoints independently of the retained reservoir.
- Real graphical lifecycle: **all 9 scenarios passed** on the initial 1.1 integration package (`9dd6c5c179f02a48c8a2d23d7d4b42990414d0267728b9a7563b83d9005a8f3b`): multiplayer manual stop, bare profile command, leave, graceful shutdown, cancel, immediate stop, duplicate start and same-process reconnect; singleplayer leave. Ten world sessions produced nine exports, with cancellation producing none. Graceful shutdown exported 4.024 seconds after client exit. Evidence: `test-runs/session-game/lifecycle-verification-20260919.json`.
- Graphical capture data: **16,631 frames**, **1,292,664 retained stack observations**, **16,634 actual GPU measurements**. Every full-length capture completed four segments, with 92.74–93.29% usable stack interval coverage and explicitly reported transition gaps; frame/runtime start/end coverage passed. A loopback dedicated server had no Ember installed, and the client joined as the ordinary `suplayer` role. This does not test external public-server authentication/network conditions.
- Descriptive frame-interval means: baseline 16.61–16.92 ms; recording 16.79–17.36 ms. Recording included injected stalls/allocations; these sequential observations are **not a controlled profiler-overhead estimate**. GPU elapsed measurements were finite; weighted means 0.20–0.49 ms, maximum 10.57 ms.
- Updated-package regression: **3/3 passed** on `0ec085313f91e90e4339311286d11c276c18537832e31fe90fbdaa8733ad96d7`: cancellation (zero exports), immediate stop (122.5 ms), graceful shutdown (35.501-second capture; export committed 4.434 seconds after client exit). Evidence: `test-runs/session-game/20260919-200417-556-9abcd7/regression-verification.json`.
- Actual **30-minute multiplayer soak passed** on that updated package: **1,800.216 seconds**, **107,384 frames**, **1,801 runtime windows**, **199,984 retained stack observations**, **30 completed segments**, and GPU measurements in all 1,801 windows. Usable stack interval coverage was **98.974956%** (1,781.763 seconds); the remaining **18.453 seconds** are explicitly reported transition gaps. Both the first and last frame/runtime windows were retained with ordinary `HistorySeconds=900`, `MaxTickRecords=120000`, and `SessionSegmentSeconds=60`; no history extension was needed. Evidence: `test-runs/session-game/20260919-201249-741-a395cd/Multiplayer-manual/verification.json`.
- Soak descriptive frame intervals: baseline mean 16.736 ms / recording mean 16.765 ms; recording p95 18.588 ms, p99 22.198 ms, max 902.619 ms. Collector windows averaged 6.636 ms, p95 7.174 ms, with a **886.991 ms outlier**. These wall-time observations include scheduling, workload and injected-test effects; they do not isolate instrumentation overhead or establish its cause. A representative controlled overhead benchmark remains outstanding.
- All lifecycle/soak clients and helpers exited; temporary raw traces and copied settings/backups were removed. The final release is rebuilt only to incorporate this verification documentation; its binary payload is compared against the updated package used for the soak.

Commands for the new checks:

```powershell
dotnet run --project tests/Ember.Tests/Ember.Tests.csproj -c Release
dotnet run --project tests/Ember.Session.Tests/Ember.Session.Tests.csproj -c Release
dotnet run --project tests/Ember.Trace.Session.Tests/Ember.Trace.Session.Tests.csproj -c Release
node viewer/verify.cjs
pwsh -File scripts/test-session-client.ps1 -Package artifacts/emberdiagnostics-1.1.0.zip
```

## Historical version 1.0 verification — 2026-09-19

The following results were obtained in this workspace. The later checklist describes additional release scenarios, not an assertion that every platform and scenario has been exercised.

| Area | Recorded result |
| --- | --- |
| Release/net10.0 build against game 1.22.7 | `dotnet build Ember.slnx -c Release --no-restore` and `./scripts/build.ps1` passed on Windows with SDK 10.0.110; zero warnings/errors. |
| Worker publish and ZIP inspection | Framework-dependent publish and ZIP creation passed. Root mod files, complete tracer output and embedded viewer present; game DLLs excluded. |
| Automated project checks | `dotnet run --project tests/Ember.Tests/Ember.Tests.csproj -c Release`: **21/21 passed**. Includes bounded collection, statistics, redaction, compact context, ZIP hashes, JSON/CSV/Speedscope and hostile HTML. Results: `test-runs/core/results.json`. |
| Real graphical client integration | **Passed twice**, Vintage Story 1.22.7, isolated singleplayer worlds, 960×540. Final production ZIP: commands `mark`, `health`, `profile 30`, and `export` succeeded. Profile: **30.017 s, 200,000 retained samples, 467 stack frames, 38 threads**, and **2,529 rendered-frame records**. Manual export: 2,614 frame records. Mod inventory/stages/markers/Speedscope/LLM context/manifest verified. Also verified reservoir retention reaches the capture tail and hotspot self totals match retained observations. Final results: `test-runs/client/verification.json`; first 8-second run retained in `test-runs/client-first/`. |
| Client-only boundary | `ShouldLoad(Client)=true`, `ShouldLoad(Server)=false`, Ember absent from integrated-server systems. No server commands or diagnostic network channels. Public-server connection itself has **not** been tested. |
| EventPipe capture, stop/cancel and failure handling | Full durable harness passed normal capture (7,436 samples), early stop (0.73 s), cancel (exit 2/no profile), size limit (12,915,090 bytes within 16 MiB), and invalid PID (exit 1/no profile). Verified resolved signatures, multi-owner mapping, recursion-correct counts, allocations/exceptions/contention and child-process cleanup. Results: `test-runs/trace/20260919T185818351Z-4ba214c9/results.json`. |
| Viewer/export interoperability | `node viewer/verify.cjs` passed all eight sections, embedded/empty reports, file import, baseline charts, flame zoom, lazy call tree, thread/managed filters, hostile strings, invalid input, 390px mobile layout, and zero browser errors. |
| Collection/profile overhead | Not benchmarked. |
| Bash build on Linux/macOS | Pending. |

Script syntax checks: PowerShell parser passed; Git Bash `bash -n scripts/build.sh` passed. These syntax checks do not establish Linux/macOS runtime compatibility. Rebuild after final source/documentation changes and record the resulting package hash.

## Build procedure

From the repository root, run one of:

```powershell
dotnet --info
./scripts/build.ps1 -GamePath 'C:\path\to\Vintagestory'
```

```bash
dotnet --info
bash scripts/build.sh --game-path /path/to/vintagestory
```

The SDK must support .NET 10. The scripts restore/build `src/Ember.Mod/Ember.Mod.csproj` in Release and publish `src/Ember.Trace/Ember.Trace.csproj` in Release with `--self-contained false -p:UseAppHost=false`. They pass the `modinfo.json` version into MSBuild and derive the ZIP name from it. Temporary package staging is isolated under `artifacts/` and cleaned after success or failure. A failed build or publish must not replace the last successful archive.

PowerShell uses `System.IO.Compression`; Bash uses Python 3's standard-library JSON and ZIP support. Both accept paths containing spaces and are independent of the caller's working directory.

## Package inspection

- [ ] ZIP is named `artifacts/emberdiagnostics-<modinfo version>.zip`.
- [ ] `modinfo.json`, `Ember.dll` and `Ember.Core.dll` occur at ZIP root, with no extra wrapper directory.
- [ ] Root `.ignore` contains `*tracer/*`. This is required to prevent Vintage Story treating worker dependencies as mod assemblies. The rule was verified in the graphical test.
- [ ] Metadata declares client side, optional client/server installation, and game dependency 1.22.7.
- [ ] `tracer/` includes `Ember.Trace.dll`, `.deps.json`, `.runtimeconfig.json`, `Ember.Core.dll` and all NuGet publish dependencies.
- [ ] Worker runtime configuration targets `net10.0` / `Microsoft.NETCore.App` 10.x and is framework-dependent.
- [ ] No `Vintagestory*.dll`, `0Harmony.dll` or game-provided `Newtonsoft.Json.dll` occurs anywhere in the ZIP.
- [ ] README, docs, LICENSE and THIRD_PARTY_NOTICES are present.
- [ ] `Ember.Core.dll` contains the `Ember.Report.html` embedded resource.
- [ ] Save the build log, package hash, SDK version and installed game version.

## Functional validation

- [ ] Install the ZIP into a clean 1.22.7 client profile; verify loading and `.ember help` / `.ember health`.
- [ ] Join a multiplayer server with no Ember installed and use all commands as a regular player. Verify no Ember custom channel/packets or server installation is needed.
- [ ] Repeat in singleplayer; confirm exported scope identifies shared process counters and client frame hooks correctly.
- [ ] Complete a timed capture; verify the worker targets the client PID and resolves real managed frames.
- [ ] Stop early, cancel, attempt a second concurrent capture, disconnect and exit during capture. Check completion/error reporting and worker cleanup.
- [ ] Exercise unavailable `dotnet`, disabled diagnostics and worker attach failure; ensure ordinary client collection/export remains usable.
- [ ] Mark a reproducible event and verify marker and frame/runtime timestamps align plausibly.
- [ ] Exercise trace limits and inspect loss/limit notes rather than assuming a full capture.
- [ ] Verify bounded history and export retention at the configured limits.

## Export validation

- [ ] Timeline-only ZIP contains `report.json`, `llm-context.json`, `SUMMARY.md`, `ASK-AN-LLM.md`, `metrics.csv`, `frames.csv`, `manifest.json`, `report.html`.
- [ ] A completed profile adds `profile.speedscope.json`; import it in speedscope and inspect per-thread stacks.
- [ ] Recompute every manifest payload's size and SHA-256; all entries match. Manifest need not include itself.
- [ ] Open extracted `report.html` offline; verify data, navigation/filtering and absent-profile states.
- [ ] Check CSVs against JSON for units, invariant-culture numbers and record counts.
- [ ] Check empty history, unavailable census fields, truncated data and unresolved stack/module states.
- [ ] Verify `IncludeModConfigs=false` excludes optional config contents; with opt-in enabled, check redaction using synthetic secrets and paths.
- [ ] Use synthetic marker/log/config text containing HTML metacharacters to verify data remains text in the offline report.

## Performance record

Measure at least a settled baseline, collection enabled, an active stack capture, and export on the same repeatable route/settings. Record hardware, OS/runtime, mod list, resolution/frame cap, duration, frame median/P95/P99, process CPU, memory and external worker resource use. Repeat enough to distinguish ordinary workload variance from instrumentation effects. Publish observed differences; do not describe the profiler as having zero overhead.
