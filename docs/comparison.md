# Research comparison and design rationale

Sources reviewed 2026-09-19. These are comparisons of documented approaches, not measured overhead benchmarks or claims of feature parity. Ember's implementation is original; no spark or Lithos Probe source code is copied.

## Comparison

| Tool | Documented approach / strengths | Relevance to Ember |
| --- | --- | --- |
| **spark** | Minecraft ecosystem profiler with profiler start/stop/cancel, timed captures, health/runtime context, thread selection, GC/allocation tools and a rich viewer. Client installations use `/sparkc`; server commands have permissions. | Familiar command lifecycle and an evidence-rich report reduce the steps from a symptom to useful data. Ember adapts these ideas to local Vintage Story client commands and offline files, without requiring server cooperation. |
| **Lithos Probe** | Vintage Story **server-side** background health monitoring, export and timed call-tree captures with a dedicated viewer. The ModDB page lists server-side releases compatible through 1.22.7. Repository is MIT, copyright imtsubaki. | The closest game-specific reference demonstrates the value of packaged runtime context. Ember prioritizes client frame/render evidence. In singleplayer either process profiler can encounter integrated-server activity; that does not make remote server data available to a client. |
| **Minecraft Timings** | Explicitly instrumented areas with invocation counts and friendly descriptions; useful for known event/tick categories. Coverage depends on predefined instrumented boundaries. | Bounded frame/stage timing provides recognizable context, while sampled stacks can investigate inside work that a broad stage label alone cannot explain. Ember does not instrument every mod method or provide exact method invocation counts. |
| **WarmRoast** | Java sampling profiler from which spark's CPU profiler evolved. Spark documents improvements in installation, sharing, method grouping and filtering. | A stand-alone profiler can be powerful but has more setup. Ember packages the attach worker and result viewer with the client workflow. |
| **async-profiler** | JVM profiler using native mechanisms, with CPU, allocation, lock and wall-clock modes depending on platform. It addresses JVM-specific sampling concerns such as safepoint bias. | It is not a .NET backend. Ember uses EventPipe and explicitly avoids borrowing exact on-CPU semantics from a different sampling mechanism. |
| **dotnet-trace** | Cross-platform EventPipe attach/capture and trace conversion. Current documentation distinguishes sampled thread time from kernel CPU sampling; supports speedscope conversion. | The same .NET diagnostic substrate is appropriate for Vintage Story. Ember adds automatic client PID targeting, bounded capture, mod context, frame metrics and a packaged report. The full CLI remains useful for specialist provider/session control. |
| **PerfView** | Advanced .NET performance investigation, including trace analysis, stacks and GC; Windows UI with support for `.nettrace` files. | A specialist tool for deeper trace investigations. Ember's JSON/HTML summary trades depth for a reproducible user-facing capture. A speedscope export does not contain all raw EventPipe data. |
| **dotnet-counters** | Live monitoring and collection of runtime/application counters. | Counters explain pressure and trends but do not by themselves identify a slow call path. Ember combines periodic runtime context with frame and optional stack evidence. |
| **speedscope** | Interactive viewer for sampled/evented stack profiles, with time-order, left-heavy and sandwich views. It consumes profiles rather than collecting them. | An interoperable secondary viewer. Ember also embeds its own offline report so timeline, inventory and limitations accompany the stack data. |

## Lessons applied

1. **Capture the symptom's time window.** spark's lag-spike guide explains how normal samples can mask an isolated slow tick and uses monitoring plus `--only-ticks-over`. Ember retains frame tails and markers to locate client spikes. A whole-capture hotspot ranking can still dilute a brief stall; Ember does not claim spark's server-tick filter is implemented.
2. **State sources and units.** spark's metrics page maps each metric to a game, runtime or OS source and notes container reporting issues. Ember records metric definitions and separates process one-core CPU, machine-normalized CPU, wall time and sampled observations.
3. **Use both timing and sampling.** Timings-style stage boundaries offer understandable context; samples expose call paths beyond those boundaries. Neither alone provides exact universal attribution.
4. **Respect the client boundary.** No server installation, privileges or custom packets are necessary. Local observations cannot diagnose remote internals. Singleplayer process sharing is explicitly documented.
5. **Bound the observer.** Ring buffers and capture limits make memory/history behavior predictable. External parsing isolates diagnostics dependencies from the game. Overhead must still be measured on representative workloads.
6. **Keep reports usable and inspectable.** Offline HTML serves players, JSON/CSVs serve scripts, summaries and evidence-oriented LLM instructions serve support conversations, and speedscope supports stack exploration. Optional configuration capture stays off by default.

## Sources

### spark

- [Command Usage](https://spark.lucko.me/docs/Command-Usage): profiler lifecycle, timed captures, permissions and health/GC tools.
- [spark vs. others](https://spark.lucko.me/docs/misc/spark-vs-others): WarmRoast lineage, Timings comparison and sampling tradeoffs.
- [About spark metrics](https://spark.lucko.me/docs/misc/About-spark-metrics): metric sources and runtime/container caveats.
- [Finding the cause of lag spikes](https://spark.lucko.me/docs/guides/Finding-lag-spikes): why average profiles can mask spikes and the monitoring/filtering workflow.

### Vintage Story and other profilers

- [Lithos Probe mod page](https://mods.vintagestory.at/probe), [repository](https://github.com/trevorftp/LithosProbe), [MIT license](https://github.com/trevorftp/LithosProbe/blob/main/LICENSE). The page contains both `/lithos` examples and `/probe` command listings; consult its current release for exact command spelling.
- [Timings source](https://github.com/aikar/timings) and [WarmRoast source](https://github.com/sk89q/WarmRoast).
- [async-profiler](https://github.com/async-profiler/async-profiler) and [profiling documentation](https://github.com/async-profiler/async-profiler/blob/master/docs/Profiling.md).
- [dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace) and [EventPipe](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventpipe).
- [PerfView](https://github.com/microsoft/perfview).
- [dotnet-counters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters).
- [speedscope](https://www.speedscope.app/), [source and file format](https://github.com/jlfwong/speedscope).
