# Ember.Core regression tests

Dependency-free .NET 10 console tests referencing the real `Ember.Core` project.

```powershell
dotnet run --project tests/Ember.Tests/Ember.Tests.csproj -c Release
```

The process exits `0` when every check passes and `1` on any failure. It continues
after individual failures so a single run reports all regressions. Timer checks
use real collector callbacks with bounded waits, normally taking about six seconds.
The core project's embedded `viewer/report.html` must exist before building.

Artifacts default to `test-runs/core` at the repository root:

- `results.json`: individual outcomes and exception details.
- `bundles/*.zip`: a synthetic capture containing hostile HTML strings for export validation.
- `retention/<run-id>/*.zip`: monitoring-only export retention fixtures.

An explicit output directory can be passed after `--`:

```powershell
dotnet run --project tests/Ember.Tests/Ember.Tests.csproj -c Release -- test-runs/core
```

Coverage includes ring chronology and overwrite accounting; finite filtering and
nearest-rank percentiles; collector frame, stage, census, log and history retention;
actual timer sampling; runtime metric sanity; text/nested JSON redaction; Speedscope
JSON structure, indices and sample-count weights; evidence-limited findings;
ZIP completeness, manifest byte counts and SHA-256 hashes; hostile inline JSON;
CSV invariant culture; and export retention. Privacy regressions deliberately
include bearer credentials, UNC paths, and IPv6 alongside basic redaction cases.
