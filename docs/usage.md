# Usage and troubleshooting

## One-command session workflow

1. Enter the affected world and let initial loading settle. Note game/mod versions, graphics settings, and whether the issue is singleplayer or multiplayer.
2. Run **`.ember start`** and play normally. `.ember profile` with no argument does the same thing. `.ember health` and `.ember status` show current performance and recording status.
3. Use `.ember mark <text>` for a short observation such as `opened inventory` or `crossed chunk boundary`. Markers are report content, so keep them relevant.
4. Run **`.ember stop` or leave the world/server** when finished. Both automatically finalize and export the session. Use `.ember cancel` to cancel an unwanted active recording.
5. The export folder opens by default when the standalone worker finishes, even if the client has exited. Extract the ZIP and attach **`GPT-REPORT.md`** first when asking GPT for help, or open `report.html` locally to inspect it. It works offline without hosting; Ember does not automatically upload reports. `.ember folder` opens the export directory manually.
6. Compare frame spikes to runtime counters, render stages and markers. Inspect the corresponding stack evidence, keeping process scope and sample quality in mind.

For a timed session, use `.ember profile 1800` for 30 minutes; the optional timer accepts 5–28,800 seconds. With no timer, `SessionMaxSeconds` defaults to four hours. Time, disk and worker limits can end a recording automatically. Session runtime/frame batches are journaled approximately once per second; a 30-minute session needs no `HistorySeconds` edit and is not reduced to the last 15 minutes.

`.ember export` saves a separate recent monitoring snapshot without stopping a session. When not recording, lightweight monitoring retains only the bounded recent history (15 minutes by default), so export soon after an unexpected stall. Stacks from before recording cannot be reconstructed. A frozen main thread cannot process chat commands until it resumes.

Saved recordings live under `<DataPath>/Ember/sessions/`. Ember checks for unfinished recordings at startup; `.ember recover` retries their finalization after a crash or export failure. Keep those files until recovery succeeds. Recovery uses complete saved journal batches and usable stack segments; queued/unflushed data, incomplete trace rundown and damaged files can leave gaps. It does not promise perfect crash recovery.

Stack sampling runs in bounded segments, with possible attachment, rundown and parsing gaps. If diagnostics access is denied but the worker can run, recording can continue with monitoring data and disclosed missing stack coverage. Inspect session coverage, segment status, parse times and notes before interpreting an incident.

After each successfully parsed segment is committed, its raw trace and parser working files are removed while recording continues. The bounded parsed profile remains available for final assembly or recovery. Failed segments retain their recovery material within the session disk budget.

For repeatable comparisons, use the same world/location, camera, graphics settings, actions and duration. Record a baseline without an active stack capture as well as a capture run: profiler and export overhead are real and depend on workload. A change after disabling a mod is evidence worth reproducing, not a verdict from one run.

## Scope

Ember is a client mod on both multiplayer and singleplayer. Commands begin with a **dot**, not `/`. Ember does not register server commands, require server privileges, or exchange custom diagnostic packets with a server.

In multiplayer, it cannot inspect remote server tick timing, remote mods, remote GC, or other players' machines. The locally visible entity census is not the server's entire world. Network delay and server stalls can affect the experience without identifying their remote cause in a client capture.

In singleplayer, the integrated server runs inside the same process. Frame hooks are client-oriented, but CPU/memory/GC counters and the external worker's process-wide samples can include integrated-server threads. Distinguish threads and modules before interpreting a hotspot as rendering work.

## Configuration

The generated `<DataPath>/ModConfig/emberdiagnostics.json` configuration exposes these defaults (restart the client after editing):

| Setting | Default | Meaning |
| --- | --- | --- |
| `HistorySeconds` | `900` | Rolling runtime/frame history time budget. |
| `MaxTickRecords` | `120000` | Maximum retained individual **client frame** records; the name is shared with the collector model. |
| `DefaultProfileSeconds` | `30` | Legacy setting; not the duration of `.ember start` or no-argument `.ember profile`. |
| `MaxTraceMegabytes` | `256` | Trace size setting; session segment requests cap this at 128 MiB and available budget can reduce it further. |
| `SessionMaxSeconds` | `14400` | Four-hour maximum when no explicit timer is supplied; normalized to 60–28,800 seconds. |
| `SessionDiskBudgetMegabytes` | `2048` | Session working-directory budget (2 GiB by default), including journal and trace/parser files. The current worker caps its effective budget at 2048 MiB. |
| `SessionSegmentSeconds` | `60` | Target duration of each stack segment; normalized to 10–120 seconds. |
| `SessionSegmentSamples` | `50000` | Retained stack observations per segment; normalized to 10,000–200,000. |
| `EnableGpuTiming` | `true` | Attempt asynchronous GPU elapsed timestamp sampling when the GL context/driver supports it. |
| `OpenFolderWhenFinished` | `true` | Ask the desktop to open the export folder after session finalization. |
| `SlowTickMilliseconds` | `50` | Slow client-frame threshold, not a server tick target. |
| `CensusIntervalSeconds` | `10` | World census cadence. |
| `MaxCensusEntities` | `50000` | Census work bound. |
| `MaxLogEntries` | `200` | Recent log context bound. |
| `KeepExports` | `20` | Retained Ember export ZIPs; older matching exports are removed. |
| `IncludeModConfigs` | `false` | Include redacted optional mod configurations. |
| `HashModFiles` | `true` | Include available mod-file SHA-256 fingerprints. |

Values are normalized to bounded ranges by the configuration model. The session journal has a separate allowance of one quarter of the requested disk budget (512 MiB by default); exhausting it can end recording before the overall budget. Final reports default to at most 200,000 global stack observations and 500,000 individual frames, with separately bounded incident stack evidence. These report bounds are session-model defaults, not additional settings in `emberdiagnostics.json`. Census snapshots can be stale or truncated; inspect their age and truncation metadata.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `.ember` is unknown | Confirm client installation, game 1.22.7, a single mod version, and the client log for load errors. |
| `dotnet` cannot be launched / runtime missing | Install a matching-architecture .NET 10 runtime and check `dotnet --list-runtimes` for `Microsoft.NETCore.App 10.x`. The worker launcher checks `DOTNET_ROOT`, the client's runtime ancestors, then PATH for the host. Restart the launcher after environment changes. A game-bundled runtime alone may lack the `dotnet` executable. Without a runnable worker, use `.ember export` for recent monitoring data; full session recording cannot start successfully. |
| Worker missing | Reinstall the complete ZIP, including `tracer/` and `.ignore`; copying only `Ember.dll` is insufficient. |
| Worker cannot attach | Run it as the same OS user as the client and enable runtime diagnostics; `DOTNET_EnableDiagnostics=0` disables attachment. On Unix, both processes need access to the diagnostic socket/temp location. A runnable supervisor can retain monitoring-only data when stack attachment fails; inspect the coverage gaps and segment errors. Linux/macOS workflows have not been validated. |
| Profile completes with little or no stack data | Read session coverage, segment status/parse times, profile notes, lost-event counts, stackless samples and size-limit indicators. Attachment delays, segment transitions, an exited client, failed rundown or unavailable symbols can limit evidence. |
| Export lacks `profile.speedscope.json` | Monitoring snapshots and sessions with no usable stack segments can legitimately omit it. Check the session notes rather than assuming complete stack coverage. |
| Recording stopped early | Inspect the stop reason: timer, session/journal disk budget, journal error or worker failure can trigger finalization. |
| Still finalizing after leaving/exiting | The standalone worker continues rundown, parsing and export after the client exits. Allow it to finish and check `<DataPath>/Ember/exports/`; use `.ember recover` on the next launch if it failed. |
| CPU exceeds 100% | The one-core metric sums process CPU across threads; see the separate machine-normalized value. |
| Low CPU but long frames | Consider waits, frame limiting, VSync, scheduling and driver/GPU work. GPU elapsed timestamps are delayed and include pipeline gaps; they are not GPU utilization or exact per-frame CPU/GPU attribution. |
| GPU timing unavailable or sparse | Check `gpuTimingStatus` and submitted/completed/skipped counts in environment context. Unsupported or changed GL contexts disable timing; pending queries can fill the bounded ring and cause skips. Missing values are unknown, not zero GPU work. |
| Folder command fails | Open `<DataPath>/Ember/exports/` manually; desktop folder launching may be unavailable. |
| Recent snapshot history is shorter than 15 minutes | Collection began recently, or the frame-count cap was reached at a high frame rate. Session journals are independent of this rolling buffer; session omissions appear in report notes. |

For build errors, check that `GamePath` points to the installation containing `VintagestoryAPI.dll` and `VintagestoryLib.dll`, not `VintagestoryData`. Keep the build output and SDK version with a bug report.
