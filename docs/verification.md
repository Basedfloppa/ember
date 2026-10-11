# Build and verification

These are development-run records, not certification or an independent human audit. Code, tests, documentation and agent reviews were produced with AI assistance using OpenAI GPT-6 Astra; see [AI_USAGE.md](../AI_USAGE.md). Historical local output paths identify retained development evidence but are excluded from the public repository. Curated screenshots are available under [docs/images](images/README.md); reproducible commands and prerequisites are in the [testing guide](testing.md).

## Manual heap-dump feasibility — 2026-10-06

Adds an explicitly requested heap dump: `.ember heapdump` writes a request file, the supervisor runs one isolated worker, the worker deletes the raw trace, and the report keeps only per-owner aggregates. Measured locally with .NET 10.

- A real Vintage Story **server** heap (same engine and runtime as the client, dedicated server, no graphical client) produced **5,464,643 objects / 721,465,325 bytes** from a **330,019,046-byte** trace. Reduction took 3.03 s and the walk events spanned 353 ms.
- Ownership partitioned exactly: `VSSurvivalMod` 35.42%, `VintagestoryAPI` 24.33%, `VSEssentials` 17.63%, `VintagestoryLib` 15.83%, `System.Private.CoreLib` 6.72%, unreachable 0.05%. Largest holder roots: `ModSystemTiledDungeons` retaining 107,658,732 bytes from an 80-byte object, `ServerMain` 90,990,506 bytes, `GenStoryStructures` 42,248,944 bytes.
- Event accounting and the byte closure both balanced (`reachable + unreachable = shallow total`). That check is what the report exposes as `complete`.
- **A session that attaches while the walk is captured makes the dump unreadable.** With the recording's own segment worker attached to the same target, the walk stretched over tens of seconds (23.7 s in one run, 59.6 s in another, both while two-second segments kept rotating) instead of arriving as one burst, and **96.1% of the heap had no matching strong root**. With nothing else attached to the process the same mechanism reported **0.05%** unattributable and a balanced closure. The shape was read at the time as one walk delivered in phases; the 2026-10-07 measurement below shows it is the same heap described once per attach, so the shares are real but the explanation was wrong. The field is named and documented as an event span, not a freeze; the recorded frame intervals remain the authority on impact; and the analysis rejects a snapshot whose unattributable share exceeds **25%** with an explicit note instead of presenting it as a result. The threshold is calibrated on the three measured points — 0.05% (large coherent heap), 11% (tiny isolated heap, where infrastructure objects outside the strong roots weigh proportionally more) and 91–96% (repeated walks) — and the actual share is always stated in the dump's notes.
- A too-small circular buffer loses the dump: 64 MiB retained 21.9% of the nodes in an earlier probe, with the accounting check as the only visible symptom.
- **The first real client dump was truncated by the buffer, not by another session — and that is now a measured, named failure mode.** A modded 1.22.7 client recording (1.43 GiB managed heap, 20,306,904 objects, 33,264,402 references) emitted **1,045.6 MiB** of trace against the then-default **1024 MiB** cap, so the ring discarded the oldest events of the walk while the worker read it: the aggregate came back `complete: false`, with **62.03%** of the heap unreachable from the strong roots, while the stack capture *was* parked and no other EventPipe session existed. Reproduced against one synthetic target and one 735 MiB heap with only the cap changed: **858.2 MiB** of trace at a **1024 MiB** cap kept every event and reported **0.00%** unreachable; **824.1 MiB** at **840 MiB** and **502.1 MiB** at **512 MiB** each lost the beginning of the walk and reported **100%**. The cap is therefore reported in the aggregate (`bufferMegabytes`), `HeapDumpBuffer.Saturated` treats a trace at or above **90%** of the cap as truncated, the analysis names the buffer as the cause instead of blaming an unattached profiler, the default rose to **4096 MiB** and the ceiling to **8192 MiB**. The ceiling is nearly free: the runtime allocates ring buffers as they fill, and the target's peak RSS was the same (**1.65 GiB**, +857 MiB over its baseline) at 1024, 2048 and 4096 MiB caps. A walk needs roughly as many bytes as the heap it walks (1.05 GiB of trace for 1.43 GiB of heap, 0.86 GiB for 0.74 GiB). The integration fixture uses 256 MiB.
- **The client dump also priced the pause, and the walk is what costs frames.** Capture took **63.8 s** (the nominal budget is 60 s; the worker only exceeds it by draining until the stream goes quiet) and the parked interval came to **70.8 s** including a 6.3 s reduction, so the recording lost that much stack coverage (session coverage 48.6%). The game did not stop: frames and metrics kept journaling (2,623 frames across the interval, 17 ms median interval), but two hitches of **3.45 s** and **1.53 s** stand against a 23 ms maximum before the request and 29.5 ms after. Their cause is in the journal, not in the walk's span: of the two GC pauses above 500 ms in the whole 138 s recording, both fall inside the parked interval — **2,099 ms** and **1,467 ms**, with `gen2=1` in both windows and the managed heap dropping 3.8 GiB → 2.7 GiB at the first. A heap walk is performed by the runtime during a collection, and here it made the client collect. So the recorded frame intervals remain the authority on impact, and a dump should be read as "a few seconds of hitching", not as "nothing happened".
- A consequence worth stating: the walk reported **1.43 GiB** shallow while the client's own `GC.GetTotalMemory(false)` journaled **2.7–3.8 GiB** across the same interval. Sampling by a counter is not a heap census and includes uncollected garbage, so the two are not expected to match — but with the buffer that truncated this walk now raised, a repeat dump should report a *larger* live set and a much smaller unreachable share, and that difference is the check that the truncation is gone.
- **The supervisor parks the stack capture around the walk.** A coherent dump required a moment with no other EventPipe session on the target, so the supervisor now stops its own stack segment before starting the worker and lets a new segment attach only when the dump settles. Frames and metrics keep journaling throughout, the interval with no stack coverage is published as its own timeline event and stated in the aggregate, and the dump's time budget starts when the worker starts rather than when the player asked, so time spent parking cannot cut the walk short.
- Measured on the session fixture with two-second segment rotation, two dumps in one recording: unattributable share **5.87%** and **6.34%** against **95.8%** for the same request without the pause, `complete` true in both, parked capture **9.8 s** per dump for walks of 9.1 s. The parked interval is therefore the walk itself — the worker waits for an eight-second quiet gap before closing its session — and not the segment rundown, which is what the first design feared.
- The defect that made the first attempt useless is worth recording, because it was invisible in the numbers: refusing a second request ran the same settling path as completing a dump, so it reset the attempt state and **released the target in the middle of the walk**. Segments re-attached during the capture, the snapshot reported 95.8% unattributable, and the aggregate recorded no pause at all. Refusal and termination are now separate paths, and the second-dump check exists specifically to keep shared attempt state from returning.
- Game-free integration: the session tier runs the isolated dump check plus a workflow that requests two dumps from a live recording. Every dump must be coherent and balance its closure, carry both ends of its own parked interval (the two must differ), state in its notes that the interval deliberately has no stack coverage, refuse a second concurrent request without releasing the running dump's target, leave no raw trace or work directory behind, and let capture attach again once it settles. Core and session suites were rerun after the change.
- **Not verified:** the graphical client (no display in this environment) and a real modded game heap. Those require a manual run on the game. Linux/macOS game workflows remain unvalidated.

## Heap-dump parking measurement — 2026-10-07

Asks whether a dump needs the target to itself, what exactly breaks the walk when it does not, and
what it is about a session that does the damage. One deterministic target heap (a rooted binary tree
of 300,000 objects, ~21 MiB) and one oversized buffer (1024 MiB, so the ring could never be the
cause: the largest trace was 246.6 MiB) were held constant, and what attached to the target was the
only variable. Run with `Ember.Trace.Session.Tests --dumppark-only [arm]`; each arm copies its
aggregate to `test-runs/parking/` (local evidence, excluded from the repository).

| what was attached to the target | span | walked objects | distinct addresses | unattributable | complete |
| --- | --- | --- | --- | --- | --- |
| nothing | 16 ms | 300,454 | 300,454 | 0.04% | yes |
| one segment, attached for the whole walk | 17 ms | 303,900 | 303,900 | 0.09% | yes |
| nothing, but a forced blocking gen2 collection every second | 16 ms | 300,589 | 300,589 | 0.06% | yes |
| segments every 2 s, rotating throughout | 33.24-33.35 s | 4,553,580 | **303,949** | 93.33% | no |
| segments every 5 s, first rotation inside the dump | **74.8-75.0 s** | 4,553,690 | **303,949** | 93.33% | no |
| production path (live segment parked first) | 18 ms | 303,901 | 303,901 | 0.09% | yes |

- **A session that stays attached is not what breaks the walk; a session that attaches is.** One
  segment attached for the whole dump measured 0.09% unattributable against 0.04% with nothing
  attached, so a coherent snapshot needs the absence of attach cycles, not the absence of sessions.
- **A collection is not the trigger either.** With no session attached at all, a forced blocking
  compacting gen2 collection every second put **90** collections inside a 90.8 s capture window, and
  the trace still held **one** walk: 300,589 objects, a 16 ms span, 0.06% unattributable and a
  balanced closure. The rotating arms varied sessions and whatever collections that churn caused in
  the target at the same time, which is why earlier runs could not say which of the two re-walked the
  heap.
- **The runtime answers every attach with a whole walk, broadcast to every session that asked for
  one, so the heap is described once per attach.** Both churning arms walked about fifteen times the
  objects of a single walk (4,553,580 and 4,553,690 against 303,900), but their traces carried only
  **303,949 distinct addresses** — fifteen complete copies of a heap that never moved — and the
  reachable remainder was one walk's worth (~21 MiB). The segment sessions do not request heap-dump
  events themselves (their CLR mask is GC sampling and type names, not `GCHeapDump`), so what
  triggers the walk is the attach, not a new subscriber asking for one. Addresses are identity in a
  heap dump, so the
  snapshot was not incoherent: it was the same coherent heap fifteen times over, and the 93.33%
  "unreachable" was our own merge, which roots one copy and counted the other fourteen as heap. That
  also corrects the reading recorded below under 2026-10-06, where the same shape was attributed to
  phases of one walk. At the time the repetition was reported rather than merged: the notes named
  repeated walks instead of claiming the objects and roots describe different moments, and the viewer
  explained the repetition instead of prescribing a larger buffer. The entry below merges the copies,
  which turns such a trace into a correct snapshot rather than an incomplete one.
- **Protecting only the walk's first burst is not enough.** The five-second arm asked for the dump
  seconds after a segment started, so the first rotation landed well after the dump session was
  open. The snapshot was still repeated fifteen times (74.8-75.0 s span, the same 303,949 distinct
  addresses, 93.33% unattributable before the copies were collapsed; **0.13%** after, see the merge
  entry below), which is why the supervisor holds the target for the whole dump session rather than
  for its first burst.
- The buffer was never the explanation in any arm: the largest trace used 24.1% of its cap, and the
  earlier trace that lost 91-96% used 0.75% of a 1024 MiB cap.
- The rotating arm reproduced across six runs to within 110 ms of span (**33,240 / 33,243 / 33,245 /
  33,334 / 33,346 / 33,350 ms**) with the same walked-object count in every run, so the effect is not
  run noise. The four single-walk arms show the diagnostic cannot fire falsely: distinct addresses
  equalled walked objects exactly in every one.
- Separate observation from the same runs: **capture time is not the walk.** With nothing attached
  the worker closes its session after its eight-second quiet gap (9.2 s), while with a segment
  attached the stream never goes quiet and capture ran to the 90 s budget — for the same 19.6 MiB
  trace, one complete walk and 0.09% unattributable. Capture time therefore bounds the parked
  interval, and a long capture is not by itself evidence of a long or a bad walk.
- Game-free regression: Core 27/27, and the full session tier at **98 assertions**, including the
  parked workflow this measurement confirms, a check applied to both dumps of that workflow that a
  parked dump describes each address once, and the chat wording for a repeated trace.


## Heap-dump repeated-walk merge — 2026-10-07

The parking measurement above left one open improvement: the arms whose target was attached to
during the walk carried the same heap about fifteen times over and were published as an incomplete
snapshot, when the trace actually held a correct snapshot repeated once per attach. The analysis now
collapses those copies onto the walk that arrived first, before any total is computed, so the heap is
described once and `walkedNodeCount` keeps what the trace carried.

The same six-arm run (`--dumppark-only`, one rooted 300,000-object heap, one 1024 MiB buffer) after
the change:

| what was attached to the target | trace | span | walked objects | objects described | unattributable | complete |
| --- | --- | --- | --- | --- | --- | --- |
| nothing | 16.4 MiB | 18 ms | 300,454 | 300,454 | 0.04% | yes |
| one segment for the whole walk | 19.6 MiB | 20 ms | 303,900 | 303,900 | 0.09% | yes |
| forced blocking gen2 every second, nothing attached | 16.7 MiB | 16 ms | 300,589 | 300,589 | 0.06% | yes |
| segments every 2 s, rotating throughout | 245.2 MiB | 33,244 ms | 4,553,580 | **303,949** | 0.13% | **yes** |
| segments every 5 s, first rotation inside the dump | 246.6 MiB | 74,949 ms | 4,553,690 | **303,949** | 0.13% | **yes** |
| production path (live segment parked first) | 16.6 MiB | 19 ms | 303,901 | 303,901 | 0.09% | yes |

- **The two churning arms now describe the same heap as the parked arm.** **303,949** objects and
  **20.9 MiB** against 303,901 and 20.9 MiB parked, so the 93.33% "unreachable" was the merge and not
  the heap. The four arms whose target was not attached to are unchanged, address for address.
- **The kept copy is one walk, not a mixture.** The two churning arms differ only in how fast their
  segments rotate (2 s against 5 s) and collapsed to byte-identical heaps — **21,948,514** bytes,
  **303,949** objects and **28,206** unreachable bytes in both — while their raw traces differ by
  1.4 MiB. Holder shares sum to **100.00%** in every arm, so the per-owner partition survives the
  collapse rather than merely looking plausible at the totals.
- **What the repetition still costs is reported.** The copies cost the trace (**245.2** MiB against
  **16.4** MiB), the capture (**42.7** s against **9.2** s) and the span (**33.2** s against **18**
  ms), so the aggregate keeps `walkedNodeCount`, the notes say the copies were collapsed, and the
  chat line says how many whole walks the trace carried. Parking still buys the trace size and the
  capture time, which is what the coordinator parks for.
- **The collapse is by address and keeps the copy that arrived first.** An address identifies one
  live object for as long as it lives, so the copies of an unchanged heap carry identical addresses
  and collapsing them is exact; keeping the first one means the kept nodes, the edges read for them
  and the roots that resolve into them all come from a single walk. An address that was freed and
  reused between two walks would be merged into the kept object, and the note states that assumption
  rather than hiding it.
- **A first implementation was wrong in a way the arms caught.** Collapsed indices were assigned in
  address order while the edges are read in arrival order, so the per-node child range stopped being
  a prefix sum, the wrong edges were read and the dominator pass threw `IndexOutOfRangeException` on
  the rotating arm while the single-walk arms passed. Indices are now ranked by arrival, and the arms
  are the regression test: an arm that repeats walks has to collapse to one walk's worth of objects.
- Game-free regression: Core **27/27**, the full session tier at **98 assertions** (both dumps of the
  parked workflow assert that the trace walked each address once), session analysis, the standalone
  trace harness as documented, and the browser suite, whose fixtures now include a heap-dump card
  with a repeated walk, a parked interval and a refused request.


## Heap-dump failure reporting — 2026-10-07

A real client recording (81.6 minutes, 75 segments, 166 mods, `manual stop`) answered **seven** heap
dump requests: six completed with a coherent snapshot next to a parked capture (**1.69–1.77%**
unattributable, **0.9–1.7 s** of blocking GC induced by each walk, buffer occupancy **34–36%** of
4096 MiB), and one failed.

- **The failure hit two limits at once and named only one of them.** Request 6 arrived in the 175 ms
  gap between two segments, so no capture was parked; the same window contained **590 MB/s** and
  **1159 MB/s** of allocation, the managed heap grew **3.58 → 5.33 GiB in three seconds** and the
  process reached a session peak of **12.87 GiB** working set. The trace came back at
  **4,272,926,726 bytes = 99.49%** of the 4096 MiB buffer, which means the ring discarded the walk's
  oldest events, and the analysis then hit its 40,000,000-object budget and threw before the buffer
  check it already contained could run. The text named `HeapDumpMaxNodes` as the fix, which is the
  one budget that cannot make a trace whose roots were overwritten readable, and **no note reached
  the aggregate**: `notes: []`, so the report could not be read to tell which limit had fired.
- **The buffer is now evaluated before the walk is read**, so a capture that filled it is reported
  even when the object budget stops the analysis first; a failure that hit both names the buffer and
  the size to raise (`HeapDumpBufferMegabytes above 4075`) and still reports the object budget. The
  failed aggregate carries that text in its notes as well as in its error, and the raw trace is still
  deleted.
- **The summary and the findings now mention dumps at all.** `SUMMARY.md` carries a **Heap dumps**
  section — every request with status, managed heap, largest holder, trace against the buffer it had
  to fit in, and what it cost in stack coverage — with failed and refused requests listed by reason,
  and `Analysis.Findings` raises a warning for a dump that was not taken or whose totals are a lower
  bound. Before this, seven dumps ran and the first document a person opens did not say so.
- **The mechanism explanation is recorded once per recording.** It carried the dump's measured
  seconds, so the per-dump text differed, the "already present" check never matched, and six
  near-identical copies of a 794-character note were appended to the session notes. The explanation
  is now free of per-dump measurements and the numbers moved to a one-line per-dump note.
- **Reproduced end to end in the sandbox.** `Ember.Trace.Session.Tests --dumpfail-only` runs one
  isolated dump against the fixed 300,000-object target with an 8 MiB buffer and a 1,000-object
  budget: the trace measured **7,665,562 bytes (7.3 MiB)**, the worker exited **1**, the aggregate
  was `failed` with `nodeCount` 0 and its error named the buffer first (`HeapDumpBufferMegabytes
  above 8`) and the object budget second, the notes carried the same reason, and `capture.nettrace`
  was gone. Core adds the message boundary itself (both limits, buffer-only, object-budget-only) and
  the summary/findings/section assertions on a six-dump fixture, including the dump whose notes
  classify as saturated and the one that produced no analysis at all.
- **Left open, and stated:** the two defaults still sit close to a modded client's reality. A
  3.3 GiB heap measured **28.0M objects** and a **1.47 GiB** trace, which is 70% of the object
  budget and 36% of the buffer, and the failing window exceeded both. Raising either is a real cost
  on the player's machine (`HeapDumpMaxNodes` in worker memory, `HeapDumpBufferMegabytes` in the
  target's ring), so the report now says which limit was hit and by how much instead of the default
  being changed silently.


## Heap-dump budgets, cancellation and failure records — 2026-10-07

A second real client recording (34.0 minutes, `singleplayer: false`, 166 mods, `manual stop`) answered
**six** heap dump requests next to a live recording: **four completed** with a coherent snapshot,
**one failed**, and one was still running when the recording was stopped by hand. The four completed
dumps measured **2.82–3.17 GiB** and **26.1–27.6M objects** at **1.68–1.88%** unattributable, each
walk inducing **1.0–2.2 s** of blocking GC and a **2.7–3.8 s** frame gap: five of the twelve selected
incidents in that recording are the dumps themselves.

- **The failed dump left nothing at all, while the supervisor knew the reason.** Dump 2 ended as
  `failed` with `"the worker exited without an aggregate"`, `notes: []`, and no mark in the event
  timeline. The reason was in `session.notes`, anonymous: *"Heap dump aborted: the heap dump exceeded
  its time budget."* — without the index, among unrelated notes. The failing dump was therefore
  indistinguishable from one never requested, and its interval read as a game problem.
- **The budgets made that outcome unavoidable, and they are what was fixed.** The worker's own
  ceiling was `TimeoutSeconds + 30` (70 s of capture plus a bounded 20 s session teardown) while the
  supervisor killed at `TimeoutSeconds + 45`, so the reduction — the longest phase, reading a trace
  as large as the heap — had fifteen seconds and **no cancellation at all**: `Analyze` took no token
  and checked none. A dump that overran was unreachable and unreportable at the same time.
  `HeapDumpBudget` now bounds the phases separately — capture `t+30`, reduction
  `clamp(t, 45, 120)` armed when the reduction starts so an early capture does not lend it time it
  never declared, and the kill `PublishGraceSeconds` (15 s) behind both — and the supervisor asks the
  worker to stop over its standard input before it kills it, the same channel a recording uses for
  its own children.
- **The reduction is interruptible in both of its halves**, which is what turns an overrun into a
  record: the deadline is checked in the walk's bulk callbacks and once per dominator sweep, and a
  stopped reduction keeps the walked-object count it had reached.
- **A failed dump keeps no heap figures.** An interrupted reduction has already counted objects and
  bytes, and an aggregate carrying a half-computed heap is still a heap somebody can quote: `Fail`
  clears the object count, the reference count, every byte total and all four tables. What survives
  is the trace size, the walked count, `CompletedUtc`, `FailureKind` and the message.
- **The cause travels as a value.** `HeapDump.FailureKind` is one of `buffer`, `node-limit`,
  `time-budget`, `no-walk`, `interrupted`, `supervisor-gone`, `not-started` or `refused`, and
  `HeapDumpReport.Failure` turns it into the cause plus the one next step, so the findings send a
  reader to `HeapDumpBufferMegabytes`, `HeapDumpMaxNodes` or `HeapDumpTimeoutSeconds` according to
  what stopped the dump instead of to all three. A record written before the kind existed is
  classified from its text, so an older bundle reads identically.
- **A dump that produced nothing marks the timeline** at its `CompletedUtc` — or, for a record that
  carries none, at the moment it was requested — and the abort note now carries the dump index.
- **Measured end to end in the sandbox, three failure paths.** `--dumpfail-only` runs the
  buffer-plus-object-budget failure unchanged (7.3 MiB trace against an 8 MiB buffer, `nodeCount` 0,
  buffer named first); a dump whose one-second reduction budget cannot cover a 6,000,000-object walk,
  which exited **1** with `time-budget`, reduction **1468 ms**, trace **321.2 MiB of 1024**, walked
  **6,000,455**, `nodeCount` **0**, a message naming *"reducing the walk to an aggregate"* and
  `HeapDumpTimeoutSeconds` alone, the raw trace deleted, and the worker stopping itself **11.6 s**
  into a run whose supervisor kill would have come at 56 s; and a dump asked to stop over its
  standard input, which recorded `interrupted`, published no heap figures and also deleted its trace.
  Core gained the budget ordering (worker before kill at three timeouts, the reduction following the
  timeout and capped), the classification of every kind including legacy text, and the
  timeline/summary/finding assertions on a seven-dump fixture.
- Game-free regression: Core **29/29**, session analysis **9 checks**, the full session tier at **122
  assertions**, and the browser suite, whose fixtures now include a dump that ran out of time and has
  to be named as such rather than shown as an empty heap.


## Heap-dump hold — keep the running segment, release at the capture — 2026-10-07

The parking design held the *whole* target for the *whole* dump, and the client showed what that
cost: **305 s of a 895 s** recording (4 dumps) had no new stack capture, while the four walks
together spanned **6.4 s** and cost four freezes of **2.6–3.4 s**. Two things were wrong with it.
The hold covered the reduction (**11.5–12.6 s** per dump), which only reads a file, and it stopped
the segment that was already capturing, which the measurement did not require: a session that merely
stays attached measured **0.09%** unattributable against **0.04%** with nothing attached, so only a
new *attach* re-walks the heap.

- **A detach does not re-walk the heap, and that was the open question.** The seven-arm measurement
  gained a production arm whose segment is short enough to stop itself inside the capture. Its walk
  is one walk - **303,901** walked objects against **303,901** described, **0.09%** unattributable,
  `complete` true - and the segment it left alone ran its full **4.0 s** instead of being stopped for
  the dump. The coordinator therefore holds new segments back from the moment it consumes a request
  until the walk has been captured, and does not touch the segment that is already attached.
- **The hold now ends at the capture, not at publication.** The worker closes its EventPipe session
  before it reports `CAPTURED`, so no later attach can reach a trace that is already closed while the
  reduction still has to run. Measured: **12.9–13.1 s** held for a **12.8 s** capture, against the
  earlier design's **10.0 s** for a **9.2 s** capture and its **0.3 s** reduction. Both coordinated
  arms assert `resumedUtc < completedUtc`, which is what separates the two designs in the aggregate.
- **A hold is recorded even when it landed in a gap between segments.** A request that arrives while
  no segment is capturing still blocks the next one for the whole capture, and a recording that
  attaches whenever it can would have started one within a poll: that interval is as real a cost as
  one that stopped a live capture. The aggregate says which of the two it was - a capture that kept
  running, or nothing at all - instead of reporting the gap case as costing nothing. The client's
  first failure was exactly this case (a request 175 ms after a segment ended).
- Seven-arm run (`--dumppark-only`, one rooted 300,000-object heap, one 1024 MiB buffer, 90 s budget):

| what was attached to the target | trace | span | capture | walked objects | objects described | unattributable | complete |
| --- | --- | --- | --- | --- | --- | --- | --- |
| nothing | 16.4 MiB | 15 ms | 9.2 s | 300,455 | 300,455 | 0.05% | yes |
| one segment for the whole walk | 19.6 MiB | 16 ms | 90.2 s | 303,901 | 303,901 | 0.09% | yes |
| segments every 2 s, rotating throughout | 245.2 MiB | 33,130 ms | 42.6 s | 4,553,595 | **303,950** | 0.13% | **yes** |
| segments every 5 s, first rotation inside the dump | 246.6 MiB | 74,652 ms | 84.1 s | 4,553,705 | **303,950** | 0.13% | **yes** |
| forced blocking gen2 every second, nothing attached | 16.7 MiB | 17 ms | 90.9 s | 300,590 | 300,590 | 0.06% | yes |
| production hold, segment outlives the capture | 19.6 MiB | 16 ms | 90.2 s | 303,901 | 303,901 | 0.09% | yes |
| production hold, segment stops itself inside the capture | 16.7 MiB | 16 ms | 12.8 s | 303,901 | 303,901 | 0.09% | yes |

- **What is left of the cost is the capture itself, and that is the transfer.** The hold equals the
  capture (90.3 s held for a 90.2 s capture in the arm whose stream never goes quiet, 12.9 s for
  12.8 s in the one that does), and the capture is the transfer: the client's four walks spanned
  1.5–2.7 s each while their traces took ~63 s to leave the runtime at a measured **22 MB/s**, against
  **4.6 GB/s** for the same volume written directly to that volume. The remaining lever is therefore
  the transfer, not the coordinator: a later change that resumes segments during it would put whole
  extra walks into the trace, which the analysis collapses but the buffer, the capture and the
  reduction all pay for.
- **The release stamp is the worker's, not the coordinator's.** The worker stamps `capturedUtc` the
  moment its capture returns — its EventPipe session is closed and the target is free — while the
  coordinator only notices within a poll. Taking the coordinator's observation for the release made
  the reported hold outlast the walk by up to a poll, and the workflow assertion caught it on the
  second dump (`resumed` 100 ms after `published`). The stamp now comes from the worker, so the
  assertion holds by construction rather than by timing.
- Game-free regression: Core **29/29**, session analysis **9 checks**, the full session tier at
  **124 assertions** (both dumps of the parked workflow assert that the hold was released before the
  aggregate was published and that the trace walked each address once), the seven parking arms above,
  and the browser suite, whose heap-dump card now shows the held-back interval instead of a parked
  one.


## Heap-dump freeze, coverage split and change between dumps — 2026-10-07

The third client bundle (`ember-client-20261007-115029-3643d160`, mod `15e6e323…`, 512.6 s, 8 × 60 s
segments, three dumps, 166 mods, 12,841 frames observed and none omitted) measured the design that
keeps the attached segment running, and priced what the earlier entries had only argued about.

- **The hold stopped being the cost.** Coverage rose from **65.0%** to **84.9%** (435.3 s of 512.6 s).
  The three holds totalled **189.0 s** – **36.9%** of the recording had no *new* stack capture – but the
  gap was **77.3 s**, of which **74.8 s** fell inside them; the rest was segment rotation and the start
  of the recording. Per dump the loss is the time from the end of the segment that was running to the
  release: **9.3 s**, **24.7 s** and **40.1 s**, **24.7 s on average against 50.9 s** per dump in the
  previous design. The report now carries the split as `uncoveredSeconds`,
  `uncoveredDuringHeapDumpHoldsSeconds` and `heapDumpHoldSeconds` and states it in a session note, so
  the summary no longer has to leave a 15.1% gap to "attach, rundown, parser backpressure and retry".
- **The walk's own stop is measured, and it is not the span.** The longest completed frame interval
  began **0.101 s**, **0.103 s** and **0.109 s** after each request and lasted **2,345 ms**, **2,806 ms**
  and **2,818 ms**, against a walk span of **1,465 ms**, **1,376 ms** and **1,416 ms**. The runtime GC
  pause recorded in the same windows was **809 ms**, **1,279 ms** and **1,280 ms** (a gen2 collection
  each), and pause plus span accounts for the freeze within 0.15 s in all three. So the walk is emitted
  once, during a blocking collection, at the start of the hold: the freeze is the collection plus the
  emission, the span is the emission window, and the remaining ~60 s of the capture is the ring
  draining afterwards while the game runs normally.
- **The hold is not that freeze.** Each hold equalled its capture (**62.8 / 63.1 / 63.2 s** held for
  **62.7 / 63.0 / 63.1 s** capturing) and `resumedUtc` equalled `capturedUtc` to the millisecond, so the
  reduction is outside it. Inside a hold the pacing is the recording's own: p50 **47.19 ms** against
  **46.99 ms** elsewhere, 32.9% of frames at or above 50 ms against 31.2%, and only the attach window
  carries the freeze.
- **Two hitches inside those holds are not the dumps, and that is why the window is narrow.** The
  recording had seven hitches of ≥1 s: three are the dumps (above) and four are the game's own blocking
  collections (**1,587 / 1,449 / 1,426 / 1,405 ms**). Two of the four fell *inside* dump holds, at
  **+35.3 s** and **+48.5 s** after the request. Attribution is therefore by the measured freeze start
  within **5 s** of the request (250 ms of lead allowed) and by the incident that contains it, not by
  "the incident happened while a dump was running".
- **What changed.** `HeapDump` gained `freezeMilliseconds`, `freezeStartUtc`,
  `freezeGcPauseMilliseconds` and `deltaSincePrevious`; the incident that contains a walk carries
  `heapDumpIndex` and says so in its evidence and its first hypothesis; the findings price the dump
  (`Heap dump 1 stopped the client for 2.30 s`) instead of leaving the largest stall of the recording
  unexplained; `SUMMARY.md` gained a **Client freeze** column and, for recordings with more than one
  completed dump, a **Change since the previous dump** table; the viewer shows the freeze on the card
  and an expandable before/now/change comparison of the totals, owners and types.
- **The wording was wrong and is fixed.** The hold note said "frames and metrics were never
  interrupted", which the same report contradicted with `max 2818.04 ms`. It now says the journal was
  never interrupted and that the walk itself stops the client once, during the blocking collection the
  runtime performs it in.
- **The comparison is between aggregates, so it costs nothing and can be negative.** On this recording
  dump 2 was **116.3 MiB smaller** than dump 1 with **50,150 more objects** (a collection had run
  between them), and dump 3 was **3.8 MiB larger** than dump 2 with **8,673 more objects**. Largest
  owner changes 1 → 2: `VintagestoryAPI.dll` +6.0 MiB, `VintageHorizons.dll` +1.08 MiB,
  `Ember.Core.dll` +1.02 MiB. A difference against a dump that is a lower bound is flagged
  `lowerBound` and must not be read as growth.
- **The session tier found a defect in the worker, not in the new code.** The supervisor keeps its own
  `SessionInfo` and swaps it into the report after assembly, copying the assembled coverage fields one
  by one; the three new fields were not copied, so the export lost the split while the note that
  describes it survived. The workflow now asserts the split on the exported bundle, which is what
  caught it.
- Game-free regression: Core **30/30** including a new fixture with exact timings (a 2,300 ms walk
  freeze inside the window, a 400 ms one, a 1,500 ms game collection inside the same hold, a covered
  and an uncovered hold, and a synthetic journal with no frame in a window for the unmeasured case),
  the heap-dump arms of the session tier, and the browser suite, whose heap-dump card now shows the
  freeze, an unmeasured freeze and a two-dump comparison.
- **Not verified:** these fields on a real client run, because the bundle above predates them. The
  unmeasured branch has only been exercised in the fixture: on the sandbox target the recording keeps
  no frames near a dump, so both dumps there report an unknown hitch. The freeze window (5 s) is
  calibrated on three client dumps and three sandbox arms, all of which attached in about 0.1 s.

## Initial public repository preparation

The public-source preparation adds documentation, curated images, community templates, CI, maintainer/repository metadata and packaged documentation. It does not change the recording or viewer algorithms. Before the initial push, local Windows validation passed:

- Core **24/24**, session analysis **9 checks**, and external supervisor **58 assertions**, rerun with .NET 10.
- The complete synthetic viewer suite, including hostile text, scoped exports, keyboard/mobile behavior and the 400,000-sample / 200,000-frame fixture.
- Tracked-document link/version/file-hygiene checks; YAML parsing, PowerShell parsing and Bash build-script syntax.
- Package build with zero warnings/errors. The existing, unpublished 1.1.2 ZIP is rebuilt to include the public documentation, AI disclosure and bbauti/repository metadata; previous local build evidence is archived privately. Its checksum is generated alongside the ZIP.
- A separate AI-agent review of the staged repository found no material publication blockers. It was a source/documentation review, not another test execution or a human audit.

There is no new gameplay soak for this preparation. Local results do not imply that a hosted GitHub Actions run has passed; consult the workflow's actual status for the relevant commit.

## Version 1.1.2 recording-first refinement — 2026-09-19

This viewer pass implements the user's request for a less template-like diagnostic interface. Capture date, duration, frame count and coverage replace the introductory slogan; flat incident rows and underlined phase controls reduce repeated outlines; sampling explanations move to a labelled disclosure; controls and the flame graph move higher in the desktop viewport.

- `node viewer/verify.cjs`: **passed**, including existing report sections, range/source alignment, keyboard controls, baseline comparison, scoped JSON export, hostile-text handling, empty reports and the 400,000-sample / 200,000-frame stress case; zero browser errors.
- `EMBER_REVIEW_NAME=recording-first node viewer/review.cjs <report.json>`: **passed** using the actual 30-minute capture in a direct offline HTML file. Desktop 1440×1050, wide 1920×1080 and mobile 390×844 screenshots are under `.impeccable/review/recording-first/`. Checks cover document overflow, visible active incidents, in-place marker selection/focus, mobile flame–inspector–results order, method-detail navigation, repeated pagination focus through both boundaries, reduced motion, and sampling-detail access to measurement/export semantics. The flame graph begins above 750px at both desktop widths.
- `dotnet run --project tests/Ember.Tests/Ember.Tests.csproj -c Release`: **24/24 passed**, including embedding/export and manifest round trips.
- Independent visual/source review of all six recording-first captures: **ship**, no material fixes requested for this refinement.
- No new gameplay capture was needed to exercise this viewer refinement; the original 30-minute session is the same historical recording documented below. Version 1.1.1 release artifacts are retained separately.

## Version 1.1.1 investigation viewer — 2026-09-19

The user-selected Expediente/Laboratorio combination is promoted into the embedded production viewer. The default Investigate workspace combines an incident queue, session timeline, context/phase controls, filtered stacks, flame zoom, method inspection, evidence disclosure, and scoped JSON export.

- `node viewer/verify.cjs`: **passed** legacy/new/empty reports, all existing diagnostic sections, shared time-origin and end-exclusive ranges, dedicated incident sample sources, baseline differences, hostile text rendering, 400,000-sample / 200,000-frame responsiveness, keyboard focus after flame/method/phase activation, scoped selection exports and zero browser errors.
- `node viewer/review.cjs <report.json>`: **passed** with the actual 30-minute capture `07444bd0fb854fe682df35032966599b`, using the production template and a direct local-file URL. Desktop 1440×1050, wide 1920×1080 and mobile 390×844 had no document overflow; selected incidents remained visible in the queue, timeline-marker keyboard activation retained focus, reduced motion was respected, and no console/runtime errors were recorded.
- `dotnet run --project tests/Ember.Tests/Ember.Tests.csproj -c Release`: **24/24 passed**, including the newly embedded HTML's export, hostile-data handling and manifest round trip.
- Viewer evidence: `.impeccable/review/workbench/verification.json` and screenshots; standalone real-data preview: `test-runs/viewer-workbench/report.html`.
- Independent finish review: mobile inspector ordering, pagination focus through both boundaries, and hotspot-column readability were corrected; the reviewer scored all three fixes resolved. Real-report checks also verify the selected method's explicit detail link and focus behavior.
- This patch's changes are the viewer, version metadata and documentation. The lifecycle and 30-minute recording measurements below belong to the historical 1.1.0 package, not a new gameplay run of 1.1.1.

## Version 1.1 session verification — 2026-09-19

- Core collection/export regression checks: **24/24 passed**, including bounded asynchronous journaling, partial-window flush, session isolation, disk limits and manifest hashes.
- Session assembly/analysis checks: **9/9 passed**, including a synthetic complete 30-minute journal, segment-density-aware sampling, pre/during/post incident evidence, truncated journal recovery, usable-prefix coverage and path validation. A nominal 60-second segment with only 40 usable seconds credits 40 seconds, even when its last reservoir-retained observation is at 20 seconds; an incident at 50 seconds receives zero coverage.
- Offline viewer: passed old/new/empty reports, time alignment, incident filters, coverage gaps, baseline differences, 400,000-sample / 200,000-frame stress fixture, mobile layout and zero browser errors.
- External session supervisor: **58 assertions passed**, covering real EventPipe rotation, startup cancellation, cancellation racing an automatic stop, parent exit/broken output pipe, duplicate supervisor locking, interrupted supervisor/raw recovery, failed export retry, unavailable diagnostics, disk/duration limits and process identity. Completed segments discard raw/TraceLog artifacts during recording after durable parsed-profile/checkpoint commit; failed parsing retains recovery material. Concurrent snapshot testing also exercises three simultaneous readers and 100 replacements on Windows, using both client and worker publishers; no sharing errors or torn JSON. Forced conversion-limit/truncated-trace checks verify usable endpoints independently of the retained reservoir.
- Real graphical lifecycle: **all 9 scenarios passed** on the initial 1.1 integration package (`9dd6c5c179f02a48c8a2d23d7d4b42990414d0267728b9a7563b83d9005a8f3b`): multiplayer manual stop, bare profile command, leave, graceful shutdown, cancel, immediate stop, duplicate start and same-process reconnect; singleplayer leave. Ten world sessions produced nine exports, with cancellation producing none. Graceful shutdown exported 4.024 seconds after client exit. Evidence: `test-runs/session-game/lifecycle-verification-20260919.json`.
- Graphical capture data: **16,631 frames**, **1,292,664 retained stack observations**, **16,634 actual GPU measurements**. Every full-length capture completed four segments, with 92.74–93.29% usable stack interval coverage and explicitly reported transition gaps; frame/runtime start/end coverage passed. A loopback dedicated server had no Ember installed, and the client joined as the ordinary `suplayer` role. This does not test external public-server authentication/network conditions.
- Descriptive frame-interval means: baseline 16.61–16.92 ms; recording 16.79–17.36 ms. Recording included injected stalls/allocations; these sequential observations are **not a controlled profiler-overhead estimate**. GPU elapsed measurements were finite; weighted means 0.20–0.49 ms, maximum 10.57 ms.
- Updated-package regression: **3/3 passed** on `0ec085313f91e90e4339311286d11c276c18537832e31fe90fbdaa8733ad96d7`: cancellation (zero exports), immediate stop (122.5 ms), graceful shutdown (35.501-second capture; export committed 4.434 seconds after client exit). Evidence: `test-runs/session-game/20260919-200417-556-9abcd7/regression-verification.json`.
- Actual **30-minute multiplayer soak passed** on that updated package: **1,800.216 seconds**, **107,384 frames**, **1,801 runtime windows**, **199,984 retained stack observations**, **30 completed segments**, and GPU measurements in all 1,801 windows. Usable stack interval coverage was **98.974956%** (1,781.763 seconds); the remaining **18.453 seconds** are explicitly reported transition gaps. Both the first and last frame/runtime windows were retained with ordinary `HistorySeconds=900`, `MaxTickRecords=120000`, and `SessionSegmentSeconds=60`; no history extension was needed. Evidence: `test-runs/session-game/20260919-201249-741-a395cd/Multiplayer-manual/verification.json`.
- Soak descriptive frame intervals: baseline mean 16.736 ms / recording mean 16.765 ms; recording p95 18.588 ms, p99 22.198 ms, max 902.619 ms. Collector windows averaged 6.636 ms, p95 7.174 ms, with a **886.991 ms outlier**. These wall-time observations include scheduling, workload and injected-test effects; they do not isolate instrumentation overhead or establish its cause. A representative controlled overhead benchmark remains outstanding.
- All lifecycle/soak clients and helpers exited; temporary raw traces and copied settings/backups were removed. The final release is rebuilt only to incorporate this verification documentation; its binary payload is compared against the updated package used for the soak.

Commands for the new checks:

```powershell
dotnet run --project tests/Ember.Tests/Ember.Tests.csproj -c Release
dotnet run --project tests/Ember.Session.Tests/Ember.Session.Tests.csproj -c Release
dotnet run --project tests/Ember.Trace.Session.Tests/Ember.Trace.Session.Tests.csproj -c Release
node viewer/verify.cjs
pwsh -File scripts/test-session-client.ps1 -Package artifacts/emberdiagnostics-1.1.0.zip
```

## Historical version 1.0 verification — 2026-09-19

The following results were obtained in this workspace. The later checklist describes additional release scenarios, not an assertion that every platform and scenario has been exercised.

| Area | Recorded result |
| --- | --- |
| Release/net10.0 build against game 1.22.7 | `dotnet build Ember.slnx -c Release --no-restore` and `./scripts/build.ps1` passed on Windows with SDK 10.0.110; zero warnings/errors. |
| Worker publish and ZIP inspection | Framework-dependent publish and ZIP creation passed. Root mod files, complete tracer output and embedded viewer present; game DLLs excluded. |
| Automated project checks | `dotnet run --project tests/Ember.Tests/Ember.Tests.csproj -c Release`: **21/21 passed**. Includes bounded collection, statistics, redaction, compact context, ZIP hashes, JSON/CSV/Speedscope and hostile HTML. Results: `test-runs/core/results.json`. |
| Real graphical client integration | **Passed twice**, Vintage Story 1.22.7, isolated singleplayer worlds, 960×540. Final production ZIP: commands `mark`, `health`, `profile 30`, and `export` succeeded. Profile: **30.017 s, 200,000 retained samples, 467 stack frames, 38 threads**, and **2,529 rendered-frame records**. Manual export: 2,614 frame records. Mod inventory/stages/markers/Speedscope/LLM context/manifest verified. Also verified reservoir retention reaches the capture tail and hotspot self totals match retained observations. Final results: `test-runs/client/verification.json`; first 8-second run retained in `test-runs/client-first/`. |
| Client-only boundary | `ShouldLoad(Client)=true`, `ShouldLoad(Server)=false`, Ember absent from integrated-server systems. No server commands or diagnostic network channels. Public-server connection itself has **not** been tested. |
| EventPipe capture, stop/cancel and failure handling | Full durable harness passed normal capture (7,436 samples), early stop (0.73 s), cancel (exit 2/no profile), size limit (12,915,090 bytes within 16 MiB), and invalid PID (exit 1/no profile). Verified resolved signatures, multi-owner mapping, recursion-correct counts, allocations/exceptions/contention and child-process cleanup. Results: `test-runs/trace/20260919T185818351Z-4ba214c9/results.json`. |
| Viewer/export interoperability | `node viewer/verify.cjs` passed all eight sections, embedded/empty reports, file import, baseline charts, flame zoom, lazy call tree, thread/managed filters, hostile strings, invalid input, 390px mobile layout, and zero browser errors. |
| Collection/profile overhead | Not benchmarked. |
| Bash build on Linux/macOS | Pending. |

Script syntax checks: PowerShell parser passed; Git Bash `bash -n scripts/build.sh` passed. These syntax checks do not establish Linux/macOS runtime compatibility. Rebuild after final source/documentation changes and record the resulting package hash.

## Build procedure

From the repository root, run one of:

```powershell
dotnet --info
./scripts/build.ps1 -GamePath 'C:\path\to\Vintagestory'
```

```bash
dotnet --info
bash scripts/build.sh --game-path /path/to/vintagestory
```

The SDK must support .NET 10. The scripts restore/build `src/Ember.Mod/Ember.Mod.csproj` in Release and publish `src/Ember.Trace/Ember.Trace.csproj` in Release with `--self-contained false -p:UseAppHost=false`. They pass the `modinfo.json` version into MSBuild and derive the ZIP name from it. Temporary package staging is isolated under `artifacts/` and cleaned after success or failure. A failed build or publish must not replace the last successful archive.

PowerShell uses `System.IO.Compression`; Bash uses Python 3's standard-library JSON and ZIP support. Both accept paths containing spaces and are independent of the caller's working directory.

## Package inspection

- [ ] ZIP is named `artifacts/emberdiagnostics-<modinfo version>.zip`.
- [ ] `modinfo.json`, `Ember.dll` and `Ember.Core.dll` occur at ZIP root, with no extra wrapper directory.
- [ ] Root `.ignore` contains `*tracer/*`. This is required to prevent Vintage Story treating worker dependencies as mod assemblies. The rule was verified in the graphical test.
- [ ] Metadata declares client side, optional client/server installation, and game dependency 1.22.7.
- [ ] `tracer/` includes `Ember.Trace.dll`, `.deps.json`, `.runtimeconfig.json`, `Ember.Core.dll` and all NuGet publish dependencies.
- [ ] Worker runtime configuration targets `net10.0` / `Microsoft.NETCore.App` 10.x and is framework-dependent.
- [ ] No `Vintagestory*.dll`, `0Harmony.dll` or game-provided `Newtonsoft.Json.dll` occurs anywhere in the ZIP.
- [ ] README, docs, LICENSE and THIRD_PARTY_NOTICES are present.
- [ ] `Ember.Core.dll` contains the `Ember.Report.html` embedded resource.
- [ ] Save the build log, package hash, SDK version and installed game version.

## Functional validation

- [ ] Install the ZIP into a clean 1.22.7 client profile; verify loading and `.ember help` / `.ember health`.
- [ ] Join a multiplayer server with no Ember installed and use all commands as a regular player. Verify no Ember custom channel/packets or server installation is needed.
- [ ] Repeat in singleplayer; confirm exported scope identifies shared process counters and client frame hooks correctly.
- [ ] Complete a timed capture; verify the worker targets the client PID and resolves real managed frames.
- [ ] Stop early, cancel, attempt a second concurrent capture, disconnect and exit during capture. Check completion/error reporting and worker cleanup.
- [ ] Exercise unavailable `dotnet`, disabled diagnostics and worker attach failure; ensure ordinary client collection/export remains usable.
- [ ] Mark a reproducible event and verify marker and frame/runtime timestamps align plausibly.
- [ ] Exercise trace limits and inspect loss/limit notes rather than assuming a full capture.
- [ ] Verify bounded history and export retention at the configured limits.

## Export validation

- [ ] Timeline-only ZIP contains `report.json`, `llm-context.json`, `SUMMARY.md`, `ASK-AN-LLM.md`, `metrics.csv`, `frames.csv`, `manifest.json`, `report.html`.
- [ ] A completed profile adds `profile.speedscope.json`; import it in speedscope and inspect per-thread stacks.
- [ ] Recompute every manifest payload's size and SHA-256; all entries match. Manifest need not include itself.
- [ ] Open extracted `report.html` offline; verify data, navigation/filtering and absent-profile states.
- [ ] Check CSVs against JSON for units, invariant-culture numbers and record counts.
- [ ] Check empty history, unavailable census fields, truncated data and unresolved stack/module states.
- [ ] Verify `IncludeModConfigs=false` excludes optional config contents; with opt-in enabled, check redaction using synthetic secrets and paths.
- [ ] Use synthetic marker/log/config text containing HTML metacharacters to verify data remains text in the offline report.

## Performance record

Measure at least a settled baseline, collection enabled, an active stack capture, and export on the same repeatable route/settings. Record hardware, OS/runtime, mod list, resolution/frame cap, duration, frame median/P95/P99, process CPU, memory and external worker resource use. Repeat enough to distinguish ordinary workload variance from instrumentation effects. Publish observed differences; do not describe the profiler as having zero overhead.
