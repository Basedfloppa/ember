# Graphical session integration tests (VS 1.22.7 / Ember 1.1)

This is a real graphical-client test driver, installed only into fresh isolated runs. It compiles against `VintagestoryAPI.dll` without depending on an in-progress Ember source build. The host requires an **explicit packaged Ember 1.1.x ZIP**. Run only once source integration and packaging are ready.

## Commands

Prepare/compile the driver without launching any game or building Ember:

```powershell
dotnet build tests/Ember.Session.GameTests/Ember.Session.GameTests.csproj -c Release
```

Run the complete loopback multiplayer suite (requires an interactive Windows desktop and existing authorized client credentials):

```powershell
pwsh -File scripts/test-session-client.ps1 -Package artifacts/emberdiagnostics-1.1.0.zip
```

Run a focused scenario or both singleplayer and dedicated-server coverage:

```powershell
pwsh -File scripts/test-session-client.ps1 -Package artifacts/emberdiagnostics-1.1.0.zip -Scenario manual -Mode Both
pwsh -File scripts/test-session-client.ps1 -Package artifacts/emberdiagnostics-1.1.0.zip -Scenario leave -Mode Singleplayer
pwsh -File scripts/test-session-client.ps1 -Package artifacts/emberdiagnostics-1.1.0.zip -Scenario reconnect -Mode Multiplayer
```

Run an actual 30-minute recording (plus world startup, 18-second warmup/baseline, and finalization):

```powershell
pwsh -File scripts/test-session-client.ps1 -Package artifacts/emberdiagnostics-1.1.0.zip -Scenario manual -Mode Multiplayer -DurationSeconds 1800 -SegmentSeconds 60 -FinalizeTimeoutSeconds 600
```

Use the agent harness's background process facility for the soak. The script remains the owner/supervisor of its own children. Avoid detached jobs whose parent is terminated when the calling shell exits.

`-SegmentSeconds` defaults to 10 for short lifecycle cases and automatically becomes 60 for durations >=900 seconds unless explicitly supplied. Long runs use normal monitoring limits (`HistorySeconds=900`, `MaxTickRecords=120000`) to test session-journal independence from the bounded monitoring history. These settings and the segment duration are recorded in `run.json` and `launch.json`. Set the tool timeout longer than the entire run (or disable it for an explicitly authorized background soak); an external hard timeout can interrupt PowerShell cleanup.

Reverify a completed case:

```powershell
pwsh -File tests/Ember.Session.GameTests/verify.ps1 -RunPath test-runs/session-game/<timestamp>/Multiplayer-manual
```

## Scenarios

| Name | Real action |
| --- | --- |
| `manual` | `.ember start`, 35 seconds by default, `.ember stop`; 10-second segment rotation |
| `profile` | `.ember profile` **without seconds**, then manual stop |
| `leave` | Start; invoke `ClientMain.DestroyGameSession(false, SoftExit)` by named enum reflection; assert LeaveWorld, menu return, automatic export |
| `shutdown` | Start; host sends `CloseMainWindow`; require graceful exit and durable supervisor completion **after** the client exit timestamp |
| `cancel` | Start; `.ember cancel`; completed cancellation and zero export ZIPs |
| `immediate` | Start and stop in the same tick, without waiting for request or worker readiness |
| `double-start` | Two immediate starts; one recording directory, nonoverlapping capture segments, one export |
| `reconnect` | Start; game's `DoReconnect`; first world auto-finalizes; second world in the **same process** starts/stops a fresh session; check independent lifetimes and no extra exports/helpers |

Default recording duration is 35 seconds, allowing extra time for attachment and rundown around two 10-second segment rotations. Verification still requires at least three segments and two completed segments; a slow machine that cannot achieve the rotations fails rather than reporting them as covered. Stack coverage and uncovered duration are reported separately from full journal/frame coverage, with explicit gap explanations required. Cancellation and immediate stop allow sparse stack data. The reconnect path is the second-world lifetime/leak test, including mod disposal/recreation.

After successful completion/cancellation, the verifier requires `request.json`, `session.json`, and `result.json`. Intermediate `seed.json`, `batches.ndjson`, and `final.json` may have been deleted after committing the outcome. Raw trace/parser temporary files and segment working files must be cleaned up. Completed report ZIPs remain the source of journal-derived timeline/frame evidence.

Each ordinary recording includes a main-thread-only 120 ms sleep plus a touched 16 MiB allocation burst at ~12 seconds. Evidence records both spike boundaries and the real `.ember mark`. Verification requires a matching slow frame and stack samples before and after the spike, full session start/end timeline coverage, finite actual GPU observations or an explicit runtime unavailability reason, and first/last frame windows on recordings >=15 minutes.

## Isolation and evidence

Runs live under `test-runs/session-game/<UTC timestamp>-<random>/`. Each scenario has independent `data/`, and multiplayer has independent `server-data/`. Existing settings are read privately, copied into the isolated data, and all copied `*clientsettings*` files/backups are removed in nested `finally` blocks. Production saves/configs/servers are never passed to the launched processes. Logs and evidence should be treated as private.

The dedicated server uses VS1.22.7's inspected `ServerProgramArgs` (`--dataPath`, `--genconfig`, `--ip`, `--port`, `--maxclients`, `--reducedthreads`). Config generation runs only against the fresh directory. It binds `127.0.0.1` to a probed unused port, disables advertising/UPnP, uses a 1024×128×1024 world and four-chunk radius, and defaults to ordinary `suplayer`. Authentication verification is disabled **on this isolated loopback server**; this covers client-only/public-server transport and permissions compatibility, not the external authentication service. Neither Ember nor the test driver is installed server-side. Unexpected mods in the game's built-in Mods directory cause refusal. Actual client role/privileges and `singleplayer=false` are checked, as are server logs and server Mods.

The host reads the driver's atomic `EmberSessionTest/status.json` and Ember's durable `result.json`; it never repeatedly sends user commands to poll progress. It records descendant PID/creation-time ownership while the client lives and waits for workers to exit before cleanup. Cleanup only terminates owned, identity-checked processes. A shutdown attempt whose export completes before the game exits fails the post-exit coverage requirement rather than claiming it passed.

`verification.json` summarizes assertions and descriptive measurements. Baseline means **Ember installed and monitoring, no active session**; recording includes profiling and the injected spike. The driver measures render-done intervals independently and reads only baseline collector windows via `Collector.Fill` reflection. Reporting compares these with recording collector windows, with no automatic causal-overhead claim or threshold. This is deliberately sensitive to the collector's reflection contract; a changed contract fails explicitly.

This directory does not replace synthetic long-window/schema/unit tests. The 1800-second command above exercises an actual graphical soak. Package verification includes manifest hashes and a private scan for known credential values in exported files.
