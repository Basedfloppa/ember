# Contributing to Ember

**All Ember project code was written with AI assistance using OpenAI GPT-6 Astra.** This means AI-assisted generation throughout the project, not occasional autocomplete or minor edits. bbauti is the project author and maintainer. This disclosure is not a claim of human code review or an independent audit.

Code review, reproducible bug reports, measurement corrections, and focused fixes are welcome. Start with [development setup](docs/development.md), the [architecture and source map](docs/architecture.md), and [testing](docs/testing.md).

## Report a problem

Describe the expected behavior, what happened, and the shortest reproduction you can provide. Include the Ember version, Vintage Story version, OS, .NET version, relevant configuration, and whether the problem occurs in singleplayer or multiplayer. For recording failures, include the command used and whether the failure happened during attachment, recording, stopping, or export.

Share only the evidence needed to reproduce the issue. Reports can contain logs, markers, mod identities and machine information; redaction is heuristic. Inspect any attachment before posting it. Do not publish game account settings, credentials, raw private test directories, or proprietary game assemblies. See [data and redaction](docs/metrics-and-data.md#redaction-and-sharing).

## Prepare a pull request

Keep the change small enough to explain and review. Discuss substantial changes to capture semantics, report formats, dependencies, or process lifetime in an issue first.

For every code change, include:

- **Purpose and evidence:** the bug/reproduction or user need, the affected files, and why the change addresses it.
- **Meaningful verification:** relevant test commands and their actual results. Add or update regression coverage when behavior changes; assertions should catch the failure or protect a real invariant. If automated coverage is impractical, explain why and supply a reproducible manual check with evidence.
- **Scope of validation:** OS, runtime/game versions where relevant, and checks not run. A game-free test does not validate graphical hooks or multiplayer lifecycle behavior.
- **Semantics and compatibility:** explain changes to units, sampling, coverage, limits, schema, exports, or recovery. Update the corresponding documentation and fixtures.
- **Reviewable artifacts:** focused logs, sanitized report excerpts, or screenshots for viewer changes. For packaged/game behavior, identify the tested ZIP and its SHA-256.

These criteria apply equally to AI-generated, AI-assisted, and manually written contributions. Please describe material AI assistance in the pull request, including the tools and their actual role (for example, generating an implementation, editing documentation, or suggesting tests). Do not claim tests, reviews, or audits that did not happen.

Preserve the client-only boundary, bounded collection, explicit missing-data reporting, and local/offline export behavior. Do not infer exact CPU utilization or mod culpability from sampled thread stacks. A heap dump is never automatic: it exists only after an explicit in-session request, keeps per-owner aggregates instead of the object graph, and deletes its raw trace once reduced. Retained bytes show which owner holds an object, not which mod caused memory to grow, and a long dump span does not prove the game was stopped. Useful reviewer entry points are listed in [architecture](docs/architecture.md#review-priorities).

## License

Contributions are made under the repository's [MIT license](LICENSE). Retain the existing `Copyright (c) 2026 Ember contributors` notice and relevant [third-party notices](THIRD_PARTY_NOTICES.md). Do not commit Vintage Story binaries, private captures, generated build outputs, or account data.
