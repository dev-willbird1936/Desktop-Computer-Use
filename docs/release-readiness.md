# Release readiness

## Result

The stable desktop-core 0.1.2 candidate is locally validated and packaged. It is
not published. Run the GitHub Windows workflow on the reviewed release branch
before approving a hosted release.

The full experimental tree is not the release candidate. It builds locally but
has unassigned Unity memory-input fields and dependency-pruning warnings. Its
JEV, browser, game, and perception behavior was not validated for publication in
this preparation.

## Assessment

Scores are engineering judgments, not certification. Project scale: S2.2.

| Category | Score / 10.0 | Basis |
| --- | --- | --- |
| Core correctness | 8.5 | Unit coverage and isolated live-window regression; application-specific limits remain. |
| Verification | 8.5 | Packaged MCP checks and stress evidence; hosted CI and broad application retesting remain pending. |
| Packaging | 9.0 | Self-contained executable, attribution files, ZIP, and SHA-256 checksums; unsigned. |
| Documentation | 9.0 | Versioned release notes, repeatable preparation, publication checklist, and explicit limits. |
| Operational safety | 7.0 | No default HTTP transport or real-input game backend; password access and optional desktop/bounds guards remain. |
| Overall readiness | 8.3 | Suitable for a reviewed core release after hosted checks, not a release of the experimental tree. |

## Verified locally

Evidence recorded on 2026-10-13 on Windows x64 with .NET SDK 10.0.301 and Python
3.11.4. The candidate branches from GitHub main at `d681854`, not from the local
JEV evolution commits.

- Release .NET suite: 42 passed, zero failed, zero omitted.
- Python harness and release-smoke unit suite: 11 passed.
- Typing-cancellation negative control: the same regression failed against the
  unchanged stable baseline because no cancellation exception was thrown after
  the full text stream. It passes on the candidate.
- Self-contained Windows x64 publish succeeded.
- Packaged MCP initialization reported `dcu` version `0.1.2`; its tool catalogue
  contained exactly the 12 core tools, and its read-only health check and shutdown
  succeeded.
- Isolated multi-window regression passed against the packaged executable. The
  fixture checked same-process window separation, movement, resize, concurrent
  actions, foreground preservation, and one-minute virtual-cursor cleanup.
- Screenshot regression tests passed ten consecutive runs after removing
  concurrent GDI+ reads during bitmap disposal.
- NuGet's transitive vulnerability check reported no vulnerable packages from
  the configured nuget.org source. This is not a comprehensive security audit.
- Credential-pattern scanning found no matching tokens or private-key headers
  in the candidate source. This does not establish that arbitrary private data
  is absent.
- Package contents are limited to `dcu.exe`, LICENSE, NOTICE.md, and README.md.
  No local settings, reference clones, JEV project, or browser runtime is shipped.

The first stable-baseline test run aborted with a test-host crash; a diagnostic
rerun passed all 32 baseline tests. The baseline screenshot test was reading a
bitmap concurrently with its disposal. That unsafe test race was removed, but no
crash dump established that it caused the initial abort. Subsequent candidate
checks and ten targeted repetitions passed.

## Remaining checks and limits

- The GitHub workflow has been added but has not run remotely. No branch push,
  tag, hosted release, or deployment was performed.
- C# LSP diagnostics were unavailable because the configured server command was
  unavailable. The compiler and test runner supplied local validation. Install
  the configured C# server or update its command in `pi-lsp.json` to enable LSP
  checks.
- Existing Chrome/application regression scripts were not rerun against the
  user's active applications. The live fixture tests operated only on windows
  they created.
- A timeout cannot abort a running UIA COM call; a permanently blocked target can
  require restarting DCU. Experimental worker replacement was not carried into
  this core candidate.
- The executable is unsigned. Minimized/elevated targets, raw-input games,
  hardware-accelerated capture, and focus-changing UIA fallbacks retain their
  documented limits.

See [Preparing a release](releasing.md) for final review, hosted checks,
publication approval, and rollback steps.
