# DCU + Jev: deduplicated computer/browser-use feature catalogue

**Recommendation: retain DCU as the Windows executor and extend it with browser-native control, a bounded Jev chooser, optional visual grounding, and verified reusable workflows.** The catalogue below contains **60 distinct capabilities: 54 core capabilities and 6 optional UX/application features**. This is a capability synthesis, not a claim that all 60 should be implemented immediately.

Research date: **20 September 2026**. DCU baseline: `d6818547e5e92bdaf7c9fc61011e5851a37a7d42`.

## Evidence and status conventions

This was a read-only code/documentation review. No repositories were modified and no live desktop or head-to-head runtime tests were performed. Sources are linked per feature. “Origin” means a project demonstrating or documenting a useful implementation, not a claim that the project invented the capability. The proposed DCU behaviour sometimes combines ideas from several sources.

**Keep** means preserve an existing DCU capability. **Extend** means DCU has part of it, but the catalogue describes stronger coverage. **New** means a proposed capability not found in the reviewed DCU implementation. **Optional** means an application/UX layer rather than a core executor requirement.

The proposed strict background mode, review of generated helpers, and conservative escalation rules are design recommendations, not claims that all cited donors already enforce the complete combined behaviour.

## What Jev actually contributes

Jev evaluates supplied state against typed questions instead of generating open-ended text. **Choice** selects an enumerated option; **Score** evaluates against described ordered levels; **Noul** returns a probability-like yes/no judgment. Independent questions can share one request. Its useful role here is selecting from actions that an adapter has already established as real and eligible.

TypeSafe’s launch article reports 70–500 ms decision latency; that is a vendor-reported range, not a measured DCU speed or a whole-task completion guarantee. Observation, network, execution, UI settling and verification also contribute to latency.

Choice is limited to 255 options, so large action spaces need local filtering or hierarchical selection. The confidence statistic is not the same as the selected option’s probability. Thresholds need validation on the actual tasks and primitive being used.

Jev is not a replacement for a screenshot parser, operating-system driver, arithmetic code or a general text generator. TypeSafe’s documented Jev 1.13 limitations include arithmetic/date mistakes, irrelevant-context sensitivity, adversarial-content susceptibility and no guarantee that separately asked questions obey mathematical identities. Schema-valid output is not evidence of a correct or safe decision.

Jev references: [introduction](https://docs.typesafe.ai/introduction), [Choice](https://docs.typesafe.ai/primitives/choice), [Score](https://docs.typesafe.ai/primitives/score), [Noul](https://docs.typesafe.ai/primitives/noul), [confidence](https://docs.typesafe.ai/confidence), [documented limitations](https://docs.typesafe.ai/model-jaggedness/jev-1.13), and [vendor latency claims](https://typesafe.ai/blog/introducing-system-one-models-and-jev).

The most directly transferable examples reviewed were **[Cua’s `jev-use`](https://cua.ai/docs/how-to-guides/driver/jev-use)**, **[`jev-ultrafast`](https://github.com/browser-use/jev-ultrafast)**, and **[Jev AX Pilot](https://github.com/dabit3/jev-experiments/blob/main/jev-ax-pilot/README.md)**. Cua’s bounded chooser is particularly useful architecturally: it returns an allowed candidate ID, keeps driver calls in the application, and checks actual submission state. AX Pilot’s foreground/CGEvent implementation should not replace DCU’s background executor.

## DCU baseline findings

DCU already has UIA-first background input, per-window selection, labelled accessibility snapshots, background capture, server-side waiting and batching, a virtual cursor, diagnostics and action-effect checks. These should not be rebuilt under different names.

Three important implementation distinctions:

1. Window IDs identify a specific window, but `e...` element IDs are minted afresh for snapshots; do not assume they are durable workflow selectors. Element recovery uses RuntimeId first and then AutomationId/name plus control type, where the fallback returns the first match.
2. `changed_elements` currently reports counts, while the click verifier internally recognises some property changes. That is not yet a full semantic diff. Also, a click’s observed UIA effect is not proof of overall task completion; verification coverage differs by action.
3. FocusGuard restores focus after a change; the README acknowledges that UIA text fallback or app behaviour can briefly activate a window. That is not the same as preventing every focus steal. Bounds and desktop checks are disabled by default, and the README explicitly says trust gates are disabled by default, including for password fields.

DCU sources: the pinned [README](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/README.md), [UIA service](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/src/ShadowUse/Automation/UiaService.cs), and [action implementation](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/src/ShadowUse/Tools/ComputerUseTools.cs).

[Cua also documents background delivery with platform/app-specific limits](https://cua.ai/docs/reference/cua-driver/platform-support), so DCU should not be described as the only project with background control. DCU’s design remains valuable; the useful comparison is actual compatibility, refusal behaviour and observed outcomes.

## Deduplicated catalogue
### A. Perception and reliable targeting

| # | Feature and practical behaviour | Origins | DCU |
|---|---|---|---|
| 1 | **Background desktop input.** Control apps through accessibility actions and targeted messages without using the real pointer. Preserve DCU’s design; do not silently substitute foreground input. | [DCU README](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/README.md) | Keep |
| 2 | **Target-bound background capture.** Capture the selected occluded window; return an explicit capture failure rather than a screenshot of an unrelated visible window. | [DCU README](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/README.md) | Keep |
| 3 | **Exact window and browser identity.** Bind actions to a specific process/window, reject ambiguity, and extend that identity to the corresponding browser tab or page. | [DCU UIA implementation](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/src/ShadowUse/Automation/UiaService.cs); [Cua Driver platform support](https://cua.ai/docs/reference/cua-driver/platform-support) | Extend |
| 4 | **Hybrid semantic observations.** Combine UIA, browser DOM/accessibility data, and optional visual evidence into a consistent element record containing names, values, roles, bounds and actions. | [UI-TARS Desktop / Agent TARS](https://github.com/bytedance/UI-TARS-desktop) | Extend |
| 5 | **Visual grounding fallback.** Detect and label icons, custom controls and screenshot regions when accessibility data is absent. Use OCR/vision as a fallback, not for every action. | [Microsoft OmniParser](https://github.com/microsoft/OmniParser); [UI-TARS Desktop / Agent TARS](https://github.com/bytedance/UI-TARS-desktop) | New |
| 6 | **Goal-relevant compact snapshots.** Scope observations to relevant regions and actionable controls, with depth/output limits rather than sending the entire application tree. | [agent-browser snapshots](https://agent-browser.dev/snapshots); [Jev AX Pilot](https://github.com/dabit3/jev-experiments/blob/main/jev-ax-pilot/README.md) | Extend |
| 7 | **Semantic and visual state diffs.** Return changed content/properties and optional screenshot differences, not just counts of added and removed elements. | [agent-browser diffing](https://agent-browser.dev/diffing); [DCU action implementation](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/src/ShadowUse/Tools/ComputerUseTools.cs) | Extend |
| 8 | **Fresh-observation action binding.** Tie candidate actions to an observation revision or capture ID; revalidate target identity and current geometry before execution. | [Cua jev-use recipe](https://cua.ai/docs/how-to-guides/driver/jev-use) | Extend |
| 9 | **Contextual locator repair.** Re-find controls by meaning, role and context after UI changes, while rejecting ambiguous matches instead of clicking the first duplicate label. | [Stagehand](https://github.com/browserbase/stagehand); [Playwright actionability](https://playwright.dev/docs/actionability) | Extend |
| 10 | **Actionability checks.** Before interaction, establish that the target is unique, enabled, stable and able to receive the intended input. | [Playwright actionability](https://playwright.dev/docs/actionability) | Extend |

### B. Execution and browser integration

| # | Feature and practical behaviour | Origins | DCU |
|---|---|---|---|
| 11 | **Native browser control backend.** Use browser DOM/CDP/Playwright operations where available instead of translating every browser action into Windows mouse and keyboard messages. | [Playwright MCP](https://github.com/microsoft/playwright-mcp); [agent-browser](https://github.com/vercel-labs/agent-browser) | New |
| 12 | **Page-provided WebMCP tools.** Discover and invoke a website’s declared structured actions, including form-based and JavaScript-defined operations. Availability depends on the site/browser. | [Chrome WebMCP introduction](https://developer.chrome.com/blog/webmcp-epp) | New |
| 13 | **Application API and GUI mixing.** Use application APIs, COM or an approved CLI for suitable operations, with GUI interaction covering the remaining gaps. | [Microsoft UFO² / UFO³](https://github.com/microsoft/UFO) | New |
| 14 | **Complete browser surface handling.** Handle tabs, frames, dialogs, file uploads and downloads explicitly rather than relying on screenshots of operating-system dialogs. | [Playwright MCP](https://github.com/microsoft/playwright-mcp) | New |
| 15 | **Reusable or isolated browser sessions.** Support approved logged-in profiles and clean isolated contexts as separate modes. Preserve authentication without repeatedly asking the model to log in. | [Playwright MCP](https://github.com/microsoft/playwright-mcp); [agent-browser](https://github.com/vercel-labs/agent-browser) | New |
| 16 | **Condition-driven settling.** Wait for a meaningful UI condition or required control change, rather than adding a fixed sleep after every operation. | [Playwright actionability](https://playwright.dev/docs/actionability); [DCU action implementation](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/src/ShadowUse/Tools/ComputerUseTools.cs) | Extend |
| 17 | **State-aware action batching.** Run multiple actions locally to reduce round trips, but stop/reobserve on failure or a page transition that invalidates remaining targets. | [DCU action implementation](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/src/ShadowUse/Tools/ComputerUseTools.cs); [Browser Use agent configuration](https://docs.browser-use.com/open-source/customize/agent/all-parameters) | Extend |
| 18 | **Reusable agent-created helpers.** Allow an agent to develop missing task helpers in a separate workspace while keeping the core executor protected. Review before promoting helpers to trusted skills. | [Browser Harness](https://github.com/browser-use/browser-harness) | New |

### C. Jev and agent decision-making

| # | Feature and practical behaviour | Origins | DCU |
|---|---|---|---|
| 19 | **Bounded next-action selection.** Give Jev a finite set of real candidate IDs, including reobserve/abstain. Resolve the selected ID to the original action in code. | [TypeSafe Choice](https://docs.typesafe.ai/primitives/choice); [Cua jev-use recipe](https://cua.ai/docs/how-to-guides/driver/jev-use) | New |
| 20 | **One-call independent decision heads.** Ask action, target and other independent questions against the same state in one request; speculative target heads avoid sequential model round trips. | [TypeSafe Jev introduction](https://docs.typesafe.ai/introduction); [jev-ultrafast](https://github.com/browser-use/jev-ultrafast) | New |
| 21 | **Typed ranking and classification.** Use Score for graded priorities and Noul for semantic yes/no probabilities; expose these judgments to pipelines without parsing generated prose. | [TypeSafe Score](https://docs.typesafe.ai/primitives/score); [TypeSafe Noul](https://docs.typesafe.ai/primitives/noul); [Vercel ai-cli](https://github.com/vercel-labs/ai-cli) | New |
| 22 | **Uncertainty-aware escalation.** Use validated thresholds to choose between acting, observing again, asking for missing information, or escalating to a stronger model/human. | [TypeSafe confidence](https://docs.typesafe.ai/confidence) | New |
| 23 | **Planner/controller separation.** Let a general model plan subtasks while a cheaper/faster controller handles immediate decisions. Keep their responsibilities and model choices separate. | [Agent S original experience-augmented architecture](https://www.simular.ai/articles/agent-s); [Nanobrowser](https://github.com/nanobrowser/nanobrowser) | New |
| 24 | **Separate text-generation helper.** Use supplied text or a generative model for open-ended writing; let Jev select the action/field rather than making it generate prose. | [jev-ultrafast](https://github.com/browser-use/jev-ultrafast) | New |
| 25 | **Deterministic bypass and candidate filtering.** Calculate exact facts and remove impossible actions in code. Skip inference entirely when only one valid action remains. | [JevPilot](https://github.com/standardagents/jevpilot) | New |
| 26 | **Adaptive cadence and stale-answer rejection.** Vary decision frequency with state changes, cancel superseded requests, and reject answers belonging to an older user query or observation. | [JevPilot](https://github.com/standardagents/jevpilot); [Jev Launcher example (archived; successor linked in README)](https://github.com/dabit3/jev-experiments/blob/main/jev-launcher/README.md) | New |
| 27 | **Pluggable models and provider failover.** Configure different providers/models for planning, extraction and execution; switch on provider failures. Local-model support is separate from local browser execution. | [Browser Use agent configuration](https://docs.browser-use.com/open-source/customize/agent/all-parameters); [Nanobrowser](https://github.com/nanobrowser/nanobrowser) | New |
| 28 | **Experience memory and loop recovery.** Retrieve prior task/subtask experience and track failed/repeated actions so the controller can change strategy rather than repeat a dead-end. | [Agent S original experience-augmented architecture](https://www.simular.ai/articles/agent-s); [Jev AX Pilot](https://github.com/dabit3/jev-experiments/blob/main/jev-ax-pilot/README.md) | New |

### D. Reusable workflows and scale

| # | Feature and practical behaviour | Origins | DCU |
|---|---|---|---|
| 29 | **Dependency-aware cross-app workflows.** Represent subtasks and dependencies explicitly, support replanning, and pass results between app/device workers. | [Microsoft UFO² / UFO³](https://github.com/microsoft/UFO) | New |
| 30 | **Demonstration-to-reviewed replay.** Turn a human/agent demonstration into an executable workflow that is reviewed before use, instead of requiring fresh generative reasoning every run. | [OpenAdapt](https://github.com/OpenAdaptAI/OpenAdapt) | New |
| 31 | **Cached action resolution.** Reuse previously resolved actions when the relevant request/state is unchanged; invalidate the cache when its assumptions no longer hold. | [Stagehand](https://github.com/browserbase/stagehand) | New |
| 32 | **Data-processing workflow steps.** Combine browser actions with loops, file parsing, HTTP requests, validation and output storage. This is more than DCU’s flat action sequence. | [Skyvern](https://github.com/Skyvern-AI/skyvern) | New |
| 33 | **Schema-validated extraction.** Extract page/application data into a defined schema for downstream code, rather than returning only screenshots or free-form summaries. | [Stagehand](https://github.com/browserbase/stagehand); [Browser Use agent configuration](https://docs.browser-use.com/open-source/customize/agent/all-parameters) | New |
| 34 | **Verbatim evidence extraction.** Select and return matching source text directly when an exact quotation or evidential passage is needed, instead of regenerating it. | [jev-browse](https://github.com/kyrylosyzonenko/jev-browse) | New |
| 35 | **Parallel independent workers.** Execute unrelated tasks in separate tabs/contexts or devices. Do not parallelise conflicting mutations against the same app/window. | [BrowserOS](https://github.com/browseros-ai/BrowserOS); [Microsoft UFO² / UFO³](https://github.com/microsoft/UFO) | New |
| 36 | **Isolated execution environments.** Run risky or unattended workflows in separate desktops/VMs or managed sandbox pools rather than on the everyday desktop. Cross-platform expansion is optional. | [Cua](https://github.com/trycua/cua) | New |

### E. Safety and control

| # | Feature and practical behaviour | Origins | DCU |
|---|---|---|---|
| 37 | **Deterministic permission policies.** Enforce allowed operations and resources in the executor. A model’s statement that an action is safe must not itself grant permission. | [agent-browser security](https://agent-browser.dev/security) | Extend |
| 38 | **Approval-required action classes.** Pause for explicit approval before designated sensitive operations; denied or expired approvals must not execute. | [agent-browser security](https://agent-browser.dev/security); [OpenAdapt](https://github.com/OpenAdaptAI/OpenAdapt) | New |
| 39 | **Secret-safe credential injection.** Keep credentials in a vault or trusted code and fill resolved controls without placing plaintext secrets into model observations. | [Stagehand](https://github.com/browserbase/stagehand); [agent-browser](https://github.com/vercel-labs/agent-browser) | New |
| 40 | **Untrusted-content boundaries.** Label page/document content as untrusted data and bound its size. These measures reduce exposure but do not make prompt injection impossible. | [agent-browser security](https://agent-browser.dev/security); [Jev 1.13 documented limits](https://docs.typesafe.ai/model-jaggedness/jev-1.13) | New |
| 41 | **Network destination restrictions.** Constrain browser destinations and outbound traffic where supported. Use OS/container egress controls when a stronger boundary is required. | [agent-browser security](https://agent-browser.dev/security) | New |
| 42 | **Explicit delivery capabilities and refusals.** Report what an app/backend can safely do. A strict DCU mode should refuse focus-taking or unsupported paths rather than silently escalating. | [Cua Driver platform support](https://cua.ai/docs/reference/cua-driver/platform-support); [DCU README](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/README.md) | Extend |
| 43 | **Bounded runs and immediate stop.** Expose stop/cancel, model and step deadlines, retry limits, and a clear stopped/failed result rather than an unbounded action loop. | [Browser Use agent configuration](https://docs.browser-use.com/open-source/customize/agent/all-parameters); [Jev AX Pilot](https://github.com/dabit3/jev-experiments/blob/main/jev-ax-pilot/README.md) | Extend |
| 44 | **Authenticated remote access.** For optional remote use, borrow TLS, bearer/OAuth-PKCE authentication, client restrictions and per-tool disablement—not unauthenticated desktop control. | [Windows-MCP](https://github.com/CursorTouch/Windows-MCP) | New |

### F. Verification, debugging and evaluation

| # | Feature and practical behaviour | Origins | DCU |
|---|---|---|---|
| 45 | **Observed per-action effects.** Report whether the intended operation changed observable state, not merely whether an input was dispatched. Extend DCU’s strongest checks consistently across tools. | [DCU action implementation](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/src/ShadowUse/Tools/ComputerUseTools.cs) | Extend |
| 46 | **Independent task-completion evidence.** Declare success only when the expected end state is verified; for example, the server received a submission. Model “done” is not sufficient. | [Cua jev-use recipe](https://cua.ai/docs/how-to-guides/driver/jev-use); [OpenAdapt](https://github.com/OpenAdaptAI/OpenAdapt) | New |
| 47 | **Semantic and visual assertions.** Check visible outcomes such as an error appearing or a selected item changing appearance, including custom-rendered interfaces. | [Midscene](https://github.com/web-infra-dev/midscene) | New |
| 48 | **Replayable run timeline.** Record actions with before/after screenshots or DOM state, timings and logs so a failed run can be inspected step by step. | [Playwright Trace Viewer](https://playwright.dev/docs/trace-viewer); [BrowserOS](https://github.com/browseros-ai/BrowserOS) | New |
| 49 | **Live action/decision inspector.** Expand the cosmetic cursor into a HUD showing selected target, step status, probabilities, latency and the source of a decision. | [Jev AX Pilot](https://github.com/dabit3/jev-experiments/blob/main/jev-ax-pilot/README.md) | Extend |
| 50 | **Network and console diagnostics.** Inspect failed requests, response details, browser console errors and source-mapped stack traces alongside GUI state. | [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp) | New |
| 51 | **Performance and environment testing.** Capture performance traces and reproduce slow CPU/network conditions, alternate viewports or other environment settings. | [Chrome DevTools MCP tool reference](https://github.com/ChromeDevTools/chrome-devtools-mcp/blob/main/docs/tool-reference.md) | New |
| 52 | **Mocked and recorded-network replay.** Use fixed API responses or HAR recordings for controlled tests, reducing dependence on changing external sites and services. | [Playwright network mocking / HAR replay](https://playwright.dev/docs/mock) | New |
| 53 | **Comparable regression benchmarks.** Run repeatable tasks across adapters/models with recorded configuration, outcomes, latency and resource use; extend DCU’s existing benchmark suite. | [WindTunnel](https://github.com/nekuda-ai/WindTunnel); [Cua](https://github.com/trycua/cua); [DCU README](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/README.md) | Extend |
| 54 | **Human takeover during a run.** Offer a live view and an intervention path when automation is blocked; reobserve the resulting state before continuing. | [Skyvern](https://github.com/Skyvern-AI/skyvern) | New |

### G. Optional predictive UX and application features

| # | Feature and practical behaviour | Origins | DCU |
|---|---|---|---|
| 55 | **Intent-aware live search and filtering.** Retrieve candidates locally, then semantically rerank or label them as the query changes; apply this to files, products or message lists. | [Jev Instant Search](https://github.com/dabit3/jev-experiments/blob/main/jev-instant-search/README.md); [Sift / inbox-blitz](https://github.com/dabit3/jev-experiments/blob/main/inbox-blitz/README.md); [Jev Launcher example (archived; successor linked in README)](https://github.com/dabit3/jev-experiments/blob/main/jev-launcher/README.md) | Optional |
| 56 | **Named multi-app workspaces.** Let users review and save groups of apps, files and links, then reopen the group by name or intent. | [Jev Launcher example (archived; successor linked in README)](https://github.com/dabit3/jev-experiments/blob/main/jev-launcher/README.md) | Optional |
| 57 | **Predictive semantic spreadsheet columns.** Infer a typed judgment from a column heading, batch row evaluations, cache unchanged judgments and recalculate dependants with preview/undo. | [Judge Sheets](https://github.com/dabit3/jev-experiments/blob/main/judge-sheets/README.md) | Optional |
| 58 | **Voice turn-taking and interruption.** Use streaming transcripts to judge intent, completion and interruption, with deterministic timing safeguards and a silence fallback. | [Jev Voice Turn](https://github.com/dabit3/jev-experiments/blob/main/jev-voice-turn/README.md) | Optional |
| 59 | **Catalog-constrained UI composition.** Choose and arrange predefined UI components with allowed properties/actions; supply actual text/data separately. The reviewed Jev integration is experimental/unreleased. | [json-render Jev experiment](https://json-render.dev/docs/jev) | Optional |
| 60 | **Shared human-and-agent state.** Synchronise state and presence, retain version history, and provide user-specific undo for a collaborative control/review interface. | [Liveblocks Sync](https://liveblocks.io/sync) | Optional |

## Proposed integration architecture

```text
User goal
  -> general planner: task decomposition and required content
  -> adapters: application API / browser DOM-WebMCP / DCU UIA / visual fallback
  -> compact, revision-bound state and real candidate actions
  -> deterministic policy and eligibility checks
  -> Jev: choose / rank / abstain
  -> trusted executor: resolve original action and revalidate target
  -> observe effects and independently verify the required outcome
  -> continue / replan / request intervention
```

This ordering is a design recommendation, not a universal speed ranking. Prefer an available, authorised, reliable structured interface over simulating a GUI, and preserve the task’s required interaction semantics.

### Implementation order I would choose

| Stage | Deliverable | Relevant catalogue IDs |
|---|---|---|
| 1 | Preserve DCU’s working executor; strengthen identity, stale-state handling, strict-background refusals and policy boundaries. | 1–3, 8–10, 37–43, 45 |
| 2 | Add a browser adapter with real tab/frame/form/file support, permitted session modes and optional WebMCP. | 4, 11–17 |
| 3 | Add the bounded Jev chooser outside the executor, plus a separate planner/text helper and abstention path. | 19–28 |
| 4 | Add visual fallback, end-state checks, traces and repeatable evaluation before expanding autonomy. | 5–7, 46–53 |
| 5 | Add reusable skills, reviewed replay, caching and cross-app workflows. Scale into isolated workers only after correctness is measured. | 18, 29–38, 54 |
| 6 | Add voice, predictive launch/search and collaborative interfaces where there is a product use case. | 55–60 |

### Important non-equivalences

- An accessible element, a currently actionable element and the correct element for the user’s goal are three different claims.
- “Input dispatched”, “UI changed” and “task succeeded” need separate result fields.
- A local browser/driver does not imply local inference or that page data never leaves the device.
- Login/profile reuse and strong fresh-context domain containment are not universally composable. agent-browser explicitly documents incompatible combinations and remaining egress limitations.
- Human approval before a sensitive action is different from human review admitting a reusable workflow.
- Workflow replay, cached action resolution, experience memory and network-fixture replay solve different problems; they were not merged.
- Recording/tracing is not an undo facility for arbitrary external actions. Document version restoration also cannot reverse a sent message, purchase or remote deletion.
- Jev’s typed probabilities do not make it a prompt-injection defence. Enforce permissions, exact arithmetic, destination restrictions and secret handling outside the model.

## Supplied-source coverage and corrections

| Supplied item | What was established in this review |
|---|---|
| Voice-controlled Mac X demo | The supplied X post was inaccessible. The separately inspected Jev Voice Turn and AX Pilot demos substantiate related voice and accessibility patterns, not that exact demo’s implementation. |
| `milind-soni/ti` | The supplied web URL could not be fetched and the GitHub README request returned 404. Its stated CoreML/OCR implementation was therefore not independently verified. This does not prove the project never existed. |
| Stagehand browser-action X demo | The post was inaccessible. Stagehand itself and `jev-browse` were inspected independently; the latter is a related project, not evidence of the original demo’s source. |
| WebMCP benchmark / WindTunnel / jev-ultrafast | Primary project/results pages inspected. Treat published measurements as those harnesses’ results, not as a universal Jev or DCU speed claim. |
| Autofill X demo / Liveblocks | X implementation inaccessible. Liveblocks confirms collaboration primitives; that alone does not establish the demo’s autofill implementation. |
| json-render | Jev composition documentation inspected. The APIs were explicitly experimental and unreleased in the reviewed documentation; they require prepared component candidates and content. |
| Predictive spreadsheets | `judge-sheets` inspected. It is a standalone Sheets-like grid with its own formula engine, not evidence of an installed Google Sheets integration. |
| Terminal decisions | Vercel ai-cli inspected; useful typed decision plumbing, not a full desktop controller. |
| Predictive launcher | `jev-launcher` inspected. Its README says that copy is archived and development moved to `dabit3/launcher`. Features in the catalogue are supported by the inspected example, not a claim about the latest successor release. |
| Intent-based Gmail search | `inbox-blitz` / Sift inspected. It demonstrates semantic labelling/filtering with an email fixture and Gmail-like UI; that is not proof of a live Gmail connector. |
| Wikipedia and Doom X demos | Posts inaccessible. The TypeSafe launch article supplies related examples, including structured-state action choice; it does not establish a generic visual-control implementation for those posts. |
| SuperX and API docs | The product site was reachable, but the supplied API documentation/integration evidence was not established. No Jev-specific implementation capability was credited on that basis. |
| OpenCode testing X demo | Post inaccessible. Related browser tools were inspected, without treating them as the missing demo’s repository. |
| FirstScreen | Supplied live app could not be fetched; no exact implementation source verified. |
| Traffic-light demo | Supplied live URL could not be fetched; retained only as user-supplied inspiration. |
| Subway Surfers / JEVRACE | X posts inaccessible; no source-code or general-purpose reliability claim inferred. |
| JevPilot | Source/README inspected. Useful transferable patterns: code-generated valid candidates, independent safety constraints, deterministic bypass and adaptive decision cadence. Driving-specific controls are not DCU features. |
| Sprite Fusion real-time level generation | Supplied technical article could not be retrieved; implementation details not independently verified. |
| Goal-completion, routing/tool-gates and adaptive-harness X posts | Posts inaccessible. Related implementations were established independently through Cua jev-use, OpenAdapt, agent-browser policy controls and Browser Harness. No claim those are the original authors’ code. |

The accessible game/simulation examples mostly contribute the same bounded-control ideas already counted in IDs 19–28; treating each game as a separate computer-use feature would defeat deduplication.

Community searches were also attempted as a sanity check. The usable feature evidence in this report comes from project-maintainer code/documentation, not Reddit anecdotes or marketing rankings. No popularity or quality rating is inferred from repository stars.

## Source register

The register includes sources used for the catalogue and the Jev/DCU analysis. Per-feature links above are the quickest route to the relevant donor. README documentation is not runtime verification; experimental and inaccessible items are explicitly qualified.
- **DCU README:** https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/README.md
- **DCU action implementation:** https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/src/ShadowUse/Tools/ComputerUseTools.cs
- **DCU UIA implementation:** https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/src/ShadowUse/Automation/UiaService.cs
- **TypeSafe Jev introduction:** https://docs.typesafe.ai/introduction
- **TypeSafe launch article:** https://typesafe.ai/blog/introducing-system-one-models-and-jev
- **TypeSafe Choice:** https://docs.typesafe.ai/primitives/choice
- **TypeSafe Score:** https://docs.typesafe.ai/primitives/score
- **TypeSafe Noul:** https://docs.typesafe.ai/primitives/noul
- **TypeSafe confidence:** https://docs.typesafe.ai/confidence
- **Jev 1.13 documented limits:** https://docs.typesafe.ai/model-jaggedness/jev-1.13
- **jev-ultrafast:** https://github.com/browser-use/jev-ultrafast
- **jev-browse:** https://github.com/kyrylosyzonenko/jev-browse
- **JevPilot:** https://github.com/standardagents/jevpilot
- **Vercel ai-cli:** https://github.com/vercel-labs/ai-cli
- **Jev Launcher example (archived; successor linked in README):** https://github.com/dabit3/jev-experiments/blob/main/jev-launcher/README.md
- **Judge Sheets:** https://github.com/dabit3/jev-experiments/blob/main/judge-sheets/README.md
- **Sift / inbox-blitz:** https://github.com/dabit3/jev-experiments/blob/main/inbox-blitz/README.md
- **Jev Instant Search:** https://github.com/dabit3/jev-experiments/blob/main/jev-instant-search/README.md
- **Jev AX Pilot:** https://github.com/dabit3/jev-experiments/blob/main/jev-ax-pilot/README.md
- **Jev Voice Turn:** https://github.com/dabit3/jev-experiments/blob/main/jev-voice-turn/README.md
- **json-render Jev experiment:** https://json-render.dev/docs/jev
- **Liveblocks Sync:** https://liveblocks.io/sync
- **WindTunnel:** https://github.com/nekuda-ai/WindTunnel
- **WebMCP benchmark results:** https://webmcp.com/benchmark
- **Playwright MCP:** https://github.com/microsoft/playwright-mcp
- **Playwright actionability:** https://playwright.dev/docs/actionability
- **Playwright Trace Viewer:** https://playwright.dev/docs/trace-viewer
- **Playwright network mocking / HAR replay:** https://playwright.dev/docs/mock
- **agent-browser:** https://github.com/vercel-labs/agent-browser
- **agent-browser security:** https://agent-browser.dev/security
- **agent-browser snapshots:** https://agent-browser.dev/snapshots
- **agent-browser diffing:** https://agent-browser.dev/diffing
- **Stagehand:** https://github.com/browserbase/stagehand
- **Browser Use agent configuration:** https://docs.browser-use.com/open-source/customize/agent/all-parameters
- **Browser Harness:** https://github.com/browser-use/browser-harness
- **Chrome WebMCP introduction:** https://developer.chrome.com/blog/webmcp-epp
- **Microsoft UFO² / UFO³:** https://github.com/microsoft/UFO
- **UI-TARS Desktop / Agent TARS:** https://github.com/bytedance/UI-TARS-desktop
- **Microsoft OmniParser:** https://github.com/microsoft/OmniParser
- **Cua:** https://github.com/trycua/cua
- **Cua Driver platform support:** https://cua.ai/docs/reference/cua-driver/platform-support
- **Cua jev-use recipe:** https://cua.ai/docs/how-to-guides/driver/jev-use
- **Agent S original experience-augmented architecture:** https://www.simular.ai/articles/agent-s
- **OpenAdapt:** https://github.com/OpenAdaptAI/OpenAdapt
- **Skyvern:** https://github.com/Skyvern-AI/skyvern
- **BrowserOS:** https://github.com/browseros-ai/BrowserOS
- **Nanobrowser:** https://github.com/nanobrowser/nanobrowser
- **Windows-MCP:** https://github.com/CursorTouch/Windows-MCP
- **Midscene:** https://github.com/web-infra-dev/midscene
- **Chrome DevTools MCP:** https://github.com/ChromeDevTools/chrome-devtools-mcp
- **Chrome DevTools MCP tool reference:** https://github.com/ChromeDevTools/chrome-devtools-mcp/blob/main/docs/tool-reference.md

## Reverse index: donor → deduplicated features

| Donor/source | Feature IDs |
|---|---|
| [DCU README](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/README.md) | 1, 2, 42, 53 |
| [DCU UIA implementation](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/src/ShadowUse/Automation/UiaService.cs) | 3 |
| [Cua Driver platform support](https://cua.ai/docs/reference/cua-driver/platform-support) | 3, 42 |
| [UI-TARS Desktop / Agent TARS](https://github.com/bytedance/UI-TARS-desktop) | 4, 5 |
| [Microsoft OmniParser](https://github.com/microsoft/OmniParser) | 5 |
| [agent-browser snapshots](https://agent-browser.dev/snapshots) | 6 |
| [Jev AX Pilot](https://github.com/dabit3/jev-experiments/blob/main/jev-ax-pilot/README.md) | 6, 28, 43, 49 |
| [agent-browser diffing](https://agent-browser.dev/diffing) | 7 |
| [DCU action implementation](https://github.com/dev-willbird1936/Desktop-Computer-Use/blob/d6818547e5e92bdaf7c9fc61011e5851a37a7d42/src/ShadowUse/Tools/ComputerUseTools.cs) | 7, 16, 17, 45 |
| [Cua jev-use recipe](https://cua.ai/docs/how-to-guides/driver/jev-use) | 8, 19, 46 |
| [Stagehand](https://github.com/browserbase/stagehand) | 9, 31, 33, 39 |
| [Playwright actionability](https://playwright.dev/docs/actionability) | 9, 10, 16 |
| [Playwright MCP](https://github.com/microsoft/playwright-mcp) | 11, 14, 15 |
| [agent-browser](https://github.com/vercel-labs/agent-browser) | 11, 15, 39 |
| [Chrome WebMCP introduction](https://developer.chrome.com/blog/webmcp-epp) | 12 |
| [Microsoft UFO² / UFO³](https://github.com/microsoft/UFO) | 13, 29, 35 |
| [Browser Use agent configuration](https://docs.browser-use.com/open-source/customize/agent/all-parameters) | 17, 27, 33, 43 |
| [Browser Harness](https://github.com/browser-use/browser-harness) | 18 |
| [TypeSafe Choice](https://docs.typesafe.ai/primitives/choice) | 19 |
| [TypeSafe Jev introduction](https://docs.typesafe.ai/introduction) | 20 |
| [jev-ultrafast](https://github.com/browser-use/jev-ultrafast) | 20, 24 |
| [TypeSafe Score](https://docs.typesafe.ai/primitives/score) | 21 |
| [TypeSafe Noul](https://docs.typesafe.ai/primitives/noul) | 21 |
| [Vercel ai-cli](https://github.com/vercel-labs/ai-cli) | 21 |
| [TypeSafe confidence](https://docs.typesafe.ai/confidence) | 22 |
| [Agent S original experience-augmented architecture](https://www.simular.ai/articles/agent-s) | 23, 28 |
| [Nanobrowser](https://github.com/nanobrowser/nanobrowser) | 23, 27 |
| [JevPilot](https://github.com/standardagents/jevpilot) | 25, 26 |
| [Jev Launcher example (archived; successor linked in README)](https://github.com/dabit3/jev-experiments/blob/main/jev-launcher/README.md) | 26, 55, 56 |
| [OpenAdapt](https://github.com/OpenAdaptAI/OpenAdapt) | 30, 38, 46 |
| [Skyvern](https://github.com/Skyvern-AI/skyvern) | 32, 54 |
| [jev-browse](https://github.com/kyrylosyzonenko/jev-browse) | 34 |
| [BrowserOS](https://github.com/browseros-ai/BrowserOS) | 35, 48 |
| [Cua](https://github.com/trycua/cua) | 36, 53 |
| [agent-browser security](https://agent-browser.dev/security) | 37, 38, 40, 41 |
| [Jev 1.13 documented limits](https://docs.typesafe.ai/model-jaggedness/jev-1.13) | 40 |
| [Windows-MCP](https://github.com/CursorTouch/Windows-MCP) | 44 |
| [Midscene](https://github.com/web-infra-dev/midscene) | 47 |
| [Playwright Trace Viewer](https://playwright.dev/docs/trace-viewer) | 48 |
| [Chrome DevTools MCP](https://github.com/ChromeDevTools/chrome-devtools-mcp) | 50 |
| [Chrome DevTools MCP tool reference](https://github.com/ChromeDevTools/chrome-devtools-mcp/blob/main/docs/tool-reference.md) | 51 |
| [Playwright network mocking / HAR replay](https://playwright.dev/docs/mock) | 52 |
| [WindTunnel](https://github.com/nekuda-ai/WindTunnel) | 53 |
| [Jev Instant Search](https://github.com/dabit3/jev-experiments/blob/main/jev-instant-search/README.md) | 55 |
| [Sift / inbox-blitz](https://github.com/dabit3/jev-experiments/blob/main/inbox-blitz/README.md) | 55 |
| [Judge Sheets](https://github.com/dabit3/jev-experiments/blob/main/judge-sheets/README.md) | 57 |
| [Jev Voice Turn](https://github.com/dabit3/jev-experiments/blob/main/jev-voice-turn/README.md) | 58 |
| [json-render Jev experiment](https://json-render.dev/docs/jev) | 59 |
| [Liveblocks Sync](https://liveblocks.io/sync) | 60 |
