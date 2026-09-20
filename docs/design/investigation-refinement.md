# Investigation refinement: diagnostic-tool density

The project owner approved a second pass to make the integrated Expediente/Laboratorio viewer feel more like a focused diagnostic tool. This refines the selected composition and Ember palette; it is not another prototype round. Implementation and finish review were AI-assisted; see [AI disclosure](../../AI_USAGE.md).

## Grounding

Public sources consulted before this pass:

- [Anthropic frontend-design guidance](https://github.com/anthropics/skills/blob/main/skills/frontend-design/SKILL.md): subject-specific structure and typography, functional rather than decorative boundaries, plain action-oriented writing, restraint. A prescribed alternative font or fashionable palette is not the goal.
- [Impeccable's pattern catalog](https://impeccable.style/slop): redundant writing, nested cards, repeated rounded outlines, hierarchy and readable interface type.
- [Chrome DevTools Performance reference](https://developer.chrome.com/docs/devtools/performance/reference): recording context, range selection, adjacent inspection and dense analysis surfaces. This is a workflow reference, not a claim that Ember measures exact CPU duration like a browser trace.

## Before / after contract

| Before | After | Purpose |
| --- | --- | --- |
| “Follow the slowdown” and a three-part instructional slogan | Actual capture date, duration, frame count and coverage | Identify the recording rather than promote the product |
| Rounded outer workspace, selected cards, pills and disclosure boxes | Shared pane boundaries, flat incident rows and restrained underlined tabs | Make data grouping clearer with fewer competing outlines |
| Multiple paragraphs between timeline, filters and stacks | Compact range/source labels and a sampling-details disclosure | Keep the evidence limits accessible without repeating them in the work path |
| Tall app bar and context area | Compact app bar, shorter timeline, adjacent selection controls | Bring the flame graph substantially higher in the initial desktop viewport |

## Shipping direction

- **THESIS:** Make the captured recording and selected interval the first thing the interface communicates.
- **OWN-WORLD:** Retain graphite/amber Ember, system UI for controls and monospace only for signatures/time coordinates. Reserve amber for selection and diagnostic data. Flat app panels; controls retain a small usable radius.
- **STORY:** Recording context → incident/time selection → filtered stacks with nearby method/evidence inspector → export or controlled experiment.
- **FIRST VIEWPORT:** At 1440×1050, expose a useful part of the flame graph and inspector above the fold, with the incident queue and compact timeline in view. Mobile preserves horizontal incident access and flame → inspector → results ordering.
- **FORM:** Immediate actions, readable 12–13px interface labels, progressive disclosure for technical explanation, offline and keyboard-capable. No added ambient motion.
- **FINISH:** One batched browser inspection, at most one corrective batch, existing regression checks, independent finish review and focused design-document update. Preserve the 1.1.1 artifacts; deliver this pass as 1.1.2.

## Implemented evidence

The real capture now opens with its recorded date, a 30-minute duration, 107,384 observed frames and 98.97% stack coverage. Incident coordinates use elapsed minutes/seconds/milliseconds; exact range controls and exports retain profile-origin seconds and end-exclusive boundaries. The recorded maximum-peak incident remains the initial selection.

The first batch at 1440×1050, 1920×1080 and 390×844 passed. At 1440px the flame container begins around y=653 instead of around y=891 in 1.1.1. The change creates room for the flame graph and method inspector without inventing larger data values or hiding evidence. Font sizes in controls, method inspection and supporting explanations are mostly 12–13px; code/time fields remain monospace.

Regression and real-data checks confirm that Sampling details exposes time origin, start-inclusive/end-exclusive bounds, source density, non-CPU interpretation, graph meanings and export scope. The full-profile view retains its existing detailed measurement guidance.

The detector ran once. Its new-system warnings concern the intentional 4px control radius and expanded type scale; documentation follows the approved refinement. Other findings point to inherited fallback fonts, inactive/overridden old selectors and the baseline legend's functional dashed-line encoding. No decorative gradients, external fonts or imagery were introduced.

## Finish review

A fresh independent reviewer read the production source, approved direction and all six desktop/wide/mobile viewport/full-page screenshots. Final disposition: **ship**, with no material fixes requested for this bounded refinement. The review confirmed recording/interval-first hierarchy, retained Ember identity, first-viewport flame allocation, mobile inspection order, progressive disclosure and measurement semantics. Browser functionality was supported by the supplied passing tests and source inspection; the reviewer did not independently run the browser.
