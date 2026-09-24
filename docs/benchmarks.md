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
| 8 | high | **`type_text` could press real Enter on a `\n`** — `VkKeyScanW('\n')` returns `VK_RETURN` with the Ctrl modifier bit set on a US layout (empirically confirmed: result `525` = key `0x0D` + ctrl bit `0x0200`); the render-widget typing tier only ever masked in the shift bit and ignored ctrl/alt, so any `\n` in typed text sent a REAL `VK_RETURN` keydown/keyup to the target — a web page's Enter handler can read that as "submit the form", an unintended action from a tool that is only supposed to type literal text. Found via an Opus 5.5 advisor-guided bug-hunt pass, verified empirically via a direct `VkKeyScanW` P/Invoke before touching any source. | `BackgroundInput.ShouldSendCharOnly(vk)` now routes any vk that is unmapped (`-1`) OR requires ctrl/alt (bits `0x0600`) to a plain `WM_CHAR`, never a simulated keydown/keyup. Also fixed in the same pass: both per-character typing loops (`render_widget_keys`, `wm_char`) ignored a caller's `CancellationToken` mid-loop (`Task.Run(..., ct)` only prevents the delegate from *starting*) — added `ct.ThrowIfCancellationRequested()` per character so a cancelled/timed-out `type_text` call actually stops typing instead of silently finishing the whole string. Tests: `BackgroundInputKeystrokeTests.cs` (3, direct `VkKeyScanW` + reflection into the real internal method), `BackgroundInputCancellationTests.cs` (1, live owned window, proves cancellation stops the loop in <3s instead of running the full ~16s). |
| 9 | high | **A wedged UIA target could silently freeze every DCU tool until restart** — `UiaThread`'s caller-side timeout only marked that ONE call's `Task` as failed; the underlying STA COM call kept running forever on the single dedicated thread (there is no safe way to abort a running STA COM call from another thread), so every OTHER queued or future call — against any other, unrelated target — also timed out, with no recovery short of restarting DCU. Reproduced directly (not assumed): a regression test against the unmodified code fails exactly this way. | New watchdog thread in `UiaThread` polls for a currently-executing item that has run longer than its OWN configured timeout plus a 2s grace period, then abandons that wedged worker (marks its queue complete so it exits on its own if the call ever returns, or leaks harmlessly as a background thread otherwise) and starts a fresh worker carrying over every not-yet-started queued item. Only the single genuinely-stuck call stays stuck; everything else recovers automatically. Test: `UiaThreadCancellationTests.InvokeAsync_RecoversAfterAWedgedCall_SoASubsequentUnrelatedCallStillCompletes` — confirmed to FAIL against the pre-fix code (via a temporary `git stash` of only this file) before confirming it passes with the fix. |

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

## Bug-hunt pass (2026-09-24, Opus 5.5 advisor-guided)

Per explicit user request, once the F01-F60 spec ledger reached 60/60 `in_progress` with real evidence: consulted `claude-advisor` (Opus 5.5, `high` effort) for where to focus a bug hunt on a codebase this size, rather than a blind file-by-file review. Its recommendation (followed): invariant-driven hunt, not static analysis or a broad read -- pick safety invariants, grep every site that could break each one, write a live/reflection-backed failing test before fixing anything, fix only what a failing test proves. Severity bar for "major": wrong/unintended action, false success on a mutating action, secret exposure, or a server-wide wedge/crash; everything else is documented and deferred, never fixed speculatively.

Findings, in the advisor's suggested priority order:

- **Class A (input dispatch reports success when delivery went wrong)**: found and fixed bugs #8 above (real Enter keypress from a `\n`, and ignored mid-loop cancellation). Not pursued further this pass: `PostMessageW` return values are ignored throughout `BackgroundInput` (fire-and-forget messaging is expected for background input; the per-thread ~10,000 posted-message quota is a real theoretical limit but requires an unrealistically long single `type_text` call against an unresponsive target to matter, so it is documented, not fixed) and Tier 3's `SetValue(CurrentValue + text)` read-modify-write can silently replace content on a field whose current value can't be read back (masked/password fields already have this documented limitation elsewhere; Tier 3 is an explicitly opt-in fallback, not the default path).
- **Class B (UIA thread wedge; "timed out" ≠ "stopped running")**: found and fixed bug #9 above (watchdog + worker replacement).
- **Class C (an id resolves to a different element/window/process)**: investigated, not a new finding -- this was already the primary focus of several earlier sessions (F09 native identity hardening): `UiaService.TryValidateTarget`/`TryValidateSnapshot` check `Process.StartTime` (a real defense against PID reuse, not just PID equality), `NativeMethods.IsWindow`, and a WinEvent-driven `NativeTargetMonitor` with quarantine-on-destroy re-confirmed immediately before every dispatch. No new gap found; re-litigating already-hardened, already-tested ground was judged lower value than the untested areas above.
- **Class D (browser observation model vs the DOM)**: the specific failure mode the advisor named (`InnerTextAsync` on form fields) was already found and fixed in an earlier session (`InputValueAsync` for input/textarea/select), with a live test (`LiveInstalledEdge_ObservationTextReflectsFilledInputValue_NotEmptyInnerText`). Not re-investigated further this pass (iframes/shadow DOM/post-navigation refs already have separate live coverage per earlier STATE.md sessions).
- **Class E (process-lifetime store concurrency)**: spot-checked `BackgroundInput._addressBarInputs` (the advisor's named example) -- a real, narrow, low-severity finding: a `hwnd` entry set when the address bar is selected but never followed by a Return keypress (or the window closing) was never removed, a small unbounded-in-theory leak keyed by window handle. Below this pass's own "major" bar (no wrong action, false success, secret exposure, or wedge), but cheap and low-risk to fix once found, so fixed anyway: `PruneDeadAddressBarEntries()` opportunistically removes any entry whose window no longer exists (`NativeMethods.IsWindow`), called from both entry points that touch the dictionary. Test: `BackgroundInputAddressBarLeakTests.cs` (1, live, confirmed to fail pre-fix via the same scoped-`git stash` discipline as bugs #8/#9).

All fixes above were verified with a regression test that reproduces the original failure against the pre-fix code (via a scoped `git stash` per fix) before being marked fixed, not just a test that happens to pass. Full re-verification after this pass: .NET Release **543/543** (was 537, +6: 2 keystroke tests, 1 cancellation test, 1 watchdog-recovery test, 1 address-bar-leak test, 1 from the earlier same-session SetValue read-only fix), Python pytest **17/17**, both live native scenarios (login, ambiguity) PASS.

### Round 2 (same day, per explicit user follow-up request): one real finding requiring a user policy decision, two claims that did not hold up on inspection

Consulted the advisor again for where to focus next. Its strongest finding, verified against source before acting on it (not taken on faith):

- **`http_request`'s `allowed_hosts`, and `read_file`/`write_file`'s `allowed_root`, are parameters supplied by the SAME MCP caller the check is meant to restrict** (`ComputerUseTools.cs`: `HttpRequest(... string allowed_hosts ...)`, `ReadFile`/`WriteFile(... string allowed_root ...)`). The redirect-hop and path-traversal validation against that list is real and tested (F32-T02) -- but the list itself is not read from any operator-controlled configuration (`Settings`/`settings.json`), so a compromised or prompt-injected caller can set `allowed_hosts=attacker.example.com` or `allowed_root="C:\\"` in the very same call and the check passes trivially. `WorkflowValueStore.Resolve`'s `value_env` path can also pull any environment variable of the DCU process by name, again with no allowlist.
  - **This is a genuine, verified property of the code, not a fabricated concern** -- but whether it is a *bug* depends on a policy question this session cannot answer unilaterally: the objective's own stated principle is "Jev must not become a permission/security gate" and DCU is designed to trust its calling orchestrator for user-authorized tasks including private data. F32-T02's literal acceptance test ("unapproved HTTP redirect is blocked") is about redirect-hop validation within a caller-approved list, which is implemented and tested -- it does not literally require an *operator*-level (as opposed to caller-level) allowlist.
  - **Not implemented this session, left as a recorded, undecided finding**: adding a `Settings`-level (operator-configured, not tool-argument) host/path/env allowlist that a caller's own `allowed_hosts`/`allowed_root`/`value_env` request must additionally satisfy would close the silent-exfiltration-channel concern, but is a real change to DCU's trust model that the user should decide on explicitly, not something to build speculatively against the objective's own anti-gate principle. Recorded in `docs/capability-status.json`'s F32 entry.
- **Two other specific claims were checked against source and did NOT hold up, so nothing was changed for them**: (1) the advisor hypothesized `execute_sequence`/`run_workflow`'s failure-detection (`HasFailed`) could miss a failure-shaped result lacking `error`/`ok` fields (e.g. a `hit:false` cache-miss shape) and let `stop_on_error` continue into a later mutating step -- verified that every tool actually reachable through `DispatchStep`'s dispatch table (the only tools usable as workflow/sequence steps) consistently returns `{error:...}` or `{ok:false,...}` on every failure path; the `hit:false` shapes the advisor cited belong to tools (`resolve_cached_action`, `resolve_field_text`, `resolve_grid_cell`) that are not in `KnownWorkflowTools`/`DispatchStep` at all and cannot be used as a step. (2) the advisor hypothesized a per-target resource lease could be skipped or keyed inconsistently, letting two concurrent DAG nodes race on the same real window -- verified `ComputerUseTools` has a `private static readonly SemaphoreSlim _mutationLock = new(1, 1)` used by every native mutating tool (click/type_text/press_key/scroll/drag/set_value), which already fully serializes all native mutations process-wide regardless of lease-key granularity, so the specific race the advisor described cannot occur for native targets. Browser targets have no equivalent global lock and rely solely on the per-`candidate_id` lease, which is a real but much more contrived edge case (would require a workflow author to reference the same physical DOM element via two different observation-derived `candidate_id`s in the same concurrent wave) -- not concretely reproduced, not fixed speculatively.

No code changed this round; the one real finding needs a user decision, not a unilateral fix, and the other two investigated leads did not reproduce.

## F31 action-resolution cache: does it actually help? (honest measurement, 2026-09-24)

`bench/cache_benefit_bench.py` measures real wall-clock latency for the SAME target field on
the repository's owned WindowStressFixture, comparing:

- **cold path** (no cache): `get_app_state` (full snapshot, up to 50 elements) + `set_value`
  by the element id that scan found -- what an agent does with no memory of the UI.
- **warm path** (F31 cache hit): `resolve_cached_action` (attribute-filtered re-resolution
  against a fresh snapshot, never a replayed stale id) + `set_value` -- what an agent does
  after one prior `remember_action_resolution` call for the same `task_intent`.

Both paths are exactly 2 MCP round trips, so there is no inherent round-trip-count advantage
to the cache on a small form -- only the difference in the server's own element-resolution
work. Three real runs against this fixture (15/15/30 iterations, `python bench/cache_benefit_bench.py`):

| Run | Iterations | Cold mean | Warm mean | Result |
|---|---|---|---|---|
| 1 | 15 | 236.6ms | 207.4ms | warm faster by 29.2ms |
| 2 | 15 | 121.7ms | 183.2ms | warm **slower** by 61.5ms |
| 3 | 30 | 223.6ms | 203.7ms | warm faster by 19.9ms |

**Honest conclusion**: at this fixture's small element count (a single WinForms window,
under 50 elements), the F31 cache shows **no reliable measurable latency benefit** --
results swing between a modest win and a modest loss, dominated by process/MCP-transport
noise rather than any consistent algorithmic advantage. This does not mean the cache is
worthless: its real value proposition is bounded resolution cost as element-tree size grows
(a `get_app_state` scan is O(tree size); `resolve_cached_action`'s attribute filter over
the same snapshot is comparable, so the two converge at small scale and would only diverge
on a much larger/busier target than this fixture provides). Recorded here rather than
asserted from the design intent alone, per this project's standing rule against claiming a
capability without live measurement. A future run against a genuinely large element tree
(hundreds+ of elements) would be needed to observe a real divergence, if one exists.

## Jev-mode comparison: jev=off vs jev=fast_loop (honest measurement, 2026-09-24)

`bench/jev_mode_benchmark.py` is the first REAL end-to-end Jev-off vs Jev-on comparison in this
project. No live TypeSafe API key exists in this environment (same documented constraint as
every other Jev-adjacent feature) -- but DCU's own `JevClient` never validates the key's
authenticity, only that one is present, and it speaks a real HTTP POST/JSON protocol to whatever
`JevBaseUrl` is configured. This script stands up a local HTTP server that implements the real
Choice/Noul answer SHAPE `JevController` requires, points DCU's real `fast_loop` path at it, and
reads genuine measured timing through the real `get_benchmark_report` tool (F53) -- not synthetic
data, not a hand-computed estimate.

Two real runs against the owned WindowStressFixture (5 calls per group each run):

| Run | Jev OFF baseline (`set_value`) | Jev decision overhead (`jev_decide`, real round trip) | Jev fast_loop dispatch | Estimated fast_loop total |
|---|---|---|---|---|
| 1 | 79.6ms | 14.6ms | 74.0ms | 88.6ms (+11.3% vs baseline) |
| 2 | 83.6ms | 11.4ms | 70.6ms | 82.0ms (-1.9% vs baseline) |

**Honest conclusions:**

- The Jev decision round trip itself (protocol build, POST, parse, validate, target re-resolve)
  genuinely costs low double-digit milliseconds against a local loopback server that answers
  almost instantly -- confirming the pipeline itself (request construction, Choice/Noul
  validation, confidence gating, stale-decision rejection) adds only a small, real, measured
  overhead on top of a plain dispatch, not a large structural cost.
- **This measurement significantly UNDERSTATES real-world Jev latency.** A real TypeSafe/Jev
  endpoint involves actual network round-trip time plus real model inference, both of which are
  absent here by construction (the loopback server has no network hop and no model to run). The
  `avgDecisionMs` figures above measure DCU's own dispatch/parsing overhead only, not Jev's real
  end-to-end latency -- that number is not obtainable in this environment without a live
  `TYPESAFE_API_KEY`, and is not claimed here.
- As with the F31 cache-benefit benchmark above, the two runs disagree on direction (+11.3% vs
  -1.9%) at this fixture's small scale -- recorded honestly rather than cherry-picked, consistent
  with this project's standing rule against claiming a capability without live measurement.
- `RunTimelineBenchmark`'s `jevDriven` grouping is scoped to the `jev_decide` entry itself, by
  design (see `JevAct`'s own code comment): `DecisionMs` never merges into the resulting
  `click`/`set_value` entry's own row. A caller reconstructing a fast-loop step's full cost pairs
  the adjacent `jev_decide` and dispatch entries, exactly as this benchmark does.

## Known platform limits (warn, not fail)

- **HTML5 drag-and-drop** ignores posted mouse messages (needs the real input
  pipeline). Message drags work for classic targets (Explorer, sliders).
- **Raw-input apps** (games, `GetAsyncKeyState` hotkeys) can't see any posted input.
- **Elevated targets** unreachable from a non-elevated dcu (clean refusal).
- **Lock screen / secure desktop**: no automation possible by design of the OS.
