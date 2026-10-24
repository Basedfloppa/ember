# Changelog

Versions below describe local development milestones. A heading here does not mean that a matching GitHub or ModDB release has been published; see [GitHub Releases](https://github.com/bbauti/ember/releases) for downloadable releases.

## Unreleased

- Attribute sampled CPU-profile frames to the mod that owns the module, recovering module names from the raw trace when the converted trace lost them and marking namespace-derived owners. Add `profile.modSamples` (self/inclusive/managed self per mod) to `SUMMARY.md`, `report.json`, the LLM context and the viewer, with the unowned-leaf count stated instead of silently folded into "other".
- Prepare the public repository: installation/reviewer entry points, documentation index, architecture and testing guides, contribution and security policies, issue/PR templates, CI and curated viewer screenshots.
- Prominently disclose that the entire implementation was written with AI assistance using OpenAI GPT-6 Astra.
- Credit spark and Lithos Probe as the principal motivations for Ember.
- Identify bbauti as maintainer and link the repository from the mod metadata.

## 1.1.2 — 2026-09-19

- Replace the investigation slogan with capture date, duration, observed frames and stack coverage.
- Compact incident rows, phase controls and filters; move sampling explanations into a disclosure.
- Give the flame graph and method inspector more room in the first desktop viewport.
- Preserve offline operation, range/export semantics and keyboard/mobile interactions.

## 1.1.1 — 2026-09-19

- Introduce the integrated Investigate workspace: incident queue, timeline, phase selection, flame graph, method inspector and evidence.
- Add scoped JSON exports and paginated hotspots; preserve selection and keyboard focus.
- Refine narrow-screen ordering and large-report responsiveness.

## 1.1.0 — 2026-09-19

- Add full-session disk journals, segmented EventPipe capture and an external finalization/recovery supervisor.
- Add automatic export on world exit, bounded retention, coverage-gap reporting and incident analysis.
- Correct cancellation/session isolation, atomic checkpoint replacement and truncated-trace coverage handling.
- Validate graphical lifecycle scenarios and a 30-minute loopback multiplayer recording; see [verification history](docs/verification.md).

## 1.0.0 — 2026-09-19

- Initial client diagnostics, runtime/frame collection, inventory, sampled stacks and offline exports.
