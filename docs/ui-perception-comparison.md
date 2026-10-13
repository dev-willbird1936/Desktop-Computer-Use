# Offline UI-perception comparison

## Result

This document records the original **local-only** phase. The same HTML was later extended with separately authorized OpenRouter cloud trials after the user asked to test through OpenRouter; its original local-only timing measurements remain unchanged. Cloud results are labelled separately and include complete HTTP/output-generation latency, not local detector latency.

An actual tool-only comparison was run on the operator-supplied BTD2 and Anvil screenshots. The standalone HTML contains annotated outputs, original-image coordinates, per-run timings, and explicit unavailable-method entries:

`C:/Users/warcr/AppData/Local/Temp/ui-perception-offline-compare-r1/comparison.html`

Raw samples are in `comparison.json` alongside it. Full-resolution PNG overlays are alongside the report; the standalone HTML embeds compressed previews.

This historical comparison did not satisfy the broader discovery/Jev goal. It did not deploy DCU, control games, call Jev, or use a remote vision provider. A later follow-up added a resident local detector path; its evidence and limitations are recorded below.

## Measured warm timings

Hardware: RTX 3080 12 GiB; AMD Ryzen 9 3950X. Concurrent host/GPU workloads were not controlled. Seven timed repeats per detector/CPU method after warmup. These are still-file smoke measurements, not a general benchmark.

| Method | BTD2 | Anvil | Scope |
|---|---:|---:|---|
| Edge-component heuristic | 26.47 ms / 41 regions | 47.46 ms / 57 regions | Detection only; decorative geometry included |
| Windows.Media.Ocr | 331.86 ms / 4 regions | 1031.48 ms / 25 regions | Text only, not interactive controls |
| OCR + edge | 361.65 ms / 45 regions | 1091.00 ms / 81 regions | Sequential detector stages; not a learned UI detector |
| Existing BTD2 calibration | 68.09 ms / 23 anchors | 279.27 ms / 0 anchors | Both refused complete layout coverage; supplied BTD2 aspect differs from calibration |
| YOLOv9-E 640 FP32 | 108.58 ms / 24 regions | 131.19 ms / 18 regions | Warm pixels-to-boxes, no labels |
| YOLOv9-E 960 FP32 | 174.99 ms / 21 regions | 170.61 ms / 43 regions | Warm pixels-to-boxes, no labels |
| YOLOv9-E 1280 FP32 | 258.47 ms / 22 regions | 310.36 ms / 43 regions | Warm pixels-to-boxes, no labels |
| YOLOv9-E 1280 FP16 autocast | 200.09 ms / 22 regions | 240.43 ms / 43 regions | Warm pixels-to-boxes, no labels |
| Seeded thumbnail-map cache | 13.75 ms | 31.86 ms | Repeated identical image only; not discovery, tracking or identity recognition |
| Actual parallel YOLO FP16 + OCR tool path | 766.92 ms / 25 regions | 2409.72 ms / 56 regions | Includes OCR file decode, annotation, MCP serialization/round-trip; unlike detector-only scope |

YOLO timing includes resize, CPU-to-GPU copy, inference, decode and NMS. It excludes disk decode, startup/model loading, overlay generation and Jev. GPU calls are synchronized. FP16 peak allocated GPU memory measured about 1516 MiB; this is allocated tensor memory, not total process/GPU residency. Model loading was separately measured at about 5.6 seconds, excluding Python import/CUDA cold initialization.

The HTML also includes an independently composed serial fusion cost estimate; it is labelled an estimate and is not a measured end-to-end or parallel result.

## What was not tested

- DOM/UIA: unavailable from PNG/JPEG files; a live target tree is required.
- DXGI capture: not a still-image analysis method; no live target was captured for this comparison.
- DINOv2/SigLIP memory: no checkpoint or verified label database available. No embeddings were substituted with image hashes.
- Florence/OmniParser captioning was tested locally after authorization, but the captions were too vague and sometimes wrong to add to DCU; see the local-model experiment below. No caption model is integrated into the runtime.
- Small GUI VLM: no verified local image-capable GUI VLM available.
- Full assistant visual analysis: excluded at the operator's request. No assistant-labelled boxes appear as detector outputs.

These methods are explicitly marked **not run** in the report, with no invented boxes or latency.

## Interpretation

The generic YOLO detector locates useful regions but is not exhaustive: the BTD2 output misses some hero/arrow regions and includes container/background proposals. Anvil output includes hotbar controls plus text-like regions such as player names and chat messages. Appearance cannot verify those are interactive. More boxes is not greater recall, and detector confidence is not clickability probability.

Windows OCR is the large measured cost in this full-frame pipeline. It does not establish the quoted 40–150 ms combined target on this host. Detector-only, cached layouts and targeted OCR need separate quality/latency validation before choosing a production default.

## Later follow-up: resident local detector and Jev handoff

`game_screenshot(include_local_yolo_candidates=true)` starts an opt-in persistent local YOLOv9-E worker before capture, then routes its frame-bound boxes and generic local proposals through the owned `ScreenDiscoveryService`. `local_candidate_method="edge"` is the fast default; `"combined"` and `"ocr"` add Windows OCR and may exceed Jev's freshness window. This generic path does not invoke Bloons-specific layout detection. The incomplete inventory is cached for decision-only Jev. Combined local detection retains overlapping OCR boxes so C# fusion can attach text as an unverified clue; textless proposals remain generic. Jev receives screenshot-image and screen bounding boxes. Native UIA choices sent through the general Jev path also carry screen and window-relative boxes. UIA sees controls exposed through its Control view, not visually rendered controls the app does not expose. Game visual proposals remain incomplete and semantically unverified.

The original offline resident-worker evaluation used local source images re-encoded to JPEG quality 95, two untimed per-image warm-ups, and five measured repeats. It made no provider or Jev calls. Its Python fuser mirrors the then-current 228-YOLO/27-local cap and used combined OCR/edge proposals; the opt-in C# component test separately exercises worker/fusion/mapper/request construction. Internal detector and worker pipe timings exclude game capture, OCR/edge processing, .NET fusion, and Jev latency. The table below is that historical full-cap, combined-mode baseline; the current cap is 160 YOLO plus up to 27 local proposals, with edge as the GameTools default.

| Image | Input | Marked-reference matches, every run | Candidates | Detector median / p95 | Worker round-trip median / p95 |
|---|---:|---:|---:|---:|---:|
| BTD2 | 1280 | 21/21, exact frame | 237 | 250 / 252 ms | 277 / 279 ms |
| Anvil | 1280 | 7/8 geometric transfers; 6/7 stable controls | 234 | 259 / 263 ms | 322 / 326 ms |
| BTD2 | 1536 | 21/21, exact frame | 255 (cap) | 364 / 368 ms | 391 / 394 ms |
| Anvil | 1536 | 8/8 geometric transfers; 7/7 stable controls | 235 | 381 / 384 ms | 450 / 453 ms |

At both resolutions, all five measured runs had identical fused geometry and identical reference-box outcomes. This is repeatability on two static images, not general reliability. The BTD2 layout check found 23/23 anchors, but rejected the 1.781 frame aspect ratio; those anchors therefore remain incomplete evidence. Anvil matched zero BTD2 anchors and fell back to YOLO/OCR/edge proposals. Its marks come from a different night-state frame, so these are approximate transfers, not same-frame recall. Visual review confirms the `location-alert-action` mark is absent from the clean inference screenshot; its geometric overlap must not be counted as a true hit. The 1280 set also missed `lower-left-town-control`; at 1536 the seven stable chat/town controls were covered. Extra detections were not scored. Candidate sets remain explicitly incomplete; results do not establish exhaustive discovery, semantic identity, enabled state, or clickability.

At 1536, observed worker readiness was 8.4 seconds in a fresh process. The first two BTD2 image requests took about 3.0 and 6.1 seconds before the five stable measured requests; warm worker round-trip was about 0.39–0.45 seconds. A cold first call is therefore much slower than steady state. The Anvil geometric-transfer count rose by one at 1536, at about 0.13 seconds extra warm round-trip; the C# actual-worker/fake-Jev test likewise passed the seven stable Anvil controls and did not find the absent transient alert mark.

Historical full-cap benchmark outputs: `C:/Users/warcr/AppData/Local/Temp/ui-perception-local-yolo-resident-1280-hybrid-q95-r1.json` and `C:/Users/warcr/AppData/Local/Temp/ui-perception-local-yolo-resident-1536-hybrid-q95-r1.json`. The opt-in component-level C# test ran the actual Python/Torch worker over three requests, fused and mapped candidates, and reached a fake Jev endpoint for BTD2 (23 calibrated anchors), the selection fixture (13 targets plus OCR clues), and clean Anvil (seven stable cross-frame controls); this test uses combined OCR/edge, not GameTools orchestration. An initial eight-mark Anvil assertion exposed the absent `location-alert-action` mark; it was removed from the stable-transfer assertion, not counted as a detected target. A separate edge-default GameTools screenshot-to-cached-inventory-to-fake-Jev smoke test is described below. The running MCP server was not restarted; no real Jev call or game input was made.

### Candidate-budget and freshness follow-up

The earlier full-cap benchmark allowed 228 YOLO plus 27 OCR/edge/layout proposals (255 total). The current C# fuser caps this at 160 YOLO plus up to 27 generic local proposals (187 total). `game_screenshot(include_local_yolo_candidates=true)` now defaults `local_candidate_method` to fast `edge`; `ocr` and `combined` remain optional. Named-layout measurements in the historical tables are not part of the generic runtime path.

Five-repeat resident-worker benchmarks at JPEG quality 95, with edge-only local proposals and stable Anvil marks, reported:

| Image | Scored reference targets | Candidates | Detector median / p95 | Worker round-trip median / p95 |
|---|---:|---:|---:|---:|
| BTD2, 1280 | 21/21, exact frame | 187 | 409 / 427 ms | 435 / 453 ms |
| Anvil, 1280 | 6/7 in Python mirror; misses chat input | 187 | 425 / 459 ms | 493 / 527 ms |
| BTD2, 1536 | 21/21, exact frame | 187 | 562 / 595 ms | 592 / 625 ms |
| Anvil, 1536 | 7/7 stable controls, approximate transfer | 187 | 579 / 624 ms | 651 / 697 ms |

After the `ScreenDiscoveryService` refactor, one 960-input CPU smoke run per fixture sent an unfamiliar non-Bloons synthetic UI and a separate settings UI through `game_screenshot`'s owned local YOLO+edge path, omitted caller candidate JSON, and reached the fake Jev handler. It matched 13/13 and 14/14 fixture-marked positive targets, respectively; both local passes stayed below the 10-second freshness limit. Candidate completeness remained false, no input/network occurred, and unmarked proposals were not scored. These two synthetic frames verify integration and positive localization only, not visual completeness, semantic identity, false-positive rate, or general accuracy. On a separate static Anvil transfer image using the available CPU-only Torch runtime, 1280 input finished local processing under 10 seconds but missed the approximate `chat-input-region` and `lower-left-town-control` marks. At 1536, local processing took 12,757 ms and correctly stopped before the fake Jev request. These cross-frame marks are approximate, not same-frame recall. A separate opt-in memory smoke test used a static BTD2 hero-selection screenshot for one clean and three warmed fake-Jev decisions. It confirmed a repeated semantic screen ID, recent simulated action history and unverified transitions in later Jev payloads, and fresh frame IDs/boxes on every run; the screen recipe cache contained no bounding boxes. It sent no real Jev request or game input, so it verifies memory plumbing, not model learning or real-Jev behavior. The Anvil `location-alert-action` reference is excluded because it is absent from the clean inference image. The marks still come from a different frame and are transfer evidence, not same-frame recall. The edge-only Python budget sweep found BTD2 21/21 at 16 YOLO+27 local proposals; Anvil required 160+27 at 1536 (128+27 missed the chat input). At 1280, its first tested budget reaching all seven stable Anvil marks was 192+27; the Pillow-reencoded 160+27 set matched 6/7. The actual C# edge-only component/fake-Jev test passed all seven stable Anvil targets on each of three worker outputs at both 1280 and 1536, while a separate C# combined-OCR test at 1280 failed on `lower-left-town-control`. At 1280, a strict raw-detection equality assertion found two signatures across three requests, but all three fused sets still covered the seven marked controls. Replaying the exact GDI+ JPEG from the C# test through the same Python worker reproduced its tight 718×48 `interactive-region-288` box (score 0.0027, secondary-NMS rank 156) in three runs. Pillow-reencoded inputs instead select a broad 672×321 box at the 160-YOLO cap, which exceeds the scorer's 8× area limit; at 192 they include a 718×65 `interactive-region-357` box and score 7/7. This localizes the component-test mirror difference to JPEG encoding/inference input, not the fusion policy. Anvil references remain cross-frame approximate evidence.

The candidate cap reduces the maximum Jev inventory by 68 boxes (27%) versus the previous limit. The 1536 runs were roughly 157–158 ms slower in warm worker round-trip than the 1280 runs in this set; timing excludes capture, local proposal fusion, and Jev latency, and GPU load varied between runs. Detector-only top-K sweeps covered 13/13 selection-fixture targets within 32 boxes and 14/14 settings-fixture targets within 16 boxes. Extra detections remain unscored; these positive-only results do not establish completeness, identity, or actionability. Edge-only outputs: `C:/Users/warcr/AppData/Local/Temp/ui-perception-local-yolo-resident-1280-budget160-edge-r1.json` and `C:/Users/warcr/AppData/Local/Temp/ui-perception-local-yolo-resident-1536-budget160-edge-r3.json`.

#### Game screenshot to Jev smoke test

`GameScreenshotAutomaticCandidateJevIntegrationTests.GameScreenshotBuildsLocalInventoryAndSendsItToFakeJev` calls the actual `GameTools.GameScreenshot` on a temporary non-activating window displaying the settings fixture, omits `candidate_set_json`, then calls `GameJevTools.GameJevDecideCandidates` against the cached inventory and a fake Jev endpoint. It passed in Release using the default edge method at both 1280 and 1536 YOLO input sizes. The test matches all 14 fixture-defined controls in the screenshot candidate set and outgoing Jev request, and verifies frame IDs, box bounds, the 187-candidate ceiling, and `perception_ms < 10,000`. No real Jev call or game input was made.

Reproduce with the settings fixture and local model environment:

```powershell
$env:DCU_GAME_SCREENSHOT_FAKE_JEV_SMOKE = '1'
$env:DCU_GAME_SCREENSHOT_EXPECT_SETTINGS_UI = '1'
$env:DCU_LOCAL_YOLO_TEST_PYTHON = 'E:/models/dcu-ui-candidate-bench-20261003/venv/Scripts/python.exe'
$env:DCU_LOCAL_YOLO_TEST_WEIGHTS = 'E:/models/OmniParser-v2.0/icon_detect_v3/model.pt'
$env:DCU_LOCAL_YOLO_TEST_IMAGE = (Resolve-Path './tests/fixtures/settings-ui-discovery-fixture.png').Path
$env:DCU_LOCAL_YOLO_TEST_DEVICE = 'cuda'
$env:DCU_LOCAL_YOLO_TEST_IMAGE_SIZE = '1536'
dotnet test ./tests/ShadowUse.Tests/ShadowUse.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~GameScreenshotAutomaticCandidateJevIntegrationTests
```

The full GameTools path was also tested on the generic UI fixture, captured in a non-activating 1280×800 window. Edge mode with a 960 YOLO input matched all 13 marked targets (cards, textless stars, arrows, fields, and buttons) in the candidate set and outgoing fake-Jev request on each of three consecutive captures, each within the 10-second limit. Combined OCR at the same capture/input sizes also passed three consecutive captures and carried `Search`/`Continue` text clues into Jev. The same full-width edge capture at 1280 YOLO input took 10,839 ms and failed freshness before candidate/Jev assertions; capture width 640 with 1280 input passed once. These results show a resolution/latency tradeoff on this fixture, not universal settings.

Reproduce the three-run generic UI smoke test after setting the local Python/weight/device variables above:

```powershell
$env:DCU_GAME_SCREENSHOT_EXPECT_SETTINGS_UI = '0'
$env:DCU_GAME_SCREENSHOT_EXPECT_ANVIL = '0'
$env:DCU_GAME_SCREENSHOT_EXPECT_SYNTHETIC_UI = '1'
$env:DCU_GAME_SCREENSHOT_CAPTURE_WIDTH = '1280'
$env:DCU_GAME_SCREENSHOT_REPEATS = '3'
$env:DCU_GAME_SCREENSHOT_LOCAL_METHOD = 'edge'
$env:DCU_LOCAL_YOLO_TEST_IMAGE = (Resolve-Path './tests/fixtures/generic-ui-discovery-fixture.png').Path
$env:DCU_LOCAL_YOLO_TEST_IMAGE_SIZE = '960'
dotnet test ./tests/ShadowUse.Tests/ShadowUse.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~GameScreenshotAutomaticCandidateJevIntegrationTests
```

To check the optional OCR-clue path on the same fixture, change `DCU_GAME_SCREENSHOT_LOCAL_METHOD` to `combined`; it passed three captures at this size and input resolution:

```powershell
$env:DCU_GAME_SCREENSHOT_LOCAL_METHOD = 'combined'
dotnet test ./tests/ShadowUse.Tests/ShadowUse.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~GameScreenshotAutomaticCandidateJevIntegrationTests
```

The same full path was also tested on the clean Anvil screenshot displayed in a non-activating 1280×719 window; `game_screenshot` captured it at quality 80, then sent the cached candidates to fake Jev. With `LocalUiYoloImageSize=1536`, all seven stable cross-frame controls appeared in both the candidate set and outgoing Jev request, under the 10-second freshness limit. At 1280, the test failed before the fake Jev call because the candidate set missed `chat-input-region` and `lower-left-town-control`; count remained 187. The direct full-resolution C# component test did cover those marks at 1280, so this shows sensitivity to the actual capture/resizing path. Anvil marks remain approximate transfer evidence; the transient alert is excluded. A combined-OCR Anvil pass at 1536 also stayed fresh and matched seven controls. After nearby-text association was restricted to one nearest, size-bounded candidate per OCR line, 26 proposals carried OCR clues; the chat-input box stayed generic, and one tiny bottom-left box still carried the `Press E to use T` clue. The lower duplicate count is better, but the remaining relation is not verified; treat all nearby text as advisory, not identity.

Reproduce the Anvil pass with the local screenshot and model files used in this run:

```powershell
$env:DCU_GAME_SCREENSHOT_FAKE_JEV_SMOKE = '1'
$env:DCU_GAME_SCREENSHOT_EXPECT_ANVIL = '1'
$env:DCU_GAME_SCREENSHOT_LOCAL_METHOD = 'edge'
$env:DCU_LOCAL_YOLO_TEST_PYTHON = 'E:/models/dcu-ui-candidate-bench-20261003/venv/Scripts/python.exe'
$env:DCU_LOCAL_YOLO_TEST_WEIGHTS = 'E:/models/OmniParser-v2.0/icon_detect_v3/model.pt'
$env:DCU_LOCAL_YOLO_TEST_IMAGE = 'C:/Users/warcr/AppData/Local/Temp/pi-clipboard-a896b0bb-aa69-48a4-a42e-9bd14dee000b.png'
$env:DCU_LOCAL_YOLO_TEST_DEVICE = 'cuda'
$env:DCU_LOCAL_YOLO_TEST_IMAGE_SIZE = '1536'
dotnet test ./tests/ShadowUse.Tests/ShadowUse.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~GameScreenshotAutomaticCandidateJevIntegrationTests
```

Change the input size to `1280` to reproduce the two-target failure.

An earlier version using combined Windows OCR failed the freshness check with `The screenshot is older than 10 seconds; reobserve before asking Jev.`: observed local perception took about 17–26 seconds before removing a redundant template OCR pass, and about 10.2 seconds afterward on the smaller capture. A later full-size settings-fixture GameTools run (1280×800 capture, 1280 YOLO input, `local_candidate_method=combined`) passed three consecutive screenshot-to-fake-Jev rounds under 10 seconds. Each request included all 14 marked controls and nearby OCR clues for the checkbox, update toggle, and volume control. This is a three-run static-fixture result, not a latency guarantee; the earlier stale failures remain. Edge-only stays the fast default, and combined may still require a new capture.

With the settings environment above, reproduce the optional full-size combined pass by overriding these settings:

```powershell
$env:DCU_GAME_SCREENSHOT_CAPTURE_WIDTH = '1280'
$env:DCU_GAME_SCREENSHOT_REPEATS = '3'
$env:DCU_GAME_SCREENSHOT_LOCAL_METHOD = 'combined'
$env:DCU_LOCAL_YOLO_TEST_IMAGE_SIZE = '1280'
dotnet test ./tests/ShadowUse.Tests/ShadowUse.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~GameScreenshotAutomaticCandidateJevIntegrationTests
```

The component-level combined-OCR settings fixture now adds bounded, same-row OCR clues to visual candidates that lack a stronger overlapping clue. The fake-Jev request carries unverified phrases for the notification checkbox (`V Enable notifications`), updates toggle (`Automatic updates`), and volume control (`Master vol ume`). The nearby rule also caps visual-box area at four times OCR-box area; this kept the broad Anvil chat-input candidate from inheriting a lower-left `Press E to use Town Keep` prompt. Several small bottom-bar proposals still received that prompt as a nearby clue, so association is not reliable identity. No label proves that a box is clickable. This optional clue path remains subject to the freshness failure above; the fast edge default still has generic textless candidates.

### Native UIA-to-Jev smoke test

`NativeUiaJevIntegrationTests.ExposedControlsReachFakeJevWithScreenAndWindowBounds` exercises the public `ComputerUseTools.JevDecide` path on a temporary non-activating WinForms fixture. UIA exposes a button, text field, checkbox, combo box, two radio buttons, slider, tabs, and Apply button; the test verifies their names and screen/window-relative boxes in the outgoing fake Jev request, including `candidate_set_complete=true` for that exposed UIA projection. The fake returns `STOP`; no input is dispatched and no network request is made. Run it with `DCU_NATIVE_UIA_JEV_SMOKE=1` and filter `FullyQualifiedName~NativeUiaJevIntegrationTests`. This establishes handoff for this standard-controls fixture only. Completeness applies to the enumerated UIA Control View, not canvas content or controls an application does not expose.

## Additional local Florence model experiment

After authorization, the isolated benchmark venv received `transformers==4.46.1`, `timm==1.0.11`, `einops==0.8.0`, and `huggingface_hub==0.26.5`. Pinned Microsoft model artifacts and code were downloaded under `E:/models/dcu-local-florence-caption-r1`; the total payload was 1,549,940,499 bytes. `PROVENANCE.json` records repository revisions, file lists, and licenses. Screenshots stayed local. These models were evaluated only in standalone scripts and are not integrated into DCU.

On the exact-frame BTD2 screenshot, Florence-2-base `<REGION_PROPOSAL>` returned 22 boxes and overlapped 16/21 marked positives (all 16 cards; none of the five controls) in 1.60 s. `<OD>` returned 17 boxes and overlapped 15/21 in 4.54 s. `<DENSE_REGION_CAPTION>` returned 17 boxes and overlapped 13/21 in 6.05 s; its labels hallucinated Fortnite-like character names. On the cross-frame Anvil check, `<REGION_PROPOSAL>` returned three large boxes and hit 0/8 approximate marks in a 13.20 s first inference; `<OD>` returned one whole-screen “building” box and `<DENSE_REGION_CAPTION>` returned no boxes. A 2x2 tiled pass produced 36 boxes and 15/21 hits on BTD2, and 16 boxes with 0/8 approximate Anvil hits. Only marked positives were counted; extra proposals were not scored. Anvil figures are approximate transfer evidence because its reference marks come from another frame.

The separate OmniParser icon-caption checkpoint generated captions for 21 YOLO-matched BTD2 positive crops in 10.50 s after a 3.72 s model load. Outputs were often generic or wrong (for example, “ready” became “a power button” and preview arrows became a paint bucket or blue tool). Its full-screen `<OD>` result was one “toy” box; dense captioning returned no boxes. A qualitative 10-region synthetic UI probe showed the same problem: the avatar was described as a profile, but stars/arrows received bookmark, zoom/delete, or profile labels, and Continue was called a computer. That run took 34.70 s under competing GPU load, so it is not a standalone latency benchmark. Trying Florence-2-base `<REGION_TO_DESCRIPTION>` on native positive crops also hallucinated labels: BTD2 preview arrows became chair/mirror, and Anvil toolbar icons became toilet/picture frames. These captions are not reliable identities or actionability evidence. Florence is slower than the warm resident YOLO worker, misses controls, and did not improve the tested Anvil coverage; it was not integrated. Raw model outputs and timing records are kept beside `PROVENANCE.json` under `E:/models/dcu-local-florence-caption-r1`.

## EmbeddingGemma-2 crop-labeling probe

After explicit authorization, `google/embeddinggemma-2` was tested in an isolated environment at `E:/models/dcu-embeddinggemma2-r1`. On 27 known control crops and 21 fixed text labels, top-choice accuracy was 13/27. This was a closed-set crop-labeling probe: the correct label was always among the choices, so it does not establish open-world control discovery or identity. It missed all four navigation buttons, radio buttons, stars, the volume knob, Learn more, the filter dropdown, and the left arrow. Incorrect labels sometimes led by only 0.0001–0.0135 cosine similarity, indicating ambiguous choices. The model has 438,760,448 parameters; loading took 14.277 s, already beyond Jev's 10-second freshness window, with an additional 8.776 s warmup. It was not integrated. Model/runtime and probe output remain outside the repository at `E:/models/dcu-embeddinggemma2-r1`.

## Tiled YOLO probe

A 2x2 overlapping-tile pass was also tested with the existing YOLOv9-E model at 1280 input. On BTD2, full-frame detection hit 20/21 marked positives; tiling hit 18/21. On Anvil, full-frame had 7/8 geometric reference-box matches and tiling 8/8, but visual review shows the `location-alert-action` mark is absent from the clean inference frame. Excluding that transient mark, full-frame covers 6/7 stable controls and tiling covers 7/7; the capped full+tile union also covers 7/7. Median detector-only time rose from 180/198 ms full-frame to 700/729 ms tiled (BTD2/Anvil); GPU contention caused one BTD2 latency outlier. Extra detections were not scored. Given the repeatable 1536 hybrid results above and the tile pass's BTD2 regressions/latency cost, tiled detection was not integrated. Raw results: `E:/models/dcu-local-florence-caption-r1/yolo-tiled-1280-eval.json`.

## Synthetic generic-UI handoff check

Two synthetic 1280x800 fixtures cover distinct UI patterns: `generic-ui-discovery-fixture.png` has cards, textless stars, search/dropdown fields, arrows, a primary action, and an intentionally ambiguous decorative art panel; `settings-ui-discovery-fixture.png` has navigation buttons, checkbox/toggle, dropdown, slider, radio options, and Apply/Cancel. Regenerate them with `python tests/fixtures/generate_ui_discovery_fixture.py` and `python tests/fixtures/generate_settings_ui_fixture.py`. Standalone YOLO emitted 158 proposals on the selection fixture at 1280 input. At 1536 input, the detector-only top 32 boxes covered all 13 selection targets and the top 16 covered all 14 settings targets. Extra proposals were not scored.

Opt-in C# smoke tests use the actual resident Python worker, local OCR/edge fusion, candidate mapper, and fake Jev handler. They assert that all 13 selection targets plus Search/Continue OCR clues, and all 14 settings targets, appear in the outgoing fake Jev candidate array. The selection fixture passed on CPU and CUDA; both fixtures passed with the 160-YOLO+27-local cap and enforce the 187-candidate upper bound. This verifies geometry and fake-request routing on two synthetic layouts, not general/exhaustive discovery, actionability, or live Jev behavior.

```powershell
$env:DCU_LOCAL_YOLO_TEST_PYTHON = 'E:/models/dcu-ui-candidate-bench-20261003/venv/Scripts/python.exe'
$env:DCU_LOCAL_YOLO_TEST_WEIGHTS = 'E:/models/OmniParser-v2.0/icon_detect_v3/model.pt'
$env:DCU_LOCAL_YOLO_TEST_IMAGE = (Resolve-Path './tests/fixtures/generic-ui-discovery-fixture.png').Path
$env:DCU_LOCAL_YOLO_TEST_DEVICE = 'cpu'
$env:DCU_LOCAL_YOLO_TEST_EXPECT_SYNTHETIC_UI = '1'
dotnet test ./tests/ShadowUse.Tests/ShadowUse.Tests.csproj --no-restore --filter FullyQualifiedName~ResidentWorkerReturnsFrameBoundCandidatesAcrossOneProcessLifetime

$env:DCU_LOCAL_YOLO_TEST_IMAGE = (Resolve-Path './tests/fixtures/settings-ui-discovery-fixture.png').Path
$env:DCU_LOCAL_YOLO_TEST_DEVICE = 'cuda'
$env:DCU_LOCAL_YOLO_TEST_EXPECT_SYNTHETIC_UI = '0'
$env:DCU_LOCAL_YOLO_TEST_EXPECT_SETTINGS_UI = '1'
dotnet test ./tests/ShadowUse.Tests/ShadowUse.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ResidentWorkerReturnsFrameBoundCandidatesAcrossOneProcessLifetime
```

## Reproduce

Use the already-installed isolated environment at `E:/models/dcu-ui-candidate-bench-20261003/venv`, not the global/agent Python environment. Detector weights: `E:/models/OmniParser-v2.0/icon_detect_v3/model.pt` (Microsoft MIT detector). Model SHA-256 is recorded in `comparison.json`.

Reproduce the current five-repeat candidate-budget benchmark using the saved local marks and OCR/edge baseline. Supply a fresh output path; the script refuses to overwrite files.

```powershell
$python = 'E:/models/dcu-ui-candidate-bench-20261003/venv/Scripts/python.exe'
& $python bench/ui_local_yolo_resident_eval.py `
  --python $python `
  --weights 'E:/models/OmniParser-v2.0/icon_detect_v3/model.pt' `
  --references 'C:/Users/warcr/AppData/Local/Temp/ui-perception-offline-compare-r1/marked-positive-target-reference.json' `
  --edge-results 'C:/Users/warcr/AppData/Local/Temp/ui-perception-exact-local-r1/local-results.json' `
  --device cuda --image-size 1536 --max-yolo-candidates 160 --local-method edge `
  --runs 5 --warmup-runs 2 --jpeg-quality 95 `
  --output 'C:/Users/warcr/AppData/Local/Temp/ui-perception-local-yolo-budget160-new.json'
```

```powershell
$python = 'E:/models/dcu-ui-candidate-bench-20261003/venv/Scripts/python.exe'
& $python bench/ui_perception_compare.py `
  --btd 'C:/Users/warcr/AppData/Local/Temp/pi-clipboard-bec114a8-de4a-4a00-89fe-79309ada5503.png' `
  --anvil 'C:/Users/warcr/AppData/Local/Temp/pi-clipboard-1ba6ba2e-7e27-4091-a70a-b3688514ff2a.png' `
  --weights 'E:/models/OmniParser-v2.0/icon_detect_v3/model.pt' `
  --output 'C:/Users/warcr/AppData/Local/Temp/ui-perception-offline-compare-NEW' --repeats 7
& $python bench/ui_fusion_compare.py 'C:/Users/warcr/AppData/Local/Temp/ui-perception-offline-compare-NEW'
```

Choose a new output folder. The comparison starts an owned, isolated Release DCU subprocess with GameMode disabled and Jev off, then closes only that subprocess. It never restarts the deployed MCP server.

`tools/ui_candidate_detect.py` is a standalone detector-only CLI. `ui_candidates_detect_image` is the separate local DCU OCR/edge/calibration tool used for CPU baselines; it is present in the current Release build, not deployed to the active server. Static image IDs are not live game-frame bindings and cannot simply be passed into the live game Jev tool.

## Checks

- Focused Release tests: 99 passed, four reported warnings, including five new detector/tool contract tests.
- Python syntax checks passed for all three scripts.
- Chrome report check: all overlays loaded, filtering selected two matching image cards, no external image URLs.
- Default C# LSP was unavailable; build/test checks were used instead.
- Earlier local-worker follow-up: 24 focused .NET tests passed, five Python fusion tests passed, Python compilation passed, and the Release build succeeded with existing package/interop warnings.
- The earlier focused Release suite passed 18 tests across fast detection, local fusion, Jev recommendation, and native UIA. Subsequent targeted checks passed all four current C# fusion tests, all six Python fuser tests, and Python compilation. The actual combined-OCR settings component/fake-Jev test also passed all 14 marked controls and asserts the three nearby OCR clues above. Release builds completed with existing dependency/interop warnings.
- Component-level actual-worker/fake-Jev Release runs using combined OCR/edge passed on exact-frame BTD2 (23 anchors), Anvil's seven stable cross-frame controls at1536, the selection fixture (13 targets plus Search/Continue OCR clues), and the settings fixture (14 controls). The integration test enforces the 187-candidate upper bound. The transient Anvil alert mark was not counted.
- The C# edge-only component/fake-Jev test matched all seven stable Anvil controls in each of three worker outputs at both 1280 and 1536. Raw detections varied across two signatures at 1280, but target coverage stayed stable. The Pillow-based Python mirror misses the chat input at 160 proposals; replaying the exact GDI+ JPEG through the same worker produces the C# 718×48 candidate at rank 156. The full GameTools capture path's candidate-set assertion missed the chat input and lower-left control at 1280 before sending a fake Jev request; at 1536 it passed and checked all seven boxes in the outgoing request. This distinguishes an input-encoding effect in the component mirror from a real capture/resizing sensitivity in the full tool path.
- The actual `GameTools.GameScreenshot` → cached local inventory → `GameJevTools.GameJevDecideCandidates` Release smoke test passed with the default edge-only local YOLO method and a fake Jev STOP response. It sends no caller-generated boxes or game input and asserts `perception_ms < 10,000`.
- A full-size settings-fixture GameTools run with optional `combined` OCR passed three consecutive captures at 1280 capture/input width, matched all 14 targets, stayed under the freshness limit, and placed three nearby OCR clues in each outgoing fake-Jev request. Earlier combined runs have exceeded 10 seconds; this remains static-fixture evidence, not a general latency guarantee.
- Combined OCR freshness failures are preserved: combined mode produced 10.2 seconds of warm perception after deduplicating template OCR, and Jev rejected it with `The screenshot is older than 10 seconds; reobserve before asking Jev.` This motivated edge-only as the GameTools default; OCR can be explicitly requested but may require reobserve.
- The opt-in native UIA smoke test passed through public `ComputerUseTools.JevDecide`, confirming names and screen/window boxes for buttons, text fields, a checkbox, combo box, radio buttons, slider, and tabs reach a fake Jev request with UIA-projection completeness.
- The opt-in GameTools end-to-end smoke test passed in Release: `game_screenshot` created an edge-default local candidate set, and `game_jev_decide_candidates` sent the cached boxes to a fake Jev call without caller-supplied JSON. It verifies frame-bound boxes, the 187-item cap, and the freshness-window timing. No real Jev request, game input, or active MCP path was exercised.
- The full-width generic-UI GameTools fixture matched all 13 marked targets through fake Jev for three edge-only and three combined-OCR runs at 960 YOLO input; combined also carried Search/Continue text clues. Edge-only at 1280 input exceeded freshness once at 10,839 ms. Extra proposals and OCR clue accuracy remain unscored.
- The current edge-only five-repeat Python mirror at 160+27 matched 21/21 exact-frame BTD2 marks at both resolutions, 6/7 stable Anvil controls at 1280, and 7/7 at 1536. Exact GDI+ JPEG replay explains the component-test difference; the full GameTools capture path still missed two Anvil marks at 1280 and reached all seven at 1536. Warm worker round-trip medians ranged from 435–493 ms at 1280 and 592–651 ms at 1536. Anvil's absent transient alert mark was excluded. These figures exclude capture, edge fusion, .NET processing, and Jev latency.

Self-review (not model accuracy): implementation 8.3/10, evidence 8.4/10 (automatic tool-level path and text clues are better tested; exhaustive completeness, semantic/actionability accuracy, and live MCP remain unverified), presentation 8.2/10, overall 8.3/10. Scope: S2.2.
