# Experimental Jev game actions

`game_jev_act` is an experimental Jev controller for game UI actions. It is not part of the stable `dcu` MCP. Keep it disabled outside a separate experimental build and MCP entry. The default is one action per call; an opt-in serial run is bounded to four unique menu actions and 60 seconds. The authorized live scope for this experiment is one **single-player BTD2 match**.

## Enablement

All of these conditions are required before the tool can dispatch input:

- `EnableExperimentalGameJevAct: true` in the experimental executable's own `settings.json` (defaults to `false`).
- `GameMode: true`, `JevMode: "fast_loop"`, and `DecisionBackend: "jev"`.
- `JevModel: "jev-1.14.0"` or `"jev-preview"`; other models are refused because this path needs Box.
- `TYPESAFE_API_KEY` present in the process environment. The key is not stored in settings.
- `execute: true` on any invocation that may dispatch input; `execute:false` is preview-only.
- `ExperimentalGameJevActChoiceThreshold` (default `0.85`) applies to Choice confidence, operation/target probabilities, target confidence, and applicability Noul.
- `ExperimentalGameJevActClickThreshold` (default `0.90`) applies to click target-presence Noul and Box confidence.
- `ExperimentalGameJevActAmbiguityMargin` (default `0.10`) rejects a top-two score gap below that margin. Set any cutoff to `0` to disable only that score/margin cutoff; all structural, model-identity, same-frame, geometry, approved-region, freshness, and ownership checks remain.

Use a unique publish directory and a separate MCP entry, pinned to that executable. Isolate experimental `APPDATA` and `LOCALAPPDATA` so settings, logs, action catalogs, and screen maps do not share state. Do not redirect the game-input ownership lease: DCU keeps that lease under the real user's LocalApplicationData so stable and experimental processes cannot control the same window concurrently. Leave the stable MCP entry and `publish/dcu.exe` unchanged.

The intended side-by-side live-test entry `dcu-jev-experimental-live` is registered in the canonical and Pi adapter configs and points to `publish/experimental-jev-configurable-20261018/dcu.exe`, with dedicated `APPDATA`/`LOCALAPPDATA`. The stable `dcu` entry and `publish/dcu.exe` remain unchanged. The current session exposes only the older `dcu-jev-experimental` entry (20261016). Read-only input-ownership checks on both currently connected controllers found no held lease.

A health check showed that the running 20261016 process had loaded cutoffs of `0/0/0`. I restored both experimental `settings.json` files on disk to the intended `0.85/0.90/0.10` cutoffs, but the running process retains its loaded zero values until restart. The active MCP gateway does not expose `dcu-jev-experimental-live`; a fresh or reloaded adapter is required. Do not use the old process for input. After reload, verify the live executable path, cutoffs, feature flag, and tool schema before any input.

Health checks expose `decision_settings` so the active process can be verified (`decision_backend`, `jev_mode`, `jev_model`, Box support, and the feature flag). Verify the executable path, process identity, version, and schema before using the experimental entry.

## Invocation contract

Pass a bounded action menu of 1–12 options in `action_options_json`. A click can opt into target-region stability only for a static menu control by setting `stability:"static_menu"`; this still requires the clicked target's tight `approved_region` and at least one separate, disjoint approved region for a visible menu landmark. This exception is limited to `max_actions:1`, preserving post-action verification; multi-action runs reject menus that request it. Other clicks and all key taps keep strict whole-frame stability checks.

Optional `app_id`, `app_version`, `profile`, `keyboard_layout`, and `screen_hint` scope local screen/action memory. `current_state`, `previous_state`, `last_action`, and `recent_actions` are caller-provided unverified context. `app_id` causes the caller-listed menu and approved-region hashes to be stored in the isolated `ScreenStateStore`; menu coverage remains incomplete. A uniquely matching prior map (app/version/profile/layout/screen hint and geometry) is passed as untrusted historical context, but never expands the current allowed menu. Repeated matching observations can promote hash-verified anchors. Sourced app keybindings are passed as context, but are executable only when also present in the caller menu.

To map an unfamiliar screen, capture with `game_screenshot(include_local_fast_candidates:true)` or the client-vision candidate mode, then use `game_jev_decide_candidates` with `allow_incomplete_coverage:true`, `app_id`, and a semantic `screen_hint`. That tool is recommendation-only and may persist candidate labels/regions; its inventory is advisory, may be incomplete, and must be reviewed before its controls are added to a later caller menu. No discovered control becomes executable automatically.

Example menu:

```json
[
  {
    "id": "start_solo",
    "kind": "click",
    "description": "Start a solo match",
    "target_description": "Start Solo button"
  }
]
```

`kind` is `click` or `key_tap`. Clicks are left-click-only. Key taps are limited to `space`, `enter`, `escape`, `tab`, arrow keys, and digits 0–9; chords, holds, arbitrary keys, and extra fields are rejected. `stability` is optional and click-only; its only accepted value is `static_menu`. The menu is complete only as the caller's allowed choices for this invocation; it is **not** a claim that DCU found every control or target in the game.

Use `execute:false` for a one-step preview first. If any click options are present, DCU sends one fresh image-backed request containing Choice, per-option applicability Noul, click-target presence Noul, and one Box question per click option. It returns the selected option and an overlay preview. For a key-only menu, Jev receives the bounded state/action history as text and no image is attached. Screenshot capture and DCU frame guards still run for key taps.

Before a live click, visually inspect the current single-player screen and the preview. Then invoke again with `execute: true` and a visually reviewed `approved_region` on every click option:

```json
[
  {
    "id": "start_solo",
    "kind": "click",
    "description": "Start a solo match",
    "target_description": "Start Solo button",
    "approved_region": { "x": 20, "y": 20, "width": 240, "height": 100 }
  }
]
```

The coordinates are pixels in the full-screenshot image. Every invocation captures a new frame and recomputes Choice/Noul/Box; the execute call does not reuse a preview frame.

`max_actions` defaults to 1. Values 2–4 require `execute:true` and `max_runtime_ms` between 1,000 and 60,000 ms (default 30,000). The loop is serial, consumes each action ID at most once, and stops on no-input choices, errors, uncertain dispatch, cancellation, or its limit. It takes a fresh frame for the next decision but does not take a post-action verification screenshot in this multi-action mode. Do not interpret `input_sent` as proof that the UI changed or the goal succeeded. Static-menu region stability is rejected in this mode because it requires post-action verification.

## Dispatch gates

- The screenshot must be full-frame, unmasked, at least 280×280 pixels, available in the frame cache, at most 4 MiB, and less than 5 seconds old at dispatch.
- Choice/Noul and click/Box score cutoffs, plus the ambiguity margin, use the experimental `settings.json` values above. The defaults preserve the original 0.85/0.90/0.10 gates; setting a threshold or margin to zero disables only that cutoff.
- Box coordinates must be finite normalized edges, form a nonempty rectangle, occupy at most 15% of the image, and lie at least 3 image pixels inside the caller-approved region. Both coordinate-click paths verify the actual cursor is within 2 screen pixels of the intended point; the humanized path also refuses a locked cursor.
- An approved region must fit the current image, be at least 2×2 pixels, and cover no more than 25% of the image.
- Image Choice answers must have `type: "choice"`; required applicability and target-presence answers must have `type: "noul"`; Box must have `type: "box"`. Choice and Box must explicitly report the configured model as a string. Missing, malformed, mismatched, low-confidence, ambiguous, expired, or out-of-region evidence means no input. Duplicate JSON properties in Jev responses are rejected.
- At most one click or single-key tap is dispatched per step. Input routes through DCU's guarded game input path with `source_frame_id`, preserving target identity, window geometry/DPI, freshness, foreground, cancellation and shared ownership checks. Jev dispatch never retries after it starts. An unsent foreground retry is disabled on the Jev path.
- Ordinary clicks and key taps reject unavailable, moved, or unclassified whole-frame motion. For one-step calls only, an explicitly marked `static_menu` click may override only an **unclassified** whole-frame change, and only when motion evidence is available, no camera/view translation was detected, and all three same-frame pixel regions remain stable: the Jev Box, the clicked option's approved region, and a separate caller-approved menu landmark. The landmark must also pass the same-request applicability and target-presence Noul gates. Single-step post-action verification remains enabled. The internal color comparison normalizes both captures through the same JPEG pipeline and requires <0.2% of pixels to differ by at least 12/255 and mean absolute channel difference ≤2/255. Missing pixels, changed regions, or detected motion still refuse the click. This opt-in is for static menu controls, not moving targets or gameplay placement.
- Choice, Noul and Box questions share one image request and the exact same screenshot asset. Requests with a click use the TypeSafe image safety header; key-only requests do not attach an image. Retries are disabled. The screenshot is sent to the configured Jev endpoint; do not use the tool if that disclosure is not authorized.
- A pre-dispatch refusal reports `input_sent:false`, `no_input_sent:true`, source/validation frame IDs, motion flags, and per-region status/measurements. `not_evaluated` regions are not considered stable. In the Jev wrapper, `dispatch_attempted` means the guarded dispatcher was invoked; use `input_sent` to determine whether game input was actually sent.

## BTD2 live validation status

- The side-by-side experimental executable and isolated settings were health/schema-checked. The stable executable and MCP entry were not changed. The current assistant adapter may need a reload to expose the new MCP entry.
- The Jev `BATTLE!` preview selected the visible button with strong Choice/Noul/Box evidence. Two guarded single-step clicks opened a £9.99 Club Membership offer instead of starting a match. Escape dismissed the offer. No purchase or online matchmaking occurred.
- A guarded Back click was sent from Bloon Lab setup. Its immediate post-action screenshot still showed setup, but a later fresh preview showed that the delayed transition eventually reached the main menu. This demonstrates why `input_sent` and an immediate frame change are not proof of navigation success.
- On the main menu, Jev localized the free Hero Challenge button (`choice 0.97`, applicability Noul `0.96`, target-presence Noul `0.98`, Box confidence `0.96`; decision `1,273 ms`, preview step `1,592 ms`). The preview Box narrowly failed the initial approved-region containment margin, so no click was attempted. Four no-input production-pipeline comparisons of caller-reviewed full-button ROIs changed by `0.0158`, `0.00335`, `0.00706`, and `0.01020`, all above the unchanged `<0.002` limit, while the Box region itself stayed within limits. A focused `frame_still` wait did not make the subsequent comparison pass. The saved source/current images and thresholded diff place the change cluster at the button's upper-left outline/glow, overlapping the visible control. They do not establish that those pixels are outside its complete border/effects, so no ROI correction is justified. Do not shrink this ROI to a stable interior patch or lower the threshold; static-menu motion checks remain fail-closed.
- A controlled offline frozen-image benchmark compared the production calibrated BTD2 detector with a 16-question Jev Box batch. Across five alternating pairs, local detection median was `49.567 ms` (range `41.75–56.42 ms`); Jev median was `553.752 ms` (range `338.87–588.91 ms`). Jev was about `11.2×` slower, with lower mean IoU (`0.702` vs `0.814`), although both localized all 16 cards at IoU ≥0.50. This valid component-level result is a speed loss for Jev; it is not a comparison against the full normal agent-driven DCU loop. Raw pairs and scores are in `bench/results/jev-box-speed-20261018T052129509Z-0ba95b225dc64dfbbd01318278c2ec1d/benchmark.json`.
- A separate read-only host-agent surrogate used the same redacted image and 16-card task with `openai/gpt-6-luna` at `xhigh`. Its five subagent wall times had a `49.484 s` median (range `48.080–78.671 s`), but only three outputs passed the wrapper's acceptance check; two earlier outputs were rejected for formatting and one was recovered from the child transcript. The three attested outputs all matched 16/16 cards at IoU ≥0.50, with mean IoU `0.945`. This is not the configured normal DCU backend. It tests localization only; the OpenAI Responses path selects among caller-listed operations and target IDs and does not emit Box coordinates. The surrogate's timing also includes child startup, file reading, and output-wrapper work, unlike Jev's request timing. Its output format also changed after the first two attempts. Jev's apparently faster time is therefore not a valid normal-DCU speed win, and Jev's mean IoU was lower (`0.702` vs `0.945`). Details and raw boxes are in `bench/results/jev-host-agent-surrogate-20261018/benchmark.json`.
- No match has started. The ordinary-DCU comparison is incomplete: the earlier click used a 32-second-old frame and is not a valid baseline. The Jev preview (`1,273 ms` decision, `1,592 ms` preview step) and normal screenshot/OCR are one-sided observations from different captures; they do not form a matched benchmark or support a superiority claim. A read-only normal-path screenshot succeeded, but the OpenAI Responses backend could not be exercised: the source router requires `DecisionBackend: "openai"`, and the backend reads `OPENAI_API_KEY` from its process environment; that variable was absent, so no OpenAI request was made. A focused offline run of `OpenAiResponsesDecisionBackendTests` passed `4/4`, including the missing-key no-request check and router-selection checks using fake handlers. A valid paired baseline needs an isolated compatible runtime with that backend selected, not a change to the stable DCU. Opt-in generic VLM candidate sampling also returned `MCP sampling image content is not supported`; local OCR misread the Hero Challenge label as `neR0` and `CNALLenee`. There is no normal-DCU target-selection result or reliable decision timing. A valid decision-level A/B needs the same frozen screenshot, user intent, and caller-listed action menu; each path must return a validated choice from that menu using comparable start/end timing and repeated runs. A box-localization benchmark alone does not measure that decision path, and the actual normal backend remains unavailable. The game remains at the main menu; no purchase or matchmaking occurred.
- `FrameRegionStabilityTests.OptionalLiveBloonsRegionDiagnosticUsesTheProductionComparisonWithoutInput` is an opt-in, read-only source/current capture using the production comparison pipeline. Set `DCU_BTDB2_STATIC_REGION_DIAGNOSTIC=1`, `DCU_BTDB2_TARGET_PID`, and a `DCU_BTDB2_STATIC_REGION_DIAGNOSTIC_PRESET`; adding `DCU_BTDB2_STATIC_REGION_DIAGNOSTIC_SAVE=1` saves normalized source/current JPEGs in a unique temp directory for inspection. It never sends input.
