# Jev flow: the responsive game loop (experimental)

`game_jev_flow` replaces the usual per-step cycle

```
screenshot -> model looks -> model decides + finds target -> input -> screenshot to verify
```

with a loop that only pays for vision when it has to:

```
capture locally -> recognise the screen from a saved fingerprint (no model)
   -> ask Jev one question: "which known input moves toward the goal?"  (text state; image only if needed)
   -> send the input -> assume it worked -> loop (no verification screenshot)
```

It runs as a **bounded serial run**: at most 4 dispatched inputs per call (`max_actions` 1-4), each (screen, input) used at most once, a fresh local frame before every
decision, no verification screenshot between inputs, and a hard deadline (`max_runtime_ms`, at most 60 s, default 30 s). Chain calls for longer routes; the next call
re-recognises the screen locally. An observed transition is **not** required after each input. It only decides what is learned (see below) and what is predicted.

It is experimental, off by default, and separate from the guarded one-action tool (`game_jev_act`, see
[experimental-game-jev.md](experimental-game-jev.md)).

## What it remembers (the action book)

Per `app_id` / version / profile / keyboard layout, DCU saves a small JSON book under
`%LOCALAPPDATA%\shadow-use\jev-flow\`. It never stores screenshots.

| Saved | Used for |
| --- | --- |
| Screens: id, a description, up to 6 tiny 48x27 luminance fingerprints | Recognising "I am on screen X" locally, with no model call |
| Inputs per screen: allowlisted key taps, clicks with a target description | The menu Jev chooses from |
| Learned click boxes (normalized 0-1) from Jev Box or the local detector | Clicking later without an image |
| Transition counts (screen + input -> screen) | Predicting the next screen so the next step can be decided from text |
| Goal -> end screen, learned from the first verified completion | Finishing later runs with a free fingerprint check |

A global screen (`*global*`) can hold inputs offered everywhere (for example Escape).

## Tiers per step

1. **Local capture, always.** A capture is cheap; sending pixels to a model is the expensive part.
2. **Screen resolution.** A fingerprint match confirms the screen for free. No match (or a prediction that is too old,
   `JevFlowSyncEvery`) costs one image call that recognises the screen, learns its fingerprint and, if the screen has no
   inputs yet, maps it from local edge/OCR proposals (`JevFlowDiscovery`) or caller seeds.
3. **Text-only decision** over inputs that need no vision (key taps and clicks with a saved box). `NEED_VISION` is one of the choices;
   choosing it, low confidence, or an ambiguous top-two escalates the same frame to an image decision.
4. **Image decision** only when needed. Unlocated clicks get one Noul + Box question each (at most 8), and the Box is saved on success.
5. **Dispatch.** A fresh local capture re-checks that the screen is still the one Jev decided on (the model call took time); if it changed, nothing is sent.
   Then one guarded input, no verification frame, then a local wait until the saved fingerprint of the predicted screen appears (or, for an unknown
   destination, until the frame leaves the origin screen and holds still). That wait is also what prevents double input on slow transitions.

The loop stops with a reason instead of guessing: `goal_reached`, `goal_screen_reached`, `action_limit` (bounded run finished; call again), `action_menu_exhausted`, `unmapped_screen`, `uncertain`,
`uncertain_screen`, `box_rejected`, `invalid_placement`, `stale_frame`, `dispatch_failed`, `stuck_loop`, `jev_stop`, `ask_user`, `jev_unavailable`, `runtime_limit`,
`max_steps`, `error`, `cancelled`. After any stop other than success, continue with the normal `game_*` tools.

## Safety

Learned menus are executable, so they are filtered harder than a caller-typed menu:

- **Stop and reobserve, never guess.** A model refusal or invalid response, a stale frame or screen (the live screen no longer matches the one Jev decided on), a below-threshold or ambiguous choice, an invalid target, or an unsafe Box all end the run with `reobserve: true` and no input. No second model and no post-action verification is added to compensate.
- Purchase, subscription, account, destructive and exit-looking labels are never offered or clicked (`JevFlowGuard`), including OCR-discovered controls and edited book files. The earlier live BTD2 run opened a paid-membership offer; this class is excluded by default.
- Keys: one unmodified key per tap, as in `game_jev_act`: named navigation keys and digits anywhere; **single letters (tower hotkeys W/E/R) only from reviewed caller seeds**. No chords or shortcuts (`Alt+F4`, `Ctrl+W`), function keys or holds.
- Boxes: normalized, finite, at most 15% of the window. A Jev Box needs visibility and confidence at or above `ExperimentalGameJevActClickThreshold` (0.90). A live masked call returned the right Choice (1.0) with a Box at confidence **0.74** that was shifted off the intended card: that is refused by this gate, and a regression test pins it. A box is never reused until a click on it was followed by an observed screen change, and a box that has only a local-detector origin must also agree with Jev's own box.
- Choices need `ExperimentalGameJevActChoiceThreshold` and the ambiguity margin. A learned click that changes nothing is retried at most within the run's bounds, and its box is dropped after one confirmed no-effect click so Jev re-localizes it.
- **In-match placement** is a reviewed `place` action only: select by hotkey, then `place` = Jev Box for the described spot, which must lie inside the action's `approved_region` (your half of the map; refused before the cursor moves otherwise), cursor move, a fresh frame, one Jev check that the preview is valid (no "Wrong Side!"), and only then the click. A preview that is not confirmed valid leaves the cursor moved but sends no click. Placement targets are never stored. Gameplay screens are marked `dynamic` (identified by the last verified sync, not by pixels).
- Dispatch still uses the guarded game input path: target identity, geometry/DPI, freshness (5 s), foreground, input ownership and detected camera/view motion. **One check is relaxed for this tool only:** an unclassified whole-frame change (an animated menu or gameplay) no longer blocks, because the flow vouches for the screen (fingerprint match, or the last verified sync on a dynamic screen). Missing motion evidence and detected motion still refuse.
- No post-action verification means input success is *assumed*. The final result says so; confirm the end state before relying on it.
- Credentials: the key is read only from the DCU process environment (`TYPESAFE_API_KEY`). It is never stored in settings, the book or reports. If it is absent from the process (a key can exist in your shell and still be missing from an already-running DCU), `execute=true` is refused before any capture, Jev request or input and says so; the engine also stops with `jev_unavailable` if a call is refused mid-run. Restart DCU from a shell that has the key.

## Enablement and the DCU-vs-Jev toggle

In the isolated experimental `settings.json` (also on the settings page, `dcu-settings.bat`):

```json
{ "GameMode": true, "JevMode": "fast_loop", "DecisionBackend": "jev", "JevModel": "jev-1.14.0",
  "EnableExperimentalGameJevFlow": true }
```

plus `TYPESAFE_API_KEY` in the DCU process environment. Turn `EnableExperimentalGameJevFlow` off (or call the normal `game_*` tools instead) to use ordinary DCU;
the two paths do not share state except the persisted screen-action memory. Tuning: `JevFlowSyncEvery` (default 4), `JevFlowFingerprintTolerance` (0.06),
`JevFlowDiscovery` (`combined` | `edge` | `off`).

## Tools

- `game_jev_flow(app, goal, app_id, execute, max_actions, max_runtime_ms, seed_screen, goal_screen, seed_actions_json, dynamic_screens, ...)`. `execute=false` reports the matched screen and known inputs with no Jev request and no input.
- `game_jev_flow_book(app_id, operation = show | seed | describe | set_dynamic | forget_screen | forget_action, ...)`: inspect and curate the book. Give new screens a real description (`describe`): Jev recognises screens from it. Auto-descriptions list the screen's control labels.

## Local observations that shaped this (reported from the Windows machine)

- Normal DCU reached gameplay in the Hero Challenge practice mode; first-round placement attempts missed because the screenshot was stale or the ghost was on the wrong side. One Dart Monkey alone lost at round 3.
- A key-only Jev Choice selected `W` (tower 1) at confidence 1.0 and DCU sent it; the ghost first landed on the opponent half ("Wrong Side!"), and a click on our half placed it. -> letter hotkeys on the image-free path; approved region plus preview check for placement.
- A masked Box+Choice call: correct Choice, **mislocalized Box at 0.74**. -> refused by the Box gate; kept as a negative test.
- The running DCU processes lacked `TYPESAFE_API_KEY` even though the shell had it. -> pre-capture refusal and `jev_unavailable` stop; the harness records this as a safe refusal. The integrated runtime test needs DCU relaunched from a shell that has the key (nothing is written to the repo or settings).
- The local `jev.cjs` validator predates the Box/assets contract, so it is not used by the harness.
- A preview-only `game_jev_act` call with W/E/R was refused before Jev (digits-only key policy). -> the guarded key policy now accepts single unmodified letters/digits and still rejects chords and shortcuts.

## Verification status

What has actually been run (Linux, .NET 10, `tests/ShadowUse.Jev.FlowTests`, 97 tests): the action book and guard (including the key policy table: `W/E/R` accepted, `Alt+F4`/`Ctrl+W`/function keys rejected); the engine on a simulated BTD2-shaped menu graph
with an oracle standing in for Jev (cold mapping run, warm run with zero images, the 4-action bound and chaining, slow transitions with no double input, layout drift, misprediction recovery,
stale-screen hold-back and stop, dispatch failure, missing key, purchase control exclusion, no-fingerprint mode, low-confidence and shifted Box refusals); a gameplay-shaped placement fixture (hotkey select, region and preview guards, invalid preview, dynamic screen);
and the real `JevController` over HTTP against a fake endpoint that answers from the simulated true screen. The whole solution (including the Windows tool layer and the Windows test project) compiles.

What has **not** been run: anything on Windows or against the real game, the real TypeSafe/Jev service, or the Windows-only test project
(`GameJevFlowToolsTests`, the new `game_jev_act` letter-key tests, the stability-policy test). The cloud session that wrote this had no Windows, no game window, no `TYPESAFE_API_KEY`, and a network policy that blocks the Jev host.
Fingerprint tolerance, OCR labelling quality, text-only decision accuracy, settle timing, the placement region and real latency are therefore unmeasured. [bench/jev_flow_live.py](../bench/jev_flow_live.py) (with [bench/seeds/btd2-practice-placement.json](../bench/seeds/btd2-practice-placement.json), whose region is an example to review) runs the cold/warm and placement checks on your machine and records safe refusals and local-runtime limitations.

`bench/results/jev-flow-sim-20261018/benchmark.json` is cost accounting on the simulation (model calls, image uploads, verification screenshots). Its latencies are a parameterised model, not measurements.
