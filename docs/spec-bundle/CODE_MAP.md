# Reference code map

Checkout root: `/other-software` (use the configured Windows mapping). Pin actual commits before relying on source. Each path is a read-first anchor or a documentation-named path; resolve it against the checkout and follow imports to implementation/tests. This file is an initial reading guide, not completed source-admission evidence.

## S01 — Cua / Cua Driver

Repository: https://github.com/trycua/cua.git

Root: `/other-software/cua`

Features: F03, F08, F19, F22, F36, F42, F46, F53

Read first: `libs/cua-driver/examples/jev-use/python/choose_action.py`; `libs/cua-driver/examples/jev-use/verify_setup.py`; `libs/cua-driver/examples/jev-use/fixtures/jev-choice-request-v1.json`; `README.md`

```bash
rg -n 'choose_bounded_with_typesafe|capture_id|capability|permission|sandbox|benchmark' /other-software/cua
```

Mechanism: The public jev-use example separates a bounded chooser from the application-owned Driver loop. Its chooser validates a versioned request, accepts only candidate IDs/descriptions and compact evidence, requires reobserve/abstain, and returns an allowlisted ID. The application resolves the original action. Visual actions carry the exact capture_id. The form example verifies the value received by the test server instead of accepting a model completion claim. The wider project supplies desktop-driver, sandbox and evaluation components.

Caution: The form recipe is a public preview, not proof that arbitrary applications work. Its code-level 32-candidate limit is an example limit, not the TypeSafe API limit. A platform driver may allow best-effort background behaviour that DCU strict mode must refuse.

## S02 — Jev Ultrafast

Repository: https://github.com/browser-use/jev-ultrafast.git

Root: `/other-software/jev-ultrafast`

Features: F08, F19, F20, F24, F26, F49

Read first: `jev_ultrafast/agent.py`; `jev_ultrafast/snapshot.js`; `jev_ultrafast/browser.py`; `jev_ultrafast/model.py`; `jev_ultrafast/questions.py`; `jev_ultrafast/demo.py`; `tests/`

```bash
rg -n 'click_target|type_text_target|TYPE_TEXT|focus|fresh|occlusion' /other-software/jev-ultrafast
```

Mechanism: An atomic browser observation builds an indexed table of actual DOM nodes. One TypeSafe request contains an operation question plus speculative target questions for compatible operations. Only the target for the selected operation is used. A separate small text model runs only for TYPE_TEXT. Browser execution rechecks freshness, geometry and occlusion. The inspector observes decisions but does not drive the underlying policy.

Caution: Its README uses browser focus emulation to keep hidden tabs rendering. Do not copy that into strict_background. The reviewed MVP explicitly excludes several surfaces, including frames, shadow roots, canvas, uploads and popup tabs; obtain those from the full browser adapter, not by assuming this demo implements them.

## S03 — Browser Harness

Repository: https://github.com/browser-use/browser-harness.git

Root: `/other-software/browser-harness`

Features: F18

Read first: `README.md`; `src/browser_harness/`; `docs/MCP.md`; `install.md`

```bash
rg -n 'helper|workspace|browser|doctor|CDP' /other-software/browser-harness
```

Mechanism: Browser Harness connects to a browser over CDP and exposes reusable helpers through an MCP interface. Its own src/browser_harness package stays protected while the agent writes missing helpers in a separate local agent workspace. DCU adds a formal admission/test boundary before those helpers can become trusted automation.

Caution: Review/admission, sandboxing and a protected executor are DCU requirements, not claims that every upstream helper workflow already provides them. Read actual helper-loading and execution code before adopting its interface.

## S04 — jev-browse

Repository: https://github.com/kyrylosyzonenko/jev-browse.git

Root: `/other-software/jev-browse`

Features: F34

Read first: `README.md`

```bash
rg -n 'extract|verbatim|noul|choice|browser' /other-software/jev-browse
```

Mechanism: The project uses Jev for browser decisions and includes an extraction mode intended to select relevant source material instead of having a text model rewrite it.

Caution: Verify the current extraction implementation after checkout; do not treat this repository as the missing source of the unrelated X demos.

## S05 — Jev experiments

Repository: https://github.com/dabit3/jev-experiments.git

Root: `/other-software/jev-experiments`

Features: F06, F21, F26, F28, F43, F49, F55, F56, F57, F58

Read first: `jev-ax-pilot/README.md`; `jev-ax-pilot/Sources/StateBuilder.swift`; `jev-ax-pilot/Sources/AnswerHandler.swift`; `jev-launcher/README.md`; `jev-launcher/Sources/JevQuestions.swift`; `judge-sheets/server/jev.ts`; `judge-sheets/src/lib/predict.ts`; `inbox-blitz/server/jev.ts`; `jev-instant-search/src/jev.ts`; `jev-voice-turn/Sources/TurnState.swift`; `jev-voice-turn/Sources/TurnPolicy.swift`

```bash
rg -n 'TreeFlattener|sequence|batchSpecs|JEV_CONCURRENCY|turn_complete|is_barge_in|time_window' /other-software/jev-experiments
```

Mechanism: AX Pilot flattens a macOS accessibility tree, supplies candidate IDs and code-extracted text, batches typed questions, and applies code-owned thresholds and loop guards. Launcher locally retrieves candidates, adds contextual evidence, reranks with Jev, rejects old responses and preserves manual selection. Judge Sheets batches questions by row/source text and caches asynchronous semantic formula results. Sift streams semantic email labels from a fixture. Voice Turn evaluates partial speech transcripts for completion and interruption while deterministic pause/silence rules remain in code.

Caution: Paths beyond the README anchors are named in the reviewed example documentation; resolve them against the pinned checkout. Launcher in this repository is archived and points to dabit3/launcher; that successor README could not be verified in the spec-writing pass. Sift is not proof of a live Gmail connector; Judge Sheets is not a Google Sheets add-on. Demo thresholds are not production calibration.

## S06 — JevPilot

Repository: https://github.com/standardagents/jevpilot.git

Root: `/other-software/jevpilot`

Features: F25, F26

Read first: `README.md`

```bash
rg -n 'candidate|cadence|interval|safety|choice|deterministic' /other-software/jevpilot
```

Mechanism: A simulation controller constructs valid candidate controls in ordinary code, avoids model decisions when the action is already determined, and varies decision timing with the state. This separates numerical/control constraints from semantic action selection.

Caution: Driving-specific controls and simulation results are not desktop reliability evidence. Having one technically valid action does not by itself authorise it or prove it advances the user goal.

## S07 — Vercel ai-cli

Repository: https://github.com/vercel-labs/ai-cli.git

Root: `/other-software/ai-cli`

Features: F21

Read first: `README.md`

```bash
rg -n 'choice|noul|score|stdin|stdout' /other-software/ai-cli
```

Mechanism: Typed decision primitives are exposed through a command-line interface so ordinary programs can consume classifications and judgments instead of parsing unconstrained prose.

Caution: This is decision plumbing, not a Windows driver, credential boundary or general task planner.

## S08 — Playwright MCP

Repository: https://github.com/microsoft/playwright-mcp.git

Root: `/other-software/playwright-mcp`

Features: F11, F14, F15

Read first: `README.md`

```bash
rg -n 'snapshot|browser_tabs|file_upload|dialog|isolated|storage-state' /other-software/playwright-mcp
```

Mechanism: An MCP interface exposes browser automation operations over Playwright, including structured page observations and tools for common browser surfaces. Configuration distinguishes persistent/profile-backed sessions from isolated contexts.

Caution: The public MCP repository can be a packaging/configuration entry point while implementation lives in the Playwright monorepo. Follow imports and package versions into S09; do not conclude that missing local implementation means the capability is absent.

## S09 — Playwright

Repository: https://github.com/microsoft/playwright.git

Root: `/other-software/playwright`

Features: F09, F10, F11, F14, F15, F16, F48, F52

Read first: `README.md`

```bash
rg -n 'waitForSelector|checkElementStates|retryAction|routeFromHAR|tracing|TraceViewer|frameLocator' /other-software/playwright
```

Mechanism: Locators resolve at action time and actionability checks wait for properties such as uniqueness, visibility, stability and event reception where relevant. Contexts isolate browser state. Traces retain action-linked evidence; network routing can fulfil requests from mocks or HAR recordings.

Caution: Checks differ by operation. Do not use force or JavaScript-dispatched clicks merely to bypass a failing actionability check. Browser-context isolation is not equivalent to OS/network isolation.

## S10 — agent-browser

Repository: https://github.com/vercel-labs/agent-browser.git

Root: `/other-software/agent-browser`

Features: F06, F07, F11, F15, F37, F38, F39, F40, F41

Read first: `README.md`

```bash
rg -n 'snapshot|diff|action-policy|confirm|auth|allowlist|content-boundaries' /other-software/agent-browser
```

Mechanism: A browser CLI exposes compact semantic snapshots with element references, scoped output, state/screenshot comparisons and session management. Its security interfaces include action policies, confirmation gates, an authentication vault, untrusted-content markers and destination restrictions.

Caution: The inspected README describes a Rust CLI/daemon; do not assume an older TypeScript daemon path. Follow the checked-out implementation. Browser allowlists have documented limitations and cannot be advertised as complete egress containment.

## S11 — Stagehand

Repository: https://github.com/browserbase/stagehand.git

Root: `/other-software/stagehand`

Features: F09, F31, F33, F39

Read first: `README.md`

```bash
rg -n 'observe|act|extract|cache|selector|selfHeal' /other-software/stagehand
```

Mechanism: The act, observe and extract interfaces interpret natural-language intent into browser operations or structured results. observe can resolve an action before sensitive values are supplied. Caching can reuse a previously resolved operation and reduce repeated inference.

Caution: Do not call cached selectors permanently valid or claim every UI change is automatically healed. Source version, page identity, parameter schema and observed context must be part of the cache contract.

## S12 — Browser Use

Repository: https://github.com/browser-use/browser-use.git

Root: `/other-software/browser-use`

Features: F17, F27, F33, F43

Read first: `README.md`

```bash
rg -n 'max_actions_per_step|max_failures|output_model_schema|controller|fallback|Agent' /other-software/browser-use
```

Mechanism: The agent loop supports configurable browser actions, model-related settings, structured output, failure/step controls and multi-action responses. Actions still run through a browser/session controller rather than being mere text descriptions.

Caution: Determine the exact model-fallback support in the pinned version. Provider failover must not widen data-egress permissions or cause duplicate external actions.

## S13 — Chrome DevTools MCP

Repository: https://github.com/ChromeDevTools/chrome-devtools-mcp.git

Root: `/other-software/chrome-devtools-mcp`

Features: F50, F51

Read first: `README.md`; `docs/tool-reference.md`

```bash
rg -n 'network|console|performance|emulate|sourceMap' /other-software/chrome-devtools-mcp
```

Mechanism: MCP tools expose DevTools browser diagnostics: network requests, console errors, performance traces and environment emulation. They provide evidence about what the browser did beyond its visible pixels.

Caution: Attach only to an approved browser context. Debugging ports, request bodies and traces can contain credentials; filter access and redact exports.

## S14 — WebMCP specification/examples

Repository: https://github.com/webmachinelearning/webmcp.git

Root: `/other-software/webmcp`

Features: F12

Read first: `README.md`; `implementation-status.md`

```bash
rg -n 'registerTool|modelContext|toolname|form|inputSchema' /other-software/webmcp
```

Mechanism: Pages expose JavaScript functions or HTML forms as described, schema-bearing tools. The invocation occurs in the page context so site-owned logic can perform the operation and update its own UI, unlike a separate backend integration.

Caution: This is an evolving web-platform interface, not automatically a standalone remote MCP server. Read implementation-status.md and test the installed browser; unsupported environments must fall back explicitly.

## S15 — Microsoft UFO

Repository: https://github.com/microsoft/UFO.git

Root: `/other-software/ufo`

Features: F13, F29, F35

Read first: `README.md`

```bash
rg -n 'AppAgent|HostAgent|Constellation|DAG|COM|dispatcher|api' /other-software/ufo
```

Mechanism: UFO combines host/application agents with UI and application-level operations. Its newer orchestration work represents cross-device/application tasks and dependencies as explicit graphs, allowing suitable independent work to be scheduled separately.

Caution: Distinguish UFO generations and follow the implementation corresponding to the feature. Cross-device orchestration is not permission to mutate one shared desktop concurrently.

## S16 — UI-TARS Desktop / Agent TARS

Repository: https://github.com/bytedance/UI-TARS-desktop.git

Root: `/other-software/ui-tars-desktop`

Features: F04, F05

Read first: `README.md`

```bash
rg -n 'operator|snapshot|screenshot|accessibility|grounding|browser' /other-software/ui-tars-desktop
```

Mechanism: The project family combines model-driven UI interaction with browser/computer operators and different observation modes, including visual grounding and semantic browser evidence.

Caution: Do not flatten all operators into an assumed focus-free backend. Check the selected operator implementation and preserve evidence provenance when modalities disagree.

## S17 — Microsoft OmniParser

Repository: https://github.com/microsoft/OmniParser.git

Root: `/other-software/omniparser`

Features: F05

Read first: `README.md`

```bash
rg -n 'get_som_labeled_img|check_ocr_box|icon|caption|bbox' /other-software/omniparser
```

Mechanism: Screenshot parsing identifies interactable regions and adds text/icon descriptions so a controller can choose among labelled visual targets even where application accessibility data is weak.

Caution: A detector bounding box does not prove clickability, goal relevance or authorisation. Review separate code/model-weight licences and requirements. Do not download weights or use OCR for every observation by default.

## S18 — Agent S

Repository: https://github.com/simular-ai/Agent-S.git

Root: `/other-software/agent-s`

Features: F23, F28

Read first: `README.md`

```bash
rg -n 'Manager|Worker|memory|experience|grounding|planner' /other-software/agent-s
```

Mechanism: The original Agent S architecture uses experience-augmented hierarchical planning and an agent-computer interface. Later versions separate general planning responsibilities and specialised grounding/control components in different ways.

Caution: The memory idea in this specification is associated with the original architecture. Do not assume the newest default branch retains the same modules; pin the relevant version or use the paper as architectural inspiration.

## S19 — OpenAdapt

Repository: https://github.com/OpenAdaptAI/OpenAdapt.git

Root: `/other-software/openadapt`

Features: F30, F38, F46

Read first: `README.md`

```bash
rg -n 'demo|replay|admission|compile|verify|workflow' /other-software/openadapt
```

Mechanism: The reviewed project describes learning/replay from demonstrations with a reviewed/admitted workflow boundary and outcome evidence. The transferable pattern is to turn a human-demonstrated task into a reusable artefact whose execution and success are separately checked.

Caution: This repository is the installer/CLI, not the compiler/runtime. Clone S28 openadapt-flow as well and follow its implementation. A recorded UI success banner is not independent evidence and replay is not automatic rollback.

## S20 — Skyvern

Repository: https://github.com/Skyvern-AI/skyvern.git

Root: `/other-software/skyvern`

Features: F32, F54

Read first: `README.md`

```bash
rg -n 'workflow|block|loop|file|http|live|takeover' /other-software/skyvern
```

Mechanism: Browser tasks can be composed with workflow blocks such as iteration, HTTP/file processing and outputs, while live browser access gives a human an intervention path during a run.

Caution: Do not require the whole platform to ship with DCU. Live human interaction must suspend the agent on that target; a view-only stream is not a complete takeover mechanism.

## S21 — BrowserOS

Repository: https://github.com/browseros-ai/BrowserOS.git

Root: `/other-software/browseros`

Features: F35, F48

Read first: `README.md`

```bash
rg -n 'parallel|workflow|trace|history|tab|agent' /other-software/browseros
```

Mechanism: The browser-agent environment exposes workflows and visibility into agent work, with support for independent browser work and inspecting a run. The useful pattern is explicit ownership of browser execution contexts with an observable history.

Caution: Validate the current implementation before promising concurrency or replay semantics. Shared browser profiles may still share cookies, downloads and other mutable resources.

## S22 — Nanobrowser

Repository: https://github.com/nanobrowser/nanobrowser.git

Root: `/other-software/nanobrowser`

Features: F23, F27

Read first: `README.md`

```bash
rg -n 'planner|navigator|model|provider|validator' /other-software/nanobrowser
```

Mechanism: A browser-extension agent separates planning and navigation responsibilities and supports configurable model/provider choices for agent roles.

Caution: Extension permissions and browser-profile access are separate security decisions. Configurable providers do not imply local inference or unrestricted cross-provider data sharing.

## S23 — Windows-MCP

Repository: https://github.com/CursorTouch/Windows-MCP.git

Root: `/other-software/windows-mcp`

Features: F44

Read first: `README.md`

```bash
rg -n 'OAuth|PKCE|bearer|TLS|transport|tool' /other-software/windows-mcp
```

Mechanism: The reviewed server exposes Windows capabilities through MCP and documents optional authenticated remote transport, including bearer/OAuth-PKCE related setup and tool restrictions.

Caution: Verify available authentication modes at the checkout. Its desktop input mechanism is not a substitute for DCU background guarantees. Remote transport must remain disabled unless configured.

## S24 — Midscene

Repository: https://github.com/web-infra-dev/midscene.git

Root: `/other-software/midscene`

Features: F47

Read first: `README.md`

```bash
rg -n 'aiAssert|aiWaitFor|assert|screenshot|report' /other-software/midscene
```

Mechanism: Semantic assertions and waits evaluate a natural-language condition against current UI evidence, supplementing deterministic selectors and values for visually expressed results.

Caution: A model-based assertion can be wrong. It is not sufficient by itself to confirm a high-impact external action, nor should it be labelled deterministic.

## S25 — WindTunnel

Repository: https://github.com/nekuda-ai/WindTunnel.git

Root: `/other-software/windtunnel`

Features: F53

Read first: `README.md`

```bash
rg -n 'benchmark|task|runner|metric|success|webmcp' /other-software/windtunnel
```

Mechanism: The benchmark harness runs configured web-agent/model combinations against a defined task environment and records outcomes and timings for comparison, including the associated WebMCP benchmark context.

Caution: Do not transfer a published leaderboard number into DCU. Re-run on DCU with failed/refused tasks retained in the denominator and publish local environment/version details.

## S26 — json-render

Repository: https://github.com/vercel-labs/json-render.git

Root: `/other-software/json-render`

Features: F59

Read first: `README.md`

```bash
rg -n 'jev|catalog|candidate|compose|schema' /other-software/json-render
```

Mechanism: The experimental Jev integration chooses/composes among prepared component candidates from a defined UI catalogue. Text and data must be supplied independently; the model does not make arbitrary UI code safe by generating it.

Caution: The reviewed Jev documentation labels the integration experimental/unreleased. Build against the actual pinned API or keep this optional feature blocked with a precise reason.

## S27 — Liveblocks

Repository: https://github.com/liveblocks/liveblocks.git

Root: `/other-software/liveblocks`

Features: F60

Read first: `README.md`; `examples/`; `docs/`

```bash
rg -n 'presence|history|undo|storage|room|authorize' /other-software/liveblocks
```

Mechanism: Sync exposes collaboration primitives for shared state and presence. Client integrations support collaborative updates and user-specific history/undo; server-side infrastructure manages authenticated room access and synchronisation.

Caution: Cloning SDKs/examples does not itself reproduce the hosted service. The inspected repository identifies mixed component licences; review the exact imported packages. Document undo does not undo emails, purchases or arbitrary desktop changes.

## S28 — OpenAdapt Flow compiler/runtime

Repository: https://github.com/OpenAdaptAI/openadapt-flow.git

Root: `/other-software/openadapt-flow`

Features: F30, F38, F46

Read first: `README.md`; `docs/EFFECT_KIT.md`; `docs/TUTORIAL.md`

```bash
rg -n 'compile|admit|effect|verifier|RECONCILIATION_REQUIRED|runtime' /other-software/openadapt-flow
```

Mechanism: The OpenAdapt root repository explicitly delegates compilation and execution to openadapt-flow. Flow compiles a demonstration into a program whose healthy replay path does not need a generative-model API. Governed verification checks declared effects through independent system-of-record reads. An apparently successful UI write that the independent read refutes ends in reconciliation rather than a blind retry.

Caution: A fixture-specific generated effect proposal requires review for a real application. Do not copy product-specific certification, commercial labels or evidence-tier names into DCU as if DCU had earned them.

