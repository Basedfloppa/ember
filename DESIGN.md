---
name: Ember Diagnostics
description: Local, offline diagnostic investigation; evidence before assumptions.
colors:
  bg: "#0e1117"
  panel: "#151a22"
  panel2: "#1c222c"
  line: "#2b3443"
  text: "#edf0f6"
  muted: "#abb7c8"
  accent: "#ffab70"
  cyan: "#6ddbd4"
  purple: "#b5a0ff"
  red: "#ff929d"
  green: "#93ddad"
  primary-ink: "#251708"
  control-hover: "#2a2b30"
  selected: "#ffab7012"
  incident-hover: "#ffffff08"
  notice-bg: "#ffab7009"
  notice-line: "#ffab7035"
  notice-text: "#dac5b5"
  flame-root: "#242c37"
  flame-teal: "#374e57"
  flame-purple: "#4f455d"
  flame-green: "#485849"
  flame-brown: "#605047"
  flame-blue: "#3b5266"
  flame-rose: "#5b424d"
typography:
  headline:
    fontFamily: '"Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif'
    fontSize: "19px"
    fontWeight: 600
    letterSpacing: "-.02em"
  section-title:
    fontFamily: '"Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif'
    fontSize: "17px"
    fontWeight: 600
    letterSpacing: "-.015em"
  incident-title:
    fontFamily: '"Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif'
    fontSize: "16px"
    fontWeight: 600
    letterSpacing: "0"
  workbench-title:
    fontFamily: '"Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif'
    fontSize: "14px"
    fontWeight: 600
    letterSpacing: "0"
  navigation:
    fontFamily: '"Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif'
    fontSize: "13px"
    fontWeight: 400
  brand-word:
    fontFamily: '"Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif'
    fontSize: "18px"
    fontWeight: 650
    letterSpacing: "-.02em"
  body:
    fontFamily: '"Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif'
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.65
  label:
    fontFamily: '"Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif'
    fontSize: "12px"
    fontWeight: 400
  evidence:
    fontFamily: '"Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif'
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.7
  selection-note:
    fontFamily: '"Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif'
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.7
  table-heading:
    fontFamily: '"Segoe UI", system-ui, -apple-system, BlinkMacSystemFont, sans-serif'
    fontSize: "12px"
    fontWeight: 600
    lineHeight: 1.5
    letterSpacing: "0"
  method-signature:
    fontFamily: "ui-monospace, Consolas, monospace"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.7
  method-action:
    fontFamily: "ui-monospace, Consolas, monospace"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.5
  code:
    fontFamily: "ui-monospace, SFMono-Regular, Consolas, monospace"
    fontSize: "14px"
    fontWeight: 400
rounded:
  flame: "2px"
  method: "3px"
  workbench-control: "4px"
  compact: "6px"
  control: "8px"
  disclosure: "9px"
  notice: "10px"
  panel: "12px"
spacing:
  queue-gap: "1px"
  control-gap: "8px"
  row-gap: "10px"
  filter-gap: "10px"
  app-bar-gap: "10px"
  mobile-inset: "16px"
  compact-pane-inset: "18px"
  panel-inset: "20px"
  workbench-gap: "18px"
  desktop-inset: "22px"
  desktop-bottom: "30px"
components:
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.primary-ink}"
    rounded: "{rounded.control}"
    padding: "9px 12px"
  button-secondary:
    backgroundColor: "{colors.panel2}"
    textColor: "{colors.text}"
    rounded: "{rounded.control}"
    padding: "9px 12px"
  button-secondary-hover:
    backgroundColor: "{colors.control-hover}"
  button-app-bar:
    backgroundColor: "{colors.panel2}"
    textColor: "{colors.text}"
    rounded: "{rounded.workbench-control}"
    padding: "6px 10px"
    typography: "{typography.label}"
  button-ghost:
    backgroundColor: "transparent"
    textColor: "{colors.text}"
    rounded: "{rounded.control}"
    padding: "5px 9px"
    typography: "{typography.label}"
  search:
    backgroundColor: "{colors.bg}"
    textColor: "{colors.text}"
    rounded: "{rounded.workbench-control}"
    padding: "6px 8px"
    typography: "{typography.label}"
  nav-tab:
    backgroundColor: "transparent"
    textColor: "{colors.muted}"
    rounded: "0"
    padding: "9px 3px"
    typography: "{typography.navigation}"
  nav-tab-selected:
    backgroundColor: "transparent"
    textColor: "{colors.text}"
  badge:
    backgroundColor: "{colors.panel2}"
    textColor: "{colors.muted}"
    rounded: "{rounded.compact}"
    padding: "4px 8px"
  card:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.text}"
    rounded: "{rounded.panel}"
    padding: "20px"
  incident:
    backgroundColor: "transparent"
    textColor: "{colors.text}"
    rounded: "{rounded.method}"
    padding: "9px 8px"
  incident-selected:
    backgroundColor: "{colors.selected}"
  range-selected:
    backgroundColor: "transparent"
    textColor: "{colors.accent}"
    rounded: "0"
    padding: "8px 9px"
    typography: "{typography.label}"
  method-action:
    backgroundColor: "transparent"
    textColor: "{colors.text}"
    rounded: "{rounded.method}"
    padding: "3px 0"
    typography: "{typography.method-action}"
  flame-container:
    backgroundColor: "{colors.bg}"
    rounded: "{rounded.method}"
    padding: "6px"
---

# Design System: Ember Diagnostics

## Overview

**Creative North Star: "una mezcla de expediente y laboratorio"**

The user's confirmed mixture of Expediente and Laboratorio inherits Ember's dark graphite, warm amber selection and restrained data colors. This is a dense, quiet technical workspace for investigating deeply. Preserve the product name and “Evidence before assumptions.” English application copy accompanies measurements, coverage and uncertainty; signatures and observations remain inspectable.

The shipped interface pairs an incident queue with a stack laboratory, under a compact app bar and horizontal report navigation. Recording metadata supplies the heading context; elapsed incident rows, underlined tabs and flat, shared pane boundaries organize the investigation. Direct selection and progressive disclosure keep detailed evidence close to the data. Its materials are local HTML, CSS and SVG, with installed UI fonts and monospace signatures. The app bar identifies Ember with a wordmark; no brand-mark element or visual assets ship. Review screenshots are verification evidence only.

**Key Characteristics:**
- Dark graphite surfaces with warm amber selection and restrained diagnostic colors.
- Compact UI typography, tabular figures and complete monospace signatures.
- Shared pane boundaries, flame-first investigation and explicit evidence access.
- Immediate selection, retained keyboard focus and deliberate narrow-screen reading order.
- Self-contained offline rendering without external fonts or visual dependencies.

Recorded from the complete CSS cascade and component builders in `viewer/report.html`, including the final “Recording-first density” block and `investigation()`, `drawProfile()`, `seriesChart()` and `drawFlame()`. The approved refinement contract is `docs/design/investigation-refinement.md`; `PRODUCT.md` supplies durable commitments. Desktop, wide and mobile images in `.impeccable/review/recording-first/` corroborate the refined surface. The frontmatter records effective overrides while retaining the base controls and report-card vocabulary used elsewhere.

## Colors

Graphite carries the workspace; amber identifies actions and selections; restrained cool and warm series distinguish observations.

### Primary
- **Warm amber (`accent`)** marks selected tab/range underlines, selected incident peak values and the first chart series. Base primary actions use amber with `primary-ink` text; app-bar and investigation-heading actions use quiet neutral treatments.
- **Selection treatments** are contextual: report tabs use primary text with an amber underline; range tabs use amber text and underline; incident rows use the `selected` wash without an outline. Amber is not a universal severity code.

### Secondary
- **Cyan (`cyan`)** carries links, selected-method signatures, focus outlines and a chart series.
- **Purple, green and red (`purple`, `green`, `red`)** complete the chart-series palette. Coverage uses green for observed segments and red for gaps; labels explain these meanings.
- **Module colors (`flame-*`)** are a separate muted categorical palette. The module-name hash selects a fill; flame width encodes inclusive observations, not severity or elapsed CPU time. `flame-root` identifies the aggregate root.

### Neutral
- **Background (`bg`)** grounds the page, app bar and flame canvas; the incident rail is transparent. **Panel (`panel`)** holds report content, the filter strip and workbench table headings; **control surface (`panel2`)** distinguishes controls and badges.
- **Text (`text`)** carries primary information; **muted (`muted`)** carries supporting context. **Line (`line`)** separates panes, rows and controls.
- `control-hover` and `incident-hover` provide local pointer feedback. Notice tokens give measurement caveats a subtle amber-tinted field rather than a raised alert card.

**The Selection Has Context Rule.** Use the selection treatment belonging to the component, and retain its text label and semantic selected/pressed state. Data colors require a legend or explanatory text.

## Typography

**UI Font:** the installed Segoe UI/system stack in the frontmatter. **Signature Font:** `ui-monospace, Consolas, monospace`; general code also uses `ui-monospace, SFMono-Regular, Consolas, monospace`. No font files are fetched. Root numeric styling is `font-variant-numeric: tabular-nums`.

### Hierarchy
- **Investigation headline:** `headline`; changes to (18px) at the mobile breakpoint. Other report-page h1 headings are (25px), becoming (23px) on mobile. The Ember wordmark uses `brand-word`; report tabs use `navigation`.
- **Section and incident titles:** `section-title` remains the general report heading; `workbench-title` applies inside the stack canvas and `incident-title` stays the same size on mobile. Ordinary h3 titles are (14px, weight 650); inspector h3 titles are (13px, weight 650).
- **Body:** `body` describes ordinary paragraphs; the root size is (14px), while controls retain their native normal line height unless explicitly overridden. Page introductions are (13px).
- **Labels and evidence:** `label`, `evidence` and `selection-note` support dense controls, recording metadata, context and scope. Sampling-details content is (12px), with container line height (1.6) and the inherited paragraph line height (1.65); the observation-count paragraph uses (1.5). Mobile text inputs and selects become (16px).
- **Tables:** body text is (12px). Investigation column headings use `table-heading`, sentence case, normal wrapping and `overflow-wrap:anywhere`. They have zero tracking; legacy report tables retain their separate compact heading style.
- **Methods:** `method-action` is a single-line, ellipsized table control; `method-signature` wraps anywhere in the inspector. Chart axes are (10px); flame labels are (11px) SVG units in the graph's coordinate system, not a promise of constant screen-pixel size.

**The Complete Signature Rule.** Table truncation must retain the full signature in the inspector and expose the selected row's “View selected method” link. Do not truncate the inspection destination.

The inherited welcome headline uses a system face as display text. It is an existing exception, not a reusable display-font role. There is no new display typeface or invented modular scale in this record.

## Layout

The main container is fluid, centered and capped at (1920px). Desktop padding is (16px 22px 30px). The app bar uses a (112px) brand column plus flexible file actions, with (12px 22px 0) padding; the horizontally scrolling report tab strip spans both columns. This replaces the original vertical report sidebar.

The investigation frame has a (192px) incident rail and a flexible detail pane, separated by a shared edge beneath a top rule. Detail padding is (14px 0 0 20px). Inside it, the stack workbench has a flexible flame/results column, a (265px) inspector and an (18px) gap. The inspector spans the flame and results rows; results comprise the paginated hotspot table and lazy call tree. The queue is sticky at (10px), with maximum height `calc(100svh - 80px)` and local overflow.

Responsive rules are based on viewport width:
- **At or below 1180px:** rail width becomes (180px); the app bar hides report-state text. The workbench collapses in DOM order: **flame → selected method and evidence → results**. The inspector uses two internal columns, then block flow on mobile; its evidence region has maximum width (74ch).
- **At or below 1100px:** existing two-column report grids collapse and summary statistics use three columns.
- **At or below 760px:** main padding is (16px); detail padding is (14px 0 0). Brand, four-column file actions and tabs occupy successive app-bar rows. The incident queue becomes horizontal with (150px) entries and (6px) gaps; selection scrolls only the queue to reveal the active incident. Search uses a full row, inputs/selects enlarge, and method/evidence precede results.
- **At or below 700px:** incumbent report cards use (16px) padding and statistics use two columns. **At or above 1450px:** incumbent statistics use six columns.

Flame and table overflow stays local: flame CSS minimum width is (540px), becoming (650px) in the mobile workbench; mobile hotspot tables have a (520px) minimum width. The first hotspot column uses (48%) on desktop and (45%) on mobile. Investigation headers wrap rather than reducing text below their recorded size. The investigation timeline derives its SVG width from the available pane, uses a (134-unit) viewBox height and automatic rendered height. Flame geometry now derives from its container with a minimum (650-unit) viewBox width; frame height (22), row pitch (23) and corner radius (2) remain SVG units.

Spacing is a compact, observed vocabulary rather than a rigid grid: small queue/control gaps, medium field and pane insets, and larger shared-workbench separations. Preserve the distinctions in the frontmatter rather than forcing every measurement onto a new scale.

## Elevation & Depth

Depth comes from tonal layering, thin borders and aligned separators. Report cards explicitly use `box-shadow:none`. The workbench's inner sections have transparent backgrounds, no card border and no padding shell. Evidence disclosures and result tables use shared top rules; the filter strip uses a quiet panel fill. Existing notices retain their thin tinted outline. Focus uses a (3px) cyan outline with (3px) offset; SVG flame focus instead strokes the frame (3px), and timeline-marker focus strokes its circle (4px).

**The Shared Boundaries Rule.** Use shared pane edges and top rules to separate adjacent investigation roles. Keep the workspace unboxed; local control and flame-canvas borders provide the remaining boundaries.

The functional dashed baseline legend and chart gridlines remain legitimate data encoding. The unloaded welcome surface is now transparent and shadowless with a dashed line border, compact heading and local-file action.

## Shapes

Rectangular controls and shared pane edges establish the form language. Investigation controls and the filter strip use `workbench-control` corners; incident rows and the workbench flame container use `method` corners. Report and range tabs, the workspace, table shell and evidence disclosures are square. Base control, badge, disclosure/notice and report-panel radii remain available in their incumbent contexts. Flame frames retain their separate tiny SVG radius; the call tree uses square left rules. The app bar renders the Ember wordmark without the old clipped brand polygon.

## Components

### Buttons
Compact, explicit actions. Base primary is amber with dark text and weight (700); secondary is a raised-tone field with the shared line border; small ghost actions are transparent. The default and small-action paddings are recorded in the frontmatter. App-bar actions use (12px) text, weight (500), `button-app-bar` padding and radius, including a neutral Open report action. Mobile retains this text size and padding with minimum height (38px). Investigation-heading actions use transparent backgrounds, neutral text/borders, (7px 10px) padding and (4px) corners; inspector copy actions also use (4px) corners. Other mobile buttons have minimum height (36px).

Secondary hover shifts the border to amber and uses `control-hover`. Primary and ghost backgrounds retain their own variant styling on hover. Disabled buttons use opacity (.4) and the default cursor. Global focus is retained. Only fine-pointer hover-capable devices without reduced motion receive a transform transition (120ms, `cubic-bezier(.23,1,.32,1)`) and a pressed scale (.98); reduced motion removes both. Selection, filtering and section rendering have no entrance choreography.

### Inputs / Fields
Shared line borders, amber caret, muted placeholder and cyan focus. Workbench fields use the background tone inside a panel-filled filter strip, (4px) corners and (6px 8px) padding. Search uses minimum width (170px), no maximum width and flex growth until the mobile full-row treatment. Selects have maximum width (240px), changing to (220px) on mobile. Checkbox accent is amber, with (16px) width/height. Validation and processing use textual status; large-profile aggregation yields and cancels stale work.

### Navigation
The horizontal tablist has muted inactive labels, primary selected text and an amber (2px) underline on a transparent background. It scrolls locally rather than creating a second sidebar. Selected state uses `aria-selected`; only the active tab is in the tab sequence. Arrow keys, Home and End change tabs and retain focus. The gap is (12px) on desktop and (14px) on mobile; mobile tab padding is (8px 2px).

### Badges and containers
Base badges are compact bordered metadata labels, with (11px) text and no implied interactivity; evidence badges become plain (12px) text without fill, border or padding. Standard report cards remain available on other tabs. Inside the workbench, shared borders and transparent sections replace nested cards. Supporting evidence uses native disclosures, built lazily on first expansion. Notices retain units, scope and interpretation limits near the affected data.

### Incident queue and time controls
Recording context uses a compact definition list for date, duration, frame count and stack coverage beneath “Session recording” or “Performance snapshot.” An incident is a flat two-column button: monospace elapsed minutes/seconds/milliseconds, peak interval, then muted slow-frame count. Confidence and exact profile-origin seconds remain in the title tooltip. Selected entries retain `aria-pressed`, tinted fill and amber peak text; there is no row outline. Fine-pointer hover uses the incident hover wash. Context/before/during/after/normal/full-capture controls use underlined tabs on the shared stack range. The timeline supports pointer inspection, dragged ranges and keyboard activation of windows and incident markers.

Selected-range and source labels remain above the filters; matching-observation counts sit beside the lazy native “Sampling details” disclosure. Its content retains profile-start origin, start-inclusive/end-exclusive bounds, source observations and retention density, the dedicated-incident/global-reservoir distinction, non-CPU interpretation, search/self/inclusive meanings, chart encoding and export scope. Flame zoom limits the displayed branch; exports use time, thread, managed and search filters. Full Stack profile retains its detailed measurement guidance.

### Flame workspace, method inspector and results
The flame is an SVG with categorical module fills and proportional inclusive-observation widths. Zoom and parent/reset controls retain keyboard focus through redraw. The inspector shows a wrapping full signature, attribution, self/inclusive counts and copy action, then incident evidence and counter-evidence. Method selection preserves focus on the table button; an explicit “View selected method” link focuses the inspector, whose scroll margin is (24px).

Investigation hotspots page in groups of (12); generic report tables retain (60). Pagination restores focus to the same enabled direction or the remaining enabled direction at a boundary. Wrapped (12px), sentence-case investigation column headings preserve legibility. The call tree lazily expands branches beneath results. The collapsed DOM order, explicit inspector link, pagination focus and wrapping headings are the shipped review fixes, not optional presentation variants.

## Do's and Don'ts

### Do:
- **Do** preserve graphite surfaces, amber contextual selection and labeled diagnostic colors.
- **Do** keep units, sampled-observation scope, uncertainty and evidence access beside the data.
- **Do** show a heap dump's capture window, completeness and holder attribution next to its numbers, including the bytes no owner could be attributed to.
- **Do** retain complete signatures, explicit inspection links and keyboard focus through selection and pagination.
- **Do** preserve flame → method/evidence → results when the workbench collapses, with local chart/table overflow.
- **Do** keep rendering local and self-contained with installed fonts, CSS and SVG.

### Don't:
- **Don't** turn module colors, sampled hotspots, ownership hints or retained-memory figures into unsupported causal or severity claims.
- **Don't** reuse superseded root colors, the old vertical report navigation or discarded prototype-picker chrome.
- **Don't** add decorative gradients, entrance choreography or nested card shells to the flat investigation workspace.
- **Don't** propagate legacy eyebrow styling, system-face display treatment or text-glyph action icons as new component conventions.
- **Don't** treat review screenshots as shipping visual assets or invent a tonal scale absent from the source.

Not canonized or repaired in this persistence pass: inherited system-face welcome display treatment, dormant eyebrow/removed-brand/old-sidebar selectors, overridden Inter and gradient declarations, and inherited arrow glyphs in action labels. These are legacy exceptions or inactive CSS rather than rules for new surfaces. The active SFMono-Regular fallback in general code is valid and recorded. The detector's new radius/type-scale warnings reflect the approved refinement. The provided independent finish review marked all five blocks **SHIP** with no material fixes; regression and real-data checks passed.
