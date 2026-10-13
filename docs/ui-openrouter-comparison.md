# OpenRouter screenshot comparison

## Result

The existing comparison HTML now includes actual OpenRouter responses, annotated outputs, requested thinking levels, provider/usage metadata, failures, and a separate BTD character-card reference test:

`C:/Users/warcr/AppData/Local/Temp/ui-perception-offline-compare-r1/comparison.html`

The user explicitly authorized cloud inference with “then test it via open router and add it to comparison.” This overrides the earlier local-only restriction for these two supplied images. No game input, Jev request, deployed-server restart, or persistent-goal resume occurred.

**Best current successful cloud sample:** `google/gemini-3.1-flash-lite`, reasoning effort `minimal`, JSON-object mode, explicitly declared normalized bounding boxes. It returned 21 BTD proposals in **3.629 seconds**, and its 16 character-card boxes all matched the separate manual reference at one-to-one IoU >= 0.5. This is one sample, not a repeatability/reliability claim or proof of all UI clickability. It did not establish verified hero names.

Its first Anvil response returned in **5.468 seconds** but had extra trailing JSON data and was rejected. The initial normalized trial stopped immediately, so low-thinking and the other normalized configurations were not sent.

The user then requested one Anvil redo. With the same model, minimal effort and normalized contract plus a JSON-syntax reminder, it returned a **valid-format 16-proposal inventory in 8.700 seconds**. The inspected overlay includes individual boxes for all ten bottom hotbar slots, but groups broad chat/tutorial/compass regions and misses separate toolbar/input targets. Coverage and clickability remain unverified. The earlier failed artifact is retained; there was no automatic retry.

The paired report now rates this saved Gemini output **8.9/10 on BTD, 6.8/10 on Anvil, 7.9/10 overall**, using its explicit manual review rubric. These are subjective map-usefulness scores, not measured accuracy or production reliability. This remains a layout-bootstrap candidate, not a sub-150 ms per-action detector.

### One-off League of Legends screenshot probe

After the user explicitly requested one additional inference, `google/gemini-3.1-flash-lite` with minimal reasoning processed a static 2551x1439 League of Legends frame in 5.62 s; provider-reported cost was $0.00172525. It returned 19 normalized candidates covering Q/W/E/R, D/F, six inventory slots, the minimap, chat/settings buttons, and four ally portraits. No marked-positive reference was supplied, so recall and false-positive burden were not scored. The minimap is one large candidate; this output does not prove atomic control coverage, identity, clickability, or enabled state.

The host converted coordinates to source-image pixels and `ExternalGameVisualCandidateSetParser` accepted the result. An isolated harness passed the 19 candidates through `GameJevCandidateMapper`, `JevRecommendationRequestBuilder`, and the production `JevController` with a fake `HttpMessageHandler`: the outgoing request contained 19 frame-bound elements, and the fake returned `ability_q`. That selection is only a plumbing check; the fake did not assess the image. No real Jev request or game input was sent. The image ID hashes a static file, not a live game frame; its boxes must not be reused on a live capture. Raw response, candidate set, numbered overlay, and fake request preview remain under `C:/Users/warcr/AppData/Local/Temp/dcu-ui-lol-gemini31-flashlite-r1*` and `dcu-ui-lol-jev-request-preview.json`.

## Actual outcomes

| Model / requested thinking / contract | BTD | Anvil |
|---|---|---|
| Gemini 3.1 Flash Lite / minimal / initial strict schema + original pixels | HTTP 400 on all three attempts | HTTP 400 on all three attempts |
| Gemini 3.1 Flash Lite / low / initial strict schema + original pixels | HTTP 400 on all three attempts | HTTP 400 on all three attempts |
| Gemini 3 Flash / minimal / initial strict schema + original pixels | HTTP 400 on all three attempts | HTTP 400 on all three attempts |
| Qwen3-VL-8B Instruct / reasoning omitted (not supported) / original pixels | 11.1–15.1 s; 12.85 s median; schema-valid inventories, inaccurate geometry (2–3 of 16 reference cards matched) | 72.7–133.5 s; all responses truncated at 6144 tokens, rejected |
| Qwen3-VL-32B Instruct / reasoning omitted (not supported) / original pixels | 40.5–57.7 s; 50.01 s median; schema-valid inventories, inaccurate geometry (0–4 of 16 reference cards matched) | One persisted valid-format response: 87.58 s, 38 proposals; remaining planned outcomes not recorded |
| Gemini 3.1 Flash Lite / minimal / JSON-object compatibility probe | Response in 5.001 s, rejected because candidate_set_issue was null under that probe's string-only contract; coordinate geometry was also unsuitable for direct pixel interpretation | Not sent: probe stopped after first validation failure |
| Gemini 3.1 Flash Lite / minimal / normalized yxyx + host conversion | **3.629 s, 16/16 reference character cards matched** | First: **5.468 s, malformed JSON, rejected**. Explicit redo with syntax reminder: **8.700 s, 16 proposals, valid format** |
| Gemini 3.1 Flash Lite / low / normalized | Not sent after stop gate | Not sent after stop gate |
| Gemini 3 Flash / minimal / normalized | Not sent after stop gate | Not sent after stop gate |
| Qwen3-VL-8B Instruct / reasoning omitted / normalized xyxy | Not sent after stop gate | Not sent after stop gate |

The initial original-pixel trials are not a fair test of the models' best native grounding interface. They expose a failed request/coordinate contract, and are retained rather than silently rescaled. The later normalized request also reduces the output contract to short labels and four coordinates, so its lower latency is not attributable solely to thinking level or model capability.

## Coordinate convention

Google's image-understanding documentation describes boxes as normalized 0–1000 `[ymin, xmin, ymax, xmax]`. The revised host requests this convention explicitly, verifies that the response declares it, validates four in-range integers and positive extents, then converts to original image pixels using floor for left/top and ceil for right/bottom. The unexecuted normalized Qwen variant requests `[xmin, ymin, xmax, ymax]` explicitly.

No hidden scale is guessed. Original model outputs and normalized source boxes are retained in the local JSON artifacts. The reference card rectangles are benchmark-only and were never supplied to the models.

## Latency, thinking and cost

- Cloud timings cover the complete non-streaming POST, including upload, network, provider queue and full JSON generation; they are not time-to-first-token. They exclude preparing the image data URI and report drawing.
- Requests selected providers sorted by latency, required parameter support, used `data_collection=deny`, and disabled provider fallback, redirects and automatic retries. These privacy settings filter advertised provider policies; they are not an independent policy audit.
- Gemini effort was requested as `minimal` or `low`. Only the corrected minimal configuration produced an accepted map. Successful Qwen responses reported zero reasoning tokens; their model metadata does not expose a reasoning parameter.
- The initial 30-call plan hit a 900-second tool timeout after 28 responses were persisted. One further request may have been in flight and billed without a saved response. No benchmark process remained afterward.
- The existing BTD Gemini call cost **$0.00142025**; the newly requested Anvil call cost **$0.00146475**. Their two-call total is **$0.00288500 (0.2885 cents)**. The earlier rejected Anvil call cost **$0.00132450**, separately, and is not erased or included in that two-call total.
- The local report now records **32 persisted benchmark responses** and **$0.023952** in rounded API-reported cost (including rejected output). This is not a guaranteed final account charge. A separately reviewed HTTP-400 diagnostic is recorded separately.
- Additional compatibility/normalized phases were separately reviewed and bounded; they stopped at validation failures. No uncontrolled retry or automatic provider/model substitution occurred.

## Marked-positive cheap-model sweep (no consistent pass)

The user supplied a positive-only reference: 21 intended BTD targets (16 cards + five controls) and eight marked Anvil controls. Unmarked boxes are not penalized. All scores count only correctly localized marked positives, with one-to-one assignment; they do not prove unmarked false positives are safe or that the marked inventory is exhaustive.

| Model / thinking setting | BTD positives across five runs | Anvil marked positives across five runs | Outcome |
|---|---:|---:|---|
| Qwen 3.7 Flash / reasoning off | 16, 20, 0, 21, 0 of 21 | 0, 0, 6, 1, invalid of 8 | Inconsistent |
| Qwen 3.7 Flash / provider-default reasoning | 21 of 21 in four valid runs; one truncated | 4, 6, 5, 4, 4 of 8 | Inconsistent |
| Qwen 3.7 Flash / reasoning budget 2048, output cap 4096 | 21/21 in all five full-frame runs | 5, 6, 3, 5, 5 of 8 | Inconsistent |
| Qwen 3.7 Flash / reasoning budget 2048, full overview + four overlapping crops | 21, 18, 5, 20, 2 of 21 | 6, 5, 4, 5, 6 of 8 | Inconsistent; 31–54 s BTD, 103–139 s Anvil |
| Gemma 3 4B / no reasoning | 0/21 in all five runs | Five invalid or truncated responses | Failed |
| Gemma 3 12B / no reasoning | 0, 2, HTTP failure of 21 | 0, 0, 1, 0, 0 of 8 (four validation failures) | Failed |
| Qwen3-VL 8B Instruct / no reasoning | 16, 7, 10, 16, 16 of 21 | 1, 4, invalid, 3, invalid of 8 | Failed |
| GPT-4.1 Nano / no reasoning | 0–1/21 | 1–2/8 in valid runs; remaining responses invalid | Failed |
| GPT-4o Mini / no reasoning | 0/21 | 0/8 | Failed |
| GPT-4.1 Mini / no reasoning | 0–1/21 | 0/8 | Failed |
| Gemini 2.5 Flash Lite / reasoning off | BTD route unavailable (HTTP 429) | Five truncated responses | Failed/unavailable |
| Gemini 2.5 Flash Lite / provider-default reasoning | 1/21 in all five runs | Five truncated responses | Failed |
| GPT-5 Nano and GPT-5 Mini / minimal and low | No eligible endpoints under the requested provider/privacy/parameter filters | No eligible endpoints | Not evaluated |
| Gemma 4 Free variants and Ling 3.0 Flash VL / off or default | Provider returned HTTP 429/404; no candidate inventory | Provider returned HTTP 429/404; no candidate inventory | Not evaluated |

The best repeated cloud model on BTD was Qwen 3.7 Flash with reasoning budget 2048: 21/21 positives in all five full-frame calls. It still missed 2–5 of eight approximate Anvil positives in every call. Its provider-reported cost averaged $0.000320/request across both image sets (range $0.000281–$0.000435); the no-reasoning Qwen run was cheaper at about $0.000210/request but highly inconsistent (BTD: 0, 16, 20, 21, 0 of 21). Tiling made latency worse (31–139 seconds) and recall unstable; no cloud configuration passed both sets.

Local results on the exact inference files show a useful split. The saved BTD calibrated-template output matched 21/21 marked positives within its 23-candidate scope; its five warm timing samples had a 64 ms median. The generic YOLO+edge fusion configuration matched only 4/21 BTD positives, so the calibrated template remains necessary for that known page. On the clean Anvil frame, the model-free C# edge output matched 2/8, OCR 1/8, and edge+OCR 2/8. A local OmniParser YOLOv9-E run at confidence 0.001 retained 436 raw proposals; five warm timing samples had a 264 ms median and 411 ms p95. Its saved detection set matched 7/8 marked positives. The local fusion script applied a second IoU=0.20 suppression pass to YOLO boxes, then selected edge proposals; the tested output contained **239 boxes (212 YOLO + 27 edge) and matched all 8 marked positives**. Repeating the same clean Anvil input in a separate process produced an identical candidate array. The Anvil marks come from a different night-state screenshot, so this remains approximate transfer evidence. The positive-only evaluator ignores extras; neither result establishes exhaustive control discovery or clickability.

A reproducible local bridge now exposes frame-bound incomplete candidate JSON from `game_screenshot(include_local_fast_candidates=true)`, accepts an externally generated YOLO candidate set with the same screenshot frame ID, fuses bounded proposals in `tools/ui_candidate_fuse.py`, and maps them into the advisory-only `game_jev_decide_candidates` request. A fake Jev endpoint test successfully serialized 239 incomplete candidates; no real Jev request was sent. YOLO's measured latency excludes model loading; its first inference pipeline after model load varied from 3.6 to 7.0 seconds across runs, and a persistent resident worker has not been integrated or measured. The fused result is promising for this Anvil sample, not yet a repeated general-screen reliability result.

The Anvil positives are approximate transfer references from the user's marked night-state screenshot, evaluated against the earlier clean 2559×1437 screenshot because an unmarked copy of that exact marked frame was unavailable. Anvil results are not same-frame ground truth. Extra model proposals are deliberately not scored, per the user's instruction.

The sweep reused all persisted responses and made no automatic retries. Across **130 total requests**, API-reported inference cost was **$0.09887388**. The reviewed tiled-image continuation accounted for the last 10 requests and $0.00595563 reported cost. Requests used `data_collection=deny`, disabled provider fallbacks and redirects, and declared coordinate conventions. The $0.20 campaign limit uses the OpenRouter `max_price.request` routing filter on each remaining-budget calculation; reviewers noted this is conditional on provider/router honor and reported usage, not an unconditional guarantee of the final account bill. Results are in `C:/Users/warcr/AppData/Local/Temp/ui-perception-offline-compare-r1/cheap-model-sweep-r2/results.json`, `.../cheap-model-sweep-r3/results.json`, and `.../cheap-model-sweep-r4/results.json`, and `.../cheap-model-sweep-r5-retry1/results.json`. The first r5 launch stopped at local setup before sending any request; its plan-only folder is retained separately.

No false-positive/clickability or exhaustive-coverage claim is made. No real Jev request, game input, server restart or deployment occurred.

## Sources

- https://openrouter.ai/api/v1/models — verified model IDs, image support, pricing and supported parameters; metadata saved in the report folder.
- https://openrouter.ai/docs/guides/best-practices/reasoning-tokens — reasoning effort controls.
- https://openrouter.ai/docs/guides/routing/provider-selection — latency routing and data-collection filters.
- https://ai.google.dev/gemini-api/docs/image-understanding#object-detection — declared normalized bounding boxes.

## Verification

- Eight focused Jev visual-recommendation tests passed, including a fake-transport post with 239 frame-bound incomplete candidates; five local FastUi detector tests passed; five Python local-fusion tests passed.
- Python syntax and Git whitespace checks passed.
- The full `ShadowUse.Tests` suite timed out at 300 seconds; focused tests passed. No live game or Jev endpoint was used.
- All cloud raw response artifacts remain local; no API key was printed or persisted in benchmark outputs.

Self-review (not model accuracy): implementation 8.0/10, evidence 7.0/10 (corrected successful outputs are single-sample, with an earlier Anvil failure and no Anvil ground truth), presentation 9.0/10, overall 8.0/10. Scope: S2.2.
