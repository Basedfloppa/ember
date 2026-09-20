# Investigation workspace exploration

Historical AI-assisted design exploration. The project owner prioritized deep investigation and requested three working prototypes, informed by Emil Kowalski's design-engineering guidance and Impeccable. See [AI disclosure](../../AI_USAGE.md) for the authorship and review context.

The implemented viewer, rather than the context detector's false-negative, establishes the incumbent identity: dark blue/graphite surfaces, light neutral text, warm amber selection, restrained cyan/green/red data series, system UI typography and monospace for code/measurements. Preserve that world. A player inspects this on the same desktop used for gameplay, often in subdued light, so retain its existing dark surface rather than impose a replacement theme.

Explore only the investigation entry/workspace. Production viewer, mod and exported evidence remain separate during the prototype selection phase. The prototype is a self-contained local HTML using the complete real 30-minute capture. It is not a replacement release until the user chooses a direction.

## Grounded structures

1. Full-session timeline with an adjacent inspector.
2. Stack-first workbench: flame graph, searchable call paths and compact thread/time controls.
3. Incident master-detail: a persistent incident queue and a focused evidence workspace.
4. Correlated signal grid with a shared cursor.
5. Before/during/after comparison bench with aligned phase columns.
6. Chronological investigation notebook.
7. Query-first event and stack table.

Surface seed `6a2d1e4b` dealt 5, 3, 2. These become Comparador, Expediente, Laboratorio. Prototype's explicit live-picker workflow is the visual decision surface; each variant is fully rendered, not a separate comp. No direction is preselected for production. Other scopes are deferred until this highest-leverage workspace is chosen.

## Direction contract

THESIS: Make time-local stack investigation the entry point instead of requiring the player to cross a wall of metrics and nested cards.

OWN-WORLD: Inherit Ember's graphite, warm amber and restrained data colors. One UI sans, tabular figures and monospace signatures. Use separators and aligned pane boundaries instead of nested cards. No decorative gradients, page-load choreography or external dependencies.

STORY: Pick a measured slowdown, locate it on the recording, inspect thread observations and surrounding runtime windows, then export the selected evidence. Keep sampling scope and uncertainty visible.

FIRST VIEWPORT: Shared compact brand/file actions and session context; Comparador opens three phase columns, Expediente opens a left-hand incident queue and evidence pane, Laboratorio opens an expansive flame graph with a stack inspector. Primary investigation controls stay beside the data they affect.

FORM: Real-time Operate composition. Seed `6a2d1e4b`; ranks 5, 3, 2, in that order. Pointer feedback is subtle; keyboard/filter/variant switching is instantaneous. Mobile restructures columns into a deliberate vertical sequence.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

Prototype-stage boundary: no shipping identity is chosen yet. Store exploration findings here; defer the production DESIGN.md and integration until user selection, as required by prototype. No shipping raster assets are required for this data UI.

## Review of the incumbent

| Before | After in the exploration | Why |
| --- | --- | --- |
| The initial viewport is dominated by twelve full-width incident buttons. | Open the highest recorded peak with its timeline and investigation controls; offer a compact queue or selector. | Investigation starts with evidence rather than navigation work. |
| Incident detail adds multiple nested card boundaries and long text blocks. | Separate pane roles with shared borders; use progressive disclosure for the original detailed evidence. | Keep technical context available without giving every sentence equal visual weight. |
| Card entrance animations run on repeated section renders. | Render selections and keyboard-driven changes immediately; subtle pointer-press feedback only. | Frequent investigation actions should feel direct. |

## Bounded verification

Two batched desktop/mobile inspections were performed at 1440×1050 and 390×844. The first exposed illegible scaled mobile chart axes; the correction uses responsive SVG coordinates and an intrinsic aspect ratio, with matching pointer-to-time mapping. Timestamp formatting now rounds integer milliseconds instead of losing one millisecond to binary floating-point flooring.

Functional Chromium checks pass for incident and phase selection, thread/managed filters, focused method search, flame zoom/reset, method inspection, call-tree disclosure, picker keyboard/URL behavior, dragged intervals, selection JSON contents, reduced-motion handling, invalid-import preservation, direct local-file loading, and no browser console/runtime errors. Export assertions inspect a small projection inside the browser instead of transferring a multi-megabyte Blob string through CDP.

The mechanical detector ran once. Its sole advisory was a status-message border combined with a wide shadow; computed toast styling now removes the shadow and keeps the defined edge. The fixed prototype-picker chrome retains its mandated verbatim styling.

## Independent finish review

The independent reviewer identified two material interaction defects: method/flame/timeline activation could lose keyboard focus after rerender, and the initial selected incident was outside the visible mobile queue. Both were corrected in one batch with stable control identities, focus restoration and rail-only selected-entry scrolling. Targeted Chromium checks verify repeated Enter/Space flame activation, method and marker focus, and selected-entry visibility. The targeted screenshot is `mobile-2-selected-incident.png`.

The reviewer confirmed both fixes with final disposition **ship at prototype-selection scope**. Production direction and integration remain pending the user's choice. No further visual polishing pass was performed.

## Selected direction and promotion

The user chose **a mixture of Expediente and Laboratorio**. The production `viewer/report.html` now opens an Investigate workspace: a compact incident queue, shared session timeline and range/thread/search controls, flame graph first, adjacent method inspector and evidence, then paginated hotspots and the lazy call tree. The existing report sections remain available through a horizontal tab strip, preserving the incident queue as the only investigation sidebar.

The original direction contract above is historical exploration evidence. The shipping contract is:

- **THESIS:** Keep the incident and its sampled stacks in the same investigation workspace.
- **OWN-WORLD:** Inherit Ember graphite, warm amber selections, flat pane boundaries, system UI typography and monospace method signatures. Technical application copy follows the existing English viewer.
- **STORY:** Choose an incident → inspect its context and thread stacks → examine evidence/counter-evidence → export a scoped selection or a GPT brief.
- **FIRST VIEWPORT:** At desktop sizes show the incident queue, time controls and the start of the flame graph with its adjacent inspector. Mobile reflows the incident queue horizontally and the workspace vertically, keeping the active incident visible.
- **FORM:** Direct selection without entrance choreography. Nonblocking, cancellable large-profile aggregation. Keyboard activation retains focus. Desktop/wide/mobile layouts; local self-contained HTML with safe text-only report rendering.
- **FINISH:** Review the promoted surface independently, document its tokens and interaction rules, remove the prototype picker and surface after integration.

The first production screenshot batch showed excessive header/chart height and an overly long 60-row hotspot table. The correction batches the header into one app bar, scales chart coordinates to actual pane width, and paginates investigation hotspots at 12 rows, with full signatures available in the inspector. The detector's obsolete empty-state eyebrow was removed. Its dashed-gradient advisory refers to the baseline legend's functional dashed-line encoding, not decoration. Its zero-size text match belongs to the removed sidebar-footer element's legacy selector; no body copy uses it.

### Production finish verdict

The fresh production reviewer requested three fixes: keep method feedback next to the flame graph after mobile collapse, preserve keyboard focus through pagination, and wrap metric headings instead of overlapping their columns. All three were corrected together and verified with the real offline report at 1440×1050, 1920×1080 and 390×844, plus repeated keyboard pagination through both boundaries and explicit method-detail navigation. The reviewer's verdict pass scored **all three fixes resolved**, with disposition **ship at the scored-fix scope**.

The picker, generated prototype HTML and prototype build/serve/test helpers were deleted after promotion. The design history and screenshots remain as decision evidence. `viewer/review.cjs` generates a fresh standalone production preview from an existing `report.json`.
