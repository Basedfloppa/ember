# Third-party notices

Ember's original code and documentation are licensed under the [MIT license](LICENSE).

## Distributed diagnostics dependencies

The framework-dependent `tracer/` publish output includes the NuGet runtime dependencies of `Ember.Trace`, including:

| Component | Project | License |
| --- | --- | --- |
| Microsoft.Diagnostics.NETCore.Client | [dotnet/diagnostics](https://github.com/dotnet/diagnostics) | MIT |
| Microsoft.Diagnostics.Tracing.TraceEvent | [microsoft/perfview](https://github.com/microsoft/perfview) | MIT |
| Microsoft.Diagnostics.FastSerialization, Dia2Lib, TraceReloggerLib and architecture-specific native support files | Distributed by the TraceEvent NuGet package | TraceEvent package declares MIT; copyright Microsoft Corporation |
| Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2 / Microsoft.Extensions.Logging.Abstractions 8.0.3 | [dotnet/runtime](https://github.com/dotnet/runtime) | MIT |
| System.Reflection.TypeExtensions 4.7.0 (resolved dependency; runtime may supply implementation) | [dotnet/runtime](https://github.com/dotnet/runtime) | MIT |

The exact resolved dependency versions are recorded in `tracer/Ember.Trace.deps.json`; direct version pins are in `src/Ember.Trace/Ember.Trace.csproj`. The .NET runtime is a prerequisite and is not bundled. Microsoft diagnostics projects carry the following license ([diagnostics source](https://github.com/dotnet/diagnostics/blob/main/LICENSE.TXT), [TraceEvent source](https://github.com/microsoft/perfview/blob/main/LICENSE.TXT)):

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Game-provided references

Vintage Story is copyright Anego Studios. `VintagestoryAPI.dll`, `VintagestoryLib.dll`, and game-provided `0Harmony.dll` and `Newtonsoft.Json.dll` are build/runtime references, excluded from Ember's package. A separately installed Vintage Story 1.22.7 supplies them. Ember is an independent community project.

## Research and interoperability credits

**spark and Lithos Probe were the principal motivations for creating Ember Diagnostics.** The credit below acknowledges inspiration, not a claim of endorsement or shared maintenance.

- **spark**, by lucko and contributors: command ergonomics, health context and lag-spike investigation motivated the capture-and-investigate workflow. [Project](https://spark.lucko.me/), [documentation](https://spark.lucko.me/docs/) and [source](https://github.com/lucko/spark). No spark code is copied or distributed.
- **Lithos Probe**, by imtsubaki (trevorftp): its Vintage Story server profiler motivated a complementary client-focused tool. [Mod page](https://mods.vintagestory.at/probe), [source](https://github.com/trevorftp/LithosProbe), [MIT license](https://github.com/trevorftp/LithosProbe/blob/main/LICENSE), copyright (c) 2026 imtsubaki(tsu). No Lithos Probe code is copied or distributed.
- **speedscope**, by Jamie Wong and contributors: Ember emits its documented [file format](https://github.com/jlfwong/speedscope/blob/main/src/lib/file-format-spec.ts). The speedscope application is not bundled; Ember's embedded HTML report is its own viewer.

Research links and comparisons are in [docs/comparison.md](docs/comparison.md).

## AI-assisted authorship

Ember's implementation, tests, viewer and documentation were written entirely with AI assistance using **OpenAI GPT-6 Astra**, operated through OpenCode under the direction of **bbauti**. This does not imply endorsement by OpenAI, OpenCode or the credited projects. See [AI_USAGE.md](AI_USAGE.md) for scope and validation limits. AI use does not replace the obligation to respect third-party licenses.
