# Ember Diagnostics

<!-- impeccable:product-schema 1 -->

## Platform

web

The user-facing report viewer is a standalone browser interface. This record covers the full Ember product: the Vintage Story client mod, recording worker, offline viewer, and evidence exports for human and GPT-assisted investigation.

## Users

The primary users are Vintage Story players troubleshooting stutters. They need to capture the problem during normal gameplay, understand the recorded slowdowns, and identify the next useful setting or mod experiment.

## Product Purpose

Turn a gameplay performance symptom into inspectable evidence. A player starts one recording, plays normally, then stops or leaves the world to receive an offline session bundle.

Success means the player can locate a slowdown, understand what the capture does and does not establish, and choose a useful next troubleshooting experiment. The report also supports asking GPT for help with the same recorded evidence.

## Positioning

Ember combines Vintage Story client frame/render measurements, runtime history, sampled managed stacks, and machine/settings/mod context in one locally captured session. It packages the viewer and analysis files with the capture, without requiring remote-server installation, permissions, or cooperation.

## Operating Context

- Install the mod on the game client and control it through `.ember` chat commands.
- `.ember start` begins a session; `.ember stop` or leaving the world finalizes it. `.ember profile [seconds]` supports the same workflow with an optional timer.
- `.ember mark <text>` adds a timestamped observation for later investigation.
- Extract the session ZIP and open `report.html` directly from disk; exported HTML contains its report data. The standalone viewer also accepts `report.json` through file selection or drag and drop.
- Investigate incidents and frame/runtime timelines, inspect stack coverage, explore selected stack ranges, and compare a baseline capture alongside its context differences.
- Use `GPT-REPORT.md` as the first attachment for GPT-assisted analysis, with an explanation of what happened and when. Retain `report.json` for deeper investigation.
- Repeat the relevant gameplay action and change one setting or mod at a time when testing a hypothesis. Baseline differences require workload and coverage context.

## Capabilities and Constraints

### Confirmed product commitments

- Client-only operation: multiplayer use requires no server installation, server permission, or Ember network packets.
- Reports remain local and self-contained, work offline, and require no hosting, external viewer dependencies, or automatic uploads.
- Preserve metric sources, units, capture scope, omissions, coverage gaps, and evidence limitations in analysis and presentation.
- A sampled hotspot, mod inventory entry, assembly match, or patch owner is supporting evidence, not proof of the cause of a stall. The same applies to retained memory: a heap dump attributes bytes to the owner that holds them, not to the mod that caused growth.
- A heap dump is captured only on an explicit in-session request, never automatically. It keeps per-owner aggregates rather than the object graph, and the raw trace is deleted as soon as it has been reduced.
- Stack sample percentages describe thread observations, not exact CPU utilization. Render/stage measurements are wall times. Optional GPU elapsed timestamps are not GPU busy time or utilization.
- Remote-server internals are not observed. In singleplayer, the integrated server shares the client process and can contribute to process metrics and stack samples.

### Current implementation context

- The collector and separate EventPipe recording worker use C#/.NET 10; the documented game target is Vintage Story 1.22.7. See `README.md` for current installation/build prerequisites.
- `viewer/report.html` contains the HTML/CSS/JavaScript viewer and is embedded in `Ember.Core.dll` by `src/Ember.Core/Ember.Core.csproj`.
- The viewer provides incident investigation, frame/runtime charts, session coverage, memory and heap dumps, baseline comparison, mod inventory, render stages, world census, settings/patches, logs/events, and stack-profile exploration.
- The default Investigate workspace combines an incident queue with a stack laboratory: shared time/thread/search filters, flame zoom, method inspection, evidence and counter-evidence. It initially selects the incident with the largest recorded frame peak and its context. Scoped selection JSON exports preserve the profile time origin and filter/source semantics.
- Recording metadata leads the investigation header. A compact selected-range/source strip accompanies the filters, while Sampling details exposes time boundaries, retention density, graph interpretation and export scope on demand.
- Exports include offline HTML, structured JSON, Markdown summaries, CSV evidence, a manifest, and a speedscope profile when captured. The full file inventory is maintained in `README.md`.
- Recording and retention are bounded; recovery can salvage recorded evidence but cannot guarantee the final moments of a crash. Current limits and recovery behavior are documented in `README.md` and `docs/usage.md`.
- Optional mod-configuration collection is disabled by default and passes through redaction when enabled.
- Linux/macOS recording workflows have not been validated, according to the current installation documentation.

### Open decisions

- Additional browser/device support commitments and product-specific accessibility requirements have not been established.

## Brand Commitments

- Preserve the name **Ember Diagnostics**, with **Ember** as its short form.
- Preserve the evidence-first commitment expressed by the existing wording, “Evidence before assumptions.”

## Evidence on Hand

- `README.md`: installation, recording lifecycle, collected data, export formats, and build instructions.
- `docs/usage.md`: usage and troubleshooting guidance.
- `docs/metrics-and-data.md`: measurement semantics, data handling, and architecture.
- `docs/comparison.md`: sourced tool comparisons and implementation rationale; these are not measured overhead benchmarks or feature-parity claims.
- `docs/verification.md`: verification procedures and recorded validation status.
- `viewer/report.html`: the existing implemented browser interface.
- `viewer/verify.cjs`: synthetic browser-test fixtures and viewer checks. Synthetic fixtures are not evidence of real gameplay performance.

## Product Principles

1. Make useful capture accessible to a player during normal gameplay.
2. Connect a reported stutter to its time window and surrounding evidence.
3. Keep uncertainty and measurement boundaries visible when interpreting results.
4. Help the player choose a testable next step instead of assigning unsupported blame.
5. Keep investigation local, offline, and inspectable through portable evidence files.
