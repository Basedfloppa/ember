# Development

Ember targets **Vintage Story 1.22.7** and **.NET 10**. All Ember project code was written with AI assistance using **OpenAI GPT-6 Astra**; bbauti is the author and maintainer. See [contribution expectations](../CONTRIBUTING.md) for evidence required with changes.

## Prerequisites

| Work | Requirements |
| --- | --- |
| Core, session analysis, and trace worker | .NET 10 SDK; NuGet access for initial restore. No game installation required. |
| Mod or complete solution/package | A local Vintage Story 1.22.7 installation in addition to the SDK. |
| Offline viewer checks | Node.js 22 or newer with built-in `fetch` and `WebSocket`, plus a local Chromium browser. No npm dependencies. |
| PowerShell trace harness | PowerShell 7.4 or newer and local .NET diagnostics IPC access. |
| Graphical lifecycle checks | Windows desktop session, installed game/client settings, working graphics, and PowerShell. See [testing](testing.md#graphical-windows-checks). |

`global.json` requests SDK `10.0.100` with `latestFeature` roll-forward. `Directory.Build.props` sets `net10.0`, nullable annotations, implicit usings, and deterministic compilation. Check the selected SDK with `dotnet --info`.

## Start without the game

Run from the repository root:

```text
dotnet build src/Ember.Core/Ember.Core.csproj -c Release
dotnet build src/Ember.Trace/Ember.Trace.csproj -c Release
dotnet run --project tests/Ember.Tests/Ember.Tests.csproj -c Release
dotnet run --project tests/Ember.Session.Tests/Ember.Session.Tests.csproj -c Release
```

These test projects are console executables; **use `dotnet run`, not `dotnet test`**. See [testing](testing.md) for the real EventPipe and browser checks. Building `Ember.slnx` also builds the mod and therefore requires game references.

## Build the mod and package

Pass the **game installation** directory, not the game's data directory. The mod references `VintagestoryAPI.dll`, `VintagestoryLib.dll`, and the game-provided `Lib/0Harmony.dll`, `Lib/Newtonsoft.Json.dll`, and `Lib/OpenTK.Graphics.dll`.

```powershell
./scripts/build.ps1 -GamePath 'C:\path\to\Vintagestory'
```

```bash
bash scripts/build.sh --game-path /path/to/vintagestory
```

The Bash script also requires Python 3 and accepts `-GamePath`. Both scripts resolve the game path from the explicit argument, then `VINTAGE_STORY`, then `APPDATA/Vintagestory` when `APPDATA` exists. Linux/macOS runtime workflows have not been validated; a Bash entry point is not evidence of platform support.

For an individual mod build without packaging:

```text
dotnet build src/Ember.Mod/Ember.Mod.csproj -c Release -p:VintageStoryPath="C:\path\to\Vintagestory"
```

The scripts read the version from `src/Ember.Mod/modinfo.json`, build the mod in Release, and publish the worker with `--self-contained false -p:UseAppHost=false`. They create:

```text
artifacts/emberdiagnostics-<version>.zip
artifacts/emberdiagnostics-<version>.zip.sha256
```

The ZIP contains root mod metadata, `Ember.dll`, `Ember.Core.dll`, `.ignore`, documentation/licenses, and the complete worker publish output in `tracer/`. The `.ignore` entry `*tracer/*` prevents the game from scanning worker dependencies as mod assemblies. Game assemblies are excluded. Package staging uses a unique temporary directory under `artifacts/`; failed build/publish steps do not replace the previous ZIP.

The worker requires a discoverable matching-architecture .NET 10 runtime on the player's machine; it is not self-contained. Installing only `Ember.dll` omits required resources and dependencies. Use the complete ZIP for in-game verification.

## Work on the viewer

`viewer/report.html` is the production source: a self-contained HTML/CSS/JavaScript file embedded by `src/Ember.Core/Ember.Core.csproj` as `Ember.Report.html`. There is no separate frontend build or npm install. Keep the `/*EMBER_DATA*/null` embedding marker intact.

```text
node viewer/verify.cjs
```

Set `CHROME_PATH` if the browser is not found automatically. Rebuild Core and the package after viewer edits so new exports receive the updated embedded resource. Existing exported HTML retains its original viewer; import its `report.json` into the updated viewer to inspect old data. Earlier prototypes were removed after integration; their rationale remains in `docs/design/`.

## Before proposing a change

Use the [source map](architecture.md) to identify the responsible layer, run the relevant [checks](testing.md), and include the commands and evidence in the pull request. Keep generated files under `artifacts/`, `test-runs/`, and local review directories out of source changes. Do not attach an entire private run directory as verification evidence.
