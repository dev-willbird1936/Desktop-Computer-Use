# Release readiness

## Result

The 0.1.2 candidate on `main` includes desktop control, native game tools,
normal/humanized foreground input, fast pacing, and multi-agent coordination.
Background delivery remains the default. It is not published; GitHub Windows CI
and review are still required before a hosted release.

JEV, the browser adapter, learned visual discovery, and alternative
kernel/hardware/memory input backends remain on the local experimental branch.
That branch was not merged wholesale or changed during this integration.

## Assessment

Scores are engineering judgments, not certification. Project scale: S2.3.

| Category | Score / 10.0 | Basis |
| --- | --- | --- |
| Correctness | 8.5 | Routing, coordination, observation lifetime and interrupted-input tests; application-specific behavior remains. |
| Multi-agent usability | 8.5 | Separate processes progress independently on different windows; same-window sequences serialize without permanent claims. Host registration remains outside DCU. |
| Verification | 9.0 | Unit checks and real owned-window input evidence, including injection tagging; hosted CI and real-game coverage remain pending. |
| Packaging | 9.0 | Self-contained executable, attribution files, settings utility and checksum recipe; unsigned. |
| Documentation | 9.0 | Independent toggles, per-call overrides, multi-agent model and operational limits documented. |
| Operational safety | 8.0 | Background default, explicit foreground opt-in, bounded key holds and cleanup reporting. Password access and optional desktop/bounds guards remain. |
| Overall readiness | 8.7 | Locally validated candidate, subject to hosted CI and final review before publication. |

## Local evidence

Evidence recorded on 2026-10-13 on Windows x64 with .NET SDK 10.0.301 and Python
3.11.4. Release .NET tests passed 95 checks with no failures; Python tests passed
15 checks. Compiler builds reported zero warnings and errors. Python Ruff LSP
reported no diagnostics in the changed settings server and live-regression
scripts. C# LSP was unavailable; install its configured command or update
`pi-lsp.json` to enable that diagnostic route.

The owned multi-agent regression verified:

- Independent MCP processes can work on different windows while one window is
  reserved by an atomic sequence; a second same-window action resumes afterward.
- A new observation does not immediately invalidate an earlier element ID.
  Foreign-server IDs are rejected instead of aliasing another server's elements.
- Game image metadata survives separate MCP invocations and later screenshots
  with different scales.
- Background phases produced zero PID-tagged DCU-injected mouse events while
  physical user activity remained allowed.
- Explicit humanized foreground clicking and typing reached an owned input field.
- A separate profile with UseRealCursor, GameMode, HumanizedInput and FastInput
  enabled loaded all four settings. Explicit background overrode that profile;
  its default foreground game move retained the owned foreground as requested.
- Windows OCR returned supported output on this host. Other installations can
  lack OCR languages, which is reported explicitly.

The existing multi-window regression passed movement, resize, same-process
window separation, concurrent actions, foreground preservation and one-minute
virtual-cursor cleanup. Additional unit checks verified that a simulated wedged
window UIA worker and blocked window capture do not disable an unrelated window.
Capture resources remain bounded to eight concurrent workers.

A negative control on the original stable cache reproduced immediate loss of an
older element ID after another observation; the corresponding candidate test
passes. Earlier baseline typing-cancellation negative-control evidence is also
retained locally.

NuGet's transitive vulnerability check reported no vulnerable packages from its
configured sources. This is not a comprehensive security audit. Credential
pattern checks are likewise not a guarantee that arbitrary private data is
absent.

## Validation findings

Live checks exposed and corrected a null UIA-name crash during text waits and
request-scoped game frame storage. A cold UIA window produced transient E_FAIL;
only the read is retried, and unresolved accessibility failures now return a
structured error rather than a generic non-JSON tool failure. Mutations are
never automatically repeated to compensate for an unobserved effect.

The background oracle was corrected to detect this test's injected mouse events
instead of requiring the physical mouse to remain still. The foreground test
checks that its requested text arrives exactly once; unrelated physical or
legacy-controller input can still interfere while foreground input is selected.
The final all-toggles run observed no extra text input. Foreground is an explicit
shared-device takeover, not independent concurrent user input.

## Remaining limits and publication gate

- Hosted GitHub CI has not run; no push, tag, hosted release or deployment was
  performed. Build release assets from the exact final reviewed `main` commit.
- Install the new executable and reconnect all cooperating MCP clients. Older
  binaries do not participate in the new cross-process coordination protocol.
- DCU cannot expose tools to an agent whose MCP host does not register/inherit
  them. It cannot provide multiple independent Windows system cursors.
- A timeout cannot abort a running COM call. Its window's worker may need a DCU
  restart; other windows have independent workers and native discovery/game
  input remains available.
- Elevated/minimized targets, GPU capture, UIA focus-changing fallbacks, and
  applications that reject normal SendInput retain documented limits. Real games
  and active user applications were not used as automated input targets.
- Input cleanup is attempted on interruption. If Windows rejects a release,
  DCU reports failure instead of claiming successful cleanup. Abrupt process
  termination during real input is not a guarantee of physical key release.
- The executable is unsigned. The optional browser settings utility requires
  Python, while the MCP executable does not.

See [Preparing a release](releasing.md) for packaged validation, approval and
rollback. The old desktop-only artifact should not be used for this expanded
candidate.
