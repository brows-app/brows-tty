# TerminalEmulator code and runtime performance review

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

## Functional review (2026-11-06)

Reviewed all repository-owned production C#, C bridge code and headers, project/package
configuration, build/verification scripts, sample, and integration fixtures. The pinned
Ghostty headers and relevant implementation were consulted for encoding and render-state
semantics. This is a review of the repository's integration, not an audit of all upstream
Ghostty or Microsoft ConPTY implementation code. Earlier issue numbers and dispositions
remain unchanged. New findings start at #23; no production fixes are included.

### #23 (Resolved) — High (P1): resizing during selection can permanently stop rendering

**Evidence:** `source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:673-676`
passes the original viewport selection anchor to native code without reconciling it with
a changed grid. `ConfigureSize` changes the grid at lines 396-413 without canceling or
rebasing the drag. The timer catches the resulting selection exception at lines 469-478
and stops itself. `HandleFailure` only clears `IsRunning` for a transport fault, so this
selection error leaves the process running and the display frozen.

**Reproduction:** start with an 800-pixel-wide control, begin a selection at column 70,
shrink to 200 pixels (25 columns at the configured font), then move the endpoint to
column 5. The real Ghostty bridge returns `-2`; the control reports a failure, its timer
is disabled, and `IsRunning` remains true. Subsequent output does not refresh, and
`StartAsync` returns immediately because the control still considers itself running.
The diagnostic probe sets the stored anchor directly to reproduce the drag state without
moving the user's physical pointer.

**Resolution:** invalidate or safely rebase selection gestures when grid geometry changes,
before submitting their endpoints. Treat recoverable selection failures separately from
fatal frame failures so they cannot disable rendering indefinitely. Add a regression that
continues producing output after a resize during a drag and checks that output is displayed.

### #24 (Resolved) — High (P1): process exit during synchronized output hides final output indefinitely

**Evidence:** `native/bridge/brows_ghostty.c:146-151` only expires a synchronized-output
hold when a frame is read after its one-second deadline. The exit observer performs one
final `RefreshFrame(false)` at
`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:349-352`, then stops the
timer at line 370. A final read before the hold deadline returns the pre-hold render state;
there is no later scheduled read to release it. Forcing dirty rows does not bypass this hold.

**Reproduction:** a real native console child writes
`before ESC[?2026h CR FINAL-SYNC-MARKER` and exits without disabling mode 2026. After
`TerminalExited`, the marker is absent from the retained rows and the timer is stopped.
Explicitly releasing the hold and refreshing makes the already-drained marker appear.

**Resolution:** finalize synchronized output once process output has drained, before the
last snapshot and exit notification. Alternatively, keep a bounded final refresh scheduled
until the hold expires, with correct session identity checks. Verify final text and colors
for a child that exits without sending the release sequence.

### #25 (Resolved) — Medium (P2): application mouse dragging drops or misidentifies motion

**Evidence:** `source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:954-955`
sends every motion with button `0`. `native/bridge/brows_ghostty.c:313-314` interprets
that value as no button, regardless of `any_pressed`. The pinned encoder requires a
non-null event button in button-tracking mode (`native/ghostty/src/input/mouse_encode.zig:190-191`);
in any-event mode a null button encodes motion with no button held. The separate
`ANY_BUTTON_PRESSED` option controls out-of-viewport handling, not button identity.

**Reproduction:** enable modes 1002 and 1006 and send a motion at pixel (24,16), with
`any_pressed=true`. The control's button-0 form produces zero bytes. The same motion
with button 1 produces `ESC[<32;4;2M`. Mouse selection/dragging inside terminal
applications consequently receives presses and releases without the necessary drag events.

**Resolution:** forward the held button identity on motion, tracking which application
button owns the gesture when multiple buttons are held. Test left, middle, and right drags
in both 1002 and 1003, including movement beyond the viewport while captured.

### #26 (Resolved) — Medium (P2): changing Shift during a mouse gesture breaks press/release pairing

**Evidence:** `ReportsMouse` at
`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:716` is evaluated
independently at mouse-down, move, and mouse-up (lines 932, 954, and 976). The control
does not remember whether a button press was sent to the application or started a local
selection. Mouse capture loss also has no handler that retires application button state.

**Reproduction:** with modes 1000 and 1006 enabled, press the left button without Shift,
hold Shift, and release it. The real key-echo console child receives the SGR press
(`ESC[<0;1;1M`) but no release. Starting locally with Shift and releasing Shift before
mouse-up conversely sends an application release with no matching press. Losing capture
before mouse-up can similarly strand an application press.

**Resolution:** choose local/application ownership at mouse-down and retain it for that
button through release. Release or cancel outstanding application buttons on capture loss,
focus loss, stop, and restart. Preserve Shift's override for the next gesture. Add both
modifier-transition cases and a capture-loss case.

### #27 (Resolved) — Medium (P2): text-producing autorepeat is encoded as new key presses

**Evidence:** `OnPreviewKeyDown` reads `args.IsRepeat` for raw and deferred chords at
`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:808,813`, but does not
save it for the text path. `OnTextInput` always passes action `1` at lines 876 and 879.
Applications requesting Kitty event types therefore cannot distinguish a held printable
key from repeated distinct presses.

**Reproduction:** route an X key-down and text event, followed by another X key-down with
`IsRepeat=true` and its text event, to the real key-echo child. It receives
`ESC[120u ESC[120u`. Encoding the second event with action 2 correctly produces
`ESC[120;1:2u`. Existing tests named for repeats generally construct fresh
`KeyEventArgs` without setting their repeat state, so they do not exercise this distinction.

**Resolution:** carry the press action through text/composition delivery and use it for
the corresponding encoded event. Keep actual composition commits distinct from physical
key repeat. Test a real repeat-marked text key under Kitty event reporting and legacy mode.

### #28 (Resolved) — Medium (P2): non-AltGr composed text can become an unaccented physical key

**Evidence:** `OnTextInputStart` at
`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:261-273` tracks a
composition only when a Ctrl+Alt fallback is pending. Other committed text goes through
the last physical virtual key at line 879. `native/bridge/brows_ghostty.c:263` assigns
ASCII unshifted codepoints to A-Z regardless of the committed text. With Kitty report-all
enabled, Ghostty uses that key/codepoint in preference to the composed UTF-8 text.

**Reproduction:** with Kitty flags 2 and 8, route an E key-down followed by a composition
start and commit containing `é`. The real console child receives `ESC[101u` (E), not
`c3 a9`. Passing the same commit as `TerminalKey.Unidentified` preserves `c3 a9`.
This reproduces the control's delivery path; physical keyboard layout/dead-key and IME
interaction still needs an interactive regression on appropriate Windows input layouts.

**Resolution:** distinguish committed composition text from ordinary physical-key text
for all composition sources, not only AltGr. Deliver pure composition commits without a
stale physical identity, and derive layout-dependent unshifted codepoints correctly where
physical reporting is appropriate. Test dead-key accents, IME commits, and ordinary
modified letters with Kitty report-all and associated-text flags.

### #29 (Resolved) — Medium (P2): mouse-wheel scrolling ignores delta magnitude and Windows settings

**Evidence:** `source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:991-997`
reduces the wheel delta to its sign. Local scrolling always moves three rows; application
reporting always sends one wheel press. A delta of 240 is treated identically to 120,
small high-resolution deltas each trigger a full step, and even delta zero produces a
down-wheel application event. Local scrolling ignores disabled scrolling, customized row
counts, and page-scroll settings, although the keyboard scrolling path honors those
settings at lines 583-592.

**Resolution:** accumulate partial wheel deltas and preserve the number of complete
detents. Apply `SystemParameters.WheelScrollLines` (including zero and page mode) to
local history scrolling and emit the corresponding count of application wheel events.
Keep local and application accumulators coherent when routing changes. Check zero,
partial, one-detent, and multi-detent inputs and the supported Windows settings.

### #30 (Resolved) — Low (P3): SGR blinking text is rendered permanently visible

**Evidence:** the native bridge preserves the pinned `GHOSTTY_STYLE_FLAG_BLINK` bit
(`0x8`) in `NativeCell.Flags`, but
`source/Brows.TTY.Windows/Windows/Controls/TerminalDrawing.cs:137-193` never uses it.
The timer only toggles the cursor at `TerminalEmulator.cs:441-445`; retained row drawings
always contain the same text. Output such as `ESC[5mwarning ESC[0m` never blinks.

**Resolution:** implement a separate text-blink phase that affects blinking glyphs and
their decorations while retaining cell backgrounds, or explicitly document this as an
unsupported terminal rendition feature. Validate visible/hidden phases and SGR reset
without tying text visibility to the cursor's visibility or blink mode.

### #31 (Resolved) — Low (P3): all underline styles collapse into one solid line

**Evidence:** `source/Brows.TTY.Windows/Windows/Controls/TerminalDrawing.cs:182-184`
tests only whether any underline-style bits are set and always draws the same one-pixel
rectangle. Ghostty distinguishes single, double, curly, dotted, and dashed underlines in
those bits. SGR `4:2`, `4:3`, `4:4`, and `4:5` therefore lose their requested appearance;
for example, editor diagnostic curly underlines become ordinary solid underlines.

**Resolution:** decode the underline-style field and render the corresponding geometry,
preserving underline color and DPI scaling, or document the style limitation explicitly.
Verify distinct geometry or pixels for each supported style rather than only checking
that native flags survive parsing.

### #32 (Resolved) — Low (P3): block and underline cursors cover only half a wide character

**Evidence:** `source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:752-759`
always creates a cursor one cell wide. It does not inspect the cursor row's cell width.
The bridge also discards `GhosttyRenderStateCursor.wide_tail` when producing the frame
(`native/bridge/brows_ghostty.c:180-184`). A block cursor positioned on `界`, for
example, covers one of its two cells; a cursor positioned on its trailing spacer is not
normalized to the character's leading cell.

**Resolution:** derive the character's leading cell and span from the retained snapshot
or explicitly transfer the required wide-cursor metadata. Use the two-cell span for block,
hollow-block, and underline cursors; retain the intended bar-cursor behavior. Check
cursor movement onto both halves of a wide character and transitions to narrow cells.

### #33 (Resolved) — Low (P3): package verification fails in Windows PowerShell 5.1

**Evidence:** `scripts/Verify-Package.ps1:7` uses `System.IO.Compression.ZipFile`
without loading its assembly, and line 62 assigns the `Join-Path` result directly to an
XML attribute property. In Windows PowerShell 5.1, the former can leave the type unavailable;
after explicitly loading `System.IO.Compression.FileSystem`, the latter fails with
`Cannot set "value" because only strings can be used as values to set XmlNode properties.`
The README specifies Windows and the .NET SDK but does not require PowerShell 7.
Earlier validation notes already mention a scratch workaround; the checked-in script
still has the problem.

**Resolution:** make the script compatible with the documented environment by loading
the ZIP assembly where needed and using `SetAttribute('value', [string] $packageFolder)`
or an explicit string conversion. Alternatively, declare and document a PowerShell 7
requirement. Validate the checked-in entry point, including fresh-cache consumer restore.

### #34 (Resolved) — Medium (P2): a selection's anchor jumps to different text when output scrolls

**Evidence:** the control stores `_selectionStart` as a viewport `Point` at
`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:938-940` and reuses that
coordinate on each drag update at lines 673-676. `native/bridge/brows_ghostty.c:334-341`
resolves it into a new viewport grid reference on every call. Although Ghostty preserves
the existing selection across output scrolling, the next mouse move replaces its original
anchor with whatever text now occupies the old screen coordinate.

**Reproduction:** fill a three-row terminal with `one`, `two`, and `three`, and select
`one` from viewport column 0 through 2. Write a newline and `four`. Ghostty correctly
continues to report `one` as the selected text. The next drag update using the control's
unchanged start coordinate (0,0) and a new endpoint (3,0) changes the selection to `two`.
Dragging while a command produces output can consequently copy different text from
the text where the user began the gesture.

**Resolution:** keep a terminal/grid anchor tied to the original content for the lifetime
of the gesture, with an explicit policy for resize, reflow, and history eviction. Convert
only the moving endpoint from current viewport coordinates. If preserving an anchor is
impossible, cancel the gesture rather than silently moving it to different content. Test
continued dragging and copy after one or several output-induced scrolls. This is distinct
from #23's out-of-range failure: it also occurs at an unchanged grid size with valid coordinates.

### Architecture assessment

Keep the existing separation between the UI-independent engine, native ConPTY transport,
and dispatcher-owned retained renderer. ABI scalars match the managed declarations,
published snapshots own their buffers, and process creation/disposal are off the dispatcher.
This review found no reason to replace those boundaries or undertake the previously
deferred performance redesigns.

The main architectural weakness is gesture state represented by loosely related fields
inside the control: keyboard press action/composition identity, local/application mouse
ownership, and viewport-relative selection anchors. Small internal state owners with
explicit start/update/end/cancel operations would address the findings without expanding
the public API. Preserve the native terminal lock, immutable snapshots, and separate
reader/writer workers. Prioritize #23-#28 before cosmetic rendition improvements.

### Validation for this review

- Native x64 and ARM64 builds passed using the pinned source/toolchain. ARM64 was
  cross-compiled only; no ARM64 runtime validation was performed.
- Release solution build passed for all four frameworks using
  `dotnet build brows-tty.slnx -c Release --artifacts-path out/review-build`.
  Restore emitted NU1900 vulnerability-feed warnings for an unavailable configured
  GitHub feed. An initial build in the normal output directory encountered DLL locks
  from an already-running sample; that process was left alone.
- Existing suites ran sequentially from the isolated build, using
  `dotnet test tests/Brows.TTY.Windows.Tests/Brows.TTY.Windows.Tests.csproj -c Release`
  with `-f <framework> --no-build --no-restore --artifacts-path out/review-build`
  and TRX results under `out/test-results/review/`:

  | Framework/run | Passed | Failed | Skipped |
  |---|---:|---:|---:|
  | net10.0-windows | 194 | 0 | 0 |
  | net8.0-windows, first full run | 189 | 5 | 0 |
  | net48 | 194 | 0 | 0 |
  | net462 | 194 | 0 | 0 |
  | net8.0-windows, recheck of affected methods | 8 | 0 | 0 |

  The first .NET 8 failures were clipboard access, missing expected mouse input, and
  timeouts in command/resize tests. All affected methods passed on recheck; their
  intermittent cause was not established. This is not a claim of a clean initial
  four-framework suite. The passing suites do not cover the newly identified defects.
- An ignored diagnostic console harness under `out/review-probe/` exercised real
  Ghostty, ConPTY, the WPF control in an invisible HWND, and the real key-echo child.
  It reproduced #23-#28 and #34 as described above. It used reflection to arrange selection
  state, invoke private update paths, and mark the synthetic repeat event. The sync-exit
  case used a compiled native child. It did not physically switch keyboard layouts or
  move the user's pointer. #29-#32 are static findings, not claimed interactive proofs.
- All three packages packed successfully from the isolated build. ZIP asset ownership,
  dependency direction, target-framework entries, and x64/ARM64 PE-machine checks passed
  using the verifier's archive-checking portion. The PowerShell 5.1 compatibility failure
  is recorded as #33. A scratch copy with that workaround reached fresh-cache consumer
  restore, which failed with NU1301 because network socket access to NuGet's repository
  signature endpoint was denied. Package-consuming applications therefore did not run
  in this review; earlier historical results above are not substituted for current evidence.
- Only this review document is changed in tracked source. Diagnostic projects, binaries,
  package outputs, and TRX files remain ignored under `out/`. No issue was fixed or marked
  resolved during this review.

## Independent correctness triage and additional findings (2026-11-06)

Rechecked the unresolved findings against the current source, pinned Ghostty encoder/renderer
contracts, tests, package scripts, workflow, and documentation. The previous real-process
reproductions for #23-#28 and #34 remain applicable; no production source has changed since
that review. This pass adds #35-#44. No issue is marked resolved.

### Disposition of existing unresolved issues

| Issue | Assessment | Required action |
|---|---|---|
| #23 | Confirmed by the existing real Ghostty reproduction and current control flow. A recoverable selection error can permanently stop rendering while `IsRunning` remains true. | Fix first. Cancel/rebase the drag on grid changes and keep rendering alive after a rejected selection update. |
| #24 | Confirmed. Output is drained before completion, but an unexpired synchronized-output hold can hide the final frame after the timer stops. | Fix first. Finalize the hold after draining and before the last snapshot/exit notification. |
| #25 | Confirmed against the pinned mouse encoder. Button-tracking drags can emit nothing, and any-event drags lose button identity. | Fix with #26 using explicit per-button gesture ownership. |
| #26 | Confirmed. Current Shift state, rather than press ownership, determines release routing; capture loss and multiple buttons are also not retired correctly. | Fix with #25. Preserve local/application ownership through release or cancellation. |
| #27 | Confirmed. Text-producing repeat always uses press action 1, so Kitty repeat reporting is wrong. | Fix by carrying the physical press action to its text event without changing composition commits. |
| #28 | Confirmed by the existing synthetic composition reproduction. Non-AltGr commits can inherit a stale physical letter under Kitty report-all. | Fix with layout-aware physical-key tracking and text-only composition delivery; validate real layouts/IME separately. |
| #29 | Confirmed statically. Wheel magnitude, partial detents, zero delta, and Windows scroll settings are discarded. | Fix after the mouse gesture-state corrections; cover both local and application routing. |
| #30-#32 | Real rendition limitations, not performance problems. Text blink, underline variants, and wide-cursor geometry are absent or incorrect. | Lower priority. Implement them, or explicitly document unsupported rendition behavior; #32 has a concrete geometry fix. |
| #33 | Confirmed again with Windows PowerShell 5.1 probes: both ZIP type loading and XML attribute assignment fail. | Fix the checked-in script for the documented environment, or declare and enforce PowerShell 7. |
| #34 | Confirmed independently of #23: valid coordinates at an unchanged grid still re-anchor a selection to different content after output scrolls. | Fix with #23 using a content-tracked anchor, or cancel when that anchor cannot be preserved. |

Resolved #1, #2, #6, #7, #15-#22 and the completed blink-gating portion of #9 remain resolved.
The measured deferrals for #3-#5, #8, #10-#14 and the remaining visual restructuring in #9
remain appropriate: they are optimization tradeoffs, not established correctness defects.
No new evidence justifies reopening them.

Recommended implementation order is #23/#34, #24, #25/#26, #27/#28/#37/#38, then #29/#39/#40.
Fix #35 and #36 before broader cosmetic rendition work. The internal resize and tooling/documentation
items are small, independent follow-ups. Shared gesture/input state should be corrected coherently,
but issue numbers remain separate because their triggers and observable failures differ.

### #35 (Resolved) — Medium (P2): selection reveals concealed text and concealment leaves decorations visible

**Evidence:** the bridge handles `GHOSTTY_STYLE_FLAG_INVISIBLE` only by assigning the original
background to the foreground (`native/bridge/brows_ghostty.c:215-229`). Selection later substitutes
a blue background without changing that foreground
(`source/Brows.TTY.Windows/Windows/Controls/TerminalDrawing.cs:121-134`). The glyph and decoration
paths at `TerminalDrawing.cs:137-193` never test the preserved invisible bit (`0x20`). An explicit
SGR 58 underline color can therefore remain visible even without selection. Ghostty's own renderer
skips all foreground elements for invisible cells (`native/ghostty/src/renderer/generic.zig:3233-3243`).

A read-only drawing probe using a native-equivalent selected cell produced a blue selection
rectangle, a glyph run in the original black background color, and a red underline. Thus concealed
text becomes visible against the selection background, and colored decorations reveal concealed
content placement. This is visual concealment behavior, not a claim that terminal copy must redact
selected text.

**Resolution:** after emitting normal/selected backgrounds, skip glyphs and all foreground
decorations for invisible cells. Add selected and unselected drawing/pixel tests, including an
explicit underline color and SGR reset.

### #36 (Resolved) — Medium (P2): incomplete font glyph maps can throw and permanently stop frame refresh

**Evidence:** configuration accepts every available style face and validates a usable `M` only for
the normal face (`source/Brows.TTY.Windows/Windows/Controls/TerminalDrawing.cs:55-99`). The ASCII
path directly indexes `glyph.CharacterToGlyphMap[...]` at `TerminalDrawing.cs:161-171`. A missing
character throws `KeyNotFoundException`; zero/`.notdef` mappings are not rejected either. The timer
then stops at `TerminalEmulator.cs:469-478`, while a non-faulted session remains marked running.

A font-metadata probe found that valid Segoe Fluent Icons and Segoe MDL2 Assets style faces lack
ordinary ASCII glyphs. Normal can be replaced because it lacks `M`, while a bold face remains
incomplete and reaches the throwing lookup. An independent drawing probe also reproduced
`KeyNotFoundException` from `DrawRow` for bold ASCII with Segoe Fluent Icons. The complete
render/timer failure was not dynamically reproduced.

**Resolution:** use checked glyph lookup, reject unusable glyph zero, split runs at unavailable
characters, and use WPF text fallback or a compatible fallback glyph face. Preserve run grouping by
actual typeface. Add partial-coverage font cases for normal, bold, italic, and bold-italic cells,
and verify that later output continues to refresh.

### #37 (Resolved) — Medium (P2): non-letter printable keys lose Kitty report-all identity and releases

**Evidence:** Win32 translation maps ordinary digits, punctuation, and space
(`native/bridge/brows_session.c:278-310`), but `bt_key` supplies `unshifted_codepoint` only for A-Z
(`native/bridge/brows_ghostty.c:255-264`). The pinned Kitty encoder needs a functional-key entry or
a nonzero unshifted codepoint (`native/ghostty/src/input/key_encode.zig:130-149`). Otherwise a press
falls back to raw UTF-8 and a text-empty release returns no bytes (`key_encode.zig:224-231`).
For example, report-all mode receives raw `1` rather than a CSI-u Digit1 event and can omit its
release. Existing exact-key coverage primarily exercises X and does not cover these identities.
Numpad keys already have explicit entries in Ghostty's Kitty table and are not examples of this
missing-codepoint defect; preserve them as regression baselines.

**Resolution:** obtain the layout-dependent unshifted codepoint for every applicable writing-system
key from Windows translation and pass it through the native event instead of synthesizing only A-Z.
Cover digits, shifted punctuation, space, keypad keys, press/release, alternate-key reporting, and a
non-US layout. This shares translation plumbing with #28 but is a distinct physical-key failure.

### #38 (Resolved) — Medium (P2): modifier changes can leak text from an already consumed local shortcut

**Evidence:** a locally handled press is retained in `_shortcutKeys`, but `OnTextInput` recomputes
the shortcut using current modifiers and requires a non-null property
(`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:843-881`). If a local Ctrl+V press's
text arrives after Ctrl is released, for example, the unmodified V lookup no longer identifies the
tracked local press and the text can reach the child. This violates the documented guarantee that
text from an already handled press stays local (`README.md:78-81`). Resolved #21 covers changing the
option during a press, not changing its modifiers.
This is a conditional delivery-path finding: reproduce delayed text across the modifier change
before claiming an interactive Windows-input failure.

**Resolution:** route text using the retained key-down decision rather than a live modifier lookup.
A subsequent forwarded press should still retire stale local tracking, and genuine composition
commits must retain their existing behavior. Add modifier-transition counterparts to the #21 tests,
including Enter newline suppression and clipboard shortcuts.

### #39 (Resolved) — Medium (P2): XButton1/XButton2 are reported as middle-button input

**Evidence:** mouse down/up map every button other than left/right to native button 3, Ghostty's
middle button (`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:930-935,967-979`).
`SendMouse` also omits XButton states from `pressed` (`TerminalEmulator.cs:707-713`). Ghostty has
separate eight/nine buttons, encoded as extended buttons 128/129
(`native/ghostty/include/ghostty/vt/mouse/event.h:56-57`;
`native/ghostty/src/input/mouse_encode.zig:213-223`). Back/forward clicks therefore become middle
clicks and motion/outside-viewport state is wrong.

**Resolution:** map WPF XButton1/XButton2 explicitly to Ghostty eight/nine and include their pressed
states. Coordinate capture and multi-button ownership with #25/#26, and test exact press/release and
drag events for both extended buttons.

### #40 (Resolved) — Medium (P2): exited mouse-aware applications disable local interaction with retained output

**Evidence:** normal exit retains `_session` and the final frame but sets `IsRunning` false and stops
the timer (`source/Brows.TTY.Windows/Windows/Controls/TerminalEmulator.cs:329-376`). `ReportsMouse`
still checks only the stale frame's mouse flag and current Shift (`TerminalEmulator.cs:716`). An
unshifted drag therefore takes the application path without creating a selection, and the wheel
takes the application path instead of local history scrolling (`TerminalEmulator.cs:930-999`).
`Send` then drops the operation because the session is no longer running (`TerminalEmulator.cs:482-486`).

**Resolution:** route mouse gestures locally for an exited retained session rather than following
stale application mouse modes. Preserve explicit failure behavior for faulted/disposed sessions.
Add exit-after-1000/1006 tests proving unshifted selection and wheel scrolling work on final output.

### #41 (Resolved) — Low (P3): zero cell-pixel dimensions mutate Ghostty before mouse validation fails

**Evidence:** `bt_resize` calls `ghostty_terminal_resize` before configuring mouse geometry
(`native/bridge/brows_ghostty.c:134-141`). Ghostty accepts nonzero grid dimensions with zero pixel
dimensions and updates pixel/synchronized-output state
(`native/ghostty/src/terminal/Terminal.zig:4919-4953`), but mouse setup rejects zero cell width or
height (`native/ghostty/src/terminal/c/mouse_encode.zig:49-51`). Consequently internal
`TerminalEngine.Resize(40, 10, 0, 16)` can resize/reflow and release a synchronized-output hold,
then throw with mouse geometry left stale. WPF currently supplies at least one pixel, so this is an
internal core API edge.

**Resolution:** validate cell dimensions and derived screen-size overflow before calling Ghostty.
Add regressions proving rejected metrics leave grid, pixel, mouse, and synchronized-output state
unchanged.

### #42 (Resolved) — Low (P3): oversized Win32 resize leaves Ghostty and ConPTY on different grids

**Evidence:** `TerminalSession.Resize` resizes Ghostty first and ConPTY second
(`source/Brows.TTY.Win32/Win32/TerminalSession.cs:59-62`). Ghostty accepts any nonzero `uint16_t`
grid, while native ConPTY rejects zero or values above 32767
(`native/bridge/brows_session.c:252-256`). `Resize(32768, 24, 8, 16)` can therefore reflow Ghostty
and then throw, leaving ConPTY at its old dimensions and subsequent output/cursor coordinates
inconsistent. The WPF caller clamps to 32767, so this affects the internal transport API.

**Resolution:** apply ConPTY's dimension constraints before `Engine.Resize`, preserving the existing
valid-resize ordering. Add a rejection test proving both grids remain unchanged.

### #43 (Resolved) — Low (P3): validation documentation is stale and links to a nonexistent evidence file

**Evidence:** `README.md:216-218` says six consumers run on "both frameworks", but
`scripts/Verify-Package.ps1:69-100` executes core, Win32, and WPF consumers on four frameworks each
(12 executions). `README.md:233-238` says the workflow runs "both test targets", while
`.github/workflows/workflow.yml:60-82` runs four sequential suites. `README.md:213-215` links to
`_agents/Progress.md`, which is absent from the working tree and untracked; `_agents/` contains only
`Review.md`. Since README.md is the package readme, the dead link can reach package consumers.

**Resolution:** document the four-framework/four-suite/12-consumer validation scope and remove or
replace the dead link, or supply the intended tracked evidence page.

### #44 (Resolved) — Low (P3, CI): the native buffer-growth regression is absent from documented and CI validation

**Evidence:** `scripts/Test-NativeBufferGrowth.ps1:22-36` compiles and runs allocation-count probes,
but `.github/workflows/workflow.yml:49-94` never invokes it and the local command sequence at
`README.md:194-200` omits it. Only historical notes in this review record its execution. A future
return to per-cell native `realloc` growth can therefore pass CI because managed correctness tests
do not enforce allocation counts.

**Resolution:** invoke the checked-in script after the x64 native build in CI and include it in the
documented local validation sequence. Keep this separate from ARM64 runtime claims.

### Validation for this pass

- `dotnet build brows-tty.slnx -c Release --artifacts-path out/audit-build` succeeded for all managed
  targets with zero warnings and zero errors.
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-NativeBufferGrowth.ps1` passed;
  the three real UTF-8 row cases reported 2/3/6 reallocations at 200/512/4096 columns.
- The existing x64 net10.0-windows suite from the isolated review build passed 193/194 tests with no
  skips. `RightClick_WithoutSelectionPreservesClipboardAndApplicationMouseInput(True)` received SGR
  button code 10 rather than 2, indicating an unexpected modifier bit. Its isolated recheck passed
  both parameter cases (2/2). The intermittent cause was not established, so this is recorded as
  validation evidence, not promoted to a new finding.
- Fresh read-only Windows PowerShell 5.1 probes confirmed #33. Read-only drawing/font probes support
  #35/#36 as described above. Other new findings are source- and pinned-API-confirmed code paths;
  no new interactive or physical-input reproduction is claimed.
- No full four-framework rerun, native library rebuild, package/consumer verification, or ARM64
  runtime execution was performed in this pass. Existing historical results are not substituted for
  those checks. Generated outputs and test results remain ignored under `out/`; no production code,
  tests, public APIs, or build settings were changed.
## Correctness fixes and acceptance (2026-11-06)

All accepted correctness findings #23–#44 are resolved. Earlier measured performance
deferrals retain their existing dispositions. GPT-6-Luna subagents with Max reasoning
implemented the changes; the parent reviewed their source, tests, integration, and
final diff before accepting the work. Existing review evidence above describes the
pre-fix code; line numbers there are historical.

| Issue | Resolution and regression evidence |
| --- | --- |
| #23 | Cancel local drags before grid changes and isolate selection failures from frame scheduling. `ResizeDuringLocalDrag_CancelsTheGestureAndKeepsFramesUpdating` verifies later displayed output. |
| #24 | Complete synchronized output after transport drain and before the final frame/event. `CompleteOutput_ReleasesHeldSnapshotAndCapturesFinalTextAndColor` and `ObserveExit_CompletesSynchronizedOutputBeforeRaisingFinalFrameEvent` verify text, background, and exit code. |
| #25 | Track held application buttons and retain their identity for motion, including outside the viewport. `ApplicationDrag_MotionKeepsThePressedButtonIdentity` and `ApplicationDrag_ReportsMotionBeyondTheViewportWhileHeld` cover tracking modes 1002/1003. |
| #26 | Retain gesture ownership through Shift changes and retire buttons on capture/focus loss. `ShiftChangesDuringGestures_DoNotChangeButtonOwnership` and `MultipleApplicationButtons_KeepMotionAndCaptureLossReleasesPaired` verify exact report bytes. |
| #27 | Capture repeat action with the physical press and use it when text commits. `PrintableTextInput_UsesRepeatActionForRepeatKeyDown` verifies Kitty press/repeat/release bytes. |
| #28 | Preserve committed composition text without substituting a physical key or inventing its release. `CommittedTextWithoutPreedit_MismatchingPhysicalKeyUsesTextOnly`, `CommittedPreeditText_UsesTextOnlyAndDoesNotReleasePhysicalKey`, and `FrameworkTextComposition_UsesCommittedTextInsteadOfPhysicalIdentity` cover composition paths; `PlainTextCompositionStart_PreservesPhysicalKeyIdentity` protects ordinary typing. |
| #29 | Accumulate partial wheel detents, apply their magnitude and Windows row/page setting, and reset residuals when routing/lifetime changes. `LocalWheel_PreservesPartialAndMultipleDetentsAndWindowsRowCount`, `ApplicationWheel_AccumulatesDetentsAndResetsWhenRoutingChanges`, and `WheelScrollRows_UsesDisabledCustomAndPageScrollSettings` verify these outcomes. |
| #30 | Blink text from retained snapshots independently of cursor blink and restore final visibility on exit/stop. `RefreshFrame_TextBlinkPhaseRedrawsOnlyRetainedBlinkRowsAndStopRestoresVisibility`, `FontReconfiguration_PreservesHiddenTextBlinkPhase`, and `ObserveExit_RestoresBlinkingTextInTheFinalFrame` verify phase and snapshot behavior. |
| #31 | Draw distinct single/double/curly/dotted/dashed underline geometry with SGR color and DPI scaling. `DrawRow_UnderlineStylesProduceDistinctRedPixelsAtHighDpi` checks geometry and pixels. |
| #32 | Resolve wide-character head/tail cells for block/hollow/underline cursor bounds; retain narrow bars. `CursorBounds_WideLeadingAndTailPositionsCoverBothCellsAndNarrowPositionsOne` and `CursorBounds_BarRemainsNarrowAtTheReportedCursorColumn` verify widths and positions. |
| #33 | Load ZIP support explicitly and set the local NuGet XML attribute through `XmlElement.SetAttribute`. The full package verifier passed under Windows PowerShell 5.1. |
| #34 | Own a Ghostty tracked grid reference during each local drag, rather than an old viewport coordinate. `SelectionAnchor_FollowsOriginalContentAcrossOutputScrollAndCopy` verifies the original text, current endpoint, exclusions, and clipboard copy. Screen switches and evicted history cancel safely in `AlternateScreenChange_CancelsTheTrackedAnchorWithoutStoppingFrames` and `ClearedScrollback_CancelsAnEvictedTrackedAnchorWithoutStoppingFrames`. |
| #35 | Suppress concealed glyphs and all foreground decorations while retaining selected backgrounds and copy semantics. `DrawRow_ConcealPreservesSelectedBackgroundAndSuppressesGlyphAndColoredUnderline` checks drawings and pixels. |
| #36 | Check glyph availability and use per-style fallback while preserving exact run arrays and fixed advances. `DrawRow_MissingAsciiGlyphFallsBackForEveryStyleAndLaterRows` covers normal/bold/italic/bold-italic and later rendering. |
| #37 | Pass explicit layout-specific unshifted identity and consumed modifiers through ABI 3; keep numpad mappings. `PrintableKeys_KeepKittyIdentityAndMatchingReleases`, `CoreDefaultKeys_KeepKittyIdentityAndNumpadBaseline`, `PrintableKey_EncodesAlternateAndAssociatedText`, and `PrintableKeys_KeepLegacyTextEncoding` cover protocol modes. German/Russian, dead-key, Caps/Shift/NumLock, and delayed-layout cases verify Windows translation without consuming input-thread dead-key state. |
| #38 | Keep consumed shortcut text local using the original press decision. `DelayedLocalPasteText_IsSuppressedAfterModifierChangeAndLaterPressIsForwarded` and `DelayedEnterNewline_AfterModifierChangeStaysLocal` verify the conditional delayed-text path and subsequent normal input. No physical-device reproduction is claimed. |
| #39 | Map XButton1/XButton2 to Ghostty buttons eight/nine. `ExtendedMouseButtons_KeepTheirGhosttyIdentity` verifies exact press/motion/release bytes. |
| #40 | Disable application mouse routing after exit and clear stale interaction state. `ExitedMouseAwareSession_RetainedOutputStillSupportsLocalWheelAndSelection` verifies wheel, selection, and copy against retained output; `NaturalExit_CancelsHeldApplicationMouseOwnership` verifies exit cleanup. |
| #41 | Validate zero and overflowing cell-pixel dimensions before any native resize mutation. `InvalidCellMetrics_PreserveGridPixelMouseAndSynchronizedOutputState` verifies grid, mouse geometry, held output, and later output. |
| #42 | Reject zero/oversized Win32 dimensions before Ghostty or ConPTY changes. `SessionResize_RejectsInvalidGridBeforeGhosttyOrConptyChanges` verifies both dimensions, subsequent command input, and process exit. |
| #43 | Align README validation commands/frameworks/consumer counts with the repository and remove the nonexistent evidence-file link. |
| #44 | Run `Test-NativeBufferGrowth.ps1` in the documented local sequence and CI immediately after native builds. The final native regression passed for 200/512/4096 columns with 2/3/6 reallocations. |

Validation:

- Native x64 and ARM64 builds passed from pinned sources. ARM64 was cross-compiled;
  runtime execution requires an ARM64 Windows runner.
- Full non-incremental Release solution builds passed for all four target frameworks
  with zero warnings/errors using the pinned .NET SDK 10.0.402.
- Sequential x64 suites passed **263/263 on each of net462, net48,
  net8.0-windows, and net10.0-windows: 1,052 passed, zero failed/skipped**.
  TRX files are under ignored `out/test-results/correctness/accepted-*`.
  One earlier .NET 8 run encountered `CLIPBRD_E_CANT_OPEN` in an existing clipboard
  test; a complete rerun passed. No clipboard assertions were weakened.
- Three packages passed archive/native-machine/dependency checks and all twelve
  isolated core, Win32, and WPF consumers passed across the four frameworks.
  Fresh restores and verification ran under Windows PowerShell 5.1.
- This session has no display device. Pixel checks required the existing WPF
  headless-rendering switch in ignored generated test/consumer runtime configs
  (and an isolated Framework test host). The original package run failed its pixel
  check without that switch; the configured run retained all assertions and passed.
  Shipping project/runtime configuration is unchanged.
- Public control API reflection comparison is unchanged. New managed helpers and
  native bindings remain internal/private; the native bridge ABI advances to 3.
- Parent assertion/gap review checked exact bytes, selection content, state changes,
  rejected inputs, rendering geometry/pixels, and lifecycle events. Synthetic routed
  input and real WPF composition objects exercise integration; physical IME/device
  sessions and ARM64 runtime behavior were not exercised. No empirical mutation-test
  survival claim is made.
- Final diff, new files, encodings, line endings, and whitespace were reviewed.
  Generated output and intermediate review/testing artifacts remain untracked/ignored.
