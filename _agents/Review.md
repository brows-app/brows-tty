# Runtime performance review

## Scope and approach

Reviewed the managed Ghostty core, Win32 ConPTY transport, WPF control and renderer,
repository-owned native bridge, native build settings, sample, and relevant integration
tests. The pinned upstream checkout was consulted only for API and dirty-state semantics;
upstream implementation changes are outside these findings.

Severity reflects potential workload impact from code inspection, not measured speedups.
Each finding identifies a concrete trigger and a way to reduce the work without weakening
snapshot ownership, terminal correctness, or lifecycle guarantees.

## Findings

### #1 (Resolved) — High: dirty-row text growth can require a `realloc` per cell

**Evidence:** `native/bridge/brows_ghostty.c:39-46` grows a buffer to the exact requested
size once the request is at least 4,096 bytes. `bt_next_row` requests `used + 128` before
each cell at `native/bridge/brows_ghostty.c:223-230`.

After the initial 4 KiB is full, a non-empty cell increases `used`, so the next cell's
request exceeds the exact-sized allocation again. A wide or grapheme-heavy row can therefore
make hundreds or tens of thousands of allocator calls while being extracted. If the allocator
cannot extend in place, repeatedly copying the growing buffer makes this path quadratic in
row text size. The existing 512-column grapheme test reaches this path
(`tests/Brows.TTY.Windows.Tests/TerminalEngineTests.cs:47-54`). Capacity is retained after the
first growth, so this is a growth-path problem rather than a cost on every subsequent frame.

**Resolution:** Grow geometrically, retaining the 4,096-byte minimum and doubling capacity
until it covers `needed`, with checked `size_t` overflow handling. Apply this to the shared
`reserve` helper or at least to `text`. Add a native allocation-count regression test or a
benchmark using the existing large-grapheme workload; do not rely on `realloc` extending in
place.

### #2 (Resolved) — High: selection dragging rebuilds the full screen per raw mouse move

**Evidence:** every selecting `OnMouseMove` calls `ApplyLocal` synchronously
(`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:476-485`), and `ApplyLocal` performs
`Engine.Select` plus an immediate `RefreshFrame` (`TerminalEmulator.cs:276-282`). Changed rows are then
copied and rebuilt at `TerminalEmulator.cs:214-227`.

There is no same-cell check or render-tick coalescing. In the pinned Ghostty implementation,
selection marks screen dirty state and the render update treats any such flag as a full rebuild
(`native/ghostty/src/terminal/Screen.zig:3088-3089` and
`native/ghostty/src/terminal/render.zig:993-999`). A high-polling-rate mouse can consequently
cause full-screen native extraction and WPF row reconstruction at mouse-event frequency, even
when successive moves remain within one cell.

**Resolution:** Ignore moves whose cell endpoint equals the last applied endpoint. Store the
latest endpoint and apply it at most once per render tick. Flush the final pending endpoint on
mouse-up and before `CopySelection()` so coalescing cannot lose the final selection or copy stale
text. Initial mouse-down selection can remain immediate.

### #3 — Medium: row extraction makes about eight imported Ghostty calls per cell

**Evidence:** the loop at `native/bridge/brows_ghostty.c:183-237` performs cell iteration,
style/raw getters, width lookup, selection lookup, foreground/background lookups, and grapheme
encoding separately for every cell. The bridge dynamically links Ghostty
(`scripts/Build-Native.ps1:66-70`), so these are imported API boundaries rather than bridge-local
inline operations.

A 200x50 full dirty frame contains 10,000 cells and currently causes approximately 80,000
Ghostty calls before WPF drawing. Scrolling, selection, alternate-screen applications, theme
changes, and resize reflow amplify this cost.

The pinned API already provides lower-call alternatives: a row-local selection range
(`native/ghostty/include/ghostty/vt/render.h:944-955`, `1484-1492`), a whole-row raw-cell view
(`render.h:947-961`), and `ghostty_render_state_row_cells_get_multi` (`render.h:1571-1598`).

**Resolution:** Query selection once per row and apply its inclusive range locally, fetch raw
cells as a row view, and use `get_multi` for compatible per-cell fields. Benchmark full-screen
output and scrolling before and after. Preserve per-cell width/style semantics and grapheme
retry behavior. Foreground/background getters may legitimately return `GHOSTTY_INVALID_VALUE`
for default colors, while `get_multi` stops at the first error; optional colors must not be
blindly combined without preserving fallback behavior.

### #4 — Medium: one terminal gate covers whole parse batches and all managed snapshot work

**Evidence:** the ConPTY reader holds the native terminal gate while parsing an entire read of
up to 65,536 bytes (`native/bridge/brows_session.c:78-87`). Conversely, managed `ReadFrame`
holds its managed gate and `NativeAccess` across native render update, every row extraction,
all managed array allocations/copies, title processing, and frame cleanup
(`source/Brows.TTY/Ghostty/TerminalEngine.cs:140-174`;
`source/Brows.TTY/Ghostty/NativeAccess.cs:6-13`). Native update and row extraction are at
`native/bridge/brows_ghostty.c:137-245`.

Under sustained output, frame acquisition can wait for a large parse batch, while ConPTY parsing
waits for UI-side extraction, allocation, possible GC, and copying. This head-of-line blocking
can reduce both throughput and responsiveness even though each operation is individually
necessary.

**Resolution:** Use Ghostty's two-phase render update: only `begin_update` needs terminal access;
`end_update` works on render-state-owned memory
(`native/ghostty/include/ghostty/vt/render.h:1137-1183`). Protect the render state separately so
terminal parsing can proceed during deferred update and row consumption. Also benchmark smaller
parse sections that release the terminal gate between chunks, balancing lower latency against
additional lock operations.

Do not simply unlock around the existing borrowed row pointers. Render-state updates invalidate
them, and synchronized output's `render_hold` callback updates the render state synchronously
(`native/bridge/brows_ghostty.c:57-64`). Define a consistent terminal/render lock order and
preserve the exact held-frame behavior.

### #5 — Medium: each dirty row creates two arrays and a row object

**Evidence:** `TerminalEngine.ReadFrame` allocates a new `NativeCell[]`, a new text `byte[]`, and
a `TerminalRow` for every dirty row (`source/Brows.TTY/Ghostty/TerminalEngine.cs:145-169`).
`GC.AllocateUninitializedArray` avoids initialization, not allocation or GC tracking. WPF retains
the resulting rows and drawings (`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:224-227`,
`303-325`).

A 200x50 full dirty frame allocates 50 cell arrays, 50 text arrays, and 50 row objects per frame,
in addition to the frame/list objects. At 60 full-screen frames per second, that is 6,000 array
allocations and 3,000 row objects per second. Cell data alone is 240,000 bytes per frame, or
14.4 MB/s of copies at that cadence. These allocations also extend the native lock scope in #4.

**Resolution:** Use contiguous managed cell and text buffers owned by a frame, with lightweight
row descriptors containing row index and buffer offsets. Explicit snapshot-lifetime pooling is an
alternative if contiguous storage is too disruptive. Preserve immutable published snapshots:
native reusable staging buffers and pooled managed storage must never be mutated or reused while
retained rows still reference them. Account for shared buffers extending the lifetime of a whole
frame when only one row is retained.

### #6 (Resolved) — Medium: ASCII runs allocate backing-array growth chains

**Evidence:** every ASCII run creates default-capacity `List<ushort>` and `List<double>` instances
and appends one item per cell (`source/Brows.TTY.Windows/Windows/Controls/TerminalDrawing.cs:139-156`).

An 80-cell run grows each list through capacities 4, 8, 16, 32, 64, and 128, producing six
backing arrays per list, including the final arrays. Long runs and repeated scrolling/full-screen
redraws turn this into steady dispatcher-thread allocation and GC pressure. The sustained-output
test exercises this path but measures responsiveness rather than allocations
(`tests/Brows.TTY.Windows.Tests/TerminalControlTests.cs:788-828`).

**Resolution:** Determine the run boundary first, then allocate exact-size `ushort[]` and
`double[]` arrays and fill them for the `GlyphRun`. This removes growth chains and the two list
objects. Do not return arrays to a pool while a frozen drawing may retain them. Caching immutable
advance-width arrays by run length is a further profiling-led option, not a prerequisite.

### #7 (Resolved) — Medium: sub-cell size changes trigger redundant native resizes and forced frames

**Evidence:** every `OnRenderSizeChanged` sets `_forceFrame`
(`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:340-343`). `ConfigureSize` treats that
flag as a size change even when calculated columns and rows are unchanged, calls
`_session.Resize`, and leaves a forced frame pending (`TerminalEmulator.cs:195-219`). The next refresh
requests and rebuilds every row (`TerminalEmulator.cs:214-227`).

Dragging a window edge by a few pixels without crossing a cell boundary therefore invokes both
Ghostty and ConPTY resize paths, native full-frame extraction, managed row copies, and WPF row
reconstruction. The timer coalesces notifications but does not eliminate redundant resizes.

**Resolution:** Do not set `_forceFrame` solely because the pixel render size changed. Let actual
column/row changes drive native resize and a full snapshot. Keep explicit font/DPI invalidation so
changed cell pixel metrics still reach native mouse sizing and retained rows are rebuilt when a
session is stopped.

### #8 — Medium: an idle running terminal performs native frame work about 60 times per second

**Evidence:** the 16 ms dispatcher timer starts with every session
(`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:585-589`, `618-620`). Every tick performs
transport checks and `RefreshFrame` (`TerminalEmulator.cs:244-259`), even without output. An empty frame
still allocates managed frame/list objects (`source/Brows.TTY/Ghostty/TerminalEngine.cs:140-174`),
takes locks, updates render state, queries colors/cursor/mouse/title state, and compares title
bytes (`native/bridge/brows_ghostty.c:137-180`;
`source/Brows.TTY/Ghostty/TerminalEngine.cs:75-97`).

This creates continuous timer wakeups, native transitions, and power overhead for every idle
hosted control. Upstream may optimize an unchanged update internally, but the surrounding work is
definite.

**Resolution:** Maintain a monotonically increasing native output/state generation and a change
notification. Schedule frame reads only when the generation changes, coalescing bursts to the
desired frame cap. Check the generation after registering/scheduling to avoid lost wakeups.
Retain separate low-frequency scheduling for the 500 ms cursor blink and the one-second
synchronized-output hold deadline; local selection and scrolling can still request an immediate
refresh.

### #9 (Partially resolved) — Medium: cursor invalidation re-emits every row, including useless hidden/non-blinking blinks

**Evidence:** elapsed-time `blink` is calculated without checking whether the cursor is running,
visible, or blinking (`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:228-241`). Every 500 ms
it toggles state and invalidates the visual even when that state cannot affect output. Every
`OnRender` then fills the background and calls `DrawDrawing` for every retained row before drawing
the cursor (`TerminalEmulator.cs:303-336`).

Frozen row groups avoid glyph reconstruction, as the cursor tests verify
(`tests/Brows.TTY.Windows.Tests/TerminalCursorTests.cs:123-139`), but sparse cursor/status updates
still make drawing-command emission scale with total row count. A normal visible blinking cursor
takes the same path twice per second.

**Resolution:** Gate elapsed-time invalidation on a running, visible, blinking cursor. Separate
terminal contents and cursor into cached visuals, or an equivalent retained-content host, so
cursor blink/movement does not invalidate terminal text. Preserve focused/hollow cursor behavior
and the existing cursor-only repaint tests.

### #10 — Medium: input rejected by the bounded queue still incurs full encoding and buffer work

**Evidence:** the native queue rejects a chunk when it exceeds the remaining 16 MiB capacity
(`native/bridge/brows_session.c:51-60`). Before that check, modern managed input counts UTF-8
bytes, rents a buffer, converts the whole string, enters native paste encoding, and clears the
rented buffer (`source/Brows.TTY/Ghostty/TerminalEngine.cs:49-71`; native paste encoding at
`native/bridge/brows_ghostty.c:269-278`). WPF paste executes synchronously on the dispatcher
(`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:659-662`).

The existing 17 MiB queue-overflow tests therefore perform multiple full-input passes and a large
allocation before an intentional drop (`tests/Brows.TTY.Windows.Tests/TerminalSessionTests.cs:125-134`;
`tests/Brows.TTY.Windows.Tests/TerminalControlTests.cs:858-879`). Similar waste occurs when an
otherwise encodable chunk arrives while the queue is too full. Ordinary keystrokes and inputs of
at most 512 UTF-8 bytes are not material cases.

**Resolution:** Add a session-level preflight for input that is definitively too large before
managed rental/conversion, and report it through the existing one-shot dropped-input channel. A
conservative UTF-8 lower bound can reject impossible inputs without fully encoding them. Preserve
the final atomic native enqueue check for capacity races and bracketed-paste expansion. If the
preflight is extended to merely likely-full queues, use reservation or preserve current timing
semantics rather than dropping data that could have drained during encoding.

### #11 — Low: pooled input return clears unused capacity

**Evidence:** on .NET 5+, buffers larger than the stack-allocation threshold are rented and
returned with `clearArray: true` (`source/Brows.TTY/Ghostty/TerminalEngine.cs:49-65`). `ArrayPool`
may return an array larger than requested, and full-array clearing writes that entire capacity,
including bytes never initialized or exposed to native code.

Large pastes can therefore clear nearly twice the used prefix, depending on pool bucket sizes.
This is separate from #10: accepted large pastes still incur the excess clearing even if early
rejection is added.

**Resolution:** In `finally`, clear only the potentially initialized prefix and then return the
buffer without a second full clear, for example `rented.AsSpan(0, length).Clear()` followed by
`ArrayPool<byte>.Shared.Return(rented)`. Prefix clearing also covers partial encoding if encoding
throws. Preserve this secret hygiene; do not remove clearing altogether.

### #12 — Low: rows repaint the default background over the control background

**Evidence:** `OnRender` fills the whole control with the effective default background
(`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:321-325`). `DrawRow` nevertheless emits
background rectangles for every run, including a full-row rectangle when every cell uses that
same default (`source/Brows.TTY.Windows/Windows/Controls/TerminalDrawing.cs:117-130`).

Ordinary output therefore adds a redundant drawing command/allocation per dirty row, and a full
redraw paints a second screen's worth of default-background coverage.

**Resolution:** Pass the current snapshot's effective default background into row drawing and skip
rectangle runs whose resolved color equals it. Continue emitting non-default and selection colors.
Use the snapshot currently being built rather than the previously stored `_frame.Background`,
which is assigned after changed rows are drawn; keep the no-session fallback consistent.

### #13 — Low: each session dedicates a managed thread to a lifetime process wait

**Evidence:** every `TerminalSession` starts `WaitForExit` with `TaskCreationOptions.LongRunning`
(`source/Brows.TTY.Win32/Win32/TerminalSession.cs:11-16`, `39-40`). The default scheduler uses a
dedicated long-running thread, which blocks in `bt_session_wait` until process exit and then
performs native stop/drain.

N concurrent sessions therefore add N blocked managed wait threads, and every restart adds thread
creation/teardown even when the shell is idle. There is no polling or repeated wakeup in this path.

**Resolution:** For multi-session or high-churn hosting, use a native Windows threadpool wait or
equivalent completion signal to complete a `TaskCompletionSource<uint>` with asynchronous
continuations. Preserve the existing completion contract: output must be drained before completion
is observed. Do not merely replace this with `Task.Run(WaitForExit)`, which moves the same
indefinite block onto the .NET thread pool.

The dedicated thread is an intentional way to avoid thread-pool starvation and is a reasonable
single-terminal tradeoff; this finding concerns structural scaling and churn, not a measured
single-session regression.

### #14 — Low (test-only): polling helpers force and decode complete screens every 20 ms

**Evidence:** `TerminalSessionTests.ReadText` calls `ReadFrame(true)` and decodes every cell
(`tests/Brows.TTY.Windows.Tests/TerminalSessionTests.cs:14-18`). `WaitForText` repeats this every
20 ms until success or timeout (`TerminalSessionTests.cs:20-29`), including the larger resize
cases at `TerminalSessionTests.cs:74-110`.

At 140x50, each poll copies 7,000 cells (168,000 bytes of cell data), plus text arrays and row
objects, and then allocates cell/row strings. This creates substantial test-only GC and native
frame-copy contention and can prolong failing/timeout runs. It does not affect shipped library
performance.

**Resolution:** In polling loops, read normal dirty frames and retain row snapshots between polls.
Decode only target/cursor rows where possible. Keep forced full-screen materialization for one-off
final assertions where it simplifies verification.

## Existing design choices not flagged

- Native ConPTY output feeds Ghostty directly using reusable native buffers; there is no managed
  output callback, per-read managed array, or dispatcher hop per shell read.
- Native input uses a bounded queue and an event-driven writer, not a polling sleep loop. Session
  construction/disposal and process stopping are kept off the WPF dispatcher.
- Managed snapshot copies are required for immutable frame ownership. #4 and #5 target lock scope
  and allocation count, not removal of the ownership copy.
- Small modern input uses stack allocation and larger input uses pooling. Unchanged title strings
  are cached, and blittable cells are copied in bulk rather than marshalled one cell at a time.
- WPF retains frozen row drawings and rebuilds only dirty rows. It groups ASCII glyph runs and does
  not create a UI element per cell.
- Upstream native code is built with `ReleaseFast`, bridge code with `-O2`, and SIMD remains enabled.
  No compiler-flag change is recommended without comparative benchmarks.

## Validation and limitations

Ran the existing x64 .NET 10 sustained-output integration test:

```powershell
dotnet test tests/Brows.TTY.Windows.Tests/Brows.TTY.Windows.Tests.csproj -c Release -f net10.0-windows --filter "FullyQualifiedName~SustainedOutput_KeepsDispatcherResponsive" --logger "console;verbosity=normal"
```

Result: **passed**, one test. The reported baseline was 20,000 output lines in 2,185 ms, input-tick
p95 of 20.1 ms, and maximum of 25.6 ms. This establishes current responsiveness for that workload;
it is not a comparative benchmark and does not measure the allocation, idle, selection, resize, or
large-paste costs above.

No production code was changed. No full multi-framework suite, allocation profile, ARM64 runtime
test, or before/after performance benchmark was run. Priorities and workload calculations should
be validated with targeted benchmarks when fixes are implemented.

## Independent triage (2026-11-05)

Rechecked the cited production paths, polling helper, and pinned render-update contract.
All fourteen findings describe real work in the current implementation. None establishes
an observed speedup: the prior responsiveness result is a baseline, not comparative evidence.
The priorities below distinguish avoidable work from designs whose replacement may cost more.
Existing issue numbers and findings are retained; no issue is marked resolved.

| Issue | Assessment and expected value | Priority |
|---|---|---|
| #1 | Confirmed exact-capacity growth. Geometric growth removes repeated reallocations once row text exceeds 4 KiB. Strong algorithmic improvement, but primarily unusually wide or combining-character-heavy rows and initial capacity growth. High severity is workload-specific. | Early, small change |
| #2 | Confirmed immediate selection and snapshot on every selecting move; pinned screen dirty handling rebuilds the screen. Same-cell suppression and frame coalescing can materially improve dragging. Preserve final endpoint and selection behavior while output scrolls. | First |
| #3 | Confirmed roughly eight API calls per cell. Fewer calls can help large dirty frames, but imported-call count alone does not establish a bottleneck; bulk getters still perform internal field work. Measure CPU time before choosing the API rewrite. | Profile first |
| #4 | Confirmed shared terminal lock spans parsing or snapshot allocation/copy. Two-phase update is explicitly supported by the pinned header. Contention and benefit remain unmeasured; smaller parse chunks can reduce throughput. The synchronous render-hold callback makes lock splitting a substantial correctness change. | Profile first; high implementation risk |
| #5 | Confirmed per-row arrays and objects. Allocation reduction is plausible, but contiguous frames retain unrelated row data when individual rows survive; large cell buffers also enter the large-object heap. Pooling requires explicit retained-row ownership. This is an optimization candidate, not an established defect in immutable snapshots. | Defer until allocation profile |
| #6 | Confirmed two growing lists per ASCII run. Exact-size arrays remove predictable growth allocations on common output; finding the boundary adds a scan, so verify overall rendering time as well as allocated bytes. | First |
| #7 | Confirmed pixel-size changes force native resize and full extraction despite unchanged grid dimensions. Avoiding this is valuable during interactive resizing. Track cell pixel metrics separately so font/DPI changes still propagate. | First |
| #8 | Confirmed recurring idle frame work. Likely useful for idle CPU/power and many controls; it does not imply higher busy-output throughput. Notifications must also preserve transport/drop errors, process completion, local state changes, and synchronized-output deadlines. A cheap generation check can be evaluated before replacing the scheduler. | Next, with idle measurements |
| #9 | Confirmed unnecessary blink invalidation and per-row drawing-command emission. Gate invisible/nonblinking cursor work first. Cached drawings already avoid glyph rebuilding; separate visuals have an unmeasured incremental benefit. | Early gating; profile visual split |
| #10 | Confirmed expensive work before guaranteed queue rejection. Useful for pathological oversized paste, not ordinary input throughput. Preflight must bound encoded output: paste sanitization can remove input, so raw string/UTF-8 size alone must not falsely reject input that would fit after encoding. | Conditional, oversized-input workloads |
| #11 | Confirmed full rented-capacity clearing. Prefix clearing reduces memory writes for accepted larger inputs while preserving clearing of this operation's sensitive bytes. Usually a small benefit; benchmark large paste rather than keystrokes. | Small optional follow-up |
| #12 | Confirmed duplicate default-background drawing. Skipping matching runs removes commands and coverage, but WPF composition costs and net speedup are unmeasured. Retained rows must be rebuilt when the effective default background changes. | Small optional follow-up |
| #13 | Confirmed dedicated blocked managed thread per session. Relevant to many sessions or frequent restarts; a sleeping wait is not continuous CPU consumption. Registered process wait completion must still move blocking stop/drain work off callback/UI threads. | Defer unless session scaling matters |
| #14 | Confirmed forced full-screen polling and decoding. Can reduce test allocation/lock contention; provides no shipped-library speedup. Dirty-frame polling must preserve prior rows so markers are not lost. | Test infrastructure only |

Recommended initial production batch: #2, #6, #7, plus the narrowly scoped #1 and
blink gating portion of #9. Evaluate #8 separately for idle overhead. Measure before
undertaking #3, #4, #5 or the visual restructuring in #9. Treat #10-#13 as targeted
optimizations rather than general performance blockers, and #14 as test-only.

Validation for this triage was static inspection only. No production code, tests, or
public APIs changed; no builds, tests, allocation captures, or comparative benchmarks
were run. Consequently, expected benefits above are hypotheses supported by concrete
removed work, not measured performance claims.

## Measured cost/benefit pass (completed)

Parent-agent profiling used a Release net10.0-windows STA scenario harness, real
Ghostty and WPF retained drawings, Stopwatch and current-thread allocated-byte
counters after 20 warmups. Raw captures and harness live in ignored out/perf/.
These are local scenario measurements, not BenchmarkDotNet results or production
traces; reflection setup is included in control-level measurements. Timings vary
with scheduling and concurrent activity. No ARM64 execution is implied.

| Order | Issue | Local evidence | Decision |
|---|---|---|---|
| 1 | #2 | 200x50 repeated identical selection + frame + all row drawings: 1.27-1.31 ms, 632,840 B/update. | Fix: suppress duplicate endpoints and coalesce drag updates. |
| 2 | #6 | 200 ASCII cells DrawRow: 27-28 us, 7,537 B/row. | Fix exact arrays; compare after. |
| 3 | #7 | Forced resize + refresh on unchanged grid: 0.35-0.79 ms, 336,816 B/update versus idle refresh 0.7-0.8 us, 168 B. | Fix grid/metric change detection. |
| 4 | #1 | Instrumented actual first-row extraction: 72/384/3968 realloc calls at 200/512/4096 columns with 15 combining marks/cell. | Fix geometric growth; allocation-count benefit independent of timing noise. |
| 5 | #9 | Re-emitting 50 retained row drawings into a DrawingGroup: 14 us, 3,520 B. This proxy is not OnRender or GPU timing. | Fix needless blink gating; defer visual restructuring for this small twice-per-second cost. |
| 6 | #8 | Idle engine frame: 0.7-0.8 us and 112-113 B; control refresh 0.7-0.8 us and 168 B. | Defer notification redesign: ~7 KB/s per engine at 60Hz, very little measured CPU. Timer power impact unmeasured. |
| 7 | #3 | Entire 10,000-cell forced extraction including allocations/copies: 0.158-0.161 ms. | Defer: this bounds the total removable API overhead; drawing dominates tested workload. |
| 8 | #4 | 65KiB parse: 23 us. Forced frame under unthrottled synthetic managed writer: median 0.165-0.170 ms, p95 0.181-0.201 ms, max 4.3-6.6 ms. | Defer lock redesign: small typical contention; isolated outliers do not justify callback/borrowed-pointer risk. Managed writer stress is not a ConPTY throughput measurement. |
| 9 | #5 | Forced frame 255,624 B; ~15.3 MB/s at continuous 60 full frames/s. | Defer lifetime/pooling redesign until sustained allocation is limiting; #2/#7 remove unnecessary frames first. |
| 10 | #10 | Rejected 17MiB ASCII paste into real session: 20.6-21.1 ms. | Defer: rare oversized rejection, additional input-policy/encoding semantics cost; no normal-input benefit. |
| 11 | #11 | Isolated pooled 2MiB bucket clear 24.3 us versus initialized 1MiB+1 prefix 11.4 us. Accepted engine paste ~1.21 ms (includes managed callback copy). | Defer: ~13 us saving on occasional MiB paste; preserve simple established clearing policy. |
| 12 | #12 | Isolated 50-rectangle proxy: 41-44 us/22,320 B. A real DrawRow prototype after #6 instead saved only 423 B/row (4,096 to 3,673 B), with both versions ~27 us. | Defer: no demonstrated time benefit; small allocation saving does not justify extra snapshot/background contract plumbing. |
| 13 | #13 | Eight real idle sessions: ~34 ms startup, 24 extra process threads (reader+writer+managed waiter per session). | Defer: expected architecture; no evidence of a session-count limit or idle CPU bottleneck. |
| 14 | #14 | Forced 200x50 frame plus cell decoding: 0.27 ms, 494,464 B/poll. | Defer test-only accumulator complexity; does not improve shipped performance. |

Luna Max agents implement selected fixes; parent retains profiling, independent
review, final validation and commits. Issue headings will be marked resolved only
after validation succeeds. No measured speedup is claimed yet.

The pre-change sustained-output integration test passed: 20,000 lines in 2,167 ms,
input-tick p95 16.7 ms, maximum 21.3 ms. This is a responsiveness baseline, not
a throughput comparison isolated from machine scheduling effects.

### Reviewed intermediate results

- #1: parent reviewed the checked growth implementation and independently ran
  `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-NativeBufferGrowth.ps1`:
  passed all three real UTF-8 row cases at 2/3/6 reallocations, plus minimum-capacity,
  reuse, overflow-fallback and failed-allocation state-preservation probes. The agent
  first ran the same row allocation bounds against the previous implementation and
  observed failures at 72/384/3968 allocations. Both x64 and ARM64 native builds passed.
- #6: parent reviewed array ownership and grouping; 200-cell row allocation fell from
  7,537 B to 4,096 B (about 46%). Elapsed time remained ~27 us; no time speedup claimed.
  `TerminalDrawingTests` passed 14/14 on x64 net10.0-windows. New compatibility tests are
  `DrawRow_EmitsLongCompatibleAsciiRunWithOneAdvancePerCell` and
  `DrawRow_SplitsAsciiRunsAtForegroundFlagsUnderlineAndUnicode`.
- The native 4,096-column cold scenario including terminal creation and parsing fell
  from ~2.43 ms to ~2.20 ms locally. Treat this as supporting evidence; deterministic
  allocation-count reduction is the primary justification for #1.

Full validation results are recorded below.

### Integration review correction

The first complete suite exposed a cursor-blink regression in the draft: resetting
cursor phase when VT enabled blinking prevented the existing cursor-only pixel
transition test from changing output. The change was returned to the Luna agent
for a narrower elapsed-time gate preserving the existing phase semantics.
Frameworks also ran concurrently in that initial invocation and contended for the
shared Windows clipboard. Final validation will use sequential target-framework
runs, matching repository CI. These initial failing runs are not acceptance evidence.

## Final disposition and validation

Parent profiling/analysis selected #1, #2, #6, #7 and the inexpensive blink gate
from #9. GPT-6-Luna Max agents implemented those changes; the parent reviewed
production and test diffs, required corrections, ran final validation and retained
commit responsibility. No public APIs, dependencies or build settings changed.

Resolved: #1, #2, #6, #7. Partially resolved: #9 (elapsed-time blink gating only;
separate content/cursor visuals remain deferred). Deferred: #3, #4, #5, #8,
#10, #11, #12, #13 and #14, for the measured cost/benefit reasons above.

- #2 now ignores repeated cell endpoints and applies the latest endpoint on the
  render tick. Mouse-up, copy, local scroll and a new mouse-down flush pending
  selection; stop/failure/restart clears drag state. Same-cell QueueSelection
  measured zero managed allocation and less than 0.1 us including reflection.
  This removes the prior ~1.3 ms full-frame operation for duplicate moves; it is
  not a claim about rendering a genuinely changed selection at that speed.
- #7 now compares grid dimensions and physical cell metrics independently of
  full-frame invalidation. New sessions explicitly initialize native pixel
  metrics even when geometry matches the previous session. Actual sub-cell
  layout plus refresh measured ~12.7 us/4,040 B in the harness, including layout;
  the old forced-resize/refresh path allocated ~337 KB. Font/DPI invalidation
  remains in place, and actual grid changes still rebuild the screen.
- #9 preserves the existing cursor phase when VT switches blink mode. Only
  running, visible, blinking cursors trigger elapsed-time repaint.

Acceptance evidence:

| Requirement | Evidence |
|---|---|
| Native growth correctness and allocation cost | Test-NativeBufferGrowth.ps1 passed: real UTF-8 rows, 2/3/6 reallocations, growth/reuse/overflow/OOM probes. |
| ASCII grouping and immutable glyph arrays | TerminalDrawingTests passed, including long compatible runs and mixed style/Unicode boundaries; allocation reduced ~46%. |
| Selection coalescing and final flush | SelectionMoves_AreCoalescedUntilTheNextTickAndSkipTheSameCell; MouseUp_FlushesTheFinalPendingSelectionEndpoint; CopySelection_FlushesTheFinalPendingSelectionEndpoint; LocalScroll_FlushesPendingSelectionBeforeEndingTheDrag. |
| Lifecycle cleanup | StopAsync_ClearsPendingSelectionBeforeTheNextSession. |
| Resize behavior | SubCellSizeChange_DoesNotForceAFullFrame; GridSizeChange_StillResizesAndRebuildsRows. |
| Cursor behavior | ExpiredBlinkDeadline_DoesNotInvalidateHiddenOrNonBlinkingCursor; ExpiredBlinkDeadline_StillInvalidatesAVisibleBlinkingCursor; existing CursorBlinkOnlyChange_InvalidatesAndRepaintsImmediately. |
| All affected managed targets | Release solution build passed with zero warnings/errors. Sequential x64 full tests passed 111/111 on each of net462, net48, net8.0-windows and net10.0-windows: 444 total, no skips. |
| Native targets | Build-Native.ps1 passed for win-x64 and win-arm64. ARM64 cross-compilation only; runtime execution remains unvalidated. |
| Packaging and consumers | All three packages packed; asset/dependency checks and core/Win32/WPF consumers passed on all four frameworks. |

The package verifier required an ignored out/perf/VerifyPackageCompat.ps1 copy
for Windows PowerShell 5: load System.IO.Compression.FileSystem, use SetAttribute
for the local feed XML value, and preserve the original scripts directory for
resource paths. The repository verifier was not changed. Fresh-cache restore
passed after allowing NuGet network access. Package checks validate both native
architectures' contents; consumers execute x64 only.

The final net10 sustained-output integration case reported 20,000 lines in
1,887 ms, dispatcher tick p95 16.3 ms and max 16.4 ms. The initial run was
2,167 ms/p95 16.7 ms/max 21.3 ms. These individual integration runs establish
continued responsiveness; machine scheduling and changes in dirty-frame cadence
prevent treating them as an isolated causal throughput benchmark.

Profiling artifacts and captures remain under ignored out/perf/. Final tracked
and untracked source/test/script changes were reviewed for scope, ownership,
encoding and whitespace; generated binaries, packages and logs are excluded.

## Shortcut implementation review (2026-11-05)

The requested small Windows Terminal shortcuts are being integrated in three
independently reviewed and validated groups. GPT-6-Luna Max agents implement the
changes; the parent reviews production paths and tests, runs acceptance checks,
and commits each completed group. No new public API is introduced.

### #15 (Resolved) — New clipboard fixture lacked dispatcher synchronization

The initial TerminalClipboardShortcutTests fixture omitted the dispatcher
synchronization-context setup used by existing WPF fixtures. StartAsync could
continue on a worker thread after await and fail before reaching shortcut behavior.
The fixture now installs/restores DispatcherSynchronizationContext in setup/teardown.
The corrected cases execute and pass on all four frameworks.

### #16 (Resolved) — Shortcut tests used incorrect forwarded-key expectations

Early clipboard/scroll drafts expected forwarded key-up events to be marked
Handled, although the existing control only marks locally consumed releases.
The clipboard draft also expected an unnecessary default modifier in the Kitty
Enter press encoding. These were incorrect test expectations, not requirements
to change process input. Tests now assert actual protocol bytes independently
and distinguish a local copy release from a forwarded release.

### Clipboard group acceptance

- Enter with a selection copies/clears it and consumes repeats, CR/LF text and
  release. Enter without a selection preserves the complete Kitty press/release.
- Ctrl+V uses the existing bracketed-paste encoder. Ctrl+Shift+V and Shift+Insert
  remain supported, and Ctrl+Alt+V composition remains application input.
- The agent's buildable counterfactual disabled the new Enter-copy/Ctrl+V branches:
  two cases failed with leaked key bytes, four passed. After restoring source
  byte-for-byte, the focused fixture plus transition regressions passed 12/12.
- Parent validation: both native architectures built; Release solution build had
  zero warnings/errors. Sequential x64 tests passed 126/126 on net462, net48,
  net8.0-windows and net10.0-windows (504 total; no skips).
- All three packages packed; package assets/dependency checks and consumers on
  all four frameworks passed. As in the previous validation, the ignored
  PowerShell-5-compatible verifier copy was used. ARM64 was cross-compiled and
  package-checked, not runtime-tested.

### #17 (Resolved) — Scroll test draft did not respect all target frameworks

The initial scroll fixture assigned NativeFrame.Rows (uint) to int without a
cast and used a newer string overload. The test now uses a checked row-count
conversion and string operations supported on every target. Both framework
builds and actual test runs confirm the corrected fixture compiles and executes.

### #18 (Resolved) — Scroll documentation omitted modifiers on page/history keys

The draft README described PageUp/PageDown and Home/End without repeating the
Ctrl+Shift modifiers, which could imply those plain keys were intercepted. The
README now names every complete chord. Plain and other modifier combinations
remain process input, as the real key-echo regression verifies.

### #19 (Resolved) — AltGr test setup unnecessarily persisted clipboard ownership

After the RDP session disconnected, the new Ctrl+Alt+V test could fail during
Clipboard.SetText/OleFlushClipboard before reaching the input behavior. This
case only requires in-process clipboard data. Its setup now uses a real Unicode
DataObject with copy:false, avoiding unnecessary persistence. The assertions
still verify unchanged clipboard contents and exact AltGr bytes; production
clipboard behavior was not altered.

### Scrolling group acceptance

- Ctrl+Shift+Up/Down follows the Windows wheel row count, including page mode.
  Ctrl+Shift+PageUp/PageDown moves by a viewport; Ctrl+Shift+Home/End jumps to
  the earliest/latest history. The native bridge adds TOP via the existing int
  selector while preserving the old 0/1 behavior and the ABI layout.
- ScrollShortcuts_UseConfiguredRowsPagesAndHistoryBoundsWithoutSendingKeys checks
  real history positions and a child-process byte barrier. AlternateScreenScrollShortcuts_
  StayOnActiveScreenAndDoNotSendKeys and NonExactModifiers_ForwardPageKeyToChild
  cover alternate-screen and ordinary application input.
- A compiled counterfactual disabling only the scroll handler call failed 2/3
  cases; the nonexact forwarding case remained green. Restoring source and forcing
  a fresh build passed 3/3. The forced build avoided a stale counterfactual DLL
  after Copy-Item preserved an older source timestamp.
- Both native architectures built. Release solution build: zero warnings/errors.
  Final sequential x64 runs passed 129/129 on each of the four frameworks
  (516 total, no skips). Package contents, dependency directions and all consumer
  applications passed on all four frameworks; ARM64 execution remains unvalidated.

The desktop disconnected during this group (query user reported session 2 Disc),
causing existing RenderTargetBitmap assertions to return black pixels across
frameworks. The final tests did not skip or weaken those assertions: the WPF
ShouldRenderEvenWhenNoDisplayDevicesAreAvailable switch was enabled only in
ignored generated test/consumer runtime configurations. .NET Framework required
an isolated copy of the same SDK VSTest host under out/shortcuts/vstest with its
startup config adjusted; net8/net10 used their generated runtimeconfig files.
The repository's runsettings and shipped defaults remain unchanged. See the
[WPF switch source](https://source.dot.net/PresentationCore/MS/Internal/CoreAppContextSwitches.cs.html)
for the opt-in behavior. Temporary validation scripts and captures are in out/.

### #20 (Resolved) — Font fixture assumed unsupported synthetic punctuation bytes

The draft negative-case test guessed a Kitty byte sequence for a synthetic
Ctrl+Shift+OEMMinus press without an accompanying text event, then waited for
that sequence. The observed stream contained only the AltGr plus and barrier.
The corrected test checks unchanged FontSize and the existing forwarded-key
state across the excluded minus gesture, and independently requires exact 2b21
for one AltGr plus followed by the barrier. It does not weaken AltGr-once
verification or expand this feature into a punctuation-encoding change.

### Font group acceptance and final shortcut validation

- Ctrl+OEMPlus and Ctrl+Shift+OEMPlus increase FontSize by one; Ctrl+OEMMinus
  decreases it. Ctrl+Add/Subtract and Ctrl+NumPad0 provide keypad equivalents;
  Ctrl+D0/Ctrl+NumPad0 reset to the configured size.
- SetCurrentValue preserves bindings. An external FontSize change becomes the
  next reset baseline, while the control's own zoom changes do not overwrite it.
  Zoom-out stops at one DIP and leaves a valid external size below one unchanged;
  reset can restore that external value. Upper candidates use the existing WPF
  dependency-property validation rather than an arbitrary low cap.
- FontShortcuts_ZoomAndResetBindingBaseline runs both with and without a live
  process, checks actual renderer metrics, binding preservation, external baseline
  changes and all OEM/keypad gestures. FontZoomOut_AtOrBelowOneDipLeavesSizeAndResetBaseline
  covers 0.5/1.0 boundaries. FontShortcuts_LeaveShiftedMinusAndAltGrPlusAsTerminalInput
  checks exact modifiers and AltGr text; the running positive case verifies no
  gesture press, release or generated text reaches the child.
- A buildable counterfactual disabling only the font handler failed four cases
  (local key releases were no longer consumed) and passed the excluded-modifier
  case. A forced rebuild after restoring the handler passed all five cases.
- Parent's final Release solution build passed with zero warnings/errors.
  Sequential x64 integration runs passed 134/134 on each of net462, net48,
  net8.0-windows and net10.0-windows: 536 total, no skipped cases. The test-only
  headless host configuration described above retained every rendering assertion.
- Both native architectures had already been rebuilt for the scrolling group;
  the font group changes no native source. Final package contents, dependency
  directions and all core/transport/WPF consumers passed on all four frameworks,
  including real rendered glyph checks. ARM64 runtime validation remains unavailable.

All seven requested Small items are complete: Enter-to-copy, Ctrl+V paste,
Ctrl+Shift+Up/Down, Ctrl+Shift+PageUp/PageDown, Ctrl+Shift+Home/End, Ctrl+Plus/Minus
and Ctrl+0, including keypad font keys. Each group was reviewed and validated
before its separate commit. No new public API, dependency, framework, shipping
runtime configuration or release-workflow change was introduced. Generated files,
isolated test tools and logs remain ignored under out/.

### #21 (Resolved) - Changing a shortcut option during a press could leak its text

The initial configurable-shortcut implementation checked the live dependency
property again in TerminalEmulator.OnTextInput after a local key-down had already been
handled. Turning that property off before text arrived could forward part of the
consumed press to the child. TerminalEmulator.cs now retains the key-down decision for
text and release events. A subsequent forwarded press removes stale local-key
tracking, so disabling a shortcut also works while the key repeats.

The LocalPress_StaysLocalAfterItsOptionTurnsOffBeforeTextAndRelease and
NewForwardedRepeat_RetiresOldLocalTrackingAndAllowsText tests exercise both
transitions against the real Ghostty/ConPTY key-echo process, including exact
paste, key, release and barrier bytes.

### Configurable shortcut acceptance

- Added 20 independent public Boolean dependency properties on TerminalEmulator, all
  named Press... and all defaulting to true. They cover local clipboard,
  history-scrolling and font actions. False follows normal terminal input;
  window-host gestures, mouse actions and explicit API calls are unaffected.
- README Usage documents the supported shortcuts, every option and a XAML
  example. WPF bindings and per-instance values are covered by the new fixture.
- Both implementation and test work used GPT-6-Luna with Max reasoning. Parent
  review corrected the mid-press decision described in #21. The production-gate
  counterfactual ignored all false values and failed all 20 disabled cases;
  restoring the exact source and forcing a rebuild passed all 43 focused cases.
- Native x64 and ARM64 builds passed. Release solution builds passed with zero
  warnings/errors on all four targets. Sequential x64 integration suites passed
  177/177 on net462, net48, net8.0-windows and net10.0-windows: 708 total, no skips.
  The previously described test-only headless rendering setup retained all
  rendering assertions; shipping configuration was not changed.
- All three packages and all 12 core/transport/WPF consumers passed across the
  four frameworks, including C# initializers and XAML setting the new properties.
  ARM64 was cross-compiled; runtime execution still requires an ARM64 runner.

### #22 (Resolved) - A collapsed terminal resizes its active grid to one cell

`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:305` computes terminal
columns and rows from layout dimensions, clamping each to at least one even when
no cell fits. In the sample, the fixed sidebar can leave the terminal with zero
width during a horizontal shrink. ConfigureSize sends a one-column resize to
Ghostty and ConPTY, which reflows the active screen; restoring the width leaves
old output in scrollback and a sparse active viewport that input keeps selecting.

Parent reproduction in the real sample with CMD observed ActualWidth 694 and
78 columns, then ActualWidth 0 and one column, then 78 columns after restoration.
After each of two Enter presses, only resize-row-60 remained in the active
viewport and the prompt was misaligned. Before-fix capture is in ignored
out/perf/resize-repro-before.txt.

The control fix configures font metrics, then defers grid/pixel-cache changes and
native resize while either layout dimension is below a full cell. It retains the
last usable grid (or the initial 80x24 defaults before layout), then applies the
current layout when it can display a cell. Tests cover width/height collapse,
sub-cell layouts, restoration and input against real WPF/Ghostty/ConPTY.

A gradual drag can also pass through valid one-column layouts before reaching
zero. The pinned Ghostty header explicitly requires disabling scrollback pull
for ConPTY during both row growth and column reflow. At one column, prior active
lines wrap into history and intentionally stay there on widening so the active
screen continues to match ConPTY. Changing that flag would regress cursor
alignment. The sample instead gives its terminal Grid column a 320-DIP minimum:
a narrow window clips this usable layout while retaining about 34 text columns.
The window can still shrink freely. This sample policy does not impose a global
minimum on TerminalEmulator or change valid small-grid reflow semantics.

Parent reran the exact gradual sample reproduction: 78 -> 45 -> 34 columns,
retaining 34 while fully clipped, then 78 on expansion. After each Enter, 26
history rows remained visible with aligned prompts. Capture:
out/perf/resize-repro-gradual-final.txt (ignored).

Validation:

- GPT-6-Luna Max implemented the control/sample changes and regression fixture;
  parent reviewed the source, tests, native resize contract and final diff.
- Four pre-fix zero/sub-cell cases failed with 100 -> 1 columns or 23 -> 1 rows.
  The final fixture passes all eight cases, including unmeasured 80x24 startup,
  valid one-/two-column grids, direct zero-width collapse, and the actual sample's
  gradual layout collapse. History is asserted in the active viewport after
  input, and earlier scrollback plus aligned cursor/prompt rows are verified.
- Removing only the sample column minimum failed the gradual-layout regression;
  restoring the exact XAML, rebuilding and rerunning passed all eight cases.
- Parent reproduced the gradual sample sequence with both CMD and Windows
  PowerShell. History remained visible after two Enter presses in each shell.
- Native x64/ARM64 builds passed. All four managed targets built with zero
  warnings/errors. Sequential x64 integration suites passed 185/185 on each of
  net462, net48, net8.0-windows and net10.0-windows: 740 total, no skips. The
  existing ignored headless test-host configuration retained rendering assertions.
- All three packages and 12 isolated consuming applications passed across the
  four frameworks. ARM64 was cross-compiled; runtime execution still requires
  an ARM64 Windows runner. Generated output, captures and validation scripts
  remain ignored under out/.
