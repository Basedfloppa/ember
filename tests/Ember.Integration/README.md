# Graphical Vintage Story smoke integration

Run on Windows with an authorized Vintage Story 1.22.7 installation and .NET 10:

```powershell
./scripts/smoke-client.ps1
./tests/Ember.Integration/verify.ps1
```

The launcher uses `artifacts/emberdiagnostics-1.0.0.zip` when available; `-Package` selects another package. If no package exists it builds the mod and publishes its complete profiler worker into the isolated test mod directory. The separate client-only smoke driver waits eight seconds after `LevelFinalize`, executes the real `.ember mark smoke`, `.ember health`, and `.ember profile 8` command handlers, waits for automatic profile export, then exercises `.ember export`. A fresh world's character dialog is confirmed through its own button handler using reflection from the test driver; the warm-up timer restarts after this confirmation.

All world, mod, log, and evidence files are under `test-runs/client`. The launch command creates `EmberSmoke` with `--playStyle creativebuilding`. The copied authorized settings are reduced to a 960×540 window and low render settings. The real game data and worlds are never passed to the launched client. The script closes only the process it launched and removes isolated `clientsettings` files, including backups, in `finally`.

Evidence includes `launch.json`, `graphical-client.png`, `data/EmberSmoke/events.jsonl`, original export ZIPs, and extracted reports. The window screenshot uses `PrintWindow` to capture the actual OpenGL game window even when another application is in front. Verification checks command success, client/server `ShouldLoad`, absence of Ember execution from server logs, actual positive FPS/frame records, loaded mod inventory, smoke marker, sampled managed profile stacks, speedscope data, export manifest hashes, and credential cleanup. It privately compares known credential strings against exported files without printing those values.

Archive or remove the isolated `data` directory before repeating a run. Game logs and screenshots are private test evidence and can contain account names. This singleplayer run demonstrates client-only execution and the negative server `ShouldLoad` predicate; it does not claim a connection to a public server was tested.

The driver's `TextCommandCallingArgs.Caller` uses the same local `CallerPrivileges = ["*"]` as Vintage Story's normal `ChatCommandApi.Execute(..., IClientPlayer, ...)` overload. This matches normal local `.command` execution and does not grant server privileges.
