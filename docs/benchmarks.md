# DCU Benchmark Suite

A living test catalog for dcu (Desktop Computer Use), plus the harness that runs
the automatable subset. Goal: every claim dcu makes — focus-free control, input
fidelity, capture correctness, clean lifecycle — is provable on demand.

Harness: `bench/bench.py` (drives `publish/dcu.exe` over stdio MCP).
Test page: `bench/page.html` (self-contained: click grid, mouse recorder,
textarea, scroll tracker, drag-drop, canvas, duplicate buttons, DOM mutator).
Evidence: `C:/tmp/dcu-bench/*.png`.

```bash
python bench/bench.py            # full suite
python bench/bench.py t7 t32     # filter by test function name
```

Report card legend: **pass** / **warn** (documented platform limit) /
**skip** (fixture missing) / **fail**.

---

## Tier 1 — Core promise: focus-freeness

| # | Test | Automated | Method | Pass criteria |
|---|------|-----------|--------|---------------|
| 1 | Type-along | ❌ needs human | user types in app A while dcu drives app B for 60s | zero dropped/misplaced keystrokes in A |
| 2 | Focus-yank guard | ✅ | set foreground to fixture Notepad, click Chrome omnibox (known caret-grabber), compare foreground **HWND** before/after | same HWND (title text ignored — spinners) |
| 3 | No OS focus events on target | ✅ | bench page counts `window.focus` events; burst 5 clicks; count must not change | focusEvents Δ = 0 |
| 4 | Mid-flight interference | ❌ needs human | user clicks/types elsewhere during a 20-step sequence | sequence completes, no cross-contamination |

## Tier 2 — Input fidelity

| # | Test | Method | Pass criteria |
|---|------|--------|---------------|
| 5 | Click accuracy grid | 5×5 buttons on bench page, clicked by coordinates, verified via page's own click log | 25/25 (spot-checked 5) |
| 6 | Click-button coverage | pad records mousedown/up button + dblclick | left, right, double all registered |
| 7 | Typing gauntlet | ASCII + Unicode (accents/CJK/emoji) + 1,200-char burst into (a) Notepad (b) Chrome textarea | byte-exact read-back; correct method per app (`em_replacesel` for edit controls, `render_widget_keys` for Chromium so DOM input events fire) |
| 8 | Key chords | ctrl+s / F5 / arrows posted to target | expected behavior fires, foreground app unaffected |
| 9 | Scroll fidelity | bench page mirrors `scrollY` into window title; 3 pages down | scrollY > 300 (was failing at 300 → fixed, see Bug #2) |
| 10 | HTML5 drag-drop | message-based drag from source to drop zone | drop event fires — **documented platform limit**: HTML5 DnD expects the real input pipeline; plain message drags still work (file drag in Explorer class) |

## Tier 3 — Element identity & snapshots

| # | Test | Pass criteria |
|---|------|---------------|
| 11 | Stable ids | element id from snapshot fires the *intended* control (verified via page log, not just "something changed") |
| 12 | Stale id after DOM mutation | re-resolution to correct control OR clean structured error (`Unknown element id … Call get_app_state for fresh ids`) — never a silent wrong-click (Bug #4 → fixed) |
| 13 | Duplicate-name buttons | 5 identical "Add" buttons; frame-ordered pick hits the third, verified via `dup:2` event |
| 14 | Tree scale | full snapshot of a busy page | < 2s typical, budgets respected |
| 15 | Revision sanity | every mutating call returns a fresh, changed revision |

## Tier 4 — Capture

| # | Test | Pass criteria |
|---|------|---------------|
| 16 | Occluded window | Chrome positioned exactly over Notepad; `PrintWindow` still returns Notepad's content (evidence PNG saved) |
| 17 | Minimized window | clean null/blank, no garbage, server unaffected |
| 18 | Hardware-accelerated content | real `PrintWindow` pixels or a clean null result — never pixels from an unrelated occluding app |
| 19 | Coordinate alignment (DPI) | frame-derived coordinates hit intended button at current monitor DPI |
| 20 | Multi-monitor | requires multi-monitor rig — manual |

## Tier 5 — Hostile edge cases

| # | Test | Pass criteria |
|---|------|---------------|
| 29 | Canvas (no a11y) | zero phantom child elements + coordinate click still lands (`canvas:x,y` event) |
| 31 | Hung app | all Notepad threads suspended; action returns in bounded time; server healthy after |
| 32 | Rapid-fire stress | 100 clicks + 30 types back-to-back | 0 errors, server alive, avg latency reported |
| 34 | Window closed mid-action | clean `target window no longer exists`, server survives |
| 35 | Window moved mid-action | bounds-guard ON → refusal; OFF → honest report |
| 36 | Lock screen during action | ❌ not automated (won't lock your screen on you) |
| 37 | Multi-instance app | 3 Notepads; resolution by pid hits the right one |

## Tier 6 — Overlay & lifecycle

| # | Test | Pass criteria |
|---|------|---------------|
| 38 | Ghost flags | overlay window has `WS_EX_LAYERED\|TRANSPARENT\|NOACTIVATE\|TOOLWINDOW` and display affinity `WDA_EXCLUDEFROMCAPTURE (0x11)` |
| 39 | Ghost capture-exclusion | visual/screen-record check — manual (flag verified by #38) |
| 40 | Ghost cleanup | server killed mid-action → zero orphan overlay windows |
| 41 | Shutdown regression | bare EOF and init-then-EOF both exit 0 (the shutdown-cleanup bug, canonized) |
| 42 | Schema conformance | ≥10 tools advertised; bad enum args → structured error, not exception |
| 43 | execute_sequence | failure at step 7 of 10 halts with `stopped_at=7`, partial results accurate |
| 44 | Settings resilience | corrupt `settings.json` → defaults, healthy server |

---

## Bugs found by this suite

| # | Severity | Bug | Fix |
|---|----------|-----|-----|
| 1 | high | **Chrome typing was hollow** — `uia_setvalue_append` set field text but fired no DOM input events, so web forms never registered input | new tier-2 typing path: `WM_KEYDOWN`/`WM_CHAR`/`WM_KEYUP` stream into the `Chrome_RenderWidgetHostHWND` child (method `render_widget_keys`) — real key events, real form state |
| 2 | high | **Scroll barely moved** — wheel fallback posted a single `WM_MOUSEWHEEL` regardless of `pages`, *and* used client coordinates where wheel messages require screen coordinates, *and* posted to the frame instead of the content window | per-notch loop (3 notches/page), screen-coordinate lParam, routed via `ChildWindowFromPoint`; direction validated (`up/down/left/right` only, structured error otherwise) |
| 3 | med | **Stale element id → raw exception** surfaced as unstructured MCP error | unknown ids now return a structured error: `Unknown element id 'eN' in snapshot rN. Call get_app_state for fresh ids.` |
| 4 | med | **Ghost capture-exclusion not sticking** — `SetWindowDisplayAffinity` was called before the handle reliably existed; affinity read back `0x0` | applied in `OnHandleCreated`, re-applied on every show; failure logs to debug |
| 5 | low | Clicks on dead windows posted silently into the void | `IsWindow` pre-check → `target window no longer exists` |
| 6 | test | Harness compared window *titles* for the focus test — foreground terminal has a live spinner, titles never equal | compare HWNDs |
| 7 | test | Bench pad/drag divs invisible to UIA (names < 30 chars, no roles) | aria-labels + `role="button"` on the bench page |

## First full run (before fixes)

```
pass 18 | warn 0 | skip 3 | FAIL 6
  2  focus-yank        → test bug (#6)
  7b chrome typing     → Bug #1
  9  scroll fidelity   → Bug #2
  12 stale id          → Bug #3 (test read it wrong too)
  32 rapid-fire        → test bug (fixture race)
  38 ghost affinity    → Bug #4
```

## Known platform limits (warn, not fail)

- **HTML5 drag-and-drop** ignores posted mouse messages (needs the real input
  pipeline). Message drags work for classic targets (Explorer, sliders).
- **Raw-input apps** (games, `GetAsyncKeyState` hotkeys) can't see any posted input.
- **Elevated targets** unreachable from a non-elevated dcu (clean refusal).
- **Lock screen / secure desktop**: no automation possible by design of the OS.
