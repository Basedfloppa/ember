# Architecture and reviewer guide

Ember is a client-only Vintage Story diagnostics mod with a separate .NET trace worker and a self-contained offline report viewer. **All Ember project code was written with AI assistance using OpenAI GPT-6 Astra.** The source map and checks below support independent review; they do not assert that a human audit has taken place.

## Process and dependency boundaries

```text
Vintage Story client process
  Ember.Mod -> Ember.Core
  render/runtime/context collection -> bounded journal queue -> session files
                             |
                             +-- launches dotnet Ember.Trace.dll --session <directory>
                                      |
                                      +-- supervisor: lifecycle, limits, recovery, export
                                      +-- segment processes: EventPipe capture, then parsing
                                                   |
                                                   +-- local diagnostics IPC -> client PID

Ember.Core session assembly/export -> local ZIP -> offline report.html / report.json
```

- **Core** has no game or trace-package dependencies. It supplies shared models, collection, analysis, redaction, session assembly, and export. Its HTML viewer is an embedded resource.
- **Mod** is the only production project referencing game assemblies. `EmberSystem.ShouldLoad` accepts only the client side. The mod registers local chat commands and render observers; it has no Ember server component or custom network channel.
- **Trace** references Core and Microsoft diagnostics packages, not the game. Its framework-dependent publish lives in `tracer/`, excluded from the game's assembly scan. Parsing and export after a recorded session can continue after the client exits.
- **Singleplayer** runs the integrated server in the client process. Process counters and stack observations can include its work even though the render hooks are client-specific. Multiplayer capture does not measure a remote server's CPU or full world state.

## Source map

Paths in each row are relative to the repository root.

| Boundary | Files and responsibility |
| --- | --- |
| Client entry/lifetime | `src/Ember.Mod/EmberSystem.cs`: client-only loading, render-stage observers, commands, world exit, snapshot export and recovery triggers. |
| Game context | `src/Ember.Mod/ClientSnapshot.cs`: environment/settings, census, inventory, assembly/patch associations, optional hashes/config contents. `NativeThread.cs`: OS thread identity. |
| GPU observation | `src/Ember.Mod/GpuFrameTimer.cs`: bounded asynchronous timestamp queries and availability/skip reporting. |
| Client session owner | `src/Ember.Mod/SessionRecording.cs`: request/context files, journal subscription/drain, supervisor launch, stop/cancel intent and completion monitoring. |
| Older single-capture path | `src/Ember.Mod/TraceController.cs`: one-shot worker controller and `FindDotnet`. Current `.ember start` / `.ember profile` use `SessionRecording`; that class still uses `FindDotnet`. |
| Shared contracts | `src/Ember.Core/Models.cs`, `SessionModels.cs`, `MetricCatalog.cs`: configuration defaults, report/profile/session types, units and metric meanings. |
| Collection and persistence | `src/Ember.Core/Collector.cs`, `SessionJournal.cs`, `AtomicFile.cs`: rolling buffers, runtime windows, asynchronous bounded journal writing, atomic JSON publication. |
| Session reconstruction | `src/Ember.Core/SessionAssembler.cs`, `SessionAnalysisHelpers.cs`, `ProfileMerge.cs`: journal salvage, bounded retention, validated segment loading, profile merging and coverage. |
| Interpretation/export | `src/Ember.Core/IncidentAnalysis.cs`, `Analysis.cs`, `LlmContext.cs`, `GptReport.cs`, `Exporter.cs`: incidents, evidence-based findings, summaries, CSV/JSON/HTML/Speedscope, manifest and ZIP. `Redactor.cs`: heuristic text/config redaction. |
| Worker entry and capture | `src/Ember.Trace/Program.cs`, `Options.cs`: mode dispatch, one-shot CLI, EventPipe providers and bounded capture; one-shot parsing runs in a private child process. |
| Session supervision | `src/Ember.Trace/SessionWorker.cs`: locking, PID/start-time identity, rotation, child lifetime, retry/recovery, stop/cancel, resource limits and export commit. |
| Segment worker | `src/Ember.Trace/SessionSegment.cs`: capture followed by parsing in that segment process, durable completion. `ProfileParser.cs`: TraceLog conversion, symbols, sampling and quality metadata. `WorkerFiles.cs`: worker protocol IO, process identity, commit and cleanup helpers. |
| Viewer | `viewer/report.html`: offline import/navigation, incident and stack investigation, baseline comparison, filtered selection export. `viewer/verify.cjs` and `viewer/review.cjs`: browser verification. |
| Packaging | `scripts/build.ps1`, `scripts/build.sh`, `src/Ember.Mod/modinfo.json`, `src/Ember.Mod/.ignore`: versioned ZIP, worker publish and assembly-scan isolation. |

## Recording and durable protocol

Outside recording, the collector retains a recent in-memory window (default 900 seconds and at most 120,000 individual frame records). `.ember export` snapshots that buffer without stopping an active recording.

A recording writes to `<DataPath>/Ember/sessions/<id>/`. It does not depend on extending the rolling history:

| File | Writer / meaning |
| --- | --- |
| `request.json` | Client: original PID and process start time, recording limits, export destination and assembly-to-mod map. |
| `seed.json`, `final.json` | Client: initial/enriched and optional final report context. Rolling timeline data is removed before saving these snapshots. |
| `batches.ndjson` | Client background journal writer: sequenced metric/frame/log/event/census/context batches. |
| `stop.json` | Client or supervisor: durable reason/time, cancellation intent and dropped-batch accounting. |
| `journal.closed.json` | Client: acknowledgement after journal drain and final stop-state publication. |
| `session.json` | Supervisor: identity, state, segment checkpoints, counts, notes and coverage. |
| `segments/<segment>/` | Segment capture/parse work, including `segment.json`, raw recovery material while needed, and durable `profile.json`. |
| `result.json` | Supervisor: committed completion/cancellation/failure outcome and export path when available. |
| `supervisor.lock`, segment `segment.lock` | Exclusive ownership preventing competing workers from processing the same live recording. |

Stop/world exit detaches the collector and drains the journal asynchronously. The supervisor also detects target exit and time/disk limits. It can overlap parsing a completed segment with the next capture. Completed segments discard raw/TraceLog artifacts after durable profile/checkpoint commit; failed parsing can retain recovery material. Successful export commits the result before cleanup. Cancellation commits a cancelled result without publishing a report.

Startup recovery and `.ember recover` retry unfinished work from complete journal records and segment checkpoints. PID **and process start time** distinguish an original process from a recycled PID. Atomic snapshots and exclusive locks are protocol invariants, not interchangeable with ordinary overwrite operations. Recovery cannot reconstruct batches never written or every truncated trace tail.

## Measurement and retention contract

**EventPipe samples are thread-stack observations, not exact on-CPU time.** Waiting threads may be observed. Self counts use the leaf; inclusive counts indicate that a frame occurs in a stack and overlap across frames. Speedscope uses unit `none`. Ownership maps and Harmony patch inventory are navigation hints, not proof of per-mod cost or causation.

Default limits are defined in `Models.cs` and `SessionModels.cs`:

- 60-second trace segments, at most 50,000 retained observations per segment; session capture caps each raw segment at 128 MiB even though the general trace setting defaults to 256 MiB.
- A maximum 200,000 observations in the merged global profile. Merge quotas account for segment observed/retained density; rounding can omit sparse segments. Incident samples are independently selected from segment-retained data before global downsampling and must not be added to the global reservoir as new observations.
- Up to 500,000 uniformly reservoir-retained individual frames, with incident discovery before downsampling. Runtime windows, logs and events each have a 20,000-entry tail; context history is also bounded.
- Four-hour untimed sessions, a 2048 MiB session working-directory budget, and a default 512 MiB journal allowance. Dropped batches, omissions, incomplete lines and quality limits are reported rather than interpolated.

Coverage is the **union of usable trace intervals**, not a promise of events at every instant. Attach/rotation/parse failures create gaps. Known truncation/conversion limits clip coverage to the usable prefix independently of the last randomly retained sample. Reservoir omissions and EventPipe event loss are distinct quantities.

Frame/render durations are wall times. GPU Before/Done timestamp differences include pipeline gaps, exclude earlier ticks and presentation, arrive asynchronously, and are not GPU busy time/utilization. Missing telemetry is not zero. Full interpretation is in [metrics and data](metrics-and-data.md).

## Privacy and output boundary

Exports stay under `<DataPath>/Ember/exports/`; Ember does not automatically upload them. The viewer needs no hosted service or external assets. A bundle includes structured evidence, human/LLM summaries, CSVs, HTML, an optional profile, and payload sizes/SHA-256 hashes in `manifest.json` (the manifest does not hash itself).

`IncludeModConfigs=false` by default. When enabled, configurations pass through `Redactor.Config`; text redaction recognizes selected secret assignments, paths, IP addresses and email patterns. This is heuristic filtering, not guaranteed anonymization or an export-wide secret scanner. Markers/logs, mod and method names, hardware/runtime details and unusual strings can identify a setup. Local protocol/recovery files additionally contain process identities and filesystem destinations; raw traces and test settings are not sanitized sharing artifacts. Review the actual attachment, not just its filename or summary.

## Review priorities

1. **Lifetime and durability:** stop/cancel races, journal acknowledgement, parent death, idempotent recovery, export commit before cleanup, PID identity and path validation.
2. **Bounded work:** render-thread callbacks, queue backpressure, parser/time/disk/sample limits, GPU query availability and truthful omissions.
3. **Measurement truth:** time-origin conversion, end-exclusive viewer ranges, usable-prefix coverage, observation density, recursion-correct hotspots and non-causal findings.
4. **Data handling:** redaction boundaries, text-only rendering of untrusted reports, embedding escapes and manifest integrity.

Use the matching [test tier](testing.md). Current evidence is Windows-focused; Linux/macOS workflows, external public-server authentication/network conditions, native/GPU execution attribution, exact per-mod CPU accounting, and a representative controlled overhead benchmark remain outside the validated scope. Historical soak results are not a zero-overhead claim or validation of every later package.
