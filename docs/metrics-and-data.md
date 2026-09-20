# Metrics, data and architecture

## Measurement semantics

The report's metric catalog records units, sources and meanings. Missing/unavailable measurements are not proof of zero work.

| Measurement | Interpretation and limits |
| --- | --- |
| Frame interval | Elapsed time between successive measured client frames. Includes pacing and scheduling effects. A long interval is perceptible stutter evidence, not necessarily active CPU work. |
| Render submission / stage wall time | Monotonic elapsed time around the instrumented client/render stage. Can include blocking and nested work; stage durations need not be disjoint or sum to a full frame. These are not GPU timer queries. |
| GPU elapsed timestamps | Optional GPU-clock elapsed time between submitted RenderBefore/RenderDone markers. Includes pipeline bubbles/CPU starvation; excludes earlier game ticks and subsequent present/swap. This is not GPU busy time, utilization or total frame latency. Results arrive asynchronously and are aggregated when read back, so must not be attributed to the CPU frame/window receiving them. |
| FPS and percentiles | Windowed frame rate and distributions. P95/P99 expose tails that averages hide. Percentiles of individual frames differ from percentiles of already averaged windows. |
| CPU one-core percent | `100 × change in Process.TotalProcessorTime / elapsed wall time`. One saturated logical core is about 100%; parallel work can exceed 100%. |
| CPU machine percent | One-core percent divided by the runtime's logical processor count. This is this process's normalized use, not total utilization by every process on the machine. Affinity/container CPU reporting can affect the denominator. |
| Working set / private / managed memory | Different views of process memory. They overlap and must not be added. Managed/GC snapshots do not represent every native allocation or graphics resource. |
| Allocation rate | Change in process-wide cumulative managed allocated bytes over the observation interval. This is allocation pressure, not retained memory or a per-mod allocation bill. |
| GC counts and pause context | Runtime collection/pause observations. Collections, allocation bursts and stalls may correlate; a periodic snapshot cannot establish every pause's exact relation to a particular frame. |
| Thread pool | Runtime thread, pending-work and completed-work counters. They describe the shared process pool. |
| Census and ping context | Client-accessible world/player observations; inspect age, scope and truncation. They do not measure a remote server's total entity/chunk population. |
| Collector duration | Instrumentation cost observed for collection work; does not account for every effect of instrumentation or the external worker. |

## EventPipe is sampled thread activity

`Ember.Trace` runs separately under .NET 10. The mod writes a session request identifying the current client PID and process start time; a standalone supervisor launches bounded EventPipe segment workers to collect runtime events and sampled managed stacks. The supervisor can finish parsing/export after the client exits. Keeping trace parsing and its dependencies outside the game reduces assembly conflicts and avoids performing that work on the render thread; it does not eliminate target-process sampling overhead or system resource contention.

**An EventPipe sample is a thread-stack observation, not an exact on-CPU measurement.** Threads may be observed while waiting. Self samples count observations whose leaf is a frame; inclusive samples count observations containing a frame. Inclusive totals can overlap across callers and cannot be summed as exclusive costs. A method with many samples is a candidate for investigation, not necessarily the consumer of the same percentage of CPU.

The speedscope export uses sample weights and unit `none` to preserve this distinction. Its per-thread profiles are intended for stack exploration; they do not reconstruct a complete scheduler timeline. Allocation events are sampled, not exhaustive object accounting. Lost events, missing stacks, unresolved symbols, limits, short captures and rundown failures reduce confidence. Native/GPU execution and exact per-mod CPU accounting are outside this measurement's scope.

Default segments target 60 seconds and retain up to 50,000 observations using bounded sampling. Whole-session merging uses equal-probability segment quotas constrained by the retained/observed ratios, with a global ceiling of 200,000 stack observations, then restores chronological order. Quota rounding can omit sparse segments. Incident stacks are independently selected from segment-retained observations before global downsampling, with bounded pre/during/post quotas. Hotspots count retained thread observations; reservoir omissions are distinct from lost EventPipe events. Additional stack-depth, frame-cardinality, trace-byte and parser-resource limits are reported separately.

Stack coverage is the union of usable trace intervals, not proof of continuous events or uniform sample density. Attachment, segment transitions, rundown, parsing, retries and failures can leave unobserved gaps. Reports carry segment timestamps, status, trace/sample counts, parse durations and errors, plus profile quality/rundown notes. When attachment is denied, a running supervisor can still finalize monitoring-only evidence. Missing stack data must not be interpreted as inactivity.

Known stream truncation or the parser's event-conversion limit can discard a segment's tail. In that case a segment profile records `usableDurationSeconds` separately from nominal `durationSeconds`, using resolved stack events processed before reservoir selection. Completed segment end timestamps and coverage are clipped to this usable prefix; the last randomly retained sample does not define the cutoff. Discarded tail intervals receive no stack-coverage credit.

GPU timestamps use a bounded asynchronous query ring, checking availability before result reads. Unsupported/changed contexts or errors disable timing; ring pressure causes skips. Environment context records availability/status and submitted, completed and skipped counts. This avoids deliberately waiting for query completion, but does not guarantee a fixed driver-call cost.

Microsoft explicitly documents that the former `dotnet-trace collect` profile name `cpu-sampling` was misleading because it sampled threads regardless of CPU use; see [the current profile documentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace#options). Kernel on-CPU tracing is a different mechanism.

## Inventory and attribution

The report records mod metadata and dependency declarations, loaded systems and assemblies, module identities, and available file SHA-256 hashes. Harmony inventory identifies patched methods, owners, prefixes, postfixes, transpilers and finalizers where discoverable.

`mods[].enabled` records the client loader's enabled state when available. Helper-library ownership is inferred from mod-supplied assembly filenames matched to loaded assemblies; a shared library can list multiple supplying mods. Profile thread IDs are operating-system thread IDs and must not be equated with managed thread IDs in the environment snapshot.

Assembly-to-mod associations help navigate samples. Shared libraries, multiple mods in one assembly, generated methods, cross-mod calls and patches can make ownership ambiguous. Patch presence does not show that a patch was slow, and a mod filename/hash does not show how much work it performed. Use call paths, time-local evidence and controlled reproduction together.

## Retention and export

Lightweight monitoring uses a recent in-memory buffer: defaults are 900 seconds and 120,000 frame records. `.ember export` snapshots that buffer, including while a session is active. The JSON model calls individual client records `ticks` for shared collector compatibility; `frames.csv` labels their client semantics explicitly.

`.ember start` and no-argument `.ember profile` begin a disk-journaled session. Approximately per-second batches contain runtime windows, individual frames, events/logs and available census/context updates. They are queued to a bounded background writer and flushed to `batches.ndjson` under `<DataPath>/Ember/sessions/<id>/`. Session history is independent of the 15-minute buffer: a 30-minute session needs no history configuration edit. Default limits are four hours and a 2048 MiB working-directory budget, with a separate 512 MiB journal allowance. `.ember profile <seconds>` supplies a 5–28,800-second timer. Stop, world exit, client exit or resource/time limits initiate finalization.

Final assembly retains up to 500,000 individual frames by uniform reservoir sampling across observed journal frames. Incident discovery runs before this frame downsampling. Runtime windows, logs and events have separate 20,000-entry tails, and context history is bounded; very long recordings can therefore omit earlier entries. Notes disclose omissions, dropped batches and incomplete/corrupt lines rather than interpolating missing evidence. Incident stack selections supplement the bounded global profile; neither is an exhaustive execution history.

Request/context files, the saved journal and segment checkpoints support startup recovery and `.ember recover`. Complete usable records can survive a client crash; in-flight batches, incomplete traces or damaged files may not. Recovery retries finalization from saved evidence and is not a perfect crash recorder.

`Ember.Core` contains report models, collection/analysis/export support and the embedded `viewer/report.html` resource. Export creates `GPT-REPORT.md`, structured JSON, summaries, metrics/frame/incident CSVs and a self-contained HTML report, then adds a file-size/SHA-256 manifest and ZIPs the bundle. The manifest lists report payloads; it does not hash itself. A speedscope file is added only when profile data is present.

**`GPT-REPORT.md` is the recommended single-file first attachment for GPT.** It combines selected evidence and interpretation guidance; retain `report.json` for deeper investigation. `llm-context.json`, `SUMMARY.md` and `ASK-AN-LLM.md` remain supplementary context. Selected windows and representative stacks are not substitutes for all retained evidence. Large JSON/HTML payloads and checksums are streamed during export to avoid duplicate report-sized string allocations.

Exports are local under `<DataPath>/Ember/exports/`. `report.html` opens offline without a web server or hosting. The default retention policy keeps 20 matching Ember export ZIPs. Copy a report elsewhere to retain it longer. `OpenFolderWhenFinished=true` asks the desktop to open the export directory after session finalization. Reports are not automatically uploaded.

## Redaction and sharing

`IncludeModConfigs=false` is the default. Optional configuration values pass through redaction if enabled. Diagnostic context may still identify installed mods, assembly/method names, machine/runtime characteristics, user-entered markers, and relevant log content. Redaction is heuristic: review the actual bundle before sharing it, especially if enabling optional configuration capture.

`ASK-AN-LLM.md` accompanies evidence rather than replacing it. Useful analysis should quote observed timestamps/metrics, distinguish observations from hypotheses, account for missing data and process scope, and suggest a reproducible next experiment. A report alone cannot prove which mod must be removed.
